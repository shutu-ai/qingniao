using System.Net;
using Qingniao.SelfCheck.Checks.Stun;

namespace Qingniao.SelfCheck.Tests;

/// <summary>
/// NAT 分类矩阵测试(纯函数)。裁定 R12:比较完整 XorMapped 端点(IP:port),而非仅 IP——
/// 地址相关/端口相关对称型 NAT 会呈现"同 IP 不同端口",仅比 IP 会误判为 Punchable。
/// </summary>
public class NatClassifierTests
{
    private static StunResult Mapped(string ip, int port) =>
        new(new IPEndPoint(IPAddress.Parse(ip), port), Other: null);

    // -------------------------------------------------- 两台都有响应

    [Fact]
    public void BothRespond_IdenticalEndpoints_Punchable()
    {
        Assert.Equal(
            NatClass.Punchable,
            NatClassifier.Classify(Mapped("203.0.113.7", 51000), Mapped("203.0.113.7", 51000)));
    }

    [Fact]
    public void BothRespond_DifferentIp_Symmetric()
    {
        Assert.Equal(
            NatClass.Symmetric,
            NatClassifier.Classify(Mapped("203.0.113.7", 51000), Mapped("198.51.100.9", 51000)));
    }

    [Fact]
    public void BothRespond_SameIpDifferentPort_Symmetric()
    {
        // R12 关键钉:同 IP 不同端口 = 端点相关/端口相关对称型 → Symmetric,不得 Punchable
        Assert.Equal(
            NatClass.Symmetric,
            NatClassifier.Classify(Mapped("1.2.3.4", 1000), Mapped("1.2.3.4", 2000)));
    }

    // -------------------------------------------------- 恰一台响应

    [Fact]
    public void OnlyServerA_Unknown()
    {
        Assert.Equal(NatClass.Unknown, NatClassifier.Classify(Mapped("203.0.113.7", 51000), null));
    }

    [Fact]
    public void OnlyServerB_Unknown()
    {
        Assert.Equal(NatClass.Unknown, NatClassifier.Classify(null, Mapped("203.0.113.7", 51000)));
    }

    // -------------------------------------------------- 零响应

    [Fact]
    public void NeitherResponds_Unknown_NotUdpBlocked()
    {
        // 零响应无法区分"UDP 被封"与"无网" → Unknown;UdpBlocked 为保留值,分类器不返回
        Assert.Equal(NatClass.Unknown, NatClassifier.Classify(null, null));
    }
}
