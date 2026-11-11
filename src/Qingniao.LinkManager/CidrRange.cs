using System.Net;
using System.Net.Sockets;

namespace Qingniao.LinkManager;

/// <summary>
/// IPv4 CIDR 网段主机地址枚举(纯逻辑):"a.b.c.d/prefix" → 网段内全部可用主机地址。
/// </summary>
/// <remarks>
/// 排除网络地址(主机位全 0)与广播地址(主机位全 1)。例外:
/// <list type="bullet">
/// <item><c>/32</c>:单主机,无网络/广播之分,原样返回该地址。</item>
/// <item><c>/31</c>:按 RFC 3021 点对点链路,两个地址均可用,全部返回。</item>
/// </list>
/// prefix &lt; 16 视为超过单次扫描 65536 主机上限,直接拒绝;prefix ≥ 16 时惰性枚举(/16 共 65534 项)。
/// </remarks>
public static class CidrRange
{
    /// <summary>单次扫描的主机数上限:prefix 最小为 16(/16 → 65534 主机)。</summary>
    private const int MinPrefix = 16;

    /// <summary>枚举 CIDR 网段内的全部主机地址(不含网络地址与广播地址,见类型说明的 /31、/32 例外)。</summary>
    /// <param name="cidr">形如 "a.b.c.d/prefix" 的 IPv4 CIDR 字符串。</param>
    /// <exception cref="ArgumentNullException"><paramref name="cidr"/> 为 null。</exception>
    /// <exception cref="ArgumentException">缺少 "/"、前缀非整数或 prefix 超出 [16, 32](其中 prefix &lt; 16 抛 ArgumentOutOfRangeException)。</exception>
    /// <exception cref="FormatException">IP 部分不是合法地址。</exception>
    /// <exception cref="NotSupportedException">输入为 IPv6(仅支持 IPv4)。</exception>
    public static IEnumerable<IPAddress> EnumerateHosts(string cidr)
    {
        ArgumentNullException.ThrowIfNull(cidr);

        uint network = ParseNetwork(cidr, out int prefix, out uint mask);
        uint broadcast = network | ~mask;

        if (prefix == 32)
        {
            yield return ToIPAddress(network);
            yield break;
        }

        if (prefix == 31)
        {
            // RFC 3021:点对点链路两端的两个地址均可用。
            yield return ToIPAddress(network);
            yield return ToIPAddress(broadcast);
            yield break;
        }

        for (uint host = network + 1; host < broadcast; host++)
        {
            yield return ToIPAddress(host);
        }
    }

    /// <summary>解析 "a.b.c.d/prefix":校验格式、IPv4 与 prefix 范围,返回主机序的网络地址与掩码。</summary>
    private static uint ParseNetwork(string cidr, out int prefix, out uint mask)
    {
        string[] parts = cidr.Split('/');
        if (parts.Length != 2)
        {
            throw new ArgumentException($"CIDR 格式无效,应为 a.b.c.d/prefix:\"{cidr}\"", nameof(cidr));
        }

        IPAddress address;
        try
        {
            address = IPAddress.Parse(parts[0]);
        }
        catch (FormatException ex)
        {
            // 保持 FormatException 语义,仅补充上下文。
            throw new FormatException($"CIDR 的 IP 部分不是合法地址:\"{parts[0]}\"", ex);
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new NotSupportedException($"仅支持 IPv4 网段,收到:\"{cidr}\"");
        }

        if (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32)
        {
            throw new ArgumentException($"CIDR 前缀长度无效(应为 0-32 的整数):\"{cidr}\"", nameof(cidr));
        }

        if (prefix < MinPrefix)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cidr), cidr, $"CIDR 超过 65536 主机上限:前缀 /{prefix} 小于最小值 /{MinPrefix}");
        }

        uint hostOrder = ToHostOrderUInt(address);
        mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
        return hostOrder & mask;
    }

    /// <summary>把 IPv4 地址的字节序(网络序)字节组转为主机序 uint,避免按字节比较的陷阱。</summary>
    private static uint ToHostOrderUInt(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    /// <summary>把主机序 uint 转回 <see cref="IPAddress"/>。</summary>
    private static IPAddress ToIPAddress(uint hostOrder)
    {
        return new IPAddress(
            [(byte)(hostOrder >> 24), (byte)(hostOrder >> 16), (byte)(hostOrder >> 8), (byte)hostOrder]);
    }
}
