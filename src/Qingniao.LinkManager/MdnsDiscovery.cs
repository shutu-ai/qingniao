using System.Net;
using System.Net.NetworkInformation;
using Makaretu.Dns;

namespace Qingniao.LinkManager;

/// <summary>
/// mDNS 发现的最小抽象(R15,Task 14):仅暴露 MeshRuntime 组装所需成员——PeerFound 事件与启停。
/// 注意:实现一 Stop 再 Start 会静默失效(库清空处理器),调用方须 Start 一次、Stop 一次。
/// </summary>
public interface IMdnsDiscovery
{
    /// <summary>发现到节点时触发;事件在 mDNS 接收线程上同步回调,处理器不得阻塞。</summary>
    event EventHandler<PeerDiscovered>? PeerFound;

    /// <summary>开始发现并浏览 _qingniao._udp.local 实例。幂等。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>停止发现并清空发现状态。幂等。</summary>
    Task StopAsync();
}

/// <summary>mDNS 发现到的节点(通告地址已通过 <see cref="PeerAddressFilter"/> 过滤)。</summary>
/// <param name="NodeName">节点名(= 通告方服务实例名)。</param>
/// <param name="Address">节点的可达地址(IPv4/IPv6)。</param>
/// <param name="Port">节点的 Mesh 端口。</param>
public sealed record PeerDiscovered(string NodeName, IPAddress Address, int Port);

/// <summary>
/// 通过 mDNS(DNS-SD)发现局域网内的 qingniao 节点并解析其 Mesh 端口与地址。
/// </summary>
/// <remarks>
/// 流程:QueryServiceInstances 浏览 <c>_qingniao._udp.local</c> 实例(PTR)→ 对实例补查 SRV
/// (得到目标主机名与端口)→ 对目标主机补查 A/AAAA(得到地址)。地址经
/// <see cref="PeerAddressFilter"/> 过滤后触发 <see cref="PeerFound"/>。
/// 重复通告不去重(同一节点可能多次触发,由上层编排);TTL 过期与刷新不在本期范围。
/// </remarks>
public sealed class MdnsDiscovery : IMdnsDiscovery, IAsyncDisposable
{
    /// <summary>本服务类型在 local 域下的完整服务名。</summary>
    public const string ServiceTypeFqdn = MdnsAdvertiser.ServiceType + ".local";

    /// <summary>
    /// PTR 浏览用的服务名:不带 .local 后缀 —— <c>QueryServiceInstances</c> 会自行追加,
    /// 若这里带上 .local 会发出 <c>_qingniao._udp.local.local</c> 而永远无应答。
    /// </summary>
    internal static readonly DomainName BrowseServiceName = new(MdnsAdvertiser.ServiceType);

    private readonly MulticastService _mdns = new();
    private readonly ServiceDiscovery _discovery;
    private readonly object _gate = new();
    private readonly HashSet<DomainName> _srvQueried = new();                               // 已补查 SRV 的实例名
    private readonly Dictionary<DomainName, (string NodeName, int Port)> _hosts = new();    // SRV 目标主机 → 节点/端口
    private bool _started;
    private bool _disposed;

    /// <summary>创建发现器;网络动作推迟到 <see cref="StartAsync"/>。</summary>
    public MdnsDiscovery()
    {
        _discovery = new ServiceDiscovery(_mdns);
    }

    /// <summary>发现到节点时触发。同一节点的重复通告会重复触发,事件在 mDNS 接收线程上同步回调。</summary>
    public event EventHandler<PeerDiscovered>? PeerFound;

    /// <summary>开始发现:订阅实例发现事件、启动 mDNS 并浏览 _qingniao._udp.local 实例。幂等。</summary>
    /// <exception cref="ObjectDisposedException">实例已释放。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> 已取消。</exception>
    /// <exception cref="NotSupportedException">本机没有 Up 状态且支持多播的网络接口(mDNS 不可用),明确抛出而非挂起。</exception>
    /// <exception cref="InvalidOperationException">mDNS 启动失败(如 5353 不可用),含内部异常。</exception>
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (_started)
        {
            return Task.CompletedTask;
        }

        MdnsAdvertiser.EnsureMulticastAvailable(NetworkInterface.GetAllNetworkInterfaces());

        _mdns.AnswerReceived += OnAnswerReceived;
        _discovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
        _mdns.NetworkInterfaceDiscovered += OnNetworkInterfaceDiscovered;

        try
        {
            _mdns.Start();
        }
        catch (Exception ex)
        {
            Unsubscribe();
            throw new InvalidOperationException($"mDNS 发现启动失败:{ex.Message}", ex);
        }

        _started = true;
        return Task.CompletedTask;
    }

    /// <summary>停止发现并清空发现状态。幂等:未启动或已停止时直接返回。</summary>
    public Task StopAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            return Task.CompletedTask;
        }

        _started = false;
        Unsubscribe();
        lock (_gate)
        {
            _srvQueried.Clear();
            _hosts.Clear();
        }

        _mdns.Stop(); // 库语义:Stop 会清空 MulticastService 的全部事件处理器
        return Task.CompletedTask;
    }

    /// <summary>停止发现并释放底层资源;未启动过也安全,可重复调用。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false); // 先停止再置标志:StopAsync 在未释放状态下属正常路径
        _disposed = true;
        _discovery.Dispose();
        _mdns.Dispose();
    }

    /// <summary>网卡发现(含启动后首次)即重新浏览服务实例,保证不会因启动时序漏发查询。</summary>
    private void OnNetworkInterfaceDiscovered(object? sender, NetworkInterfaceEventArgs e)
    {
        try
        {
            // 传不带 .local 的服务类型:QueryServiceInstances 自行追加 .local(见 BrowseServiceName)。
            _discovery.QueryServiceInstances(BrowseServiceName);
        }
        catch (ObjectDisposedException)
        {
            // 停止竞态:底层已释放,放弃本次浏览。
        }
    }

    /// <summary>发现到 _qingniao._udp.local 的实例后补查 SRV,取得目标主机名与 Mesh 端口。</summary>
    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        if (!IsQingniaoInstance(e.ServiceInstanceName))
        {
            return;
        }

        bool sendQuery;
        lock (_gate)
        {
            sendQuery = _srvQueried.Add(e.ServiceInstanceName);
        }

        if (sendQuery)
        {
            try
            {
                _mdns.SendQuery(e.ServiceInstanceName, DnsClass.IN, DnsType.SRV);
            }
            catch (ObjectDisposedException)
            {
                // 停止竞态:已停止,不再补查。
            }
        }
    }

    /// <summary>处理 mDNS 响应:SRV 记录登记目标主机并触发 A/AAAA 补查;地址记录命中已知主机后触发 PeerFound。</summary>
    private void OnAnswerReceived(object? sender, MessageEventArgs e)
    {
        Message message = e.Message;
        if (message.IsQuery)
        {
            return; // 只关心响应
        }

        IEnumerable<ResourceRecord> records = message.Answers.Concat(message.AdditionalRecords);

        foreach (SRVRecord srv in records.OfType<SRVRecord>())
        {
            if (!IsQingniaoInstance(srv.Name))
            {
                continue;
            }

            string nodeName = srv.Name.Labels.First();
            bool queryAddress;
            lock (_gate)
            {
                queryAddress = _hosts.TryAdd(srv.Target, (nodeName, srv.Port));
            }

            if (queryAddress)
            {
                try
                {
                    _mdns.SendQuery(srv.Target, DnsClass.IN, DnsType.A);
                    _mdns.SendQuery(srv.Target, DnsClass.IN, DnsType.AAAA);
                }
                catch (ObjectDisposedException)
                {
                    // 停止竞态:已停止,不再补查。
                }
            }
        }

        foreach (AddressRecord record in records.OfType<AddressRecord>())
        {
            (string NodeName, int Port) host;
            lock (_gate)
            {
                if (!_hosts.TryGetValue(record.Name, out host))
                {
                    continue; // 只处理本服务登记过的主机
                }
            }

            if (PeerAddressFilter.SelectAdvertisement([record.Address]) is [var address])
            {
                PeerFound?.Invoke(this, new PeerDiscovered(host.NodeName, address, host.Port));
            }
        }
    }

    /// <summary>实例名是否属于本服务(…_qingniao._udp.local 的子域)。</summary>
    private static bool IsQingniaoInstance(DomainName instanceName)
    {
        return instanceName.BelongsTo(new DomainName(ServiceTypeFqdn));
    }

    /// <summary>退订全部事件处理器(幂等)。</summary>
    private void Unsubscribe()
    {
        _mdns.AnswerReceived -= OnAnswerReceived;
        _discovery.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;
        _mdns.NetworkInterfaceDiscovered -= OnNetworkInterfaceDiscovered;
    }
}
