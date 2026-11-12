using Qingniao.LinkManager;

namespace Qingniao.SelfCheck.Checks;

/// <summary>
/// VPN 可达性自检:对配置的 VpnCidrs 做一轮 TCP 存活探测(默认接 <see cref="VpnCidrScanner"/>,
/// 探测端口取配置的 MeshPort)。未配置网段 → Unknown;有存活 → Pass;全部不通 → Warn。
/// </summary>
/// <remarks>
/// R10 契约:<see cref="RunAsync"/> 永不抛出 —— 网段提供者/扫描器异常一律降级 Unknown(Detail=原因)。
/// 默认扫描委托不带取消(R4 形状);单点探测有 500ms 上限、并发 64,总量有界,运行期不响应取消,
/// 已取消的令牌在起扫前短路为 Unknown。
/// </remarks>
public sealed class VpnReachabilityCheck : IEnvironmentCheck
{
    public const string CheckName = "vpn-reachability";

    private const string NotConfiguredSummary = "未配置 VPN 网段";
    private const string CancelledSummary = "VPN 网段探测被取消";

    private readonly Func<IReadOnlyList<string>> _cidrsProvider;
    private readonly Func<IReadOnlyList<string>, int, Task<ProbeResult[]>> _scanner;
    private readonly int _meshPort;

    /// <summary>注入网段提供者与假扫描器用于测试;缺省时用真实扫描器(R4 权威形状 + meshPort 细化)。</summary>
    public VpnReachabilityCheck(
        Func<IReadOnlyList<string>> cidrsProvider,
        Func<IReadOnlyList<string>, int, Task<ProbeResult[]>>? scanner = null,
        int meshPort = 13371)
    {
        ArgumentNullException.ThrowIfNull(cidrsProvider);
        _cidrsProvider = cidrsProvider;
        _scanner = scanner ?? ScanWithRealScannerAsync;
        _meshPort = meshPort;
    }

    public string Name => CheckName;

    public async Task<CheckResult> RunAsync(CancellationToken ct)
    {
        IReadOnlyList<string> cidrs;
        try
        {
            cidrs = _cidrsProvider();
        }
        catch (Exception ex)
        {
            return Unknown("VPN 网段配置读取失败", ex.Message);
        }

        if (cidrs.Count == 0)
        {
            return new CheckResult(
                Name, CheckStatus.Unknown, NotConfiguredSummary,
                "在 configs/sample.json 的 vpnCidrs 填入目标网段(如 \"10.66.0.0/24\")后可探测");
        }

        if (ct.IsCancellationRequested)
        {
            return new CheckResult(Name, CheckStatus.Unknown, CancelledSummary);
        }

        ProbeResult[] results;
        try
        {
            results = await _scanner(cidrs, _meshPort).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CheckResult(Name, CheckStatus.Unknown, CancelledSummary);
        }
        catch (Exception ex)
        {
            return Unknown("VPN 网段探测失败", ex.Message);
        }

        ProbeResult[] open = results.Where(r => r.IsOpen).ToArray();
        return open.Length > 0
            ? new CheckResult(
                Name, CheckStatus.Pass,
                $"VPN 网段可达: {open.Length}/{results.Length} 台存活",
                $"存活地址: {string.Join(", ", open.Select(r => r.Address))}")
            : new CheckResult(
                Name, CheckStatus.Warn,
                $"VPN 网段全部不可达(共探测 {results.Length} 台)",
                $"网段: {string.Join(", ", cidrs)};确认对端 daemon 已启动且端口 {_meshPort} 可达");
    }

    /// <summary>默认扫描实现:把真实扫描器的 IAsyncEnumerable 物化为数组。</summary>
    private static async Task<ProbeResult[]> ScanWithRealScannerAsync(
        IReadOnlyList<string> cidrs, int port)
    {
        var scanner = new VpnCidrScanner();
        var results = new List<ProbeResult>();
        await foreach (ProbeResult result in scanner.ScanAsync(cidrs, port).ConfigureAwait(false))
        {
            results.Add(result);
        }

        return [.. results];
    }

    /// <summary>内部失败统一降级:Unknown + 摘要 + 细节。</summary>
    private static CheckResult Unknown(string summary, string detail) =>
        new(VpnReachabilityCheck.CheckName, CheckStatus.Unknown, summary, detail);
}
