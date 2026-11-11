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
    public async Task CreateAll_WithConfig_PlaceholdersRunAsUnknown()
    {
        var config = new Qingniao.Core.MeshConfig
        {
            StunServers = ["stun.example.com:3478"],
            VpnCidrs = ["10.66.0.0/24"],
            BandwidthEndpoint = "https://bw.example.com/report",
        };

        var checks = CheckRegistry.CreateAll(config);

        Assert.Equal(5, checks.Count);

        // Task 7 起 ipv6、Task 9 起 nat 已换为真实实现(机器/网络相关,不在注册表测试里跑),
        // 其余占位仍应返回 Unknown("尚未实现")。
        var placeholders = checks.Where(c => c.Name != "ipv6" && c.Name != "nat").ToArray();
        Assert.Equal(3, placeholders.Length);
        foreach (var check in placeholders)
        {
            var result = await check.RunAsync(CancellationToken.None);
            Assert.Equal(CheckStatus.Unknown, result.Status);
            Assert.Equal("尚未实现", result.Summary);
            Assert.Null(result.Detail);
        }
    }
}
