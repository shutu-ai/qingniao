using System.Net;
using Qingniao.SelfCheck.Checks.Stun;

namespace Qingniao.SelfCheck.Tests;

/// <summary>
/// STUN 消息编解码测试(纯函数,无 I/O)。
/// golden 向量按 R11 裁定:端口密文 <c>A1 47</c>(32853 ⊕ 0x2112 = 0xA147),
/// 地址密文 <c>E1 12 A6 45</c>(192.0.2.1 ⊕ cookie = C0 00 02 01 ⊕ 21 12 A4 44)。
/// </summary>
public class StunMessageTests
{
    private static readonly byte[] Tid =
        [0x0B, 0x5A, 0x9C, 0x71, 0x2E, 0x44, 0x31, 0xD6, 0xA1, 0xB0, 0x33, 0xF7];

    private static readonly IPEndPoint GoldenEndpoint = new(IPAddress.Parse("192.0.2.1"), 32853);

    /// <summary>手工构造的 Binding 成功响应:仅一个 XOR-MAPPED-ADDRESS 属性(golden 向量,R11)。</summary>
    private static byte[] GoldenDatagram() =>
    [
        0x01, 0x01, // Binding 成功响应
        0x00, 0x0C, // 消息长度 = 12(一个属性)
        0x21, 0x12, 0xA4, 0x44, // magic cookie
        .. Tid,
        0x00, 0x20, 0x00, 0x08, // XOR-MAPPED-ADDRESS,值长 8
        0x00, 0x01, // 保留字节 + IPv4 族
        0xA1, 0x47, // 端口密文(32853 ⊕ 0x2112)
        0xE1, 0x12, 0xA6, 0x45, // 地址密文
    ];

    // -------------------------------------------------------------- Step 1(a) BuildBindingRequest

    [Fact]
    public void BuildBindingRequest_WithChangeIp_HasHeaderAndChangeRequestAttribute()
    {
        byte[] request = StunMessage.BuildBindingRequest(changeIp: true, transactionId: Tid);

        Assert.Equal(28, request.Length); // 20 头 + 8 属性
        Assert.Equal(0x00, request[0]);
        Assert.Equal(0x01, request[1]); // Binding Request
        Assert.Equal(0x00, request[2]);
        Assert.Equal(0x08, request[3]); // 消息长度 = 8
        Assert.Equal(0x21, request[4]);
        Assert.Equal(0x12, request[5]);
        Assert.Equal(0xA4, request[6]);
        Assert.Equal(0x44, request[7]); // magic cookie
        Assert.Equal(Tid, request[8..20]);
        Assert.Equal(0x00, request[20]);
        Assert.Equal(0x03, request[21]); // CHANGE-REQUEST
        Assert.Equal(0x00, request[22]);
        Assert.Equal(0x04, request[23]); // 值长度 = 4
        Assert.Equal(0x00, request[24]);
        Assert.Equal(0x00, request[25]);
        Assert.Equal(0x00, request[26]);
        Assert.Equal(0x04, request[27]); // change-IP 位
    }

    [Fact]
    public void BuildBindingRequest_WithChangeIpAndPort_ValueBitsAre0x06()
    {
        byte[] request = StunMessage.BuildBindingRequest(changeIp: true, changePort: true, transactionId: Tid);

        Assert.Equal(0x06, request[27]); // 0x04 | 0x02
    }

    [Fact]
    public void BuildBindingRequest_WithoutChange_OmitsAttributeAndHasZeroLength()
    {
        byte[] request = StunMessage.BuildBindingRequest(transactionId: Tid);

        Assert.Equal(20, request.Length);
        Assert.Equal(0x00, request[2]);
        Assert.Equal(0x00, request[3]); // 消息长度 = 0
    }

    [Fact]
    public void BuildBindingRequest_WithoutTid_GeneratesRandom12Bytes()
    {
        byte[] first = StunMessage.BuildBindingRequest();
        byte[] second = StunMessage.BuildBindingRequest();

        Assert.Equal(20, first.Length);
        Assert.Equal(12, first[8..20].Length);
        Assert.NotEqual(first[8..20], second[8..20]); // 随机性(碰撞概率 2^-96,可忽略)
    }

    [Fact]
    public void BuildBindingRequest_WithWrongTidLength_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(
            () => StunMessage.BuildBindingRequest(transactionId: [0x01, 0x02]));
    }

    // -------------------------------------------------------------- Step 1(b) golden XOR 解码

    [Fact]
    public void Parse_GoldenVector_DecodesXorMappedTo192_0_2_1_32853()
    {
        StunMessage? message = StunMessage.Parse(GoldenDatagram(), Tid);

        Assert.NotNull(message);
        Assert.Equal(GoldenEndpoint, message.XorMapped);
        Assert.Null(message.Mapped);
        Assert.Null(message.Other);
        Assert.Null(message.Changed);
        Assert.Equal(Tid, message.TransactionId);
    }

    [Fact]
    public void Parse_MappedAddressAttribute_DecodesPlainEndpoint()
    {
        // MAPPED-ADDRESS(0x0001):保留 00 + 族 01 + 端口 0x8055(32853,明文)+ 192.0.2.1
        byte[] datagram = Response(Tid, (0x0001, [0x00, 0x01, 0x80, 0x55, 0xC0, 0x00, 0x02, 0x01]));

        StunMessage? message = StunMessage.Parse(datagram, Tid);

        Assert.NotNull(message);
        Assert.Equal(GoldenEndpoint, message.Mapped);
        Assert.Null(message.XorMapped);
    }

    [Fact]
    public void Parse_OtherAndChangedAttributes_DecodeBoth()
    {
        // OTHER-ADDRESS(0x8028)与 CHANGED-ADDRESS(0x0005)均为明文端点
        byte[] datagram = Response(
            Tid,
            (0x8028, [0x00, 0x01, 0xC3, 0x50, 0xC6, 0x33, 0x64, 0x09]), // 198.51.100.9:50000
            (0x0005, [0x00, 0x01, 0xC3, 0x52, 0xC6, 0x33, 0x64, 0x0B])); // 198.51.100.11:50002

        StunMessage? message = StunMessage.Parse(datagram, Tid);

        Assert.NotNull(message);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.9"), 50000), message.Other);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.11"), 50002), message.Changed);
    }

    [Fact]
    public void Parse_Ipv6XorMapped_XorsWithCookieThenTid()
    {
        var expected = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 3478);
        byte[] mask = [0x21, 0x12, 0xA4, 0x44, .. Tid]; // cookie ‖ tid
        byte[] address = IPAddress.Parse("2001:db8::1").GetAddressBytes();
        for (int i = 0; i < address.Length; i++)
        {
            address[i] ^= mask[i];
        }

        byte[] value = new byte[20];
        value[1] = 0x02; // IPv6 族
        int xoredPort = 3478 ^ 0x2112; // 端口密文按 16 位整体异或
        value[2] = (byte)(xoredPort >> 8);
        value[3] = (byte)xoredPort;
        address.CopyTo(value, 4);

        byte[] datagram = Response(Tid, (0x0020, value));

        StunMessage? message = StunMessage.Parse(datagram, Tid);

        Assert.NotNull(message);
        Assert.Equal(expected, message.XorMapped);
    }

    [Fact]
    public void Parse_UnknownAttributesAreSkipped_NotErrors()
    {
        byte[] datagram = Response(
            Tid,
            (0xABCD, [0x01, 0x02, 0x03, 0x04]), // 未知类型
            (0x8022, [0x00, 0x05, 0x68, 0x71]), // SOFTWARE
            (0x0020, [0x00, 0x01, 0xA1, 0x47, 0xE1, 0x12, 0xA6, 0x45])); // golden XOR-MAPPED

        StunMessage? message = StunMessage.Parse(datagram, Tid);

        Assert.NotNull(message);
        Assert.Equal(GoldenEndpoint, message.XorMapped);
    }

    // -------------------------------------------------------------- Step 1(c) 往返

    [Fact]
    public void BuildResponse_GoldenEndpointAndTid_ReproducesGoldenBytes()
    {
        byte[] encoded = StunMessage.BuildResponse(Tid, GoldenEndpoint);

        Assert.Equal(GoldenDatagram(), encoded);
    }

    [Fact]
    public void BuildResponse_ThenParse_RoundTripsEndpoint_Ipv4AndIpv6()
    {
        var ipv4 = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 50000);
        var ipv6 = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 3478);

        StunMessage? ipv4Message = StunMessage.Parse(StunMessage.BuildResponse(Tid, ipv4), Tid);
        StunMessage? ipv6Message = StunMessage.Parse(StunMessage.BuildResponse(Tid, ipv6), Tid);

        Assert.Equal(ipv4, ipv4Message?.XorMapped);
        Assert.Equal(ipv6, ipv6Message?.XorMapped);
    }

    // -------------------------------------------------------------- Step 1(d) 残缺包 → null

    [Fact]
    public void Parse_TidMismatch_ReturnsNull()
    {
        byte[] otherTid = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];

        Assert.Null(StunMessage.Parse(GoldenDatagram(), otherTid));
    }

    [Theory]
    [InlineData(15)] // 截在头内
    [InlineData(19)] // 差 1 字节头
    [InlineData(25)] // 截在属性值中间
    [InlineData(27)] // 截在 golden 属性尾部
    public void Parse_TruncatedDatagram_ReturnsNull(int length)
    {
        byte[] datagram = GoldenDatagram()[..length];

        Assert.Null(StunMessage.Parse(datagram, Tid));
    }

    [Fact]
    public void Parse_DeclaredLengthExceedsDatagram_ReturnsNull()
    {
        byte[] datagram = GoldenDatagram()[..30]; // 实际 30 字节,头声明消息长 12 → 需 32
        datagram[2] = 0x00;
        datagram[3] = 0x0C;

        Assert.Null(StunMessage.Parse(datagram, Tid));
    }

    [Fact]
    public void Parse_GarbledAttributeLength_ReturnsNull()
    {
        byte[] datagram = GoldenDatagram();
        datagram[22] = 0xFF; // 属性长度 0x0008 → 0x00FF,越界
        datagram[23] = 0xFF;

        Assert.Null(StunMessage.Parse(datagram, Tid));
    }

    [Fact]
    public void Parse_TrailingGarbageInsideDeclaredLength_ReturnsNull()
    {
        byte[] padded = new byte[34]; // 20 头 + 声明的 14 字节消息体
        GoldenDatagram().CopyTo(padded, 0);
        padded[2] = 0x00;
        padded[3] = 0x0E; // 声明 14:属性走完后剩 2 字节残渣,不足以构成属性头

        Assert.Null(StunMessage.Parse(padded, Tid));
    }

    [Fact]
    public void Parse_ShortOrNullDatagram_ReturnsNull()
    {
        Assert.Null(StunMessage.Parse([], Tid));
        Assert.Null(StunMessage.Parse(null!, Tid));
    }

    [Fact]
    public void Parse_WrongMagicCookie_ReturnsNull()
    {
        byte[] datagram = GoldenDatagram();
        datagram[7] = 0x45; // cookie 末字节改坏

        Assert.Null(StunMessage.Parse(datagram, Tid));
    }

    [Fact]
    public void Parse_WrongLengthExpectedTid_ReturnsNull()
    {
        Assert.Null(StunMessage.Parse(GoldenDatagram(), [0x01, 0x02]));
        Assert.Null(StunMessage.Parse(GoldenDatagram(), null!));
    }

    // -------------------------------------------------------------- 测试辅助

    /// <summary>手工编码 STUN 响应:头 + 若干 TLV 属性(4 字节对齐补零),作为实现的独立参照。</summary>
    private static byte[] Response(byte[] tid, params (ushort Type, byte[] Value)[] attributes)
    {
        int bodyLength = attributes.Sum(a => 4 + ((a.Value.Length + 3) & ~3));
        byte[] datagram = new byte[20 + bodyLength];
        datagram[0] = 0x01;
        datagram[1] = 0x01;
        datagram[2] = (byte)(bodyLength >> 8);
        datagram[3] = (byte)bodyLength;
        datagram[4] = 0x21;
        datagram[5] = 0x12;
        datagram[6] = 0xA4;
        datagram[7] = 0x44;
        tid.CopyTo(datagram, 8);

        int offset = 20;
        foreach ((ushort type, byte[] value) in attributes)
        {
            datagram[offset] = (byte)(type >> 8);
            datagram[offset + 1] = (byte)type;
            datagram[offset + 2] = (byte)(value.Length >> 8);
            datagram[offset + 3] = (byte)value.Length;
            value.CopyTo(datagram, offset + 4);
            offset += 4 + ((value.Length + 3) & ~3); // 值补零对齐
        }

        return datagram;
    }
}
