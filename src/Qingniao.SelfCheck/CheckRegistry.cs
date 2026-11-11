using Qingniao.Core;
using Qingniao.SelfCheck.Checks;

namespace Qingniao.SelfCheck;

/// <summary>自检注册表:按固定顺序组装全部检查项。</summary>
public static class CheckRegistry
{
    /// <summary>检查项顺序固定:ipv6, nat, gpu-encoder, vpn-reachability, bandwidth。</summary>
    /// <param name="config">
    /// 向 nat(STUN 服务器)/vpn-reachability(网段)/bandwidth(端点)注入配置;null 时取默认。
    /// </param>
    public static IReadOnlyList<IEnvironmentCheck> CreateAll(MeshConfig? config = null) =>
    [
        new Ipv6Check(),
        new NatTypeCheck(servers: config?.StunServers),
        new UnknownCheck("gpu-encoder"),
        new UnknownCheck("vpn-reachability"),
        new UnknownCheck("bandwidth"),
    ];
}

/// <summary>占位检查:返回 Unknown("尚未实现")。Task 8/9/10/11/14 以真实实现逐项替换。</summary>
internal sealed class UnknownCheck(string name) : IEnvironmentCheck
{
    public string Name => name;

    public Task<CheckResult> RunAsync(CancellationToken ct) =>
        Task.FromResult(new CheckResult(name, CheckStatus.Unknown, "尚未实现"));
}
