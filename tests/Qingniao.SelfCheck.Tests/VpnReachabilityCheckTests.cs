using System.Net;
using Qingniao.LinkManager;
using Qingniao.SelfCheck.Checks;

namespace Qingniao.SelfCheck.Tests;

/// <summary>
/// vpn-reachability 三分支(brief):VpnCidrs 空 → Unknown("未配置 VPN 网段");
/// 有网段且有 IsOpen → Pass;全不通 → Warn。另测 R10 降级与 meshPort 透传(R4)。
/// </summary>
public class VpnReachabilityCheckTests
{
    [Fact]
    public async Task EmptyCidrs_ReturnsUnknown_WithoutScanning()
    {
        bool scannerCalled = false;
        var check = new VpnReachabilityCheck(
            () => [],
            (_, _) => { scannerCalled = true; return Task.FromResult<ProbeResult[]>([]); });

        CheckResult result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Equal("未配置 VPN 网段", result.Summary);
        Assert.False(scannerCalled);
    }

    [Fact]
    public async Task SomeOpen_ReturnsPass_AndPassesCidrsAndMeshPortToScanner()
    {
        IReadOnlyList<string>? receivedCidrs = null;
        int receivedPort = 0;
        var check = new VpnReachabilityCheck(
            () => ["10.66.0.0/24"],
            (cidrs, port) =>
            {
                receivedCidrs = cidrs;
                receivedPort = port;
                ProbeResult[] results =
                [
                    new ProbeResult(IPAddress.Parse("10.66.0.2"), IsOpen: true, LatencyMs: 1),
                    new ProbeResult(IPAddress.Parse("10.66.0.3"), IsOpen: false, LatencyMs: 500),
                ];
                return Task.FromResult(results);
            },
            meshPort: 13400);

        CheckResult result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(["10.66.0.0/24"], receivedCidrs);
        Assert.Equal(13400, receivedPort);
        Assert.Equal(CheckStatus.Pass, result.Status);
        Assert.Contains("1/2", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllClosed_ReturnsWarn()
    {
        var check = new VpnReachabilityCheck(
            () => ["10.66.0.0/24"],
            (_, _) =>
            {
                ProbeResult[] results =
                [
                    new ProbeResult(IPAddress.Parse("10.66.0.2"), IsOpen: false, LatencyMs: 500),
                    new ProbeResult(IPAddress.Parse("10.66.0.3"), IsOpen: false, LatencyMs: 500),
                ];
                return Task.FromResult(results);
            });

        CheckResult result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Warn, result.Status);
    }

    [Fact]
    public async Task ScannerThrows_DegradesToUnknown_NeverThrows()
    {
        var check = new VpnReachabilityCheck(
            () => ["10.66.0.0/24"],
            (_, _) => throw new InvalidOperationException("扫描器炸了"));

        CheckResult result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Contains("扫描器炸了", result.Detail, StringComparison.Ordinal);
    }
}
