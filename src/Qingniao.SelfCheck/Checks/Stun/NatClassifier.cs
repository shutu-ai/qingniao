namespace Qingniao.SelfCheck.Checks.Stun;

/// <summary>NAT 行为分类结论。</summary>
/// <remarks>
/// <see cref="NatClass.UdpBlocked"/> 为保留值:零响应时无法区分"UDP 被封"与"无网络",
/// 按设计一律降级为 <see cref="NatClass.Unknown"/>(文案区分提示由 <c>NatTypeCheck</c> 承载),
/// <see cref="NatClassifier.Classify"/> 不会返回 UdpBlocked。
/// </remarks>
public enum NatClass
{
    Punchable,
    Symmetric,
    UdpBlocked,
    Unknown,
}

/// <summary>
/// NAT 行为分类(纯函数,不产生 I/O)。
/// 判定矩阵(裁定 R12:比较完整 XorMapped 端点 IP:port,而非仅 IP——地址相关/端口相关
/// 对称型 NAT 会呈现"同 IP 不同端口",仅比 IP 会误判为 Punchable;设计文档要求
/// 对称 NAT 检出后明确警告,故端点任一差异即判对称):
///   两台都有响应且 XorMapped 端点完全一致(IP 且 port)→ Punchable(圆锥,可打洞)
///   两台都有响应且端点存在任一差异                      → Symmetric
///   恰一台响应                                          → Unknown(证据不足)
///   零响应                                              → Unknown(无法区分"被封"与"无网")
/// 输入 null = 该服务器无响应。
/// </summary>
public static class NatClassifier
{
    public static NatClass Classify(StunResult? serverA, StunResult? serverB)
    {
        // 防御:非 null 结果但 XorMapped 为 null 视同该服务器无响应(契约不允许,不信任外部输入)
        bool aResponded = serverA?.XorMapped is not null;
        bool bResponded = serverB?.XorMapped is not null;

        return (aResponded, bResponded) switch
        {
            (true, true) when EndpointsEqual(serverA!, serverB!) => NatClass.Punchable,
            (true, true) => NatClass.Symmetric,
            _ => NatClass.Unknown,
        };
    }

    /// <summary>完整端点相等性:IPEndPoint.Equals 同时比较 IP(含地址族)与端口。</summary>
    private static bool EndpointsEqual(StunResult left, StunResult right) =>
        left.XorMapped.Equals(right.XorMapped);
}
