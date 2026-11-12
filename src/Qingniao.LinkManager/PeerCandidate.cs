using System.Net;

namespace Qingniao.LinkManager;

/// <summary>待注入的 peer 候选:发现来源(Source,如 "mdns"|"vpn",由调用方提供)与对端地址及其 mesh 端口。</summary>
public sealed record PeerCandidate(string Source, IPAddress Address, int Port);
