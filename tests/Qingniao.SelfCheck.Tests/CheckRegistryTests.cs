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
        foreach (var check in checks)
        {
            var result = await check.RunAsync(CancellationToken.None);
            Assert.Equal(CheckStatus.Unknown, result.Status);
            Assert.Equal("尚未实现", result.Summary);
            Assert.Null(result.Detail);
        }
    }
}
