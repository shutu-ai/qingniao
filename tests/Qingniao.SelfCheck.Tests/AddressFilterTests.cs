using System.Net;
using Qingniao.SelfCheck.Checks;

namespace Qingniao.SelfCheck.Tests;

/// <summary>AddressFilter 全局 IPv6 地址过滤测试(纯函数,无 I/O)。</summary>
public class AddressFilterTests
{
    // -------------------------------------------------------------- brief 五例

    [Theory]
    [InlineData("::1", false)] // loopback
    [InlineData("fe80::1", false)] // link-local fe80::/10
    [InlineData("fd00::1", false)] // ULA fc00::/7
    [InlineData("::ffff:1.2.3.4", false)] // IPv4-mapped ::ffff:0:0/96
    [InlineData("240e:1234::1", true)] // 2000::/3(中国电信 240e::/20 在内)
    public void SelectGlobal_ClassifiesCorrectly(string ip, bool keep)
    {
        var result = AddressFilter.SelectGlobal([IPAddress.Parse(ip)]);

        AssertKeep(result, IPAddress.Parse(ip), keep);
    }

    // -------------------------------------------------------------- 补充边界

    [Theory]
    [InlineData("::", false)] // unspecified
    [InlineData("2001:db8::1", true)] // 文档前缀,仍在 2000::/3 内 → 保留
    [InlineData("2400:3200::1", true)] // 阿里 2400::/20,2000::/3 内 → 保留
    [InlineData("192.168.1.1", false)] // 非 IPv6 → 排除(过滤器仅收 IPv6)
    [InlineData("ff02::1", false)] // 组播,非 2000::/3 全局单播
    [InlineData("fe80::1%4", false)] // 带 scope id 的 link-local(Windows 常见形态)
    public void SelectGlobal_BoundaryCases(string ip, bool keep)
    {
        var result = AddressFilter.SelectGlobal([IPAddress.Parse(ip)]);

        AssertKeep(result, IPAddress.Parse(ip), keep);
    }

    [Fact]
    public void SelectGlobal_MixedList_KeepsGlobalOnlyInOrder()
    {
        var input = new[]
        {
            IPAddress.Parse("fe80::1"),
            IPAddress.Parse("240e:1234::1"),
            IPAddress.Parse("::1"),
            IPAddress.Parse("2400:3200::1"),
            IPAddress.Parse("192.168.1.1"),
            IPAddress.Parse("fd00::1"),
        };

        var result = AddressFilter.SelectGlobal(input);

        Assert.Equal(
        [
            IPAddress.Parse("240e:1234::1"),
            IPAddress.Parse("2400:3200::1"),
        ], result);
    }

    [Fact]
    public void SelectGlobal_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(AddressFilter.SelectGlobal([]));
    }

    [Fact]
    public void SelectGlobal_NullElements_AreSkipped()
    {
        var result = AddressFilter.SelectGlobal(
            [null!, IPAddress.Parse("fe80::1"), IPAddress.Parse("240e:1234::1")]);

        Assert.Equal([IPAddress.Parse("240e:1234::1")], result);
    }

    [Fact]
    public void SelectGlobal_DoesNotMutateInput()
    {
        var input = new[] { IPAddress.Parse("fe80::1"), IPAddress.Parse("240e:1234::1") };

        _ = AddressFilter.SelectGlobal(input);

        Assert.Equal(2, input.Length);
    }

    [Fact]
    public void SelectGlobal_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => AddressFilter.SelectGlobal(null!));
    }

    private static void AssertKeep(IReadOnlyList<IPAddress> result, IPAddress address, bool keep)
    {
        if (keep)
        {
            Assert.Equal([address], result);
        }
        else
        {
            Assert.Empty(result);
        }
    }
}
