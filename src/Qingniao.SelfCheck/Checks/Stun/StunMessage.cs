using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Qingniao.SelfCheck.Checks.Stun;

/// <summary>STUN Binding 响应解析结果:四种地址属性 + 事务 ID(tid 不匹配或包残缺时 Parse 返回 null,无实例)。</summary>
/// <remarks>
/// 简要说明:brief 的接口草图把 record 与 static class 分开写,同名类型在同一命名空间无法编译
/// (CS0101),故静态编解码成员合并进 record 本身——对外调用形态 <c>StunMessage.Parse(...)</c> 不变。
/// </remarks>
public sealed record StunMessage(
    IPEndPoint? Mapped, IPEndPoint? XorMapped, IPEndPoint? Other,
    IPEndPoint? Changed, byte[] TransactionId)
{
    // -------------------------------------------------------------- 协议常量

    public const ushort BindingRequest = 0x0001;
    public const ushort BindingSuccessResponse = 0x0101;

    /// <summary>RFC 5389 固定 magic cookie(0x2112A444)。</summary>
    public const uint MagicCookie = 0x2112A444;

    public const ushort AttrMapped = 0x0001;
    public const ushort AttrChangeRequest = 0x0003;
    public const ushort AttrChangedAddress = 0x0005;
    public const ushort AttrXorMapped = 0x0020;
    public const ushort AttrErrorCode = 0x0009;
    public const ushort AttrSoftware = 0x8022;
    public const ushort AttrOtherAddress = 0x8028;

    private const ushort ChangeIpFlag = 0x04;
    private const ushort ChangePortFlag = 0x02;
    private const int HeaderLength = 20;
    private const int TransactionIdLength = 12;
    private const int AttributeHeaderLength = 4;
    private const byte FamilyIpv4 = 0x01;
    private const byte FamilyIpv6 = 0x02;

    // -------------------------------------------------------------- 编码

    /// <summary>
    /// 构造 Binding Request。changeIp/changePort 均为 false 时省略 CHANGE-REQUEST 属性(消息长 0),
    /// 否则携带唯一的 CHANGE-REQUEST 属性(change-IP=0x04,change-PORT=0x02)。
    /// </summary>
    /// <exception cref="ArgumentException">transactionId 非 null 但长度不为 12 字节。</exception>
    public static byte[] BuildBindingRequest(bool changeIp = false, bool changePort = false, byte[]? transactionId = null)
    {
        byte[] tid = transactionId ?? new byte[TransactionIdLength];
        if (tid.Length != TransactionIdLength)
        {
            throw new ArgumentException($"事务 ID 必须为 {TransactionIdLength} 字节", nameof(transactionId));
        }

        if (transactionId is null)
        {
            RandomNumberGenerator.Fill(tid); // 未提供 tid → 密码学随机 12 字节
        }

        bool includeChangeRequest = changeIp || changePort;
        ushort messageLength = (ushort)(includeChangeRequest ? AttributeHeaderLength + 4 : 0);
        byte[] datagram = new byte[HeaderLength + messageLength];
        WriteU16(datagram, 0, BindingRequest);
        WriteU16(datagram, 2, messageLength);
        WriteU32(datagram, 4, MagicCookie);
        tid.CopyTo(datagram, 8);

        if (includeChangeRequest)
        {
            ushort flags = (ushort)((changeIp ? ChangeIpFlag : 0) | (changePort ? ChangePortFlag : 0));
            WriteU16(datagram, HeaderLength, AttrChangeRequest);
            WriteU16(datagram, HeaderLength + 2, 4);
            WriteU32(datagram, HeaderLength + 4, flags);
        }

        return datagram;
    }

    /// <summary>
    /// 内部编码:构造携带地址属性的 Binding 成功响应(供单元测试的假服务器与 golden 往返使用)。
    /// </summary>
    /// <exception cref="ArgumentException">transactionId 长度不为 12 字节。</exception>
    internal static byte[] BuildResponse(
        byte[] transactionId,
        IPEndPoint? xorMapped = null,
        IPEndPoint? other = null,
        IPEndPoint? changed = null,
        IPEndPoint? mapped = null)
    {
        if (transactionId.Length != TransactionIdLength)
        {
            throw new ArgumentException($"事务 ID 必须为 {TransactionIdLength} 字节", nameof(transactionId));
        }

        (ushort Type, IPEndPoint? Endpoint)[] attributes =
        [
            (AttrMapped, mapped),
            (AttrXorMapped, xorMapped),
            (AttrChangedAddress, changed),
            (AttrOtherAddress, other),
        ];

        int bodyLength = attributes.Sum(a => a.Endpoint is null ? 0 : AttributeHeaderLength + EncodedAddressLength(a.Endpoint));
        byte[] datagram = new byte[HeaderLength + bodyLength];
        WriteU16(datagram, 0, BindingSuccessResponse);
        WriteU16(datagram, 2, (ushort)bodyLength);
        WriteU32(datagram, 4, MagicCookie);
        transactionId.CopyTo(datagram, 8);

        int offset = HeaderLength;
        foreach ((ushort type, IPEndPoint? endpoint) in attributes)
        {
            if (endpoint is null)
            {
                continue;
            }

            byte[] value = EncodeAddress(endpoint, xored: type == AttrXorMapped, transactionId);
            WriteU16(datagram, offset, type);
            WriteU16(datagram, offset + 2, value.Length);
            value.CopyTo(datagram, offset + AttributeHeaderLength);
            offset += AttributeHeaderLength + value.Length;
        }

        return datagram;
    }

    // -------------------------------------------------------------- 解码

    /// <summary>
    /// 解析 STUN 消息。契约:tid 与 expectedTransactionId 不一致、包残缺(头不足、声明长度越界、
    /// 属性边界错乱)一律返回 null;未知属性(含 ERROR-CODE/SOFTWARE/CHANGE-REQUEST)跳过不算错误。
    /// </summary>
    public static StunMessage? Parse(byte[] datagram, byte[] expectedTransactionId)
    {
        if (datagram is null || datagram.Length < HeaderLength)
        {
            return null;
        }

        if (expectedTransactionId is null || expectedTransactionId.Length != TransactionIdLength)
        {
            return null;
        }

        if (ReadU32(datagram, 4) != MagicCookie)
        {
            return null;
        }

        byte[] tid = datagram[8..HeaderLength];
        if (!tid.AsSpan().SequenceEqual(expectedTransactionId))
        {
            return null;
        }

        int attributesEnd = HeaderLength + ReadU16(datagram, 2);
        if (attributesEnd > datagram.Length)
        {
            return null; // 声明长度超过实际数据 → 残缺
        }

        IPEndPoint? mapped = null;
        IPEndPoint? xorMapped = null;
        IPEndPoint? other = null;
        IPEndPoint? changed = null;

        int offset = HeaderLength;
        while (offset + AttributeHeaderLength <= attributesEnd)
        {
            ushort type = ReadU16(datagram, offset);
            int valueLength = ReadU16(datagram, offset + 2);
            int valueStart = offset + AttributeHeaderLength;
            int paddedLength = (valueLength + 3) & ~3; // 属性值按 4 字节对齐
            if (valueStart + paddedLength > attributesEnd)
            {
                return null; // 属性越界 → 边界错乱
            }

            byte[] value = datagram[valueStart..(valueStart + valueLength)];
            switch (type)
            {
                case AttrXorMapped:
                    xorMapped ??= DecodeAddress(value, xored: true, tid);
                    break;
                case AttrMapped:
                    mapped ??= DecodeAddress(value, xored: false, tid);
                    break;
                case AttrOtherAddress:
                    other ??= DecodeAddress(value, xored: false, tid);
                    break;
                case AttrChangedAddress:
                    changed ??= DecodeAddress(value, xored: false, tid);
                    break;
                default:
                    break; // 未知属性跳过
            }

            offset = valueStart + paddedLength;
        }

        return offset == attributesEnd
            ? new StunMessage(mapped, xorMapped, other, changed, tid)
            : null; // 剩余不足 4 字节的残渣 → 边界错乱
    }

    /// <summary>
    /// 解码地址属性值:保留字节(1)+ 族(1)+ 端口(2)+ 地址(4 或 16)。
    /// XOR 属性:端口 ⊕ cookie 高 16 位(0x2112);IPv4 地址 4 字节 ⊕ cookie;
    /// IPv6 地址 16 字节 ⊕ (cookie ‖ tid)。
    /// </summary>
    private static IPEndPoint? DecodeAddress(byte[] value, bool xored, byte[] tid)
    {
        if (value.Length < 8)
        {
            return null; // IPv4 值至少 8 字节(保留 + 族 + 端口 + 4 字节地址)
        }

        byte family = value[1];
        int port = ReadU16(value, 2);
        byte[] address;
        switch (family)
        {
            case FamilyIpv4:
                address = value[4..8];
                break;
            case FamilyIpv6:
                if (value.Length < 20)
                {
                    return null;
                }

                address = value[4..20];
                break;
            default:
                return null;
        }

        if (xored)
        {
            port ^= (int)(MagicCookie >> 16); // 0x2112
            Span<byte> mask = stackalloc byte[address.Length];
            WriteU32(mask, MagicCookie);
            if (address.Length == 16)
            {
                tid.AsSpan().CopyTo(mask[4..]);
            }

            for (int i = 0; i < address.Length; i++)
            {
                address[i] ^= mask[i];
            }
        }

        return new IPEndPoint(new IPAddress(address), port);
    }

    /// <summary>编码地址属性值(DecodeAddress 的逆操作)。</summary>
    private static byte[] EncodeAddress(IPEndPoint endpoint, bool xored, byte[] tid)
    {
        byte[] address = endpoint.Address.GetAddressBytes();
        if (address.Length is not (4 or 16))
        {
            throw new ArgumentException("仅支持 IPv4 / IPv6 端点", nameof(endpoint));
        }

        int port = endpoint.Port;
        if (xored)
        {
            port ^= (int)(MagicCookie >> 16);
            Span<byte> mask = stackalloc byte[address.Length];
            WriteU32(mask, MagicCookie);
            if (address.Length == 16)
            {
                tid.AsSpan().CopyTo(mask[4..]);
            }

            for (int i = 0; i < address.Length; i++)
            {
                address[i] ^= mask[i];
            }
        }

        byte[] value = new byte[4 + address.Length]; // 保留 + 族 + 端口 + 地址 = 8 或 20(天然 4 字节对齐)
        value[1] = address.Length == 4 ? FamilyIpv4 : FamilyIpv6;
        WriteU16(value, 2, port);
        address.CopyTo(value, 4);
        return value;
    }

    private static int EncodedAddressLength(IPEndPoint endpoint) =>
        endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6 ? 20 : 8;

    // -------------------------------------------------------------- 大端序读写

    private static ushort ReadU16(byte[] buffer, int offset) =>
        (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

    private static uint ReadU32(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16)
        | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

    private static void WriteU16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static void WriteU32(Span<byte> buffer, uint value)
    {
        buffer[0] = (byte)(value >> 24);
        buffer[1] = (byte)(value >> 16);
        buffer[2] = (byte)(value >> 8);
        buffer[3] = (byte)value;
    }

    private static void WriteU32(byte[] buffer, int offset, uint value) => WriteU32(buffer.AsSpan(offset), value);
}
