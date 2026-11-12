using System.Net;
using System.Net.Sockets;
using Qingniao.LinkManager;

namespace Qingniao.LinkManager.Tests;

/// <summary>PeerAddressFilter 纯逻辑测试:通告候选地址的排除/保留、顺序保持与确定性。</summary>
public sealed class PeerAddressFilterTests
{
    [Theory]
    [InlineData("127.0.0.1")]        // loopback(brief)
    [InlineData("127.255.255.255")]  // loopback 127/8 上界
    [InlineData("169.254.1.1")]      // IPv4 链路本地(brief)
    [InlineData("169.254.169.254")]  // 链路本地(云元数据端点常见地址)
    [InlineData("0.0.0.0")]          // unspecified(brief)
    [InlineData("::1")]              // IPv6 loopback
    [InlineData("::")]               // IPv6 unspecified
    [InlineData("fe80::1")]          // IPv6 链路本地(brief)
    [InlineData("febf:ffff::1")]     // fe80::/10 上界
    [InlineData("fd00::1")]          // ULA fc00::/7 — brief 未列入保留,按 brief 字面排除(设计裁定钉死)
    [InlineData("ff02::1")]          // IPv6 组播,非全局单播
    [InlineData("4000::1")]          // 2000::/3 之外,非全局 v6
    public void SelectAdvertisement_ExcludedAddresses_RemovesAll(string text)
    {
        var result = PeerAddressFilter.SelectAdvertisement([IPAddress.Parse(text)]);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("192.168.1.10")]  // 私有 v4(brief)
    [InlineData("10.8.0.5")]      // 私有 v4(brief)
    [InlineData("172.16.0.5")]    // 私有 v4(172.16/12)
    [InlineData("8.8.8.8")]       // 公网 v4
    [InlineData("240e::1")]       // 全局 v6(brief)
    [InlineData("2000::1")]       // 全局 v6 2000::/3 下界
    [InlineData("3fff:ffff::1")]  // 全局 v6 2000::/3 上界
    public void SelectAdvertisement_KeptAddresses_KeepsAll(string text)
    {
        var result = PeerAddressFilter.SelectAdvertisement([IPAddress.Parse(text)]);

        var address = Assert.Single(result);
        Assert.Equal(IPAddress.Parse(text), address);
    }

    [Fact]
    public void SelectAdvertisement_Ipv4MappedAddress_TreatedAsIpv4AndKept()
    {
        var mapped = IPAddress.Parse("::ffff:192.168.1.10");

        var result = PeerAddressFilter.SelectAdvertisement([mapped]);

        var address = Assert.Single(result);
        Assert.Equal(AddressFamily.InterNetwork, address.AddressFamily);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), address);
    }

    [Fact]
    public void SelectAdvertisement_MixedInput_PreservesOrderAndFilters()
    {
        var input = new[]
        {
            IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("192.168.1.10"),
            IPAddress.Parse("fe80::1"),
            IPAddress.Parse("10.8.0.5"),
            IPAddress.Parse("0.0.0.0"),
            IPAddress.Parse("240e::1"),
        };

        var result = PeerAddressFilter.SelectAdvertisement(input);

        Assert.Equal(
            new[]
            {
                IPAddress.Parse("192.168.1.10"),
                IPAddress.Parse("10.8.0.5"),
                IPAddress.Parse("240e::1"),
            },
            result);
    }

    [Fact]
    public void SelectAdvertisement_NullInput_ReturnsEmpty()
    {
        Assert.Empty(PeerAddressFilter.SelectAdvertisement(null!));
    }

    [Fact]
    public void SelectAdvertisement_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(PeerAddressFilter.SelectAdvertisement([]));
    }

    [Fact]
    public void SelectAdvertisement_RepeatedCalls_AreDeterministic()
    {
        var input = new[]
        {
            IPAddress.Parse("169.254.1.1"),
            IPAddress.Parse("172.16.0.5"),
            IPAddress.Parse("fd00::1"),
            IPAddress.Parse("2001:db8::10"),
        };

        var first = PeerAddressFilter.SelectAdvertisement(input);
        var second = PeerAddressFilter.SelectAdvertisement(input);

        Assert.Equal(first, second);
    }
}
