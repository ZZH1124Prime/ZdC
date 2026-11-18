using System.Security.Cryptography;
using System.Text;

namespace ZdC.Core;

/// <summary>Immutable runtime image pairs. File deployment does not detach or unload injected modules.</summary>
public static class RuntimeModuleStore
{
    private const string Image64 = "ZdC.SocketHook64.dll", Image32 = "ZdC.SocketHook32.dll";
    private const string MarkerName = ".zdc-runtime", LiveName = ".zdc-live";
    private const string Prefix = "ZdC runtime pair v2\n";
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZdC", "RuntimeModules-v1");

    public sealed class Lease : IDisposable
    {
        private FileStream[]? _pins;
        public string Path { get; }
        internal Lease(string path, FileStream[] pins) { Path = path; _pins = pins; }
        public void Dispose()
        {
            var pins = Interlocked.Exchange(ref _pins, null);
            if (pins is not null) foreach (var pin in pins) pin.Dispose();
        }
    }

    private sealed record Pair(byte[] X64, byte[] X86, string Hash64, string Hash32)
    {
        public string Hash => Digest(Encoding.ASCII.GetBytes(Hash64 + Hash32));
        public string Marker => Prefix + Hash64 + "\n" + Hash32;
    }
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static Pair ReadPair(string source)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var x64 = File.ReadAllBytes(Path.Combine(directory, Image64));
        var x86 = File.ReadAllBytes(Path.Combine(directory, Image32));
        return new(x64, x86, Digest(x64), Digest(x86));
    }

    public static Lease Prepare(string sourceDllPath)
    {
        var pair = ReadPair(sourceDllPath);
        using var gate = OpenGate();
        Enter(gate);
        try
        {
            Directory.CreateDirectory(Root);
            RequireOrdinary(Root);
            var directory = Path.Combine(Root, pair.Hash);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                RequireOrdinary(directory);
                using var marker = new FileStream(Path.Combine(directory, MarkerName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                marker.Write(Encoding.UTF8.GetBytes(pair.Marker));
                marker.Flush(true);
            }
            if (!OwnsPair(directory, pair.Hash, out var hashes) || hashes[0] != pair.Hash64 || hashes[1] != pair.Hash32)
                throw new IOException("Runtime module directory contains unrecognized files.");
            PublishImage(directory, Image64, pair.X64, pair.Hash64);
            PublishImage(directory, Image32, pair.X86, pair.Hash32);
            var live = Path.Combine(directory, LiveName);
            if (!File.Exists(live)) { using var created = new FileStream(live, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            RequireOrdinary(live);
            if (new FileInfo(live).Length != 0) throw new IOException("Runtime occupancy marker was modified.");
            var pins = new List<FileStream>();
            try
            {
                // Both host and loaded modules pin this marker, protecting the whole pair across child creation.
                pins.Add(new FileStream(live, FileMode.Open, FileAccess.Read, FileShare.Read));
                foreach (var name in new[] { Image64, Image32 })
                    pins.Add(new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.Read));
                CleanCore(pair.Hash);
                return new Lease(Path.Combine(directory, Image64), pins.ToArray());
            }
            catch { foreach (var pin in pins) pin.Dispose(); throw; }
        }
        finally { gate.ReleaseMutex(); }
    }

    private static void PublishImage(string directory, string name, byte[] bytes, string hash)
    {
        var image = Path.Combine(directory, name);
        if (File.Exists(image))
        {
            RequireOrdinary(image);
            using var input = File.OpenRead(image);
            if (Convert.ToHexStringLower(SHA256.HashData(input)) != hash)
                throw new IOException("Runtime module content does not match its identity.");
            return;
        }
        // Only this exact staging name inside a validated, owned directory is used for incomplete writes.
        var staging = image + ".part";
        if (File.Exists(staging))
        {
            RequireOrdinary(staging);
            if (new FileInfo(staging).Length > bytes.Length) throw new IOException("Runtime staging file was modified.");
            var partial = File.ReadAllBytes(staging);
            if (!partial.AsSpan().SequenceEqual(bytes.AsSpan(0, partial.Length)))
                throw new IOException("Runtime staging file was modified.");
        }
        var writing = false;
        try
        {
            using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            { writing = true; output.Write(bytes); output.Flush(true); }
            File.Move(staging, image, false);
        }
        finally { if (writing && File.Exists(staging)) File.Delete(staging); }
    }

    public static void Cleanup(string? currentSourceDll = null)
    {
        var keep = currentSourceDll is null ? null : ReadPair(currentSourceDll).Hash;
        using var gate = OpenGate();
        Enter(gate);
        try { CleanCore(keep); }
        finally { gate.ReleaseMutex(); }
    }
    private static Mutex OpenGate() => new(false, @"Local\ZdC.RuntimeModules." + Digest(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())).ToUpperInvariant());
    private static void Enter(Mutex gate)
    {
        try { if (!gate.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("Runtime module store is busy."); }
        catch (AbandonedMutexException) { }
    }
    private static void RequireOrdinary(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Runtime module store does not follow filesystem links.");
    }
    private static bool ValidHash(string text) => text.Length == 64 && text.All(char.IsAsciiHexDigit);
    private static bool OwnsPair(string directory, string hash, out string[] hashes)
    {
        hashes = [];
        RequireOrdinary(directory);
        var marker = Path.Combine(directory, MarkerName);
        if (!File.Exists(marker)) return false;
        RequireOrdinary(marker);
        if (new FileInfo(marker).Length > 256) return false;
        var text = File.ReadAllText(marker);
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        hashes = text[Prefix.Length..].Split('\n');
        return hashes.Length == 2 && hashes.All(ValidHash) && Digest(Encoding.ASCII.GetBytes(hashes[0] + hashes[1])) == hash &&
            Directory.EnumerateFileSystemEntries(directory).All(p => !Directory.Exists(p) &&
                Path.GetFileName(p) is Image64 or Image32 or MarkerName or LiveName or "ZdC.SocketHook64.dll.part" or "ZdC.SocketHook32.dll.part");
    }
    private static void CleanCore(string? keep)
    {
        if (!Directory.Exists(Root)) return;
        RequireOrdinary(Root);
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            var hash = Path.GetFileName(directory);
            if (hash == keep || !ValidHash(hash)) continue;
            try
            {
                if (!OwnsPair(directory, hash, out var hashes)) { CleanLegacy(directory, hash); continue; }
                var live = Path.Combine(directory, LiveName);
                if (!File.Exists(live)) continue; // Incomplete preparation is repaired by its next Prepare.
                RequireOrdinary(live);
                using var occupancy = new FileStream(live, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                if (occupancy.Length != 0) continue;
                var locks = new List<FileStream>();
                try
                {
                    var names = new[] { Image64, Image32 };
                    var valid = true;
                    for (var i = 0; i < names.Length; i++)
                    {
                        var file = Path.Combine(directory, names[i]);
                        if (!File.Exists(file)) continue;
                        RequireOrdinary(file);
                        var handle = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                        locks.Add(handle);
                        if (Convert.ToHexStringLower(SHA256.HashData(handle)) != hashes[i]) { valid = false; break; }
                    }
                    if (!valid) continue;
                    // An unexpected staging file is retained; cleanup never removes arbitrary contents.
                    if (Directory.EnumerateFiles(directory, "*.part").Any()) continue;
                    foreach (var name in names) File.Delete(Path.Combine(directory, name));
                }
                finally { foreach (var handle in locks) handle.Dispose(); }
                File.Delete(live);
                File.Delete(Path.Combine(directory, MarkerName));
                Directory.Delete(directory, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    // Retire only the exact single-image format previously produced here, without following links or changing live images.
    private static void CleanLegacy(string directory, string hash)
    {
        var marker = Path.Combine(directory, MarkerName);
        if (!File.Exists(marker)) return;
        RequireOrdinary(marker);
        if (new FileInfo(marker).Length != 86 || File.ReadAllText(marker) != "ZdC runtime module v1\n" + hash) return;
        if (Directory.EnumerateFileSystemEntries(directory).Any(p => Directory.Exists(p) || Path.GetFileName(p) is not (MarkerName or "ZdC.SocketHook.dll"))) return;
        var image = Path.Combine(directory, "ZdC.SocketHook.dll");
        if (File.Exists(image))
        {
            RequireOrdinary(image);
            using var handle = new FileStream(image, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
            if (Convert.ToHexStringLower(SHA256.HashData(handle)) != hash) return;
            File.Delete(image);
        }
        File.Delete(marker);
        Directory.Delete(directory, false);
    }
}
