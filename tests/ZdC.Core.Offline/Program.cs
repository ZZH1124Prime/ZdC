using System.Net;
using TrafikRuler.Core;
using ZdC.Core;


var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
UInt128 Ip(string s) => Addr.FromIPAddress(IPAddress.Parse(s));
var a=Ip("10.0.0.2"); var b=Ip("10.0.0.3"); var n=Ip("192.168.1.5"); var remote=Ip("8.8.8.8");
var bytes=new byte[128];
var size=Packet.BuildTcp(bytes,false,a,remote,50000,443,123,0,2);
Check(Packet.TryParse(bytes.AsSpan(0,size),out var packet),"parse TCP SYN");
Check(Packet.ChecksumsValid(bytes.AsSpan(0,size),packet),"initial checksums");
Packet.RewriteAddressFull(bytes.AsSpan(0,size),ref packet,true,n);
Check(packet.Src==n && Packet.ChecksumsValid(bytes.AsSpan(0,size),packet),"source NAT checksum");
Packet.RewriteAddressIncremental(bytes.AsSpan(0,size),ref packet,true,a);
Check(packet.Src==a && Packet.ChecksumsValid(bytes.AsSpan(0,size),packet),"reverse address checksum");
var table=new NatTable(new OfflinePortAllocator());
FlowEntry Entry(UInt128 local) => new() {Key=new(Packet.ProtoTcp,50000,remote,443,local),Action=FlowAction.Nat,OrigLocal=local,NatLocal=n,LastSeen=Environment.TickCount64};
var first=table.Put(Entry(a)); var second=table.Put(Entry(b));
Check(first.Action==FlowAction.Nat && second.Action==FlowAction.Nat && first.NatPort != second.NatPort,"NAT ports allocated independently");
Check(table.TryGetReverse(new(Packet.ProtoTcp,first.NatPort,remote,443,n),out var reverse)&&ReferenceEquals(first,reverse),"reverse map preserved");
table.Remove(first);
Check(!table.TryGetReverse(new(Packet.ProtoTcp,first.NatPort,remote,443,n),out _),"reverse map removed");
var owners=new[] {new ProcessResolver.SocketOwner(false,Packet.ProtoTcp,a,50000,remote,443,100,1),new ProcessResolver.SocketOwner(false,Packet.ProtoTcp,b,50000,remote,443,200,2)};
packet.Src=a;
Check(ProcessResolver.SelectOwner(packet,owners)?.Pid==100,"full TCP tuple owner");
packet.Protocol=Packet.ProtoUdp;
var udp=new[]{new ProcessResolver.SocketOwner(false,Packet.ProtoUdp,a,50000,0,0,100,1),new ProcessResolver.SocketOwner(false,Packet.ProtoUdp,a,50000,0,0,200,2)};
Check(ProcessResolver.SelectOwner(packet,udp)==null,"ambiguous UDP rejected");
Check(RoutingPreflight.IsBenchmarkOrFakeIp(IPAddress.Parse("198.19.255.254"))&&!RoutingPreflight.IsBenchmarkOrFakeIp(IPAddress.Parse("198.20.0.1")),"fake IP range");
// Registry checks use an isolated temporary directory, never the user's live settings.
var registryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ZdC.RegistryCheck." + Guid.NewGuid().ToString("N")));
var registryFile = Path.Combine(registryRoot, "applications.json");
try
{
    var registry = new ApplicationRegistry(registryFile);
    Check(!Directory.Exists(registryRoot), "registry construction is passive");
    Check((await registry.LoadAsync()).Count == 0, "missing registry loads empty");
    var executable = Environment.ProcessPath!;
    var registered = await registry.RegisterAsync(executable);
    Check(registered.Path == Path.GetFullPath(executable), "register persists full executable path");
    await registry.RegisterAsync(executable.ToUpperInvariant());
    Check((await registry.ListAsync()).Count == 1, "registration deduplicates case insensitive path");
    var reopened = new ApplicationRegistry(registryFile);
    var saved = await File.ReadAllTextAsync(registryFile);
    var external = saved + "\n ";
    await File.WriteAllTextAsync(registryFile, external);
    try { await registry.RemoveAsync(executable); throw new Exception("external write overwritten"); }
    catch (RegistryChangedException) { Check(await File.ReadAllTextAsync(registryFile) == external, "external edits preserved"); }
    await registry.LoadAsync();
    Check(await registry.RemoveAsync(executable) && (await registry.ListAsync()).Count == 0, "remove persists");
    Check(Directory.GetFiles(registryRoot).Length == 1, "atomic save leaves no backup or temporary files");
    await File.WriteAllTextAsync(registryFile, "{");
    try { await registry.LoadAsync(); throw new Exception("bad JSON accepted"); }
    catch (InvalidDataException) { Check(await File.ReadAllTextAsync(registryFile) == "{", "bad JSON preserved"); }
    try { await registry.RegisterAsync(executable); throw new Exception("invalid load allowed save"); }
    catch (InvalidOperationException) { Check(await File.ReadAllTextAsync(registryFile) == "{", "bad load blocks mutation"); }

    // Only real temporary .lnk files and copies of the SDK host. No target executable is launched.
    var appRoot = Directory.CreateDirectory(Path.Combine(registryRoot, "TestProduct")).FullName;
    string Target(string version) => Path.Combine(
        Directory.CreateDirectory(Path.Combine(appRoot, "app-" + version)).FullName, "fixture.exe");
    var original = Target("1.0");
    var updated = Target("2.0");
    var other = Target("3.0");
    var host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
    File.Copy(host, original);
    File.Copy(host, updated);
    File.Copy(executable, other);
    var mainLink = Path.Combine(registryRoot, "Entry.lnk");
    var firstFolder = Directory.CreateDirectory(Path.Combine(registryRoot, "First")).FullName;
    var secondFolder = Directory.CreateDirectory(Path.Combine(registryRoot, "Second")).FullName;
    var movedLink = Path.Combine(firstFolder, Path.GetFileName(mainLink));
    var competingLink = Path.Combine(secondFolder, Path.GetFileName(mainLink));
    WriteLink(mainLink, original);
    var entryRegistry = new ApplicationRegistry(Path.Combine(registryRoot, "entries.json"));
    await entryRegistry.LoadAsync();
    var card = await entryRegistry.ImportAsync(await ApplicationEntryReader.ReadAsync(mainLink));
    Check(!string.IsNullOrEmpty(card.ShortcutPublisher), "temporary SDK target has a verified embedded signer");
    var noSearch = Enumerable.Range(0, 1).Select<int, string>(_ => throw new Exception("unnecessary search"));
    var before = await File.ReadAllBytesAsync(entryRegistry.FilePath);
    await entryRegistry.RefreshShortcutAsync(original, default, noSearch);
    Check(before.SequenceEqual(await File.ReadAllBytesAsync(entryRegistry.FilePath)), "unchanged shortcut skips lookup and disk writes");
    WriteLink(mainLink, updated);
    var refreshed = await entryRegistry.RefreshShortcutAsync(original, default, noSearch);
    Check(refreshed.Path == updated && refreshed.ShortcutPath == mainLink &&
        refreshed.Arguments.SequenceEqual(new[] { "--fixture", "two words" }) &&
        refreshed.WorkingDirectory == Path.GetDirectoryName(updated) && refreshed.WindowStyle == 3,
        "same shortcut retargeted while old EXE exists preserves launch semantics");
    Check((await entryRegistry.LoadAsync()).Single().Path == updated, "recovered card persists in place");
    File.Move(mainLink, movedLink);
    Check((await entryRegistry.RefreshShortcutAsync(updated, default, new[] { firstFolder })).ShortcutPath == movedLink,
        "moved shortcut recovered when EXE is unchanged");
    File.Delete(original);
    WriteLink(movedLink, other);
    var dirs = new[] { firstFolder, secondFolder };
    Check((await ShortcutRecovery.ResolveAsync(card, default, dirs)).Path == original,
        "same shortcut and EXE names from another product rejected");
    WriteLink(movedLink, updated);
    var moved = await ShortcutRecovery.ResolveAsync(card, default, dirs);
    Check(moved.Path == updated && moved.ShortcutPath == movedLink && moved.Name == card.Name &&
        moved.Arguments.SequenceEqual(card.Arguments), "unique relocated shortcut preserves original card and arguments");
    Check((await ShortcutRecovery.ResolveAsync(card with { ShortcutPublisher = "different signer" }, default, dirs)).Path == original,
        "matching path and product with a different publisher rejected");
    var installerLink = Path.Combine(firstFolder, Path.GetFileNameWithoutExtension(updated) + ".lnk");
    File.Move(movedLink, installerLink);
    Check((await ShortcutRecovery.ResolveAsync(card, default, dirs)).ShortcutPath == installerLink,
        "custom shortcut moved and renamed to the installer entry still has to match verified product identity");
    File.Move(installerLink, movedLink);
    WriteLink(competingLink, updated);
    Check((await ShortcutRecovery.ResolveAsync(card, default, dirs)).Path == original,
        "two credible relocated shortcuts keep original card for manual selection");
    File.Delete(competingLink);
    WriteLink(movedLink, updated, "--different");
    Check((await ShortcutRecovery.ResolveAsync(card, default, dirs)).Path == original,
        "relocated shortcut with different arguments rejected");
    WriteLink(movedLink, updated, work: registryRoot);
    Check((await ShortcutRecovery.ResolveAsync(card, default, dirs)).Path == original,
        "relocated shortcut with different working directory rejected");

    // Exercise the manager's ordinary load and launch path. The missing controller stops before process creation.
    File.Copy(host, original);
    WriteLink(mainLink, original);
    var managerFile = Path.Combine(registryRoot, "manager.json");
    var persisted = new ApplicationRegistry(managerFile);
    await persisted.LoadAsync();
    await persisted.ImportAsync(await ApplicationEntryReader.ReadAsync(mainLink));
    WriteLink(mainLink, updated);
    var missingController = Path.Combine(registryRoot, "missing-controller.exe");
    await using var managerLog = new BoundedLocalLog(Path.Combine(registryRoot, "manager-log"));
    await using var manager = new RegisteredApplicationManager(missingController, managerFile, managerLog);
    Check((await manager.LoadAsync()).Single().Path == updated, "manager reopen resolves the original card before returning it");
    WriteLink(mainLink, original);
    try { await manager.LaunchAsync(updated); throw new Exception("missing fixture controller accepted"); }
    catch (FileNotFoundException ex) when (ex.FileName == missingController)
    {
        Check((await manager.ListAsync()).Single().Path == original,
            "normal launch resolves a further in-place update and persists it before controller startup");
    }
    // Register both targets explicitly and ensure a collision cannot absorb a second card.
    await persisted.LoadAsync();
    await persisted.RegisterAsync(updated);
    WriteLink(mainLink, updated);
    var previousCards = await File.ReadAllBytesAsync(managerFile);
    var conflict = await persisted.RefreshShortcutAsync(original, default, noSearch);
    Check(conflict.Path == original && (await persisted.ListAsync()).Count == 2 &&
        previousCards.SequenceEqual(await File.ReadAllBytesAsync(managerFile)), "collision with a registered card leaves both intact");
}
finally
{
    var safeParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (registryRoot.StartsWith(safeParent, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(registryRoot).StartsWith("ZdC.RegistryCheck.", StringComparison.Ordinal) && Directory.Exists(registryRoot))
        Directory.Delete(registryRoot, recursive: true);
}


// Bounded log lifecycle checks use only this disposable directory.
var logRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ZdC.LogCheck." + Guid.NewGuid().ToString("N")));
try
{
    await using (var passive = new BoundedLocalLog(logRoot)) { }
    Check(!Directory.Exists(logRoot), "unused log creates no files");
    Directory.CreateDirectory(logRoot);
    var active = Path.Combine(logRoot, "events.log");
    await File.WriteAllTextAsync(active, new string('x', 1024 * 1024));
    await File.WriteAllTextAsync(active + ".1", "previous");
    await File.WriteAllTextAsync(active + ".2", "oldest");
    var log = new BoundedLocalLog(logRoot);
    var input = "first\r\n" + new string('z', 100000);
    log.Write(input);
    await log.DisposeAsync();
    var saved = await File.ReadAllTextAsync(active);
    Check(Directory.GetFiles(logRoot).Length == 3 && new FileInfo(active + ".1").Length == 1024 * 1024 &&
        await File.ReadAllTextAsync(active + ".2") == "previous", "log rotates to at most three files");
    Check(saved.EndsWith(input[..2048].Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine),
        "log truncates before newline cleaning with unchanged output");
    log.Write("after close");
    await log.DisposeAsync();
    Check(await File.ReadAllTextAsync(active) == saved, "closed log ignores writes and repeated disposal");
    await using (var blocked = new BoundedLocalLog(active)) { blocked.Write("cannot write below a file"); }
    Check(await File.ReadAllTextAsync(active) == saved, "log storage failure leaves existing data intact");
}
finally
{
    var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (logRoot.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(logRoot).StartsWith("ZdC.LogCheck.", StringComparison.Ordinal) && Directory.Exists(logRoot))
        Directory.Delete(logRoot, recursive: true);
}
Console.WriteLine($"{checks} offline checks passed; no capture handles opened.");

static void WriteLink(string path, string target, string args = @"--fixture ""two words""", string? work = null)
{
    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
    try
    {
        dynamic link = shell.CreateShortcut(path);
        try
        {
            link.TargetPath = target;
            link.Arguments = args;
            link.WorkingDirectory = work ?? Path.GetDirectoryName(target);
            link.WindowStyle = 3;
            link.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
    }
    finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
}

// Pure in-memory allocator: this harness never binds a socket or loads a driver.
sealed class OfflinePortAllocator : INatPortAllocator
{
    private ushort _next = 49152;
    public INatPortLease Reserve(byte protocol, UInt128 address) => new Lease(_next++);
    private sealed class Lease(ushort port) : INatPortLease
    {
        public ushort Port { get; } = port;
        public void Dispose() { }
    }
}
