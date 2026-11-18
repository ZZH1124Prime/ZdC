using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ZdC.Core;

/// <summary>Owns only the controller process. Disposing restores the native session without terminating the application.</summary>
public sealed class ControlledApplicationSession : IAsyncDisposable
{
    private readonly string _controllerPath;
    private readonly BoundedLocalLog? _log;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _gate = new();
    private readonly TaskCompletionSource<int> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _controller;
    private Task _stdout = Task.CompletedTask, _stderr = Task.CompletedTask;
    private TaskCompletionSource<bool>? _acknowledgement;
    private string? _expectedReply;
    private bool _started, _disposed;
    private int _applicationProcessId;
    private int _routingEnabled;
    private int _running;
    private string? _physicalSource;
    private ConnectionTimingChannel? _timing;
    private ApplicationControlLease? _lease;
    private RuntimeModuleStore.Lease? _runtimeModule;
    private readonly ApplicationConnectionObserver _connections = new();
    private ApplicationSessionState? _lastConnected;
    private long _absenceSince, _lastAbsenceSample;
    private int _absenceSamples;

    public ControlledApplicationSession(string controllerPath, BoundedLocalLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerPath);
        _controllerPath = Path.GetFullPath(controllerPath);
        _log = log;
    }

    public int? ApplicationProcessId => Volatile.Read(ref _applicationProcessId) is var id && id > 0 ? id : null;
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public bool RoutingEnabled => Volatile.Read(ref _routingEnabled) != 0;

    internal bool? HasLiveApplicationProcesses(bool children)
    {
        SyncLeaseMembers();
        return _connections.HasLiveMembers(children);
    }
    private long _lastLeaseSample;
    private void SyncLeaseMembers()
    {
        if (_lease is null || (_lastLeaseSample != 0 && Environment.TickCount64 - _lastLeaseSample < 3000)) return;
        _lastLeaseSample = Environment.TickCount64;
        foreach (var member in _lease.LiveMembers()) _connections.SetKnownMember(member.Pid, member.Created);
    }

    internal void InvalidateConnectionState()
    {
        _connections.Invalidate(); _timing?.Invalidate();
        _lastConnected = null;
        _absenceSince = 0; _lastAbsenceSample = 0; _absenceSamples = 0;
    }

    internal IReadOnlyList<(int Pid, long Created)> LiveMemberIdentities()
    {
        _lastLeaseSample = 0;
        SyncLeaseMembers();
        return _connections.LiveMemberIdentities();
    }

    internal ApplicationSessionState GetConnectionState(bool children)
    {
        SyncLeaseMembers();
        var enabled = RoutingEnabled;
        var sample = _connections.Read(Volatile.Read(ref _physicalSource), children, enabled);
        var state = new ApplicationSessionState(IsRunning, enabled, ApplicationProcessId)
        { ConnectionStatus = sample.Status, DirectConnectionCount = enabled ? sample.Count : 0, ObservedConnectionCount = sample.Count, ObservedAt = sample.At };
        if (sample.Status is ApplicationConnectionStatus.DirectObserved or ApplicationConnectionStatus.DefaultObserved)
        {
            var latency = _timing?.Read(_physicalSource, _connections.MatchesConnection, enabled, _log);
            _lastConnected = state with { LatencyMs = latency };
            _absenceSince = 0; _lastAbsenceSample = 0; _absenceSamples = 0;
            return _lastConnected;
        }
        if (_lastConnected is null) return state;
        var tick = _connections.SampleTick;
        if (tick != _lastAbsenceSample)
        {
            // A long pause in UI sampling is not continuous evidence of lost connectivity.
            if (_lastAbsenceSample == 0 || tick - _lastAbsenceSample > 10000)
            { _absenceSince = tick; _absenceSamples = 0; }
            _lastAbsenceSample = tick;
            _absenceSamples++;
        }
        if (_absenceSamples < 3 || tick - _absenceSince < 15000)
            return _lastConnected with { IsObservationStale = true,
                LatencyMs = _timing?.Read(_physicalSource, _connections.MatchesConnection, enabled, _log) };
        _lastConnected = null;
        return state;
    }
    internal static ControlledApplicationSession? TryRestore(string application, string controllerPath, BoundedLocalLog? log)
    {
        ApplicationControlLease? lease = null;
        ConnectionTimingChannel? timing = null;
        try
        {
            var found = ApplicationControlLease.Find(application);
            if (found is null) return null;
            lease = new ApplicationControlLease(found.ControlName);
            var members = lease.LiveMembers();
            if (members.Count == 0) { lease.Dispose(); return null; }
            timing = new ConnectionTimingChannel(found.TimingName);
            if (!lease.Claim(false)) { timing.Dispose(); lease.Dispose(); return null; }
            var result = new ControlledApplicationSession(controllerPath, log)
            {
                _lease = lease, _timing = timing, _started = true, _running = 1,
                _applicationProcessId = members.Any(m => m.Pid == found.RootPid) ? found.RootPid : members[0].Pid,
                _physicalSource = found.Source
            };
            foreach (var member in members) result._connections.SetKnownMember(member.Pid, member.Created);
            result._ready.TrySetResult(result._applicationProcessId);
            log?.Write($"session reclaimed app={result._applicationProcessId} members={members.Count}");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { timing?.Dispose(); lease?.Dispose(); log?.Write($"session reclaim unavailable type={ex.GetType().Name}"); return null; }
    }

    /// <summary>A session launches once. Arguments are passed individually, never through a shell.</summary>
    public async Task<int> StartAsync(string executablePath, IEnumerable<string>? arguments = null,
        CancellationToken cancellationToken = default,
        string? workingDirectory = null, int windowStyle = 1, bool routingEnabled = true)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("This session has already been started.");
            var application = Path.GetFullPath(executablePath);
            if (!File.Exists(application)) throw new FileNotFoundException("Application executable was not found.", application);
            if (!File.Exists(_controllerPath)) throw new FileNotFoundException("Socket controller was not found.", _controllerPath);
            _runtimeModule = RuntimeModuleStore.Prepare(Path.Combine(Path.GetDirectoryName(_controllerPath)!, "ZdC.SocketHook64.dll"));
            var info = new ProcessStartInfo(_controllerPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_controllerPath)!
            };
            info.Environment["ZDC_RUNTIME_HOOK"] = _runtimeModule.Path;
            info.ArgumentList.Add("--app");
            info.ArgumentList.Add(application);
            foreach (var argument in arguments ?? []) info.ArgumentList.Add(argument);
            info.Environment["ZDC_WORKING_DIRECTORY"] = workingDirectory ?? Path.GetDirectoryName(application)!;
            info.Environment["ZDC_WINDOW_STYLE"] = windowStyle.ToString(CultureInfo.InvariantCulture);
            info.Environment["ZDC_START_ENABLED"] = routingEnabled ? "1" : "0";
            Volatile.Write(ref _routingEnabled, routingEnabled ? 1 : 0);
            info.Environment["ZDC_OWNER_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            using (var owner = Process.GetCurrentProcess())
                info.Environment["ZDC_OWNER_CREATED"] = owner.StartTime.ToUniversalTime().ToFileTimeUtc()
                    .ToString(CultureInfo.InvariantCulture);
            cancellationToken.ThrowIfCancellationRequested();
            _timing = new ConnectionTimingChannel();
            _lease = new ApplicationControlLease();
            if (!_lease.Claim(routingEnabled)) throw new IOException("Unable to claim new control lease.");
            info.Environment["ZDC_CONTROL_MAP"] = _lease.Name;
            info.Environment["ZDC_TIMING_MAP"] = _timing.Name;
            _started = true;
            _controller = new Process { StartInfo = info };
            try
            {
                if (!_controller.Start()) throw new IOException("The socket controller did not start.");
            }
            catch { _controller.Dispose(); _controller = null; _timing?.Dispose(); _timing = null; _lease?.Release(); _lease?.Dispose(); _lease = null; _runtimeModule?.Dispose(); _runtimeModule = null; throw; }
            _log?.Write($"controller started pid={_controller.Id}");
            _stdout = ConsumeAsync(_controller.StandardOutput, protocol: true);
            _stderr = ConsumeAsync(_controller.StandardError, protocol: false);
            try
            {
                var id = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (_controller.HasExited) throw new IOException("The socket controller exited during startup.");
                // CreateProcess returns before loader initialization. Require actual Hook registration.
                var registered = false;
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    if (_lease.LiveMembers().Any(member => member.Pid == id)) { registered = true; break; }
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
                if (!registered) throw new System.ComponentModel.Win32Exception(1114);
                ApplicationControlLease.Save(new(application, _lease.Name, _timing.Name, _physicalSource ?? "", id));
                return id;
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            if (_controller is null)
            {
                _lease?.Release(); _lease?.Dispose(); _lease = null;
                _timing?.Dispose(); _timing = null;
                _runtimeModule?.Dispose(); _runtimeModule = null;
            }
            throw;
        }
        finally { _operations.Release(); }
    }

    /// <summary>Returns only after the matching native acknowledgement. An interrupted command closes this session.</summary>
    public async Task SetRoutingEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lease is not null && _ready.Task.IsCompletedSuccessfully)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _lease.SetEnabled(enabled);
                Volatile.Write(ref _routingEnabled, enabled ? 1 : 0);
                _log?.Write($"control lease app={ApplicationProcessId} enabled={enabled}");
                return;
            }
            if (_controller is null || !_ready.Task.IsCompletedSuccessfully || _controller.HasExited)
                throw new InvalidOperationException("The controller session is not running.");
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _expectedReply = enabled ? "ROUTING_ON" : "ROUTING_OFF";
                _acknowledgement = completion;
            }
            try
            {
                _log?.Write($"control send controller={_controller.Id} app={ApplicationProcessId} enabled={enabled}");
                await _controller.StandardInput.WriteLineAsync((enabled ? "on" : "off").AsMemory(), cancellationToken).ConfigureAwait(false);
                await _controller.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Write($"control failure app={ApplicationProcessId} type={ex.GetType().Name} hresult={ex.HResult}");
                // A late reply must never satisfy a subsequent command.
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
            finally { lock (_gate) { _acknowledgement = null; _expectedReply = null; } }
        }
        finally { _operations.Release(); }
    }

    public async Task StopAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private async Task ConsumeAsync(StreamReader reader, bool protocol)
    {
        // Bound both retained log lines and the line parser, even if a child emits a huge line.
        var buffer = new char[1024];
        var line = new StringBuilder(512);
        var truncated = false;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                for (var i = 0; i < count; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        ReceiveLine(line.ToString().TrimEnd('\r'), protocol && !truncated);
                        line.Clear(); truncated = false;
                    }
                    else if (line.Length < 4096) line.Append(c);
                    else truncated = true;
                }
            if (line.Length > 0) ReceiveLine(line.ToString().TrimEnd('\r'), protocol && !truncated);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { _log?.Write($"controller output failure app={ApplicationProcessId} protocol={protocol} type={ex.GetType().Name}"); }
        finally
        {
            if (protocol && _lease is null)
            {
                _log?.Write($"controller stdout ended app={ApplicationProcessId}");
                Volatile.Write(ref _routingEnabled, 0);
                Volatile.Write(ref _running, 0);
                var error = new IOException("The socket controller output closed.");
                _ready.TrySetException(error);
                lock (_gate) { _acknowledgement?.TrySetException(error); }
            }
        }
    }

    private void ReceiveLine(string line, bool protocol)
    {
        // Never retain application stdout, addresses, paths, arguments, or payloads.
        if (line is "ROUTING_ON" or "ROUTING_OFF")
            _log?.Write($"control reply app={ApplicationProcessId} reply={line}");
        if (protocol && line.StartsWith("EXISTING_APPLICATION pid=", StringComparison.Ordinal))
            _ready.TrySetException(new InvalidOperationException("应用已在运行，请先自行退出，再从 ZdC 启动。"));
        if (!protocol) return;
        const string controllerPrefix = "APPLICATION_CONTROLLER source=";
        if (line.StartsWith(controllerPrefix, StringComparison.Ordinal))
        {
            var source = line[controllerPrefix.Length..].Split(' ', 2)[0];
            Volatile.Write(ref _physicalSource, source);
        }
        const string startErrorPrefix = "APPLICATION_START_ERROR ";
        if (protocol && line.StartsWith(startErrorPrefix, StringComparison.Ordinal) &&
            int.TryParse(line.AsSpan(startErrorPrefix.Length), out var startError))
            _ready.TrySetException(new IOException("应用启动失败：" + new System.ComponentModel.Win32Exception(startError).Message));
        const string prefix = "APPLICATION_STARTED pid=";
        if (line.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(line.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            Volatile.Write(ref _applicationProcessId, id);
            _connections.SetRoot(id);
            Volatile.Write(ref _running, 1);
            _ready.TrySetResult(id);
        }
        lock (_gate)
        {
            if (_acknowledgement is not null && string.Equals(line, _expectedReply, StringComparison.Ordinal))
            {
                Volatile.Write(ref _routingEnabled, line == "ROUTING_ON" ? 1 : 0);
                _acknowledgement.TrySetResult(true);
            }
        }
    }

    private async Task StopCoreAsync()
    {
        _lease?.Release();
        _lease?.Dispose(); _lease = null;
        _runtimeModule?.Dispose(); _runtimeModule = null;
        Volatile.Write(ref _routingEnabled, 0);
        Volatile.Write(ref _running, 0);
        var process = _controller;
        if (process is null)
        {
            _timing?.Dispose(); _timing = null;
            _connections.Dispose();
            return;
        }
        var exited = false;
        _log?.Write($"controller stop requested pid={process.Id} app={ApplicationProcessId}");
        try
        {
            if (!process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await process.StandardInput.WriteLineAsync("q".AsMemory(), timeout.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
                { _log?.Write($"controller shutdown input failure app={ApplicationProcessId} type={ex.GetType().Name}"); }
                finally { process.StandardInput.Close(); }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    // Never use entireProcessTree: the real application must remain open.
                    _log?.Write($"controller forced exit pid={process.Id} app={ApplicationProcessId}");
                    try { process.Kill(entireProcessTree: false); }
                    catch (InvalidOperationException) { }
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
            }
            exited = process.HasExited;
        }
        finally
        {
            // 退出失败时保留进程引用与状态，供调用方再次停止。
            if (exited)
            {
                Volatile.Write(ref _routingEnabled, 0);
                Volatile.Write(ref _running, 0);
                // Native children may inherit pipes, so output draining has a finite wait.
                try { await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (TimeoutException) { }
                process.StandardOutput.Dispose(); process.StandardError.Dispose();
                _log?.Write($"controller stopped pid={process.Id} code={process.ExitCode} app={ApplicationProcessId}");
                process.Dispose(); _controller = null;
                _timing?.Dispose(); _timing = null;
                _connections.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await StopCoreAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally { _operations.Release(); }
    }
}
