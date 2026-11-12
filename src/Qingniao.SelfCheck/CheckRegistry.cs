using Qingniao.Core;
using Qingniao.SelfCheck.Checks;

namespace Qingniao.SelfCheck;

/// <summary>自检注册表:按固定顺序组装全部检查项。</summary>
public static class CheckRegistry
{
    /// <summary>检查项顺序固定:ipv6, nat, gpu-encoder, vpn-reachability, bandwidth。</summary>
    /// <param name="config">
    /// 向 nat(STUN 服务器)/vpn-reachability(网段与 MeshPort)/bandwidth(端点)注入配置;
    /// null 时取默认(R16)。
    /// </param>
    public static IReadOnlyList<IEnvironmentCheck> CreateAll(MeshConfig? config = null) =>
    [
        new Ipv6Check(),
        new NatTypeCheck(servers: config?.StunServers),
        new GpuEncoderCheck(), // 注册表本地探测,不消费 config
        new VpnReachabilityCheck(() => config?.VpnCidrs ?? [], meshPort: config?.MeshPort ?? 13371),
        new BandwidthCheck(config?.BandwidthEndpoint),
    ];
}
