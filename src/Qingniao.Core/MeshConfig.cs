namespace Qingniao.Core;

/// <summary>Mesh 网络配置(不可变,init-only)。</summary>
public sealed record MeshConfig
{
    public int MeshPort { get; init; } = 13371;

    public int AdminPort { get; init; } = 13372;

    public string YggdrasilExePath { get; init; } = "tools/yggdrasil/yggdrasil.exe";

    public IReadOnlyList<string> VpnCidrs { get; init; } = [];

    public IReadOnlyList<string> StunServers { get; init; } =
        ["stun1.l.google.com:19302", "stun.cloudflare.com:3478"];

    public string? BandwidthEndpoint { get; init; }
}
