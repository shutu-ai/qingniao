using System.Net;
using System.Net.Sockets;

namespace Qingniao.SelfCheck.Checks;

/// <summary>
/// 全局 IPv6 地址过滤器(纯函数,无 I/O)。
/// 排除:loopback(::1)、unspecified(::)、link-local(fe80::/10)、ULA(fc00::/7)、
/// IPv4-mapped(::ffff:0:0/96)以及一切非 IPv6 地址;仅保留 2000::/3 全局单播。
/// </summary>
public static class AddressFilter
{
    public static IReadOnlyList<IPAddress> SelectGlobal(IEnumerable<IPAddress> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        List<IPAddress> globals = [];
        foreach (var address in candidates)
        {
            if (address is not null && IsGlobalUnicast(address))
            {
                globals.Add(address);
            }
        }

        return globals;
    }

    /// <summary>仅 IPv6 且落在 2000::/3 的全局单播地址才保留;null 元素由调用方跳过。</summary>
    private static bool IsGlobalUnicast(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false; // 过滤器仅面向 IPv6,IPv4 一律排除
        }

        if (address.Equals(IPAddress.IPv6Loopback))
        {
            return false; // ::1
        }

        if (address.Equals(IPAddress.IPv6Any))
        {
            return false; // :: 未指定地址
        }

        if (address.IsIPv6LinkLocal)
        {
            return false; // fe80::/10
        }

        if (address.IsIPv6UniqueLocal)
        {
            return false; // fc00::/7(ULA)
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return false; // ::ffff:0:0/96
        }

        return (address.GetAddressBytes()[0] & 0b1110_0000) == 0b0010_0000; // 2000::/3
    }
}
