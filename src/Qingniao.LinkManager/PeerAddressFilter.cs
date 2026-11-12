using System.Net;
using System.Net.Sockets;

namespace Qingniao.LinkManager;

/// <summary>
/// 通告/候选地址过滤(纯逻辑):从候选地址中挑选可对 Peer 通告的地址,防止虚拟网卡/链路本地地址误报。
/// </summary>
/// <remarks>
/// 排除 loopback(IPv4 <c>127.0.0.0/8</c>、IPv6 <c>::1</c>)、IPv4 链路本地 <c>169.254.0.0/16</c>、
/// IPv6 链路本地 <c>fe80::/10</c>、未指定地址(<c>0.0.0.0</c> 与 <c>::</c>)、IPv6 ULA <c>fc00::/7</c>
/// 以及 <c>2000::/3</c> 之外的非全局 v6;保留私有 v4(10/8、172.16/12、192.168/16)、其余公网 v4
/// 与全局单播 v6(<c>2000::/3</c>)。IPv4-mapped 地址按 IPv4 规则处理。
/// 输出保持输入顺序;输入为 null/空或全部被排除时返回空列表。纯函数,结果确定。
/// </remarks>
public static class PeerAddressFilter
{
    /// <summary>从候选地址中筛选可通告地址。</summary>
    /// <param name="candidates">候选地址序列;允许为 null(视为空)。</param>
    /// <returns>可通告地址列表,保持输入顺序;无可用地址时为空列表。IPv4-mapped 输入输出其 IPv4 形态。</returns>
    public static IReadOnlyList<IPAddress> SelectAdvertisement(IEnumerable<IPAddress>? candidates)
    {
        if (candidates is null)
        {
            return [];
        }

        return candidates.Select(ToAdvertiseable).OfType<IPAddress>().ToArray();
    }

    /// <summary>单个候选地址的归一化结果:不可通告返回 null;IPv4-mapped 归一为 IPv4;其余原样。</summary>
    private static IPAddress? ToAdvertiseable(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        IPAddress candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return candidate.AddressFamily == AddressFamily.InterNetwork
            ? (IsAdvertiseableV4(candidate) ? candidate : null)
            : (IsAdvertiseableV6(candidate) ? candidate : null);
    }

    /// <summary>IPv4 规则:排除 loopback、链路本地与未指定地址;私有与公网地址均保留。</summary>
    private static bool IsAdvertiseableV4(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false; // 127.0.0.0/8
        }

        byte[] b = address.GetAddressBytes();
        if (b[0] == 169 && b[1] == 254)
        {
            return false; // 169.254.0.0/16 链路本地
        }

        if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0)
        {
            return false; // 0.0.0.0 未指定
        }

        return true;
    }

    /// <summary>IPv6 规则:仅保留全局单播 2000::/3,其余(链路本地、ULA、组播、未指定等)一律排除。</summary>
    private static bool IsAdvertiseableV6(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)
        {
            return false; // fe80::/10 链路本地
        }

        if (b[0] is 0xFC or 0xFD)
        {
            return false; // fc00::/7 ULA
        }

        if (b.All(static x => x == 0))
        {
            return false; // :: 未指定(含 ::1 之外的全零形态)
        }

        return b[0] is >= 0x20 and <= 0x3F; // 仅 2000::/3(全局单播)
    }
}
