using System.Net;
using Qingniao.SelfCheck.Checks;

namespace Qingniao.SelfCheck.Tests;

/// <summary>
/// Ipv6Check 三态判定与 R10 永不抛出契约测试(全部注入假委托,无真实网络 I/O)。
/// </summary>
public class Ipv6CheckTests
{
    private static readonly IPAddress Global1 = IPAddress.Parse("240e:1234::1");
    private static readonly IPAddress Global2 = IPAddress.Parse("2400:3200::1");

    /// <summary>构造返回固定地址集的枚举委托。</summary>
    private static Func<IEnumerable<IPAddress>> Addresses(params IPAddress[] list) => () => list;

    // -------------------------------------------------------------- 基本契约

    [Fact]
    public void Name_IsIpv6()
    {
        Assert.Equal("ipv6", new Ipv6Check().Name);
    }

    // -------------------------------------------------------------- 三态判定

    [Fact]
    public async Task NoGlobalAddresses_ReturnsFail()
    {
        var check = new Ipv6Check(
            enumerateAddresses: Addresses(),
            outboundProbe: () => Task.FromResult(true));

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal("ipv6", result.Name);
        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Equal("未发现全局 IPv6(光猫/路由器可能未开 IPv6)", result.Summary);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task OnlyNonGlobalAddresses_AfterFilter_ReturnsFail()
    {
        // loopback / link-local / ULA 全被 SelectGlobal 滤掉 → 等同无全局地址
        var check = new Ipv6Check(
            enumerateAddresses: Addresses(
                IPAddress.Parse("::1"), IPAddress.Parse("fe80::1"), IPAddress.Parse("fd00::1")),
            outboundProbe: () => Task.FromResult(true));

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Equal("未发现全局 IPv6(光猫/路由器可能未开 IPv6)", result.Summary);
    }

    [Fact]
    public async Task GlobalAddressesAndProbeFails_ReturnsWarn()
    {
        var check = new Ipv6Check(
            enumerateAddresses: Addresses(Global1),
            outboundProbe: () => Task.FromResult(false));

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Warn, result.Status);
        Assert.Equal("有全局 IPv6 但出站不可达(防火墙拦截?)", result.Summary);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task GlobalAddressesAndProbeSucceeds_ReturnsPass()
    {
        var check = new Ipv6Check(
            enumerateAddresses: Addresses(Global1),
            outboundProbe: () => Task.FromResult(true));

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Pass, result.Status);
        Assert.Equal("全局 IPv6 可用: 1 个地址", result.Summary);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task PassSummary_CountsOnlyGlobalAddresses()
    {
        // 非 Global 地址过滤后不计入数量
        var check = new Ipv6Check(
            enumerateAddresses: Addresses(IPAddress.Parse("fe80::1"), Global1, Global2),
            outboundProbe: () => Task.FromResult(true));

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal("全局 IPv6 可用: 2 个地址", result.Summary);
    }

    // -------------------------------------------------- R10:RunAsync 永不抛出

    [Fact]
    public async Task EnumerationThrows_YieldsFailResult_NeverThrows()
    {
        // R10 裁定:枚举异常 → Fail(带 Detail=异常消息),不抛出
        var check = new Ipv6Check(
            enumerateAddresses: () => throw new InvalidOperationException("网卡枚举爆炸"),
            outboundProbe: () => Task.FromResult(true));

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Equal("无法枚举网络接口地址", result.Summary);
        Assert.Equal("网卡枚举爆炸", result.Detail);
    }

    [Fact]
    public async Task EnumerationCanceled_YieldsUnknownResult_NeverThrows()
    {
        // R10 裁定:枚举被取消 → Unknown(带 Detail),不抛出
        var check = new Ipv6Check(
            enumerateAddresses: () => throw new OperationCanceledException("枚举被取消"),
            outboundProbe: () => Task.FromResult(true));

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Equal("检查已取消", result.Summary);
        Assert.Equal("枚举被取消", result.Detail);
    }

    [Fact]
    public async Task ProbeThrows_YieldsWarnResultWithDetail_NeverThrows()
    {
        // R10 裁定:探测异常等同出站失败 → Warn(带 Detail=异常消息),不抛出
        var check = new Ipv6Check(
            enumerateAddresses: Addresses(Global1),
            outboundProbe: () => throw new InvalidOperationException("探测连接爆炸"));

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Warn, result.Status);
        Assert.Equal("有全局 IPv6 但出站不可达(防火墙拦截?)", result.Summary);
        Assert.Equal("探测连接爆炸", result.Detail);
    }

    [Fact]
    public async Task ProbeCanceled_YieldsWarnResult_NeverThrows()
    {
        // R10 裁定:探测 OCE 同样按出站失败降级 → Warn
        var check = new Ipv6Check(
            enumerateAddresses: Addresses(Global1),
            outboundProbe: () => throw new OperationCanceledException("探测被取消"));

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Warn, result.Status);
        Assert.Equal("探测被取消", result.Detail);
    }

    [Fact]
    public async Task PreCanceledToken_StillYieldsResult_NeverThrows()
    {
        // R10:外部提前取消时 RunAsync 也必须返回 CheckResult 而非抛出
        var check = new Ipv6Check(
            enumerateAddresses: Addresses(Global1),
            outboundProbe: () => Task.FromCanceled<bool>(new CancellationToken(canceled: true)));

        var result = await check.RunAsync(new CancellationToken(canceled: true));

        Assert.Equal(CheckStatus.Warn, result.Status);
        Assert.NotNull(result.Detail);
    }
}
