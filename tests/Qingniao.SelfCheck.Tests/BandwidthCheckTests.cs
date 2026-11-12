using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Qingniao.SelfCheck.Checks;

namespace Qingniao.SelfCheck.Tests;

/// <summary>
/// bandwidth 检查:无 endpoint 时取非 loopback 网卡最大链路速率(brief 的 Pass 文案);
/// 配置 endpoint 时经 HttpClient 实测上/下行吞吐。另测 R10 各降级路径。
/// </summary>
public class BandwidthCheckTests
{
    [Fact]
    public async Task NoEndpoint_NicSpeeds_ReturnsPassWithMaxMbps()
    {
        var check = new BandwidthCheck(
            endpoint: null,
            listNics: () =>
            [
                ("以太网", 1_000_000_000L),
                ("Wi-Fi", 100_000_000L),
            ]);

        CheckResult result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Pass, result.Status);
        Assert.Equal("网卡链路速率: 1000.0 Mbps(未测实际带宽)", result.Summary);
    }

    [Fact]
    public async Task NoEndpoint_AllSpeedsNonPositive_ReturnsUnknown()
    {
        var check = new BandwidthCheck(
            endpoint: null,
            listNics: () => [("虚拟网卡", 0L), ("断开的网卡", -1L)]);

        CheckResult result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Unknown, result.Status);
    }

    [Fact]
    public async Task NoEndpoint_NicEnumerationThrows_DegradesToUnknown()
    {
        var check = new BandwidthCheck(
            endpoint: null,
            listNics: () => throw new InvalidOperationException("枚举炸了"));

        CheckResult result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Contains("枚举炸了", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithEndpoint_KnownByteCount_ReturnsPassWithMeasuredMbps()
    {
        // GET 返回已知字节量(流会在窗口内提前结束),POST 直接 200;
        // 测速窗口缩短到 100ms 保持测试快速。吞吐值随实际耗时浮动,断言含正的 Mbps 数字。
        var check = CreateEndpointCheck(
            "http://bw.test/measure",
            handler: new FakeSpeedHandler(totalBytes: 3_000_000),
            measureWindow: TimeSpan.FromMilliseconds(100));

        CheckResult result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Pass, result.Status);
        double down = ExtractMbps(result.Summary, "下行");
        double up = ExtractMbps(result.Summary, "上行");
        Assert.True(down > 0, $"下行 Mbps 应为正,实际: {result.Summary}");
        Assert.True(up > 0, $"上行 Mbps 应为正,实际: {result.Summary}");
    }

    [Fact]
    public async Task WithEndpoint_TransportFailure_ReturnsWarn_NeverThrows()
    {
        var check = CreateEndpointCheck(
            "http://bw.test/measure",
            handler: new ThrowingHandler(new HttpRequestException("连接被拒")),
            measureWindow: TimeSpan.FromMilliseconds(100));

        CheckResult result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Warn, result.Status);
        Assert.Contains("连接被拒", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithEndpoint_AlreadyCancelled_ReturnsUnknown()
    {
        var check = CreateEndpointCheck(
            "http://bw.test/measure",
            handler: new FakeSpeedHandler(totalBytes: 3_000_000),
            measureWindow: TimeSpan.FromMilliseconds(100));

        CheckResult result = await check.RunAsync(new CancellationToken(canceled: true));

        Assert.Equal(CheckStatus.Unknown, result.Status);
    }

    /// <summary>构造带 endpoint 的被测对象(共享缩短窗口)。</summary>
    private static BandwidthCheck CreateEndpointCheck(
        string endpoint, HttpMessageHandler handler, TimeSpan measureWindow) =>
        new(endpoint, listNics: () => throw new InvalidOperationException("不应枚举网卡"), httpHandler: handler, measureWindow: measureWindow);

    /// <summary>从 Summary 提取 "下行/上行 X Mbps" 的数值。</summary>
    private static double ExtractMbps(string summary, string direction)
    {
        Match match = Regex.Match(
            summary, $"{direction}\\s+([\\d.]+) Mbps", RegexOptions.None, TimeSpan.FromSeconds(1));
        Assert.True(match.Success, $"Summary 未含 {direction} Mbps: {summary}");
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>假测速服务端:GET 回固定字节量,POST 收到请求即回 200。</summary>
    private sealed class FakeSpeedHandler(long totalBytes) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                // 先让渡一下,保证耗时统计口径稳定(非零 elapsed)
                await Task.Delay(1, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[totalBytes]),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
