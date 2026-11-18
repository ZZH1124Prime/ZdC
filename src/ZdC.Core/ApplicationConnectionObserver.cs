using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZdC.Core;

public enum ApplicationConnectionStatus { Waiting, DirectObserved, DefaultRouting, Unavailable, DefaultObserved }

// Read-only, demand-driven sampling. No sockets, traffic, or persistent worker are created.
internal sealed class ApplicationConnectionObserver : IDisposable
{
    private readonly Dictionary<int, ProcessIdentity> _members = new();
    private readonly HashSet<(int Pid, uint Local, uint LocalPort, uint Remote, uint RemotePort)> _established = new();

    public bool MatchesConnection(int pid, uint local, uint localPort, uint remote, uint remotePort) =>
        OwnsProcess(pid) && _established.Contains((pid, local, localPort, remote, remotePort));
    private static uint HostPort(uint value) => ((value & 255) << 8) | ((value >> 8) & 255);
    private bool _identityUnavailable;
    private long _lastMembershipSample;
    private bool? _hasLiveMembers;
    private long _lastSample;
    private int _count;
    private bool _available, _defaultRouting;
    private DateTimeOffset? _observedAt;

    public long SampleTick => _lastSample;

    public bool OwnsProcess(int pid) => _members.TryGetValue(pid, out var identity) && identity.IsAlive == true;

    public void Invalidate() { _established.Clear(); _lastSample = 0; _count = 0; _available = false; _observedAt = null; }

    public void SetRoot(int pid)
    {
        var identity = ProcessIdentity.Open(pid);
        if (identity is null) _identityUnavailable = true;
        else _members[pid] = identity;
    }

    internal void SetKnownMember(int pid, long created)
    {
        if (_members.TryGetValue(pid, out var known) && known.Created == created) return;
        var identity = ProcessIdentity.Open(pid);
        if (identity is null) { _identityUnavailable = true; return; }
        if (identity.Created != created || identity.IsAlive != true) { identity.Dispose(); return; }
        if (_members.Remove(pid, out var old)) old.Dispose();
        _members[pid] = identity;
        _lastMembershipSample = 0;
    }

    // Retained handles pin process identities after exit. Descendants must have
    // been created during the parent's actual lifetime, never after PID reuse.
    public bool? HasLiveMembers(bool children)
    {
        if (_lastMembershipSample != 0 && Environment.TickCount64 - _lastMembershipSample < 3000)
            return _hasLiveMembers;
        _lastMembershipSample = Environment.TickCount64;
        var uncertain = _identityUnavailable;
        if (children)
        {
            var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == new IntPtr(-1)) return _hasLiveMembers = null;
            try
            {
                var parents = new Dictionary<int, int>();
                var item = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                if (!Process32FirstW(snapshot, ref item)) return _hasLiveMembers = null;
                do { parents[(int)item.ProcessId] = (int)item.ParentId; }
                while (Process32NextW(snapshot, ref item));
                if (Marshal.GetLastWin32Error() != 18) uncertain = true;
                bool added;
                do
                {
                    added = false;
                    foreach (var pair in parents)
                    {
                        if (_members.ContainsKey(pair.Key) || !_members.TryGetValue(pair.Value, out var parent)) continue;
                        if (_members.Count >= 256) { _identityUnavailable = uncertain = true; continue; }
                        var candidate = ProcessIdentity.Open(pair.Key);
                        if (candidate is null) { uncertain = true; continue; }
                        var exit = parent.ExitTime;
                        if (candidate.Created >= parent.Created && exit.HasValue &&
                            (exit.Value == 0 || candidate.Created <= exit.Value))
                        { _members.Add(pair.Key, candidate); added = true; }
                        else { if (!exit.HasValue) uncertain = true; candidate.Dispose(); }
                    }
                } while (added);
            }
            finally { CloseHandle(snapshot); }
        }
        var alive = false;
        foreach (var identity in _members.Values)
        {
            var state = identity.IsAlive;
            if (state == true) alive = true;
            else if (state is null) uncertain = true;
        }
        return _hasLiveMembers = alive ? true : uncertain ? null : false;
    }

    public void Dispose()
    {
        foreach (var identity in _members.Values) identity.Dispose();
        _members.Clear(); _established.Clear();
    }

    public (ApplicationConnectionStatus Status, int Count, DateTimeOffset? At) Read(string? source, bool children, bool routingEnabled = true)
    {
        if (Environment.TickCount64 - _lastSample < 3000 && _lastSample != 0)
            return Result();
        _lastSample = Environment.TickCount64;
        _count = 0; _available = false; _observedAt = null;
        _defaultRouting = !routingEnabled;
        var validSource = IPAddress.TryParse(source, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        if (routingEnabled && !validSource) { _established.Clear(); return Result(); }
        try
        {
            HasLiveMembers(children);
            int size = 0;
            var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
            if (result != 122 || size < 4 || size > 16 * 1024 * 1024) return Result();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0) != 0) return Result();
                var rows = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<TcpRow>();
                if (rows < 0 || rows > (size - 4) / rowSize) return Result();
                var expected = validSource ? BitConverter.ToUInt32(ip!.GetAddressBytes()) : 0;
                var established = new HashSet<(int Pid, uint Local, uint LocalPort, uint Remote, uint RemotePort)>();
                for (var i = 0; i < rows; i++)
                {
                    var row = Marshal.PtrToStructure<TcpRow>(buffer + 4 + i * rowSize);
                    if (row.State == 5 && (!routingEnabled || row.LocalAddress == expected) && OwnsProcess((int)row.ProcessId)
                        && row.RemoteAddress != expected && (row.RemoteAddress & 255) != 127
                        && (row.RemoteAddress & 255) != 0 && (row.RemoteAddress & 255) < 224
                        && (!routingEnabled || !((row.RemoteAddress & 255) == 198 && ((row.RemoteAddress >> 8) & 254) == 18)))
                    {
                        _count++;
                        established.Add(((int)row.ProcessId, row.LocalAddress, HostPort(row.LocalPort),
                            row.RemoteAddress, HostPort(row.RemotePort)));
                    }
                }
                _established.Clear();
                _established.UnionWith(established);
                _available = true;
                if (_count > 0) _observedAt = DateTimeOffset.UtcNow;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { _available = false; }
        return Result();
    }

    private (ApplicationConnectionStatus, int, DateTimeOffset?) Result() =>
        (!_available ? ApplicationConnectionStatus.Unavailable : _count > 0 ? (_defaultRouting ? ApplicationConnectionStatus.DefaultObserved : ApplicationConnectionStatus.DirectObserved) : ApplicationConnectionStatus.Waiting,
         _count, _observedAt);
    private sealed class ProcessIdentity(SafeProcessHandle handle, long created) : IDisposable
    {
        public long Created { get; } = created;
        public long? ExitTime => GetProcessTimes(handle, out _, out var exit, out _, out _) ? exit : null;
        public bool? IsAlive => ExitTime is long exit ? exit == 0 : null;
        public static ProcessIdentity? Open(int pid)
        {
            var handle = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION only
            if (!handle.IsInvalid && GetProcessTimes(handle, out var created, out _, out _, out _))
                return new(handle, created);
            handle.Dispose();
            return null;
        }
        public void Dispose() => handle.Dispose();
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId; public UIntPtr DefaultHeap; public uint ModuleId, Threads, ParentId;
        public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
