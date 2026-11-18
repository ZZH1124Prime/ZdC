using System.IO.MemoryMappedFiles;
using System.Net;

namespace ZdC.Core;

// Bounded tuple-keyed TCP RTT samples; readers accept only live, recent connections.
// Values are kernel RttUs estimates, not synchronous connect durations.
internal sealed unsafe class ConnectionTimingChannel : IDisposable
{
    private const int Slots = 64, RecordSize = 88;
    private readonly record struct Key(int Pid, uint Local, uint LocalPort, uint Remote, uint RemotePort);
    private readonly record struct Counter(Key Key, long Tick, ulong Received, ulong Sent, ulong Age);
    private readonly Counter[] _counters = new Counter[Slots];
    private int _counterCursor;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;
    private long _since = Environment.TickCount64;
    public string Name { get; }

    public ConnectionTimingChannel(string? name = null)
    {
        Name = name ?? @"Local\ZdC.Timing3." + Guid.NewGuid().ToString("N");
        _mapping = name is null ? MemoryMappedFile.CreateNew(Name, Slots * RecordSize, MemoryMappedFileAccess.ReadWrite) : MemoryMappedFile.OpenExisting(Name);
        try
        {
            _view = _mapping.CreateViewAccessor(0, Slots * RecordSize, MemoryMappedFileAccess.ReadWrite);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
        }
        catch { _mapping.Dispose(); throw; }
    }

    public void Invalidate()
    {
        _since = Environment.TickCount64;
        Array.Clear(_counters);
        _counterCursor = 0;
    }

    public double? Read(string? source, Func<int, uint, uint, uint, uint, bool> matchesConnection, bool routingEnabled = true, BoundedLocalLog? log = null)
    {
        var now = Environment.TickCount64;
        uint expected = 0;
        if (routingEnabled)
        {
            if (!IPAddress.TryParse(source, out var ip) || ip.GetAddressBytes().Length != 4) return null;
            expected = BitConverter.ToUInt32(ip.GetAddressBytes());
        }
        Span<double> values = stackalloc double[Slots];
        Span<Key> keys = stackalloc Key[Slots];
        Span<long> ticks = stackalloc long[Slots];
        var count = 0;
        for (var index = 0; index < Slots; index++)
        {
            var record = _pointer + index * RecordSize;
            var sequence = Volatile.Read(ref *(int*)record);
            if (sequence == 0 || (sequence & 1) != 0) continue;
            var pid = *(int*)(record + 4);
            var milliseconds = *(double*)(record + 8);
            var started = *(long*)(record + 16);
            var finished = *(long*)(record + 24);
            var interfaceIndex = *(uint*)(record + 32);
            var address = *(uint*)(record + 36);
            var localPort = *(uint*)(record + 40);
            var remote = *(uint*)(record + 44);
            var remotePort = *(uint*)(record + 48);
            var received = *(ulong*)(record + 56);
            var sent = *(ulong*)(record + 64);
            var retransmitted = *(uint*)(record + 72);
            var age = *(ulong*)(record + 80);
            Thread.MemoryBarrier();
            if (sequence != Volatile.Read(ref *(int*)record)) continue;
            if (started < _since || finished < started || finished > now || now - finished > 10000
                || !double.IsFinite(milliseconds) || milliseconds <= 0 || milliseconds > 120000
                || (routingEnabled ? interfaceIndex == 0 || address != expected : interfaceIndex != 0)
                || localPort is 0 or > 65535 || remotePort is 0 or > 65535
                || !matchesConnection(pid, address, localPort, remote, remotePort)) continue;
            var key = new Key(pid, address, localPort, remote, remotePort);
            if (log is not null) LogCounter(log, key, finished, received, sent, retransmitted, interfaceIndex, routingEnabled, age);
            var slot = 0;
            while (slot < count && keys[slot] != key) slot++;
            if (slot == count) { keys[slot] = key; ticks[slot] = finished; values[slot] = milliseconds; count++; }
            else if (finished >= ticks[slot]) { ticks[slot] = finished; values[slot] = milliseconds; }
        }
        if (count == 0) return null;
        var valid = values[..count];
        valid.Sort();
        return count % 2 != 0 ? valid[count / 2] : (valid[count / 2 - 1] + valid[count / 2]) / 2;
    }

    private void LogCounter(BoundedLocalLog log, Key key, long tick, ulong received, ulong sent,
                            uint retransmitted, uint interfaceIndex, bool enabled, ulong age)
    {
        var slot = Array.FindIndex(_counters, item => item.Tick != 0 && item.Key == key);
        var previous = slot >= 0 ? _counters[slot] : default;
        if (previous.Tick >= tick) return;
        if (slot < 0) { slot = _counterCursor; _counterCursor = (_counterCursor + 1) % Slots; }
        _counters[slot] = new Counter(key, tick, received, sent, age);
        var window = tick - previous.Tick;
        var rate = previous.Tick != 0 && received >= previous.Received && sent >= previous.Sent && window <= 10000 && age >= previous.Age && Math.Abs((double)(age - previous.Age) - window) < 1000
            ? FormattableString.Invariant($"window_ms={window} received_delta_bytes={received - previous.Received} sent_delta_bytes={sent - previous.Sent} down_MiB_s={(received - previous.Received) * 1000.0 / window / 1048576:0.000}")
            : "window_ms=none";
        log.Write($"tcp_sample pid={key.Pid} local={new IPAddress(key.Local)}:{key.LocalPort} remote={new IPAddress(key.Remote)}:{key.RemotePort} configured_if={interfaceIndex} enabled={enabled} connection_age_ms={age} received_bytes={received} sent_bytes={sent} retransmitted_bytes={retransmitted} {rate}");
    }

    public void Dispose()
    {
        if (_pointer == null) return;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _pointer = null;
        _view.Dispose(); _mapping.Dispose();
    }
}
