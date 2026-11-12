using System.Net;
using System.Net.NetworkInformation;
using Makaretu.Dns;

namespace Qingniao.LinkManager;

/// <summary>
/// 通过 mDNS(DNS-SD)向局域网通告本节点:服务类型 <c>_qingniao._udp.local</c>,
/// 实例名 = 节点名,TXT 含 <c>node=&lt;nodeName&gt;</c>,SRV 端口 = meshPort。
/// </summary>
/// <remarks>
/// 实现基于 Makaretu.Dns.Multicast(以 0.27.0 API 为准);本类接口契约不依赖该库的具体形状。
/// 地址通告取 <see cref="PeerAddressFilter"/> 过滤后的本机地址,避免 loopback/链路本地等误报。
/// </remarks>
public sealed class MdnsAdvertiser : IAsyncDisposable
{
    /// <summary>服务类型(DNS-SD 的 "_service._udp" 形式);完整服务名为 _qingniao._udp.local。</summary>
    public const string ServiceType = "_qingniao._udp";

    private const string NodeTxtKey = "node";

    private readonly string _nodeName;
    private readonly MulticastService _mdns = new();
    private readonly ServiceDiscovery _discovery;
    private readonly ServiceProfile _profile;
    private bool _started;
    private bool _disposed;

    /// <summary>创建通告器:构造即生成服务档案(含过滤后的本机地址),网络动作推迟到 <see cref="StartAsync"/>。</summary>
    /// <param name="nodeName">节点名(= 服务实例名),调用方通常传 Environment.MachineName。</param>
    /// <param name="meshPort">Mesh 端口(如 MeshConfig.MeshPort,默认 13371)。</param>
    /// <exception cref="ArgumentException"><paramref name="nodeName"/> 为 null、空或空白。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="meshPort"/> 不在 [1, 65535]。</exception>
    public MdnsAdvertiser(string nodeName, int meshPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentOutOfRangeException.ThrowIfLessThan(meshPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(meshPort, 65535);

        _nodeName = nodeName;
        _discovery = new ServiceDiscovery(_mdns);
        _profile = CreateServiceProfile(nodeName, meshPort, MulticastService.GetIPAddresses());
    }

    /// <summary>开始通告:注册服务档案并启动 mDNS 应答。幂等:已在运行时直接返回。</summary>
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

        EnsureMulticastAvailable(NetworkInterface.GetAllNetworkInterfaces());

        try
        {
            _discovery.Advertise(_profile);
            _mdns.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"mDNS 通告启动失败(node={_nodeName}):{ex.Message}", ex);
        }

        _started = true;
        return Task.CompletedTask;
    }

    /// <summary>停止通告:发送 goodbye(Unadvertise)并停止 mDNS。幂等:未启动或已停止时直接返回。</summary>
    public Task StopAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
        {
            return Task.CompletedTask;
        }

        _started = false;
        TryUnadvertise();
        _mdns.Stop();
        return Task.CompletedTask;
    }

    /// <summary>停止通告并释放底层资源;未启动过也安全,可重复调用。</summary>
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

    /// <summary>构造服务档案:实例名=节点名、服务名 _qingniao._udp、SRV 端口=meshPort、TXT 含 node=&lt;nodeName&gt;、地址经 <see cref="PeerAddressFilter"/> 过滤。</summary>
    internal static ServiceProfile CreateServiceProfile(string nodeName, int meshPort, IEnumerable<IPAddress> addresses)
    {
        var profile = new ServiceProfile(
            new DomainName(nodeName),
            new DomainName(ServiceType),
            (ushort)meshPort,
            PeerAddressFilter.SelectAdvertisement(addresses));
        profile.AddProperty(NodeTxtKey, nodeName);
        return profile;
    }

    /// <summary>确认本机至少有一个 Up 状态且支持多播的网络接口;否则 mDNS 无法工作,抛出明确异常(而非挂起)。</summary>
    /// <exception cref="NotSupportedException">没有可用多播接口。</exception>
    internal static void EnsureMulticastAvailable(IEnumerable<NetworkInterface> interfaces)
    {
        bool hasMulticast = interfaces.Any(static n =>
            n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast);
        if (!hasMulticast)
        {
            throw new NotSupportedException("本机没有处于 Up 状态且支持多播(组播)的网络接口,mDNS 通告/发现不可用");
        }
    }

    /// <summary>尽力发送 goodbye(下线广播);网络已不可用时的失败不影响本地停止,刻意吞掉(见 remarks)。</summary>
    private void TryUnadvertise()
    {
        try
        {
            _discovery.Unadvertise(_profile);
        }
        catch (Exception)
        {
            // goodbye 是尽力而为:停止时网络/套接字可能已失效,此时静默放弃广播即可,本地停止不受影响。
        }
    }
}
