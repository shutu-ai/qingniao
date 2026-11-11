using System.Net;
using Qingniao.LinkManager;

namespace Qingniao.LinkManager.Tests;

/// <summary>CidrRange 纯逻辑测试:IPv4 CIDR 解析、网络/广播地址排除与参数校验。</summary>
public class CidrRangeTests
{
    [Fact]
    public void EnumerateHosts_24Bit_Yields254HostsWithoutNetworkAndBroadcast()
    {
        var hosts = CidrRange.EnumerateHosts("192.168.1.0/24").ToArray();

        Assert.Equal(254, hosts.Length);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.254"), hosts[^1]);
    }

    [Fact]
    public void EnumerateHosts_16Bit_Yields65534Hosts()
    {
        var count = CidrRange.EnumerateHosts("172.16.0.0/16").Count();

        Assert.Equal(65534, count);
    }

    [Fact]
    public void EnumerateHosts_15Bit_ThrowsArgumentOutOfRangeWith65536Message()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => CidrRange.EnumerateHosts("10.0.0.0/15").GetEnumerator().MoveNext());

        Assert.Equal("cidr", ex.ParamName);
        Assert.Contains("65536", ex.Message);
    }

    [Fact]
    public void EnumerateHosts_IpV6Input_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => CidrRange.EnumerateHosts("::1/128").GetEnumerator().MoveNext());
        Assert.Throws<NotSupportedException>(() => CidrRange.EnumerateHosts("2001:db8::/64").GetEnumerator().MoveNext());
    }

    [Fact]
    public void EnumerateHosts_32Bit_YieldsSingleHostItself()
    {
        var hosts = CidrRange.EnumerateHosts("127.0.0.1/32").ToArray();

        var expected = new[] { IPAddress.Parse("127.0.0.1") };
        Assert.Equal(expected, hosts);
    }

    [Fact]
    public void EnumerateHosts_31Bit_YieldsBothAddressesPerRfc3021()
    {
        var hosts = CidrRange.EnumerateHosts("10.0.0.0/31").ToArray();

        var expected = new[] { IPAddress.Parse("10.0.0.0"), IPAddress.Parse("10.0.0.1") };
        Assert.Equal(expected, hosts);
    }

    [Fact]
    public void EnumerateHosts_MissingSlash_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => CidrRange.EnumerateHosts("192.168.1.0").GetEnumerator().MoveNext());
        Assert.Throws<ArgumentException>(() => CidrRange.EnumerateHosts("").GetEnumerator().MoveNext());
        Assert.Throws<ArgumentException>(() => CidrRange.EnumerateHosts("192.168.1.0/24/8").GetEnumerator().MoveNext());
    }

    [Fact]
    public void EnumerateHosts_MalformedIp_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => CidrRange.EnumerateHosts("999.1.1.0/24").GetEnumerator().MoveNext());
        Assert.Throws<FormatException>(() => CidrRange.EnumerateHosts("abc/24").GetEnumerator().MoveNext());
    }

    [Fact]
    public void EnumerateHosts_PrefixOutOfRange_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => CidrRange.EnumerateHosts("10.0.0.0/33").GetEnumerator().MoveNext());
        Assert.Throws<ArgumentException>(() => CidrRange.EnumerateHosts("10.0.0.0/-1").GetEnumerator().MoveNext());
        Assert.Throws<ArgumentException>(() => CidrRange.EnumerateHosts("10.0.0.0/abc").GetEnumerator().MoveNext());
    }

    [Fact]
    public void EnumerateHosts_NullInput_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => CidrRange.EnumerateHosts(null!).GetEnumerator().MoveNext());
    }

    [Fact]
    public void EnumerateHosts_16BitRange_IsLazy_YieldsFirstHostBeforeFullEnumeration()
    {
        // /16 共 65534 项;枚举器应为惰性:取首个主机地址不需要生成整个序列。
        using var enumerator = CidrRange.EnumerateHosts("172.16.0.0/16").GetEnumerator();

        Assert.True(enumerator.MoveNext());
        Assert.Equal(IPAddress.Parse("172.16.0.1"), enumerator.Current);
    }
}
