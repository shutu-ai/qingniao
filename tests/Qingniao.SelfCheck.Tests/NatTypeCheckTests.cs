using System.Collections.Concurrent;
using System.Net;
using Qingniao.SelfCheck.Checks;
using Qingniao.SelfCheck.Checks.Stun;

namespace Qingniao.SelfCheck.Tests;

/// <summary>
/// NatTypeCheck 状态映射、文案钉(逐字)、服务器注入、R10 永不抛出与并发探测证明测试
/// (全部注入假探测委托,无真实网络 I/O)。
/// </summary>
public class NatTypeCheckTests
{
    private const string PunchableSummary = "NAT 可打洞(圆锥型)";
    private const string SymmetricSummary =
        "对称 NAT:公网直连打洞大概率失败。出路:任一侧公网入口(端口映射/VPS)或 IPv6";
    private const string UnknownSummary = "STUN 探测失败,无法判定";

    private static readonly string[] DefaultServers =
    [
        "stun1.l.google.com:19302",
        "stun.cloudflare.com:3478",
    ];

    private static StunResult Mapped(string ip, int port) =>
        new(new IPEndPoint(IPAddress.Parse(ip), port), Other: null);

    private static Func<string, Task<StunResult?>> Fixed(StunResult? value) =>
        _ => Task.FromResult(value);

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // -------------------------------------------------- 基本契约

    [Fact]
    public void Name_IsNat()
    {
        Assert.Equal("nat", new NatTypeCheck().Name);
    }

    // -------------------------------------------------- 状态映射 + 文案钉(逐字)

    [Fact]
    public async Task BothRespondIdentically_Pass_ExactCopy()
    {
        var check = new NatTypeCheck(
            probe: Fixed(Mapped("203.0.113.7", 51000)),
            servers: ["s1:1", "s2:2"]);

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Pass, result.Status);
        Assert.Equal(PunchableSummary, result.Summary);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task BothRespondDifferently_Warn_ExactCopy()
    {
        var check = new NatTypeCheck(
            probe: s => Task.FromResult<StunResult?>(
                s == "s1:1" ? Mapped("203.0.113.7", 51000) : Mapped("1.2.3.4", 1000)),
            servers: ["s1:1", "s2:2"]);

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Warn, result.Status);
        Assert.Equal(SymmetricSummary, result.Summary);
    }

    [Fact]
    public async Task BothProbesReturnNull_Unknown_FirewallHintInDetail()
    {
        // 文案区分提示落在 Detail:零响应 → 防火墙拦截或无网络
        var check = new NatTypeCheck(probe: Fixed(null), servers: ["s1:1", "s2:2"]);

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Equal(UnknownSummary, result.Summary);
        Assert.Contains("防火墙", result.Detail);
    }

    [Fact]
    public async Task OnlyOneResponds_Unknown_DetailNamesResponderAndSilentOne()
    {
        var check = new NatTypeCheck(
            probe: s => Task.FromResult<StunResult?>(s == "s2:2" ? Mapped("203.0.113.7", 51000) : null),
            servers: ["s1:1", "s2:2"]);

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Equal(UnknownSummary, result.Summary);
        Assert.NotNull(result.Detail);
        Assert.Contains("s2:2", result.Detail); // 哪台响应
        Assert.Contains("s1:1", result.Detail); // 哪台无响应
        Assert.Contains("无响应", result.Detail);
    }

    // -------------------------------------------------- 服务器注入

    [Fact]
    public async Task Probe_ReceivesBothConfiguredServerNames()
    {
        var probed = new ConcurrentQueue<string>();
        var check = new NatTypeCheck(
            probe: s => { probed.Enqueue(s); return Task.FromResult<StunResult?>(null); },
            servers: ["alpha:1", "beta:2"]);

        await check.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "alpha:1", "beta:2" }, probed.ToArray());
    }

    [Fact]
    public async Task DefaultServers_AreMeshConfigDefaults()
    {
        var probed = new ConcurrentQueue<string>();
        var check = new NatTypeCheck(probe: s => { probed.Enqueue(s); return Task.FromResult<StunResult?>(null); });

        await check.RunAsync(CancellationToken.None);

        Assert.Equal(DefaultServers, probed.ToArray());
    }

    // -------------------------------------------------- R10:RunAsync 永不抛出

    [Fact]
    public async Task ThrowingProbe_UnknownWithExceptionDetail_NeverThrows()
    {
        // R10 裁定:探测委托按约不抛(StunClient),仍防御性兜底 → Unknown + Detail=异常消息
        var check = new NatTypeCheck(
            probe: _ => throw new InvalidOperationException("boom"),
            servers: ["s1:1", "s2:2"]);

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Equal(UnknownSummary, result.Summary);
        Assert.Contains("boom", result.Detail);
    }

    [Fact]
    public async Task PreCanceledToken_StillYieldsUnknownResult_NeverThrows()
    {
        // R10 裁定:外部取消 mid-check 仍必须返回 CheckResult,Detail 注明取消
        var check = new NatTypeCheck(probe: Fixed(Mapped("203.0.113.7", 51000)), servers: ["s1:1", "s2:2"]);

        var result = await check.RunAsync(new CancellationToken(canceled: true));

        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Equal(UnknownSummary, result.Summary);
        Assert.Contains("取消", result.Detail);
    }

    // -------------------------------------------------- 并发证明(TCS 闸门,确定性,无真实计时断言)

    [Fact]
    public async Task RunAsync_FiresBothProbesConcurrently_GateProof()
    {
        var probe1Started = NewGate();
        var probe2Started = NewGate();
        var releaseProbe1 = NewGate();
        var probed = new ConcurrentQueue<string>();

        Task<StunResult?> Probe(string server)
        {
            probed.Enqueue(server);
            if (server == "s1:1")
            {
                return BlockedProbe();
            }

            probe2Started.TrySetResult();
            return Task.FromResult<StunResult?>(Mapped("203.0.113.7", 51000));
        }

        async Task<StunResult?> BlockedProbe()
        {
            probe1Started.TrySetResult();
            await releaseProbe1.Task; // 闸门:不放行就不返回
            return Mapped("203.0.113.7", 51000);
        }

        var check = new NatTypeCheck(probe: Probe, servers: ["s1:1", "s2:2"]);
        Task<CheckResult> runTask = check.RunAsync(CancellationToken.None);

        // probe1 已启动并卡在闸门
        await probe1Started.Task;

        // 并发证明:probe1 未放行时,probe2 必须已启动。
        // 若实现串行,此等待超时 → TimeoutException → 测试失败(10s 仅作挂死保险,非计时断言)。
        await probe2Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        releaseProbe1.TrySetResult();
        CheckResult result = await runTask;

        // 两端一致 → Punchable,证明两个探测结果都参与了分类
        Assert.Equal(CheckStatus.Pass, result.Status);
        Assert.Equal(new[] { "s1:1", "s2:2" }, probed.ToArray());
    }
}
