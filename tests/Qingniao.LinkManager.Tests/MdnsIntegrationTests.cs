using System.Net;
using System.Net.NetworkInformation;
using Makaretu.Dns;
using Qingniao.LinkManager;

namespace Qingniao.LinkManager.Tests;

/// <summary>MdnsAdvertiser 离线单元测试:服务档案形状、参数校验与生命周期健壮性(不启动网络)。</summary>
public sealed class MdnsAdvertiserTests
{
    /// <summary>含 loopback/链路本地/未指定/ULA 的候选集,用于钉死"档案只带可通告地址"。</summary>
    private static readonly IPAddress[] TestCandidates =
    [
        IPAddress.Parse("127.0.0.1"),
        IPAddress.Parse("169.254.1.1"),
        IPAddress.Parse("fe80::1"),
        IPAddress.Parse("0.0.0.0"),
        IPAddress.Parse("192.168.1.10"),
        IPAddress.Parse("fd00::1"),
    ];

    [Fact]
    public void CreateServiceProfile_InstanceAndFqdn_AreQingniaoUdpLocal()
    {
        var profile = MdnsAdvertiser.CreateServiceProfile("qntest-nodeA", 13371, TestCandidates);

        Assert.Equal("qntest-nodeA", profile.InstanceName.ToString());
        Assert.Equal("qntest-nodeA._qingniao._udp.local", profile.FullyQualifiedName.ToString());
    }

    [Fact]
    public void CreateServiceProfile_SrvRecord_CarriesInstanceNameAndMeshPort()
    {
        var profile = MdnsAdvertiser.CreateServiceProfile("qntest-nodeA", 13371, TestCandidates);

        var srv = Assert.Single(profile.Resources.OfType<SRVRecord>());
        Assert.Equal("qntest-nodeA._qingniao._udp.local", srv.Name.ToString());
        Assert.Equal(13371, (int)srv.Port);
    }

    [Fact]
    public void CreateServiceProfile_TxtRecord_CarriesNodeProperty()
    {
        var profile = MdnsAdvertiser.CreateServiceProfile("qntest-nodeA", 13371, TestCandidates);

        var txt = Assert.Single(profile.Resources.OfType<TXTRecord>());
        Assert.Contains("node=qntest-nodeA", txt.Strings);
    }

    [Fact]
    public void CreateServiceProfile_AddressRecords_ContainOnlyAdvertiseableCandidates()
    {
        var profile = MdnsAdvertiser.CreateServiceProfile("qntest-nodeA", 13371, TestCandidates);

        var addresses = profile.Resources.OfType<AddressRecord>().Select(r => r.Address).ToArray();

        var address = Assert.Single(addresses);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_InvalidNodeName_Throws(string? nodeName)
    {
        Assert.ThrowsAny<ArgumentException>(() => _ = new MdnsAdvertiser(nodeName!, 13371));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Ctor_PortOutOfRange_ThrowsArgumentOutOfRange(int meshPort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new MdnsAdvertiser("qntest-nodeA", meshPort));
    }

    [Fact]
    public async Task DisposeAsync_BeforeStart_IsSafe()
    {
        var advertiser = new MdnsAdvertiser("qntest-nodeA", 13371);

        await advertiser.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Twice_IsSafe()
    {
        var advertiser = new MdnsAdvertiser("qntest-nodeA", 13371);

        await advertiser.DisposeAsync();
        await advertiser.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_BeforeStart_IsSafeAndIdempotent()
    {
        var advertiser = new MdnsAdvertiser("qntest-nodeA", 13371);
        try
        {
            await advertiser.StopAsync();
            await advertiser.StopAsync();
        }
        finally
        {
            await advertiser.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var advertiser = new MdnsAdvertiser("qntest-nodeA", 13371);
        await advertiser.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => advertiser.StartAsync());
    }

    [Fact]
    public void EnsureMulticastAvailable_NoCapableInterface_ThrowsNotSupported()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => MdnsAdvertiser.EnsureMulticastAvailable(Array.Empty<NetworkInterface>()));

        Assert.Contains("多播", ex.Message);
    }
}

/// <summary>MdnsDiscovery 离线单元测试:生命周期健壮性(不启动网络)。</summary>
public sealed class MdnsDiscoveryTests
{
    [Fact]
    public void BrowseServiceName_IsBareServiceType_NoLocalSuffix()
    {
        // 回归钉死:QueryServiceInstances 自行追加 ".local",浏览名若带 .local
        // 会发出 "_qingniao._udp.local.local" 的查询而永远无应答(Live 测试曾因此失败)。
        Assert.Equal("_qingniao._udp", MdnsDiscovery.BrowseServiceName.ToString());
        Assert.EndsWith("_udp", MdnsDiscovery.BrowseServiceName.ToString());
    }

    [Fact]
    public void ServiceTypeFqdn_IsServiceTypeWithLocalSuffix()
    {
        Assert.Equal("_qingniao._udp.local", MdnsDiscovery.ServiceTypeFqdn);
    }

    [Fact]
    public async Task DisposeAsync_BeforeStart_IsSafe()
    {
        var discovery = new MdnsDiscovery();

        await discovery.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Twice_IsSafe()
    {
        var discovery = new MdnsDiscovery();

        await discovery.DisposeAsync();
        await discovery.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_BeforeStart_IsSafeAndIdempotent()
    {
        var discovery = new MdnsDiscovery();
        try
        {
            await discovery.StopAsync();
            await discovery.StopAsync();
        }
        finally
        {
            await discovery.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var discovery = new MdnsDiscovery();
        await discovery.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => discovery.StartAsync());
    }
}

/// <summary>mDNS 同机集成测试(Live):需要本机 UDP 5353 可用;环境受限时按 LIVE_DEFERRED 处理。</summary>
public sealed class MdnsLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task AdvertiserAndDiscovery_SameMachine_PeerFoundWithinFiveSeconds()
    {
        var localAddresses = GetLocalAddresses();
        var advertiser = new MdnsAdvertiser("qntest-nodeA", 13371);
        var discovery = new MdnsDiscovery();
        try
        {
            var found = new TaskCompletionSource<PeerDiscovered>(TaskCreationOptions.RunContinuationsAsynchronously);
            discovery.PeerFound += (_, peer) => found.TrySetResult(peer);

            await advertiser.StartAsync();
            await discovery.StartAsync();

            var winner = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.True(winner == found.Task, "5 秒内未收到 PeerFound(检查本机 UDP 5353 是否被防火墙/网络拦截)");
            var peer = await found.Task;

            Assert.Equal("qntest-nodeA", peer.NodeName);
            Assert.Equal(13371, peer.Port);
            Assert.Contains(peer.Address, localAddresses); // 地址属于本机地址集,而非硬编码 127.0.0.1
            Assert.False(IPAddress.IsLoopback(peer.Address));
        }
        finally
        {
            await discovery.DisposeAsync();
            await advertiser.DisposeAsync();
        }
    }

    /// <summary>收集本机全部地址(网卡单播地址 + 主机名解析),用于断言通告地址属于本机。</summary>
    private static HashSet<IPAddress> GetLocalAddresses()
    {
        var addresses = new HashSet<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                addresses.Add(unicast.Address);
            }
        }

        foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
        {
            addresses.Add(address);
        }

        return addresses;
    }
}
