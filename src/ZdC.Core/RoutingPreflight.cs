using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ZdC.Core;

public sealed record PreflightIssue(string Code, string Message);
public sealed record DirectAdapter(string Id, string Name, int InterfaceIndex, string[] Addresses, bool HasGateway);
public sealed record PreflightReport(IReadOnlyList<DirectAdapter> Adapters, IReadOnlyList<PreflightIssue> Issues)
{
    public bool ReadyForExperimentalStart => Issues.Count == 0;
}

/// <summary>Read-only inspection. This API never loads WinDivert, opens capture handles or modifies routes.</summary>
public static class RoutingPreflight
{
    public static bool IsBenchmarkOrFakeIp(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 198 && b[1] is 18 or 19;
    }

    public static PreflightReport Inspect(string nativeDirectory, IEnumerable<IPAddress>? destinations = null)
    {
        var issues = new List<PreflightIssue>();
        var adapters = new List<DirectAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var ip = nic.GetIPProperties();
            var addresses = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToArray();
            if (addresses.Any(a => IsBenchmarkOrFakeIp(a.Address))) continue;
            adapters.Add(new(nic.Id, nic.Name, ip.GetIPv4Properties()?.Index ?? 0,
                addresses.Select(a => a.Address.ToString()).ToArray(), ip.GatewayAddresses.Count > 0));
        }
        // These are candidates, not a claim of physical hardware: Windows virtual adapters
        // can report Ethernet. HardwareInterface and source-bound route lookup are separate gates.
        if (adapters.Count == 0) issues.Add(new("adapter.missing", "未发现可检查的联网接口。"));
        foreach (var file in new[] { "WinDivert.dll", "WinDivert64.sys" })
            if (!File.Exists(Path.Combine(nativeDirectory, file))) issues.Add(new("native.missing", $"缺少 {file}，尚未加载驱动。"));
        if (destinations?.Any(IsBenchmarkOrFakeIp) == true)
            issues.Add(new("dns.fake-ip", "目标包含 198.18.0.0/15 地址，需要先完成真实目标地址解析。"));
        issues.Add(new("routing.source-verification", "需要核对物理接口、强宿主设置和绑定源地址的实际出站路径。"));
        issues.Add(new("ownership.tuple", "完整端点归属已实现；首次包归属缺失时仍需完善定向阻断策略。"));
        
        issues.Add(new("proxy.explicit", "显式本地代理与系统 DNS 请求需要单独识别和处理。"));
        return new(adapters, issues);
    }
}
