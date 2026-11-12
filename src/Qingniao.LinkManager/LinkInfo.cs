namespace Qingniao.LinkManager;

/// <summary>一条已注入的链路:Endpoint = "tcp://ip:port",Source 为发现来源,AddedAt 为本机记录时间。</summary>
public sealed record LinkInfo(string Endpoint, string Source, DateTimeOffset AddedAt);
