using System.Collections.Concurrent;
using System.Text.Json;
using Qingniao.Mesh;

namespace Qingniao.LinkManager;

/// <summary>
/// peer 注入编排的最小抽象(R15,Task 14):仅暴露 MeshRuntime/Program 消费成员——
/// 幂等注入与 PeerAdded 事件。admin I/O 由实现内部持有,不进抽象。
/// </summary>
public interface ILinkOrchestrator
{
    /// <summary>注入一个 peer 候选(幂等:重复候选零 admin 请求,返回 false)。</summary>
    Task<bool> TryAddPeerAsync(PeerCandidate candidate, CancellationToken ct = default);

    /// <summary>候选注入成功(addPeer 成功并记入链路表)时触发。</summary>
    event EventHandler<LinkInfo>? PeerAdded;
}

/// <summary>
/// 幂等 peer 注入编排:查内存表去重 → getPeers 响应全树扫描去重 → addPeer,
/// 成功才记入不可变链路状态表并触发 PeerAdded;失败原因暂存 <see cref="LastError"/> 留作调试事件(本阶段无日志框架)。
/// 并发说明:单调用方语义精确;mDNS/VPN 多调用方并发时,"查表 → getPeers → addPeer" 序列为尽力而为,
/// 以内存表条目为准,不额外加锁串行化。
/// </summary>
public sealed class LinkOrchestrator : ILinkOrchestrator
{
    private readonly AdminSocketClient _admin;
    private readonly ConcurrentDictionary<string, LinkInfo> _peers = new();

    public LinkOrchestrator(AdminSocketClient admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        _admin = admin; // 调用方持有并负责释放;每次 SendAsync 自建连接,实例可反复复用
    }

    /// <summary>
    /// 最近一次 addPeer 失败的 admin 错误文本(成功后清空;提前短路如"已在表中"不算失败)。
    /// brief 接口块之外的最小补充,替代日志框架留调试事件。
    /// </summary>
    public string? LastError { get; private set; }

    public event EventHandler<LinkInfo>? PeerAdded;

    /// <summary>
    /// 幂等注入一个 peer 候选:内存表已含 endpoint → false 且零请求;
    /// getPeers 响应中任一字符串值含 "ip:port" → false 且零 addPeer;
    /// 否则 addPeer,成功 → 记入 LinkInfo、触发 PeerAdded、返回 true;失败 → false 并暂存 Error。
    /// </summary>
    public async Task<bool> TryAddPeerAsync(PeerCandidate candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(candidate.Address);
        var endpoint = $"tcp://{candidate.Address}:{candidate.Port}";

        if (_peers.ContainsKey(endpoint))
            return false; // 幂等 1:已注入,零 admin 请求

        var peers = await _admin.SendAsync(AdminRequest.GetPeers(), ct).ConfigureAwait(false);
        // 幂等 2:yggdrasil 已有该 peer(按字符串值匹配 "ip:port",不绑定其字段名)。
        // getPeers 失败时 Payload 为 Undefined → 视为未包含,继续 addPeer,由其失败兜底。
        if (ContainsString(peers.Payload, $"{candidate.Address}:{candidate.Port}"))
            return false;

        var added = await _admin.SendAsync(AdminRequest.AddPeer(endpoint), ct).ConfigureAwait(false);
        if (!added.Success)
        {
            LastError = added.Error;
            return false;
        }

        LastError = null;
        var link = new LinkInfo(endpoint, candidate.Source, DateTimeOffset.Now);
        _peers[endpoint] = link;
        PeerAdded?.Invoke(this, link);
        return true;
    }

    /// <summary>不可变快照:每次返回按加入时间排序的新数组,后续新增不影响已取出的快照。</summary>
    public IReadOnlyList<LinkInfo> Snapshot() =>
        _peers.Values.OrderBy(link => link.AddedAt).ToArray();

    /// <summary>递归遍历 JSON 树(对象/数组任意嵌套),任一字符串值含 needle 即真;属性名不参与匹配。</summary>
    private static bool ContainsString(JsonElement element, string needle)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString()?.Contains(needle, StringComparison.Ordinal) == true;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ContainsString(property.Value, needle))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsString(item, needle))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }
}
