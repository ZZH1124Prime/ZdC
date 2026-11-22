using System.Collections.Concurrent;
using System.Diagnostics;

namespace ZdC.Core;

/// <summary>Use RegisteredPath for subsequent control calls; launch may resolve an updated installation.</summary>
public sealed record ApplicationLaunchResult(string RegisteredPath, int ProcessId);
public sealed record ApplicationSessionState(bool IsRunning, bool RoutingEnabled, int? ProcessId)
{
    public ApplicationConnectionStatus ConnectionStatus { get; init; } = ApplicationConnectionStatus.Waiting;
    public double? LatencyMs { get; init; }
    public bool IsObservationStale { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public int DirectConnectionCount { get; init; }
    public int ObservedConnectionCount { get; init; }
    public bool IsExternalProcessRunning { get; init; }
}

/// <summary>
/// 持久注册与运行期控制分离。每个启动项拥有独立控制器，停用、移除或管理器退出
/// 只结束相应接管会话，保留真实应用；已有连接保持原路径，新socket遵循当前开关。
/// </summary>
public sealed class RegisteredApplicationManager : IAsyncDisposable
{
    private const string InvalidEntryMessage = "入口已失效，请拖入新入口替换。";
    private readonly ApplicationRegistry _registry;
    private readonly string _controllerPath;
    private readonly BoundedLocalLog? _log;
    private readonly bool _ownsLog;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly ConcurrentDictionary<string, ControlledApplicationSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded, _disposed;
    private readonly object _admission = new();
    private readonly Dictionary<string, EntryGate> _entryGates = new(StringComparer.OrdinalIgnoreCase);
    private int _activeOperations;
    private bool _maintenance;
    private Task _idle = Task.CompletedTask;
    private TaskCompletionSource? _idleSignal;

    public RegisteredApplicationManager(string controllerPath, string? registryPath = null, BoundedLocalLog? log = null)
    {
        _controllerPath = Path.GetFullPath(controllerPath);
        _registry = new ApplicationRegistry(registryPath);
        _ownsLog = log is null;
        _log = log ?? new BoundedLocalLog(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZdC", "diagnostics"));
        _log.Write($"manager start owner={Environment.ProcessId}");
    }

    public string RegistryPath => _registry.FilePath;

    public async Task<IReadOnlyList<RegisteredApplication>> LoadAsync(CancellationToken token = default)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await BeginMaintenance().WaitAsync(token).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            await CleanupRuntimeAsync().ConfigureAwait(false);
            var entries = await _registry.LoadAsync(token).ConfigureAwait(false);
            // 显式重读后，被外部删除的注册项停止接管；不终止对应应用。
            var retained = entries.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var path in _sessions.Keys.Where(x => !retained.ContainsKey(x)).ToArray())
                await StopCoreAsync(path).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (_sessions.ContainsKey(entry.Path)) continue;
                var restored = await Task.Run(() => ControlledApplicationSession.TryRestore(entry.Path, _controllerPath, _log), token).ConfigureAwait(false);
                if (restored is not null) _sessions[entry.Path] = restored;
            }
            foreach (var entry in entries)
                if (!_sessions.ContainsKey(entry.Path) && entry.ShortcutPath is not null)
                    await _registry.RefreshShortcutAsync(entry.Path, token).ConfigureAwait(false);
            _loaded = true;
            return await _registry.ListAsync(token).ConfigureAwait(false);
        }
        finally { lock (_admission) _maintenance = false; _operations.Release(); }
    }

    public async Task<RegisteredApplication> RegisterAsync(string executablePath,
        CancellationToken token = default)
    {
        var operation = await EnterAsync(Path.GetFullPath(executablePath), token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            return await _registry.RegisterAsync(executablePath, token).ConfigureAwait(false);
        }
        finally { operation.Dispose(); }
    }

    public async Task<IReadOnlyList<RegisteredApplication>> ListAsync(CancellationToken token = default)
    {
        var operation = await EnterAsync("registry:list", token).ConfigureAwait(false);
        try { EnsureReady(); return await _registry.ListAsync(token).ConfigureAwait(false); }
        finally { operation.Dispose(); }
    }

    public async Task<RegisteredApplication> ImportAsync(string sourcePath, CancellationToken token = default)
    {
        var entry = await ApplicationEntryReader.ReadAsync(sourcePath, token: token).ConfigureAwait(false);
        var operation = await EnterAsync(Path.GetFullPath(entry.Path), token).ConfigureAwait(false);
        try { EnsureReady(); return await _registry.ImportAsync(entry, cancellationToken: token).ConfigureAwait(false); }
        finally { operation.Dispose(); }
    }

    public async Task<RegisteredApplication> ReplaceAsync(string oldRegisteredPath, string sourcePath,
        CancellationToken token = default)
    {
        var entry = await ApplicationEntryReader.ReadAsync(sourcePath, token: token).ConfigureAwait(false);
        var operation = await EnterAsync(Path.GetFullPath(oldRegisteredPath), token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var old = Path.GetFullPath(oldRegisteredPath);
            _log?.Write("registry replace requested");
            await StopCoreAsync(old).ConfigureAwait(false);
            return await _registry.ImportAsync(entry, old, token).ConfigureAwait(false);
        }
        finally { operation.Dispose(); }
    }

    public async Task<ApplicationSessionState> GetSessionStateAsync(string registeredPath, CancellationToken token = default)
    {
        var operation = await EnterAsync(Path.GetFullPath(registeredPath), token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var path = Path.GetFullPath(registeredPath);
            if (!_sessions.TryGetValue(path, out var session))
            {
                var entry = (await _registry.ListAsync(token).ConfigureAwait(false))
                    .FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
                return entry is null ? new(false, false, null)
                    : await Task.Run(() => ObserveExternalApplication(entry), token).ConfigureAwait(false);
            }
            // Process/TCP snapshots run off the caller (including WPF's dispatcher thread).
            var state = await Task.Run(() =>
            {
                var running = session.IsRunning;
                var live = running ? session.HasLiveApplicationProcesses(true) : null;
                if (!running || live == false)
                {
                    _log?.Write($"session cleanup app={session.ApplicationProcessId} controllerRunning={running} liveMembers={live}");
                    return null;
                }
                return session.GetConnectionState(true);
            }, token).ConfigureAwait(false);
            if (state is not null) return state;
            await StopCoreAsync(path).ConfigureAwait(false);
            return new(false, false, null);
        }
        finally { operation.Dispose(); }
    }

    public async Task<ApplicationLaunchResult> LaunchAsync(string registeredPath, IEnumerable<string>? arguments = null,
        CancellationToken token = default, bool routingEnabled = true)
    {
        var old = Path.GetFullPath(registeredPath);
        string path;
        using (await EnterAsync(old, token).ConfigureAwait(false))
        {
            if (_sessions.TryGetValue(old, out var existing))
            {
                if (existing.IsRunning && existing.HasLiveApplicationProcesses(true) != false)
                    throw new InvalidOperationException("该注册项已有接管会话，请使用启用或停用操作。");
                await StopCoreAsync(old).ConfigureAwait(false);
            }
            path = (await _registry.RefreshShortcutAsync(old, token).ConfigureAwait(false)).Path;
        }

        // Lock the resolved identity before starting. Other cards may already be using that path.
        var operation = await EnterAsync(path, token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var entry = (await _registry.ListAsync(token).ConfigureAwait(false))
                .SingleOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("请先注册该应用，再从工具启动。");
            if (_sessions.TryGetValue(path, out var originalSession) && originalSession.IsRunning &&
                originalSession.HasLiveApplicationProcesses(true) != false)
                throw new InvalidOperationException("该注册项已有接管会话，请使用启用或停用操作。");
            if (!File.Exists(entry.Path))
                throw new FileNotFoundException("入口已失效，请拖入新入口替换。", entry.Path);
            if (entry.WorkingDirectory is not null && !Directory.Exists(entry.WorkingDirectory))
                throw new DirectoryNotFoundException("入口起始位置已失效，请拖入新的快捷方式替换。");
            if (HasApplicationProcesses(path))
                throw new InvalidOperationException("应用已在运行，请先自行退出应用，再从 ZdC 启动。");
            await StopCoreAsync(path).ConfigureAwait(false);
            var session = new ControlledApplicationSession(_controllerPath, _log);
            try
            {
                var pid = await session.StartAsync(entry.Path, arguments ?? entry.Arguments, token, entry.WorkingDirectory, entry.WindowStyle, routingEnabled).ConfigureAwait(false);
                _sessions[path] = session;
                return new ApplicationLaunchResult(path, pid);
            }
            catch
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch
                {
                    // 启动失败后的清理也可能失败；保留会话，允许 Stop/Dispose 重试。
                    _sessions[path] = session;
                    throw;
                }
                throw;
            }
        }
        finally { operation.Dispose(); }
    }

    public async Task SetEnabledAsync(string registeredPath, bool enabled, CancellationToken token = default)
    {
        var operation = await EnterAsync(Path.GetFullPath(registeredPath), token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var path = Path.GetFullPath(registeredPath);
            if (!_sessions.TryGetValue(path, out var session))
                throw new InvalidOperationException("该应用尚未通过本工具启动。");
            await session.SetRoutingEnabledAsync(enabled, token).ConfigureAwait(false);
            session.InvalidateConnectionState();
        }
        finally { operation.Dispose(); }
    }

    public async Task<bool> RemoveAsync(string registeredPath, CancellationToken token = default)
    {
        var operation = await EnterAsync(Path.GetFullPath(registeredPath), token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var path = Path.GetFullPath(registeredPath);
            // 先停用再保存；保存失败时注册仍保留，接管保持停止以免误报移除。
            await StopCoreAsync(path).ConfigureAwait(false);
            return await _registry.RemoveAsync(path, token).ConfigureAwait(false);
        }
        finally { operation.Dispose(); }
    }

    public async Task StopAsync(string registeredPath, CancellationToken token = default)
    {
        var operation = await EnterAsync(Path.GetFullPath(registeredPath), token).ConfigureAwait(false);
        try { EnsureReady(); _log?.Write("session explicit stop requested"); await StopCoreAsync(Path.GetFullPath(registeredPath)).ConfigureAwait(false); }
        finally { operation.Dispose(); }
    }

    // 根进程退出后，同名网络子进程可能仍在运行；保留其会话。
    // 与原生启动器相同，保守地拒绝已有同名进程，而不是终止它们。
    // Existing ordinary launches are observed without injection or changing their routing.
    // TCP state is available; historical connect duration is not, so LatencyMs stays null.
    private static ApplicationSessionState ObserveExternalApplication(RegisteredApplication entry)
    {
        using var observer = new ApplicationConnectionObserver();
        var matched = false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(entry.Path)))
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, entry.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    observer.SetRoot(process.Id);
                    matched = true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        if (!matched) return new(false, false, null);
        var sample = observer.Read(null, true, routingEnabled: false);
        return new(false, false, null)
        {
            IsExternalProcessRunning = true, ConnectionStatus = sample.Status,
            ObservedConnectionCount = sample.Count, ObservedAt = sample.At
        };
    }
    public async Task<IReadOnlyList<ApplicationBlockingProcess>> GetBlockingProcessesAsync(RegisteredApplication expectedEntry, CancellationToken token = default)
    {
        var path = Path.GetFullPath(expectedEntry.Path);
        var operation = await EnterAsync(path, token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            await RequireSameBlockingProcessEntryAsync(path, expectedEntry, token).ConfigureAwait(false);
            var snapshot = await Task.Run<IReadOnlyList<ApplicationBlockingProcess>>(() =>
            {
                var known = _sessions.TryGetValue(path, out var session)
                    ? ApplicationProcessControl.ListKnown(session.LiveMemberIdentities()) : [];
                return ApplicationProcessControl.List(path).Concat(known).DistinctBy(x => (x.ProcessId, x.CreatedFileTimeUtc))
                    .OrderBy(x => x.ProcessId).ToArray();
            }, token).ConfigureAwait(false);
            // With no process to stop, leave normal shortcut recovery to the launch path.
            using var shortcut = snapshot.Count == 0 ? null
                : await OpenMatchingShortcutAsync(expectedEntry, token).ConfigureAwait(false);
            return snapshot;
        }
        finally { operation.Dispose(); }
    }

    public async Task<IReadOnlyList<int>> ForceStopBlockingProcessesAsync(RegisteredApplication expectedEntry,
        IReadOnlyList<ApplicationBlockingProcess> confirmed, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        var frozen = confirmed.ToArray();
        var path = Path.GetFullPath(expectedEntry.Path);
        var operation = await EnterAsync(path, token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            await RequireSameBlockingProcessEntryAsync(path, expectedEntry, token).ConfigureAwait(false);
            // Keep the checked .lnk read-only for the duration of the confirmed operation.
            using var shortcut = await OpenMatchingShortcutAsync(expectedEntry, token).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                var known = _sessions.TryGetValue(path, out var session)
                    ? ApplicationProcessControl.ListKnown(session.LiveMemberIdentities()) : [];
                return ApplicationProcessControl.Stop(path, frozen, known,
                    pid => _log?.Write($"confirmed process exited pid={pid}"));
            }, token).ConfigureAwait(false);
        }
        finally { operation.Dispose(); }
    }

    private async Task RequireSameBlockingProcessEntryAsync(string path, RegisteredApplication expectedEntry, CancellationToken token)
    {
        var entry = (await _registry.ListAsync(token).ConfigureAwait(false))
            .FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !string.Equals(entry.ShortcutPath, expectedEntry.ShortcutPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(InvalidEntryMessage);
    }

    internal static async Task<FileStream?> OpenMatchingShortcutAsync(RegisteredApplication expectedEntry, CancellationToken token)
    {
        if (expectedEntry.ShortcutPath is not { } shortcut) return null;
        FileStream? file = null;
        try
        {
            file = File.OpenRead(shortcut);
            var target = await ApplicationEntryReader.ReadShortcutTargetAsync(shortcut, token).ConfigureAwait(false);
            if (!string.Equals(target, Path.GetFullPath(expectedEntry.Path), StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
                throw new InvalidOperationException(InvalidEntryMessage);
            return file;
        }
        catch (Exception ex)
        {
            file?.Dispose();
            if (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
                throw new InvalidOperationException(InvalidEntryMessage, ex);
            throw;
        }
    }

    private static bool HasApplicationProcesses(string path) => ApplicationProcessControl.List(path).Count != 0;

    // Cross-application work never shares a long-running lock. Entries serialize only their own work.
    private sealed class EntryGate { public readonly SemaphoreSlim Semaphore = new(1, 1); public int Users; }
    private sealed class EntryOperation(RegisteredApplicationManager owner, string key, EntryGate gate) : IDisposable
    {
        private RegisteredApplicationManager? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave(key, gate, acquired: true);
    }

    private async Task<EntryOperation> EnterAsync(string key, CancellationToken token)
    {
        EntryGate gate;
        lock (_admission)
        {
            EnsureReady();
            if (_maintenance) throw new InvalidOperationException("正在重载或关闭管理器，请稍后重试。");
            if (!_entryGates.TryGetValue(key, out gate!)) _entryGates[key] = gate = new EntryGate();
            gate.Users++;
            if (_activeOperations++ == 0)
            {
                _idleSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _idle = _idleSignal.Task;
            }
        }
        try { await gate.Semaphore.WaitAsync(token).ConfigureAwait(false); }
        catch { Leave(key, gate, acquired: false); throw; }
        return new EntryOperation(this, key, gate);
    }

    private void Leave(string key, EntryGate gate, bool acquired)
    {
        lock (_admission)
        {
            if (acquired) gate.Semaphore.Release();
            if (--gate.Users == 0) { _entryGates.Remove(key); gate.Semaphore.Dispose(); }
            if (--_activeOperations == 0) _idleSignal?.TrySetResult();
        }
    }

    private Task BeginMaintenance()
    {
        lock (_admission) { _maintenance = true; return _idle; }
    }
    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_loaded) throw new InvalidOperationException("请先加载本地应用注册文件。");
    }

    private async Task StopCoreAsync(string path)
    {
        if (!_sessions.TryGetValue(path, out var session)) return;
        await session.DisposeAsync().ConfigureAwait(false);
        _sessions.TryRemove(path, out _);
    }

    private Task CleanupRuntimeAsync() => Task.Run(() =>
    {
        try { RuntimeModuleStore.Cleanup(Path.Combine(Path.GetDirectoryName(_controllerPath)!, "ZdC.SocketHook64.dll")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _log?.Write($"runtime cleanup deferred type={ex.GetType().Name}"); }
    });

    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _log?.Write($"manager disposal begin owner={Environment.ProcessId}");
            await BeginMaintenance().ConfigureAwait(false);
            List<Exception>? failures = null;
            foreach (var path in _sessions.Keys.ToArray())
                try { await StopCoreAsync(path).ConfigureAwait(false); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
            if (failures is not null) throw new AggregateException("部分控制器退出失败。", failures);
            await CleanupRuntimeAsync().ConfigureAwait(false);
            _disposed = true;
            _log?.Write($"manager dispose owner={Environment.ProcessId}");
            if (_ownsLog && _log is not null) await _log.DisposeAsync().ConfigureAwait(false);
        }
        finally { lock (_admission) _maintenance = false; _operations.Release(); }
    }
}
