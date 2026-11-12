using Qingniao.SelfCheck.Checks;

namespace Qingniao.SelfCheck.Tests;

public class CheckRegistryTests
{
    [Fact]
    public void CreateAll_ReturnsFiveChecksInFixedOrder()
    {
        var expected = new[] { "ipv6", "nat", "gpu-encoder", "vpn-reachability", "bandwidth" };

        var names = CheckRegistry.CreateAll().Select(c => c.Name).ToArray();

        Assert.Equal(expected, names);
    }

    [Fact]
    public void CreateAll_Ipv6Entry_IsRealCheckNotPlaceholder()
    {
        var ipv6 = CheckRegistry.CreateAll().Single(c => c.Name == "ipv6");

        Assert.IsType<Ipv6Check>(ipv6);
    }

    [Fact]
    public void CreateAll_NatEntry_IsRealCheckNotPlaceholder()
    {
        var nat = CheckRegistry.CreateAll().Single(c => c.Name == "nat");

        Assert.IsType<NatTypeCheck>(nat);
    }

    [Fact]
    public void CreateAll_GpuEncoderEntry_IsRealCheckNotPlaceholder()
    {
        var gpuEncoder = CheckRegistry.CreateAll().Single(c => c.Name == "gpu-encoder");

        Assert.IsType<GpuEncoderCheck>(gpuEncoder);
    }

    [Fact]
    public void CreateAll_VpnReachabilityEntry_IsRealCheckNotPlaceholder()
    {
        var vpn = CheckRegistry.CreateAll().Single(c => c.Name == "vpn-reachability");

        Assert.IsType<VpnReachabilityCheck>(vpn);
    }

    [Fact]
    public void CreateAll_BandwidthEntry_IsRealCheckNotPlaceholder()
    {
        var bandwidth = CheckRegistry.CreateAll().Single(c => c.Name == "bandwidth");

        Assert.IsType<BandwidthCheck>(bandwidth);
    }

    [Fact]
    public async Task CreateAll_VpnEntryConsumesConfiguredCidrs_EmptyYieldsUnknownWithoutScan()
    {
        // 空 VpnCidrs 的配置走 CreateAll → vpn-reachability 应为 Unknown("未配置 VPN 网段"),
        // 证明 config(而非常量)经 cidrsProvider 注入了检查项。
        var config = new Qingniao.Core.MeshConfig { VpnCidrs = [] };

        var vpn = CheckRegistry.CreateAll(config).Single(c => c.Name == "vpn-reachability");

        var result = await vpn.RunAsync(CancellationToken.None);
        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Equal("未配置 VPN 网段", result.Summary);
    }

    [Fact]
    public void CreateAll_WithConfig_AllFiveEntriesAreRealChecks()
    {
        var config = new Qingniao.Core.MeshConfig
        {
            StunServers = ["stun.example.com:3478"],
            VpnCidrs = ["10.66.0.0/24"],
            BandwidthEndpoint = "https://bw.example.com/report",
        };

        var checks = CheckRegistry.CreateAll(config);

        Assert.Equal(5, checks.Count);
        // Task 14 起 vpn-reachability 与 bandwidth 也换为真实实现;五项的类型逐一刻 asserting。
        // 真实机器/网络行为由各检查项自己的测试与 Live 用例覆盖,注册表测试只验证装配类型。
        Assert.IsType<Ipv6Check>(checks[0]);
        Assert.IsType<NatTypeCheck>(checks[1]);
        Assert.IsType<GpuEncoderCheck>(checks[2]);
        Assert.IsType<VpnReachabilityCheck>(checks[3]);
        Assert.IsType<BandwidthCheck>(checks[4]);
    }
}
