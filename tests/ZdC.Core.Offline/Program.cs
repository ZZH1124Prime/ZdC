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
