using Qingniao.Core;
using Qingniao.SelfCheck.Checks.Stun;

namespace Qingniao.SelfCheck.Checks;

/// <summary>
/// NAT 行为自检:并发查询两台 STUN 服务器(RFC 5389 Binding),以两侧 XorMapped 映射差异
/// 判定 NAT 行为(<see cref="NatClassifier"/>,按完整端点 IP:port 比较)。
/// Punchable → Pass;Symmetric → Warn(对称 NAT 打洞大概率失败,给出去路);其余 → Unknown。
/// </summary>
/// <remarks>
/// R10 契约:<see cref="RunAsync"/> 永不抛出。注入的探测委托按约不抛(默认为 StunClient,
/// 超时/取消一律 null),仍防御性兜底:探测异常 → Unknown(Detail=异常消息);
/// 外部取消 → Unknown(Detail 注明取消)。取消 token 链接进两台探测,取消后的探测按
/// StunClient 语义返回 null,自然汇入 Unknown 路径。
/// </remarks>
public sealed class NatTypeCheck : IEnvironmentCheck
{
    public const string CheckName = "nat";

    private const string PunchableSummary = "NAT 可打洞(圆锥型)";
    private const string SymmetricSummary =
        "对称 NAT:公网直连打洞大概率失败。出路:任一侧公网入口(端口映射/VPS)或 IPv6";
    private const string UnknownSummary = "STUN 探测失败,无法判定";

    private readonly Func<string, Task<StunResult?>>? _probe; // null → 默认 StunClient(链接 ct)
    private readonly StunClient _client = new();
    private readonly IReadOnlyList<string> _servers;

    /// <summary>注入假探测与服务器名用于测试;缺省时用 StunClient 与 MeshConfig 默认 STUN 服务器。</summary>
    public NatTypeCheck(
        Func<string, Task<StunResult?>>? probe = null,
        IReadOnlyList<string>? servers = null)
    {
        _probe = probe;
        _servers = servers ?? new MeshConfig().StunServers;
    }

    public string Name => CheckName;

    public async Task<CheckResult> RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        StunResult?[] results;
        try
        {
            // 并发查两台(Task.WhenAll);最多取前两台,单台配置时另一侧按无响应处理
            results = await Task.WhenAll(
                _servers.Take(2).Select(server => Probe(server, linked.Token)));
        }
        catch (Exception ex)
        {
            // 防御兜底(R10):委托按约不抛,仍降级为 Unknown
            string detail = linked.IsCancellationRequested
                ? $"探测被取消({ex.Message})"
                : ex.Message;
            return new CheckResult(Name, CheckStatus.Unknown, UnknownSummary, detail);
        }

        // 外部取消使探测证据失效(即使探测已返回)→ Unknown + Detail 注明取消
        if (linked.IsCancellationRequested)
        {
            return new CheckResult(Name, CheckStatus.Unknown, UnknownSummary, "探测被外部取消");
        }

        return Classify(results.ElementAtOrDefault(0), results.ElementAtOrDefault(1));
    }

    /// <summary>单台探测:注入委托直接用;默认走 StunClient 并链接取消 token。</summary>
    private Task<StunResult?> Probe(string server, CancellationToken ct) =>
        _probe is not null ? _probe(server) : _client.QueryAsync(server, ct: ct);

    /// <summary>分类结论 → CheckResult 映射;Unknown 的区分提示落在 Detail。</summary>
    private CheckResult Classify(StunResult? first, StunResult? second)
    {
        return NatClassifier.Classify(first, second) switch
        {
            NatClass.Punchable => new CheckResult(Name, CheckStatus.Pass, PunchableSummary),
            NatClass.Symmetric => new CheckResult(Name, CheckStatus.Warn, SymmetricSummary),
            _ => new CheckResult(Name, CheckStatus.Unknown, UnknownSummary, BuildUnknownDetail(first, second)),
        };
    }

    /// <summary>Unknown 细节:恰一台响应时指明哪台响应/哪台无响应;零响应给"防火墙或无网"提示。</summary>
    private string BuildUnknownDetail(StunResult? first, StunResult? second)
    {
        bool aResponded = first?.XorMapped is not null;
        bool bResponded = second?.XorMapped is not null;

        if (!aResponded && !bResponded)
        {
            return "两台 STUN 服务器均无响应,提示可能被防火墙拦截或无网络";
        }

        string responder = Describe(aResponded ? 0 : 1);
        string silent = Describe(aResponded ? 1 : 0);
        return $"仅 {responder} 有响应,{silent} 无响应,证据不足";
    }

    /// <summary>服务器展示名(下标越界防御:配置不足两台时不致抛出,维持 R10)。</summary>
    private string Describe(int index) =>
        index < _servers.Count ? $"服务器{index + 1}({_servers[index]})" : $"服务器{index + 1}(未配置)";
}
