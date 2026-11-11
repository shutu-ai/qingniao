namespace Qingniao.SelfCheck;

/// <summary>单项环境检查结果状态。</summary>
public enum CheckStatus
{
    Pass,
    Warn,
    Fail,
    Unknown,
}

/// <summary>单项自检结果(不可变)。</summary>
/// <param name="Name">稳定英文检查 ID,如 "ipv6"、"nat"。</param>
/// <param name="Status">结果状态。</param>
/// <param name="Summary">一句话人读摘要。</param>
/// <param name="Detail">可选的补充细节(诊断信息、度量值等)。</param>
public sealed record CheckResult(string Name, CheckStatus Status, string Summary, string? Detail = null);
