using System.Net.Sockets;
using Qingniao.Core;
using Qingniao.LinkManager;
using Qingniao.Mesh;

namespace Qingniao.MeshDaemon;

/// <summary>
/// Phase 1 守护进程组装核心:托管 yggdrasil、mDNS 通告与发现的启停,把两个发现源
/// (mDNS 通告、VPN 网段 TCP 扫描)的候选统一交给 <see cref="ILinkOrchestrator"/> 幂等注入。
/// </summary>
/// <remarks>
/// 依赖均为 R15 接口抽象,测试以假件注入;Program.cs 组装真实实现。
/// mDNS 红线(T12):Advertiser/Discovery 一旦 Stop 再 Start 会静默失效(库清空事件处理器),
/// 因此 <see cref="StartAsync"/> 只允许调用一次(重复调用抛 <see cref="InvalidOperationException"/>)。
/// PeerFound 在 mDNS 接收线程上同步回调,处理器不得阻塞/做网络 I/O —— 内部分发到后台任务处理。
/// </remarks>
public sealed class MeshRuntime
{
    private readonly MeshConfig _config;
    private readonly IYggdrasilHost _host;
    private readonly IMdnsAdvertiser _advertiser;
    private readonly IMdnsDiscovery _discovery;
    private readonly IVpnCidrScanner _scanner;
    private readonly ILinkOrchestrator _orchestrator;
    private bool _started;
    private bool _stopped;

    /// <summary>注入全部协作方;config 提供 VPN 网段与 Mesh 端口。</summary>
    public MeshRuntime(
        MeshConfig config,
        IYggdrasilHost host,
        IMdnsAdvertiser advertiser,
        IMdnsDiscovery discovery,
        IVpnCidrScanner scanner,
        ILinkOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(advertiser);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(orchestrator);
        _config = config;
        _host = host;
        _advertiser = advertiser;
        _discovery = discovery;
        _scanner = scanner;
        _orchestrator = orchestrator;
    }

    /// <summary>
    /// 启动宿主:先订阅 PeerFound(启动期间的发现不丢),再依次起 yggdrasil → mDNS 通告 → mDNS 发现。
    /// 只允许调用一次:mDNS 实例不可 Stop 后再 Start(见 remarks)。
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        if (_started)
        {
            throw new InvalidOperationException("MeshRuntime 已启动,不可重复 StartAsync(mDNS 实例不可重启)");
        }

        _started = true;
        _discovery.PeerFound += OnPeerFound;
        await _host.StartAsync(ct).ConfigureAwait(false);
        await _advertiser.StartAsync(ct).ConfigureAwait(false);
        await _discovery.StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 单轮 VPN 网段扫描:<c>ScanAsync(VpnCidrs, MeshPort)</c>,仅 IsOpen 的主机以 Source="vpn"
    /// 交给编排器注入。取消语义遵循扫描器契约(T11):取消使流以 OperationCanceledException
    /// 终止,此处不吞,向调用方传播。单候选注入失败(admin 暂不可达等)不中止本轮。
    /// </summary>
    public async Task RunVpnScanCycleAsync(CancellationToken ct)
    {
        await foreach (ProbeResult result in _scanner
            .ScanAsync(_config.VpnCidrs, _config.MeshPort, ct)
            .ConfigureAwait(false))
        {
            if (!result.IsOpen)
            {
                continue;
            }

            try
            {
                await _orchestrator.TryAddPeerAsync(
                    new PeerCandidate("vpn", result.Address, _config.MeshPort), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // T11:取消严格传播,不吞
            }
            catch (Exception ex)
            {
                // 单个候选注入失败(如 admin 短暂不可达)记录后继续本轮剩余候选;
                // 编排器自身的业务性失败(重复候选等)返回 false 不抛,不经过这里。
                Console.Error.WriteLine(
                    $"[qng] vpn 候选注入失败 {result.Address}:{_config.MeshPort}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 停止宿主:先退订 PeerFound,再停发现 → 停通告(发 goodbye)→ 停 yggdrasil。
    /// 幂等;未启动过时直接返回。
    /// </summary>
    public async Task StopAsync()
    {
        if (!_started || _stopped)
        {
            return;
        }

        _stopped = true;
        _discovery.PeerFound -= OnPeerFound;
        await _discovery.StopAsync().ConfigureAwait(false);
        await _advertiser.StopAsync().ConfigureAwait(false);
        await _host.StopAsync().ConfigureAwait(false);
    }

    /// <summary>PeerFound 同步回调:只做分发,绝不内联处理(mDNS 接收线程不得被阻塞)。</summary>
    private void OnPeerFound(object? sender, PeerDiscovered e) => _ = HandlePeerFoundAsync(e);

    /// <summary>后台处理 mDNS 候选:IPv6 在接线处丢弃(R17,Phase 1 全 IPv4),v4 交编排器注入。</summary>
    private async Task HandlePeerFoundAsync(PeerDiscovered e)
    {
        if (e.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            Console.WriteLine($"[qng] 跳过非 IPv4 mDNS 候选 {e.NodeName} ({e.Address})");
            return;
        }

        try
        {
            await _orchestrator.TryAddPeerAsync(new PeerCandidate("mdns", e.Address, e.Port))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止竞态:后台任务随宿主一起结束,静默退出
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[qng] mdns 候选注入失败 {e.NodeName} ({e.Address}): {ex.Message}");
        }
    }
}
