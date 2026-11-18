using System.Collections.Concurrent;
using System.Diagnostics;
using TrafikRuler.Native;
using TrafikRuler.Localization;

namespace TrafikRuler.Core;

internal interface IProcessResolver
{
    uint? ResolvePid(in PacketInfo packet);
    string? GetImagePath(uint pid);
}

// ZdC: endpoint events invalidate a complete IP Helper tuple snapshot. Event addresses
// are not guessed from the native union layout. Full tuples, address family and process
// creation times are used before associating a packet with a process.
internal sealed class ProcessResolver : IProcessResolver, IDisposable
{
    internal readonly record struct SocketOwner(bool IsV6, byte Protocol, UInt128 LocalAddress,
        ushort LocalPort, UInt128 RemoteAddress, ushort RemotePort, uint Pid, long Created);
    private readonly record struct EventOwner(uint Pid, long Created);
    private readonly ConcurrentDictionary<ulong, EventOwner> _endpoints = new();
    private readonly ConcurrentDictionary<uint, (long Created, string? Path)> _images = new();
    private readonly object _snapshotLock = new();
    private SocketOwner[] _owners = [];
    private long _lastSnapshotTick = long.MinValue;
    private long _eventVersion;
    private long _snapshotVersion = -1;
    private WinDivertHandle? _socketHandle;
    private Thread? _thread;
    public event Action<string>? Log;

    public void Start()
    {
        if (_socketHandle != null) return;
        var handle = WinDivertHandle.Open("true", WinDivertLayer.Socket, 0,
            WinDivertFlags.Sniff | WinDivertFlags.RecvOnly);
        _socketHandle = handle;
        _thread = new Thread(() => SocketLoop(handle)) { IsBackground = true, Name = "ZdC socket events" };
        _thread.Start();
    }

    public void Stop()
    {
        var h = Interlocked.Exchange(ref _socketHandle, null);
        h?.Dispose();
        if (_thread != Thread.CurrentThread) _thread?.Join(2000);
        _thread = null;
        _endpoints.Clear();
        lock (_snapshotLock) { _owners = []; _lastSnapshotTick = long.MinValue; _snapshotVersion = -1; }
    }
    public void Dispose() => Stop();

    private void SocketLoop(WinDivertHandle handle)
    {
        try
        {
            var buffer = new byte[1];
            while (true)
            {
                int n = handle.Recv(buffer, out var address);
                if (n < 0) return;
                if (address.Protocol is not (Packet.ProtoTcp or Packet.ProtoUdp)) continue;
                switch (address.Event)
                {
                    case WinDivertEvent.SocketBind:
                    case WinDivertEvent.SocketConnect:
                    case WinDivertEvent.SocketListen:
                    case WinDivertEvent.SocketAccept:
                        if (ProcessNative.TryQuery(address.ProcessId, false, out var created, out _))
                            _endpoints[address.EndpointId] = new(address.ProcessId, created);
                        break;
                    case WinDivertEvent.SocketClose:
                        // Endpoint ID, not a shared local port. Conditional removal prevents
                        // a stale close from removing a concurrently replaced record.
                        if (_endpoints.TryGetValue(address.EndpointId, out var owner) && owner.Pid == address.ProcessId)
                            _endpoints.TryRemove(new KeyValuePair<ulong, EventOwner>(address.EndpointId, owner));
                        break;
                }
                Interlocked.Increment(ref _eventVersion);
            }
        }
        catch (Exception ex) { Log?.Invoke(Loc.T("LogSocketEventsError", ex.Message)); }
        finally
        {
            Interlocked.CompareExchange(ref _socketHandle, null, handle);
            handle.Dispose();
            Interlocked.Increment(ref _eventVersion);
        }
    }

    public uint? ResolvePid(in PacketInfo packet)
    {
        SocketOwner[] owners;
        lock (_snapshotLock)
        {
            long now = Environment.TickCount64;
            long version = Volatile.Read(ref _eventVersion);
            if (_lastSnapshotTick == long.MinValue || now - _lastSnapshotTick >= 20 || version != _snapshotVersion)
            {
                _owners = ReadSnapshot();
                _lastSnapshotTick = now;
                _snapshotVersion = version;
            }
            owners = _owners;
        }
        var selected = SelectOwner(packet, owners);
        if (selected is not { } candidate) return null;
        return ProcessNative.TryQuery(candidate.Pid, false, out long created, out _) && created == candidate.Created
            ? candidate.Pid : null;
    }

    // Pure selection method for offline verification. Ambiguous UDP wildcard binds
    // produce no owner instead of letting the last enumerated PID win.
    internal static SocketOwner? SelectOwner(in PacketInfo packet, IEnumerable<SocketOwner> owners)
    {
        SocketOwner? result = null;
        foreach (var owner in owners)
        {
            if (owner.IsV6 != packet.IsV6 || owner.Protocol != packet.Protocol || owner.LocalPort != packet.SrcPort) continue;
            bool wildcard = owner.LocalAddress == (packet.IsV6 ? UInt128.Zero : Addr.FromIPAddress(System.Net.IPAddress.Any));
            if (packet.IsTcp)
            {
                if (owner.LocalAddress != packet.Src || owner.RemoteAddress != packet.Dst || owner.RemotePort != packet.DstPort) continue;
            }
            else if (owner.LocalAddress != packet.Src && !wildcard) continue;
            if (result is { } prior && (prior.Pid != owner.Pid || prior.Created != owner.Created)) return null;
            result = owner;
        }
        return result;
    }

    private SocketOwner[] ReadSnapshot()
    {
        try
        {
            var owners = new List<SocketOwner>();
            var createdTimes = new Dictionary<uint, long?>();
            long? Created(uint pid)
            {
                if (!createdTimes.TryGetValue(pid, out var value))
                    createdTimes[pid] = value = ProcessNative.TryQuery(pid, false, out var time, out _) ? time : null;
                return value;
            }
            foreach (bool v6 in new[] { false, true })
            {
                foreach (var c in IpHelper.GetTcpConnections(v6))
                    if (c.LocalPort != 0 && Created(c.Pid) is { } time)
                        owners.Add(new(v6, Packet.ProtoTcp, Addr.FromIPAddress(c.LocalAddress), c.LocalPort,
                            Addr.FromIPAddress(c.RemoteAddress), c.RemotePort, c.Pid, time));
                foreach (var u in IpHelper.GetUdpEndpoints(v6))
                    if (u.LocalPort != 0 && Created(u.Pid) is { } time)
                        owners.Add(new(v6, Packet.ProtoUdp, Addr.FromIPAddress(u.LocalAddress), u.LocalPort, 0, 0, u.Pid, time));
            }
            return owners.ToArray();
        }
        catch (Exception ex) { Log?.Invoke(Loc.T("LogConnTablesError", ex.Message)); return []; }
    }
    public string? GetImagePath(uint pid)
    {
        if (pid is 0 or 4)
            return null;
        bool cached = _images.TryGetValue(pid, out var entry);
        if (!ProcessNative.TryQuery(pid, wantPath: !cached, out long created, out string? path))
        {
            _images.TryRemove(pid, out _);
            return null;
        }
        if (cached && entry.Created == created)
            return entry.Path;
        if (cached)
            ProcessNative.TryQuery(pid, wantPath: true, out created, out path);
        _images[pid] = (created, path);
        return path;
    }

    /// <summary>PID всех процессов, которые подпадают под правило (для UI и сброса соединений).</summary>
    public List<uint> FindProcesses(Func<string?, bool> predicate)
    {
        var result = new List<uint>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                uint pid = (uint)p.Id;
                if (predicate(GetImagePath(pid)))
                    result.Add(pid);
            }
        }
        return result;
    }

    /// <summary>Удаляет из кэша процессы, которые уже завершились.</summary>
    public void Prune()
    {
        foreach (var pid in _images.Keys)
            if (!ProcessNative.TryQuery(pid, false, out _, out _))
                _images.TryRemove(pid, out _);
    }
}
