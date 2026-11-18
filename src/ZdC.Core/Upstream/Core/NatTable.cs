// Adapted from TrafikRuler, copyright 2026 kulich Software, GPL-3.0-or-later.
// ZdC adds complete tuples, bounded reserved-port leases and endpoint-independent UDP NAT.
using System.Collections.Concurrent;

namespace TrafikRuler.Core;

internal readonly record struct FlowKey(byte Protocol, ushort LocalPort, UInt128 Remote, ushort RemotePort, UInt128 Local = default);
internal readonly record struct FragmentKey(byte Protocol, UInt128 Src, UInt128 Dst, uint Id);
internal enum FlowAction { Pass, Nat, Block }
internal sealed class FlowEntry
{
    public required FlowKey Key { get; init; }
    public required FlowAction Action { get; set; }
    public int Generation;
    public ActiveRule? Rule;
    public bool IsV6 { get; init; }
    public UInt128 OrigLocal { get; init; }
    public uint OrigIfIdx { get; init; }
    public uint OrigSubIfIdx { get; init; }
    public UInt128 NatLocal { get; init; }
    public ushort NatPort { get; set; }
    public uint TargetIfIdx { get; init; }
    public long LastSeen;
    public long RecheckAt;
    public int Closing;
    public int Counted;
}

internal sealed class NatTable : IDisposable
{
    public static readonly TimeSpan TcpIdleTimeout = TimeSpan.FromHours(2);
    public static readonly TimeSpan TcpClosingTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan UdpIdleTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan PassIdleTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan BlockIdleTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FragmentTimeout = TimeSpan.FromSeconds(10);
    private readonly record struct EndpointKey(byte Protocol, UInt128 OriginalAddress, ushort OriginalPort, UInt128 NatAddress);
    private readonly record struct WireKey(byte Protocol, UInt128 Address, ushort Port);
    private sealed class Allocation(INatPortLease lease)
    {
        public INatPortLease Lease { get; } = lease;
        public HashSet<FlowEntry> Entries { get; } = [];
    }
    private readonly ConcurrentDictionary<FlowKey, FlowEntry> _flows = new();
    private readonly ConcurrentDictionary<FlowKey, FlowEntry> _reverse = new();
    private readonly ConcurrentDictionary<FragmentKey, (FlowEntry Entry, long Tick)> _fragments = new();
    private readonly Dictionary<EndpointKey, Allocation> _allocations = [];
    private readonly Dictionary<WireKey, Allocation> _wireEndpoints = [];
    private readonly object _writeLock = new();
    private readonly INatPortAllocator _allocator;
    private readonly int _maxEndpoints;
    private readonly int _maxFlows;
    public NatTable(INatPortAllocator? allocator = null, int maxEndpoints = 2048, int maxFlows = 65536)
    {
        _allocator = allocator ?? new SocketNatPortAllocator();
        _maxEndpoints = maxEndpoints > 0 ? maxEndpoints : throw new ArgumentOutOfRangeException(nameof(maxEndpoints));
        _maxFlows = maxFlows > 0 ? maxFlows : throw new ArgumentOutOfRangeException(nameof(maxFlows));
    }
    private static EndpointKey Endpoint(FlowEntry e) => new(e.Key.Protocol, e.Key.Local, e.Key.LocalPort, e.NatLocal);
    private static FlowKey ReverseKey(FlowEntry e) => new(e.Key.Protocol, e.NatPort, e.Key.Remote, e.Key.RemotePort, e.NatLocal);
    public int Count => _flows.Count;
    public int ReservedEndpointCount { get { lock (_writeLock) return _allocations.Count; } }
    public bool TryGet(in FlowKey key, out FlowEntry entry) => _flows.TryGetValue(key, out entry!);
    public bool TryGetReverse(in FlowKey key, out FlowEntry entry) => _reverse.TryGetValue(key, out entry!);
    public FlowEntry? FindUdpEndpoint(ushort port, UInt128 address)
    {
        lock (_writeLock)
            return _wireEndpoints.TryGetValue(new(Packet.ProtoUdp, address, port), out var a) ? a.Entries.FirstOrDefault() : null;
    }
    public FlowEntry Put(FlowEntry entry)
    {
        lock (_writeLock)
        {
            if (_flows.TryGetValue(entry.Key, out var current))
            {
                if (current.Generation == entry.Generation && current.RecheckAt == 0) return current;
                RemoveLocked(current);
            }
            // Uncached entries preserve the selected flow's decision without growing memory.
            if (_flows.Count >= _maxFlows)
            {
                if (entry.Action == FlowAction.Nat) entry.Action = FlowAction.Block;
                return entry;
            }
            if (entry.Action == FlowAction.Nat)
            {
                var key = Endpoint(entry);
                if (!_allocations.TryGetValue(key, out var allocation))
                {
                    if (_allocations.Count >= _maxEndpoints) entry.Action = FlowAction.Block;
                    else
                    {
                        INatPortLease? lease = null;
                        try
                        {
                            lease = _allocator.Reserve(entry.Key.Protocol, entry.NatLocal);
                            var wire = new WireKey(entry.Key.Protocol, entry.NatLocal, lease.Port);
                            if (lease.Port == 0 || _wireEndpoints.ContainsKey(wire))
                            { lease.Dispose(); entry.Action = FlowAction.Block; }
                            else
                            {
                                allocation = new(lease);
                                _allocations.Add(key, allocation);
                                _wireEndpoints.Add(wire, allocation);
                            }
                        }
                        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
                        { lease?.Dispose(); entry.Action = FlowAction.Block; }
                    }
                }
                if (entry.Action == FlowAction.Nat && allocation != null)
                {
                    entry.NatPort = allocation.Lease.Port;
                    allocation.Entries.Add(entry);
                    _reverse[ReverseKey(entry)] = entry;
                    if (entry.Rule != null && Interlocked.Exchange(ref entry.Counted, 1) == 0)
                        Interlocked.Increment(ref entry.Rule.Stats.ActiveFlows);
                }
            }
            _flows[entry.Key] = entry;
            return entry;
        }
    }
    public bool Remove(FlowEntry entry) { lock (_writeLock) return RemoveLocked(entry); }
    private bool RemoveLocked(FlowEntry entry)
    {
        if (!_flows.TryRemove(new KeyValuePair<FlowKey, FlowEntry>(entry.Key, entry))) return false;
        if (entry.Action != FlowAction.Nat) return true;
        _reverse.TryRemove(new KeyValuePair<FlowKey, FlowEntry>(ReverseKey(entry), entry));
        if (entry.Rule != null && Interlocked.Exchange(ref entry.Counted, 0) == 1) Interlocked.Decrement(ref entry.Rule.Stats.ActiveFlows);
        var key = Endpoint(entry);
        if (_allocations.TryGetValue(key, out var allocation))
        {
            allocation.Entries.Remove(entry);
            if (allocation.Entries.Count == 0)
            {
                _allocations.Remove(key);
                _wireEndpoints.Remove(new(entry.Key.Protocol, entry.NatLocal, allocation.Lease.Port));
                allocation.Lease.Dispose();
            }
        }
        return true;
    }
    public void RememberFragment(in FragmentKey key, FlowEntry entry)
    {
        if (_fragments.Count < _maxFlows) _fragments[key] = (entry, Environment.TickCount64);
    }
    public FlowEntry? FindFragment(in FragmentKey key) => _fragments.TryGetValue(key, out var v) ? v.Entry : null;
    public IEnumerable<FlowEntry> Entries => _flows.Values;
    public int Sweep(long nowTick)
    {
        var removed = 0;
        foreach (var e in _flows.Values)
        {
            var limit = e.Action switch
            {
                FlowAction.Pass => PassIdleTimeout,
                FlowAction.Block => BlockIdleTimeout,
                _ when e.Key.Protocol == Packet.ProtoUdp => UdpIdleTimeout,
                _ when e.Closing != 0 => TcpClosingTimeout,
                _ => TcpIdleTimeout
            };
            if (nowTick - Volatile.Read(ref e.LastSeen) > limit.TotalMilliseconds && Remove(e)) removed++;
        }
        foreach (var kv in _fragments)
            if (nowTick - kv.Value.Tick > FragmentTimeout.TotalMilliseconds) _fragments.TryRemove(kv);
        return removed;
    }
    public void Clear()
    {
        lock (_writeLock)
        {
            foreach (var e in _flows.Values) RemoveLocked(e);
            _fragments.Clear();
        }
    }
    public void Dispose() => Clear();
}
