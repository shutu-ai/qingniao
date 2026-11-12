using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Qingniao.LinkManager;
using Qingniao.Mesh;

namespace Qingniao.LinkManager.Tests;

/// <summary>
/// LinkOrchestrator 幂等注入测试:对进程内真实 TcpListener 假 admin 服务器(真实 socket,不 mock 被测代码;
/// 沿用 AdminSocketClientTests 的假服务器模式,并按命令计数以钉死幂等的"零请求"语义)。
/// 对应 Review Focus #5 场景 (a)-(e)。
/// </summary>
public sealed class LinkOrchestratorTests
{
    [Fact]
    public async Task TryAddPeerAsync_FirstAdd_SucceedsAndRecordsSnapshotAndFiresPeerAdded()
    {
        // (a) 首次添加成功:getPeers 空 peers → addPeer success
        using var server = FakeAdminServer.Start(
            "{\"status\":\"success\",\"response\":{\"peers\":[]}}",
            "{\"status\":\"success\",\"response\":{}}");
        using var admin = new AdminSocketClient(port: server.Port, timeoutMs: 300);
        var orchestrator = new LinkOrchestrator(admin);
        var fired = new List<LinkInfo>();
        orchestrator.PeerAdded += (_, link) => fired.Add(link);

        var added = await orchestrator.TryAddPeerAsync(
            new PeerCandidate("mdns", IPAddress.Parse("10.0.0.5"), 13371));

        Assert.True(added);
        var snapshot = orchestrator.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("tcp://10.0.0.5:13371", snapshot[0].Endpoint);
        Assert.Equal("mdns", snapshot[0].Source);
        Assert.Single(fired);
        Assert.Equal("tcp://10.0.0.5:13371", fired[0].Endpoint);
        Assert.Equal(1, server.CountFor("getPeers"));
        Assert.Equal(1, server.CountFor("addPeer"));
        // addPeer 的 uri 必须是精确的 candidate ip:port
        using var lastRequest = JsonDocument.Parse(server.LastRequestLine!);
        Assert.Equal("addPeer", lastRequest.RootElement.GetProperty("request").GetString());
        Assert.Equal("tcp://10.0.0.5:13371", lastRequest.RootElement.GetProperty("uri").GetString());
    }

    [Fact]
    public async Task TryAddPeerAsync_SameCandidateTwice_SecondReturnsFalseWithZeroExtraRequests()
    {
        // (b) 重复添加同一 candidate:返回 false,对假 admin 的请求计数不变(幂等)
        // 后两条脚本仅供"幂等失效时误消耗"用:一旦被消耗,下方计数断言即失败。
        using var server = FakeAdminServer.Start(
            "{\"status\":\"success\",\"response\":{\"peers\":[]}}",
            "{\"status\":\"success\",\"response\":{}}",
            "{\"status\":\"success\",\"response\":{\"peers\":[]}}",
            "{\"status\":\"success\",\"response\":{}}");
        using var admin = new AdminSocketClient(port: server.Port, timeoutMs: 300);
        var orchestrator = new LinkOrchestrator(admin);
        var fired = 0;
        orchestrator.PeerAdded += (_, _) => fired++;
        var candidate = new PeerCandidate("vpn", IPAddress.Parse("10.0.0.5"), 13371);

        Assert.True(await orchestrator.TryAddPeerAsync(candidate));
        var again = await orchestrator.TryAddPeerAsync(candidate);

        Assert.False(again);
        Assert.Equal(2, server.TotalRequests); // 首次 getPeers + addPeer,第二次零请求
        Assert.Equal(1, server.CountFor("getPeers"));
        Assert.Equal(1, server.CountFor("addPeer"));
        Assert.Single(orchestrator.Snapshot());
        Assert.Equal(1, fired); // PeerAdded 只在首次成功时触发一次
    }

    [Fact]
    public async Task TryAddPeerAsync_GetPeersResponseContainsEndpoint_ZeroAddPeerRequests()
    {
        // (c) getPeers 已含该 peer:返回 false 且零 addPeer 请求(不绑定字段名:匹配的是字符串值中的 "ip:port")
        using var server = FakeAdminServer.Start(
            "{\"status\":\"success\",\"response\":{\"peers\":[{\"endpoint\":\"tcp://10.0.0.5:13371\",\"up\":true}]}}",
            "{\"status\":\"success\",\"response\":{}}"); // 仅误消耗时用
        using var admin = new AdminSocketClient(port: server.Port, timeoutMs: 300);
        var orchestrator = new LinkOrchestrator(admin);

        var added = await orchestrator.TryAddPeerAsync(
            new PeerCandidate("mdns", IPAddress.Parse("10.0.0.5"), 13371));

        Assert.False(added);
        Assert.Equal(1, server.CountFor("getPeers"));
        Assert.Equal(0, server.CountFor("addPeer"));
        Assert.Empty(orchestrator.Snapshot());
        Assert.Null(orchestrator.LastError); // 提前短路不算失败
    }

    [Fact]
    public async Task TryAddPeerAsync_GetPeersMentionsEndpointDeepInNestedJson_ZeroAddPeerRequests()
    {
        // (c 递归钉死)字符串深埋于 对象→数组→数组→对象,且字段名任意:递归扫描不做字段名绑定
        using var server = FakeAdminServer.Start(
            "{\"status\":\"success\",\"response\":{\"swog\":{\"foo\":[{\"bar\":[[\"x\",{\"deep\":\"tcp://10.0.0.5:13371\"}]]}]}}}",
            "{\"status\":\"success\",\"response\":{}}"); // 仅误消耗时用
        using var admin = new AdminSocketClient(port: server.Port, timeoutMs: 300);
        var orchestrator = new LinkOrchestrator(admin);

        var added = await orchestrator.TryAddPeerAsync(
            new PeerCandidate("vpn", IPAddress.Parse("10.0.0.5"), 13371));

        Assert.False(added);
        Assert.Equal(1, server.CountFor("getPeers"));
        Assert.Equal(0, server.CountFor("addPeer"));
        Assert.Empty(orchestrator.Snapshot());
    }

    [Fact]
    public async Task Snapshot_PreviousSnapshotUnchangedAndNewArrayAfterAdd()
    {
        // (d) 不可变性:先取快照 s1,再成功添加 peer,s1 不变;且每次 Snapshot 返回新数组
        using var server = FakeAdminServer.Start(
            "{\"status\":\"success\",\"response\":{\"peers\":[]}}",
            "{\"status\":\"success\",\"response\":{}}",
            "{\"status\":\"success\",\"response\":{\"peers\":[]}}",
            "{\"status\":\"success\",\"response\":{}}");
        using var admin = new AdminSocketClient(port: server.Port, timeoutMs: 300);
        var orchestrator = new LinkOrchestrator(admin);

        Assert.True(await orchestrator.TryAddPeerAsync(
            new PeerCandidate("mdns", IPAddress.Parse("10.0.0.5"), 13371)));
        var s1 = orchestrator.Snapshot();
        var s1Again = orchestrator.Snapshot();
        await Task.Delay(20); // 保证两次 AddedAt 可区分,以钉死按加入时间排序
        Assert.True(await orchestrator.TryAddPeerAsync(
            new PeerCandidate("vpn", IPAddress.Parse("10.0.0.6"), 13371)));

        var s2 = orchestrator.Snapshot();
        Assert.Single(s1); // 旧快照不受新增影响,仍只有一项
        Assert.Equal("tcp://10.0.0.5:13371", s1[0].Endpoint);
        Assert.Equal(2, s2.Count);
        Assert.NotSame(s1, s2);
        Assert.NotSame(s1, s1Again); // 每次调用都是新数组
        Assert.Equal("tcp://10.0.0.5:13371", s2[0].Endpoint); // 按 AddedAt 排序,先加在前
        Assert.Equal("tcp://10.0.0.6:13371", s2[1].Endpoint);
    }

    [Fact]
    public async Task TryAddPeerAsync_AddPeerError_ReturnsFalseAndStashesErrorWithoutFiring()
    {
        // (e) addPeer 返回 error:返回 false,错误暂存 LastError,不入快照、不触发事件
        using var server = FakeAdminServer.Start(
            "{\"status\":\"success\",\"response\":{\"peers\":[]}}",
            "{\"status\":\"error\",\"error\":\"peer already connected\"}");
        using var admin = new AdminSocketClient(port: server.Port, timeoutMs: 300);
        var orchestrator = new LinkOrchestrator(admin);
        var fired = 0;
        orchestrator.PeerAdded += (_, _) => fired++;

        var added = await orchestrator.TryAddPeerAsync(
            new PeerCandidate("mdns", IPAddress.Parse("10.0.0.5"), 13371));

        Assert.False(added);
        Assert.Equal("peer already connected", orchestrator.LastError);
        Assert.Empty(orchestrator.Snapshot());
        Assert.Equal(0, fired);
        Assert.Equal(1, server.CountFor("getPeers"));
        Assert.Equal(1, server.CountFor("addPeer"));
    }

    /// <summary>
    /// 进程内 yggdrasil admin 假服务器:逐连接读一行请求,按脚本队列回一行(AdminSocketClient 每次 SendAsync 一条连接);
    /// 记录每命令的请求数与最后请求行,供幂等"零请求"断言。脚本耗尽即不回包(真实挂起,客户端超时)。
    /// </summary>
    private sealed class FakeAdminServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _connections = new();
        private readonly Task _acceptLoop;
        private readonly ConcurrentQueue<string?> _scriptedResponses;
        private readonly ConcurrentDictionary<string, int> _requestCounts = new();

        public int Port { get; }
        public string? LastRequestLine { get; private set; }

        public int TotalRequests => _requestCounts.Values.Sum();

        public int CountFor(string request) => _requestCounts.GetValueOrDefault(request);

        private FakeAdminServer(TcpListener listener, string?[] scriptedResponses)
        {
            _listener = listener;
            _scriptedResponses = new ConcurrentQueue<string?>(scriptedResponses);
            Port = ((IPEndPoint)listener.LocalEndpoint!).Port;
            _acceptLoop = AcceptLoopAsync();
        }

        public static FakeAdminServer Start(params string?[] scriptedResponses)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new FakeAdminServer(listener, scriptedResponses);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _connections.Add(client); // 连接保持打开直到 Dispose(模拟真实挂起的 admin socket)
                    try
                    {
                        await ServeAsync(client);
                    }
                    catch (IOException) { } // 客户端提前断开等,不影响接受后续连接
                    catch (SocketException) { }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            var stream = client.GetStream(); // 连接的生命周期归 TcpClient,此处不释放,以免"不回包"变成断开
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync(_cts.Token);
            if (line is null) return;
            LastRequestLine = line;
            RecordRequest(line);
            if (!_scriptedResponses.TryDequeue(out var response) || response is null) return;
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            await writer.WriteLineAsync(response.AsMemory(), _cts.Token);
        }

        private void RecordRequest(string line)
        {
            string name = "?";
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("request", out var request)
                    && request.ValueKind == JsonValueKind.String)
                    name = request.GetString()!;
            }
            catch (JsonException) { } // 非法行按 "?" 计数
            _requestCounts.AddOrUpdate(name, 1, (_, count) => count + 1);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            foreach (var connection in _connections)
                connection.Dispose();
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(1)); } catch { /* 后台循环的良性收尾 */ }
            _cts.Dispose();
        }
    }
}
