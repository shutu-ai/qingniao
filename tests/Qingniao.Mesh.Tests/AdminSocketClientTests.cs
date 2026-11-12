using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Qingniao.Mesh;

namespace Qingniao.Mesh.Tests;

/// <summary>
/// AdminSocketClient 对进程内真实 TcpListener 假服务器的协议测试(真实 socket,不 mock 被测代码)。
/// 假服务器按真实 yggdrasil v0.5.15 行为应答:多行 pretty JSON(SetIndent("", "  "));
/// 请求侧断言真实信封(R21):request + arguments 对象。
/// </summary>
public class AdminSocketClientTests
{
    [Fact]
    public async Task SendAsync_GetSelf_ParsesMultilinePrettyPayload()
    {
        using var server = FakeAdminServer.Start(_ => """
            {
              "status": "success",
              "response": {
                "address": "202:a:b:c:d:e:f:g",
                "key": "pub0"
              }
            }
            """);
        using var client = new AdminSocketClient(port: server.Port);

        var response = await client.SendAsync(AdminRequest.GetSelf());

        Assert.True(response.Success);
        Assert.Null(response.Error);
        Assert.Equal("202:a:b:c:d:e:f:g", response.GetString("address"));
        Assert.Equal("pub0", response.GetString("key"));
        Assert.Null(response.GetString("missing")); // 缺失字段 → null
    }

    [Fact]
    public async Task SendAsync_AddPeer_SendsRequestWithNestedArguments()
    {
        using var server = FakeAdminServer.Start(_ => """
            {
              "status": "success",
              "response": {}
            }
            """);
        using var client = new AdminSocketClient(port: server.Port);

        var response = await client.SendAsync(AdminRequest.AddPeer("tcp://10.0.0.5:13371"));

        Assert.True(response.Success);
        var sent = server.LastRequestLine;
        Assert.NotNull(sent);
        using var doc = JsonDocument.Parse(sent);
        Assert.Equal("addPeer", doc.RootElement.GetProperty("request").GetString());
        // R21(a):真实 yggdrasil 从 arguments 对象取 handler 参数;顶层字段会被忽略
        var arguments = doc.RootElement.GetProperty("arguments");
        Assert.Equal("tcp://10.0.0.5:13371", arguments.GetProperty("uri").GetString());
    }

    [Fact]
    public async Task SendAsync_GetSelf_OmitsArgumentsKey()
    {
        // 无参数请求省略 arguments 键(R21a 文档化选择;真实 yggdrasil 的 arguments 带 omitempty)
        using var server = FakeAdminServer.Start(_ => """
            {
              "status": "success",
              "response": {}
            }
            """);
        using var client = new AdminSocketClient(port: server.Port);

        await client.SendAsync(AdminRequest.GetSelf());

        using var doc = JsonDocument.Parse(server.LastRequestLine!);
        Assert.Equal("getSelf", doc.RootElement.GetProperty("request").GetString());
        Assert.False(doc.RootElement.TryGetProperty("arguments", out _));
    }

    [Fact]
    public async Task SendAsync_ErrorResponse_ReportsError()
    {
        using var server = FakeAdminServer.Start(_ => """
            {
              "status": "error",
              "error": "peer already connected"
            }
            """);
        using var client = new AdminSocketClient(port: server.Port);

        var response = await client.SendAsync(AdminRequest.AddPeer("tcp://10.0.0.5:13371"));

        Assert.False(response.Success);
        Assert.Equal("peer already connected", response.Error);
        Assert.Null(response.GetString("address")); // error 响应无 payload → null
    }

    [Fact]
    public async Task SendAsync_NoResponse_ThrowsTimeoutException()
    {
        using var server = FakeAdminServer.Start(_ => null); // 收到请求后不回包(T4:保持连接挂起)
        using var client = new AdminSocketClient(port: server.Port, timeoutMs: 300);

        await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(AdminRequest.GetSelf()));
    }

    /// <summary>进程内 yggdrasil admin 假服务器:逐连接读一行请求,交给 handler,回多行 pretty JSON;handler 返回 null 则保持连接挂起不回包。</summary>
    private sealed class FakeAdminServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _connections = new();
        private readonly Task _acceptLoop;

        public int Port { get; }
        public string? LastRequestLine { get; private set; }

        private FakeAdminServer(TcpListener listener, Func<string, string?> handler)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint!).Port;
            _acceptLoop = AcceptLoopAsync(handler);
        }

        public static FakeAdminServer Start(Func<string, string?> handler)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new FakeAdminServer(listener, handler);
        }

        private async Task AcceptLoopAsync(Func<string, string?> handler)
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _connections.Add(client); // 连接保持打开直到 Dispose(模拟真实挂起的 admin socket)
                    try
                    {
                        await ServeAsync(client, handler);
                    }
                    catch (IOException) { } // 客户端提前断开等,不影响接受后续连接
                    catch (SocketException) { }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient client, Func<string, string?> handler)
        {
            var stream = client.GetStream(); // 连接的生命周期归 TcpClient,此处不释放,以免“不回包”变成断开
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync(_cts.Token);
            if (line is null) return;
            LastRequestLine = line;
            var response = handler(line);
            if (response is null) return; // 不回包、不断开
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            await writer.WriteAsync(response.AsMemory(), _cts.Token);
            await writer.WriteAsync("\n".AsMemory(), _cts.Token); // pretty JSON 以单个换行收尾(真实实现对齐 SetIndent 输出)
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
