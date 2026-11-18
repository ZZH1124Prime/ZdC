using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZdC.Core;

// A same-user lease, retained by injected modules while their processes live.
// No service, socket server, polling thread, or DLL unload is required.
internal sealed unsafe class ApplicationControlLease : IDisposable
{
    private const int Size = 64 + 256 * 16;
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _mutex;
    private byte* _pointer;
    public string Name { get; }
    public ApplicationControlLease(string? name = null)
    {
        Name = name ?? @"Local\ZdC.Control1." + Guid.NewGuid().ToString("N");
        _mutex = new Mutex(false, Name + ".claim");
        try
        {
            _map = name is null ? MemoryMappedFile.CreateNew(Name, Size) : MemoryMappedFile.OpenExisting(Name);
            _view = _map.CreateViewAccessor(0, Size);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
            if (name is null) *(int*)_pointer = 1;
            else if (*(int*)_pointer != 1) throw new IOException("Unsupported control lease.");
        }
        catch
        {
            if (_pointer != null) { _view!.SafeMemoryMappedViewHandle.ReleasePointer(); _pointer = null; }
            _view?.Dispose(); _map?.Dispose(); _mutex.Dispose(); throw;
        }
    }
    public bool Claim(bool enabled)
    {
        var held = false;
        try
        {
            try { held = _mutex.WaitOne(1000); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return false;
            var owner = *(int*)(_pointer + 8);
            var created = *(long*)(_pointer + 16);
            if (owner != 0 && Alive(owner, created)) return false;
            WriteOwner(Environment.ProcessId, CurrentCreated(), enabled);
            return true;
        }
        finally { if (held) _mutex.ReleaseMutex(); }
    }
    public void SetEnabled(bool enabled)
    {
        var held = false;
        try
        {
            try { held = _mutex.WaitOne(1000); }
            catch (AbandonedMutexException) { held = true; }
            if (!held || *(int*)(_pointer + 8) != Environment.ProcessId ||
                *(long*)(_pointer + 16) != CurrentCreated()) throw new IOException("Control lease is not owned.");
            WriteOwner(Environment.ProcessId, CurrentCreated(), enabled);
        }
        finally { if (held) _mutex.ReleaseMutex(); }
    }
    public void Release()
    {
        if (_pointer == null) return;
        var held = false;
        try
        {
            try { held = _mutex.WaitOne(1000); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Control lease release timed out.");
            if (*(int*)(_pointer + 8) == Environment.ProcessId &&
                *(long*)(_pointer + 16) == CurrentCreated()) WriteOwner(0, 0, false);
        }
        finally { if (held) _mutex.ReleaseMutex(); }
    }
    private void WriteOwner(int pid, long created, bool enabled)
    {
        Volatile.Write(ref *(int*)(_pointer + 4), *(int*)(_pointer + 4) | 1);
        Thread.MemoryBarrier();
        *(int*)(_pointer + 8) = pid;
        *(int*)(_pointer + 12) = enabled ? 1 : 0;
        *(long*)(_pointer + 16) = created;
        Interlocked.Increment(ref *(int*)(_pointer + 4));
    }
    public IReadOnlyList<(int Pid, long Created)> LiveMembers()
    {
        var result = new List<(int, long)>();
        for (var i = 0; i < 256; i++)
        {
            var p = _pointer + 64 + i * 16;
            var pid = Volatile.Read(ref *(int*)p);
            var created = *(long*)(p + 8);
            Thread.MemoryBarrier();
            if (pid > 0 && pid == Volatile.Read(ref *(int*)p) && Alive(pid, created, false)) result.Add((pid, created));
        }
        return result;
    }
    public bool WasRegistered(int processId)
    {
        // A short-lived launcher can finish before the first UI-side poll.
        // Its entry in this freshly created lease still proves DLL initialization.
        for (var i = 0; i < 256; i++)
        {
            var member = _pointer + 64 + i * 16;
            var pid = Volatile.Read(ref *(int*)member);
            var created = *(long*)(member + 8);
            Thread.MemoryBarrier();
            if (pid == processId && created > 0 && pid == Volatile.Read(ref *(int*)member)) return true;
        }
        return false;
    }
    private static long CurrentCreated() { using var p = Process.GetCurrentProcess(); return p.StartTime.ToUniversalTime().ToFileTimeUtc(); }
    internal static bool Alive(int pid, long created, bool uncertainAlive = true)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited && p.StartTime.ToUniversalTime().ToFileTimeUtc() == created; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return uncertainAlive; } // An uncertain owner must not be stolen.
    }
    public void Dispose()
    {
        if (_pointer == null) return;
        _view.SafeMemoryMappedViewHandle.ReleasePointer(); _pointer = null;
        _view.Dispose(); _map.Dispose(); _mutex.Dispose();
    }

    internal sealed record Discovery(string Application, string ControlName, string TimingName, string Source, int RootPid);
    internal static string DiscoveryPath(string application)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(application).ToUpperInvariant())));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZdC", "sessions", key + ".json");
    }
    internal static void Save(Discovery value)
    {
        var path = DiscoveryPath(value.Application);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path, true);
    }
    private static bool ValidName(string? name, string prefix) =>
        name is not null && name.StartsWith(prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(name[prefix.Length..], "N", out _);
    internal static Discovery? Find(string application)
    {
        var path = DiscoveryPath(application);
        if (!File.Exists(path) || new FileInfo(path).Length > 8192) return null;
        var item = JsonSerializer.Deserialize<Discovery>(File.ReadAllText(path));
        return item is not null && string.Equals(item.Application, Path.GetFullPath(application), StringComparison.OrdinalIgnoreCase)
            && ValidName(item.ControlName, @"Local\ZdC.Control1.")
            && ValidName(item.TimingName, @"Local\ZdC.Timing3.") ? item : null;
    }
}
