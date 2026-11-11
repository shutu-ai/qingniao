using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Qingniao.SelfCheck.Checks.Stun;

namespace Qingniao.SelfCheck.Tests;

/// <summary>
/// StunClient 契约测试:仅用 127.0.0.1 回环上的假 UDP 服务器与虚拟时钟,不访问真实网络。
/// 覆盖:成功解析、超时 → null、不可解析主机 → null、外部取消 → null、
/// tid 不匹配的包被跳过、仅 MAPPED-ADDRESS 时回退构造 StunResult。
/// </summary>
public class StunClientTests
{
    private static readonly IPEndPoint ReportedMapping = new(IPAddress.Parse("203.0.113.7"), 50000);
    private static readonly IPEndPoint ReportedOther = new(IPAddress.Parse("203.0.113.9"), 50002);
    private static readonly IPEndPoint ReportedChanged = new(IPAddress.Parse("203.0.113.11"), 50004);

    // -------------------------------------------------------------- 成功路径

    [Fact]
    public async Task FakeServerReply_ReturnsStunResultWithOtherAddress()
    {
        using var server = FakeStunServer.Standard(ReportedMapping, ReportedOther, changed: null);
        var client = new StunClient();

        StunResult? result = await client.QueryAsync($"127.0.0.1:{server.Port}", timeoutMs: 2000);

        Assert.NotNull(result);
        Assert.Equal(ReportedMapping, result.XorMapped);
        Assert.Equal(ReportedOther, result.Other);
    }

    [Fact]
    public async Task OnlyChangedAddress_OtherFallsBackToChanged()
    {
        using var server = FakeStunServer.Standard(ReportedMapping, other: null, ReportedChanged);
        var client = new StunClient();

        StunResult? result = await client.QueryAsync($"127.0.0.1:{server.Port}", timeoutMs: 2000);

        Assert.NotNull(result);
        Assert.Equal(ReportedMapping, result.XorMapped);
        Assert.Equal(ReportedChanged, result.Other); // OTHER-ADDRESS 缺席 → 回退 CHANGED-ADDRESS
    }

    [Fact]
    public async Task OnlyMappedAddress_NoXorMapped_StillBuildsResult()
    {
        // StunResult.XorMapped 语义:承载“最佳已知映射”;服务器只回 MAPPED-ADDRESS 时回退使用之。
        using var server = FakeStunServer.MappedOnly(ReportedMapping);
        var client = new StunClient();

        StunResult? result = await client.QueryAsync($"127.0.0.1:{server.Port}", timeoutMs: 2000);

        Assert.NotNull(result);
        Assert.Equal(ReportedMapping, result.XorMapped);
        Assert.Null(result.Other);
    }

    [Fact]
    public async Task MismatchedTidReply_IsSkippedAndMatchingReplyWins()
    {
        // 先回一个 tid 坏掉的包,再回合法包:客户端必须跳过坏包、取第一个 tid 匹配者。
        using var server = FakeStunServer.Standard(ReportedMapping, ReportedOther, changed: null);
        server.CorruptTidOfFirstReply = true;
        var client = new StunClient();

        StunResult? result = await client.QueryAsync($"127.0.0.1:{server.Port}", timeoutMs: 2000);

        Assert.NotNull(result);
        Assert.Equal(ReportedMapping, result.XorMapped);
    }

    // -------------------------------------------------------------- 失败路径(契约:绝不抛,一律 null)

    [Fact]
    public async Task NoReply_ReturnsNullWithinTimeout()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); // 静默服务器,不回包
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var fake = new FakeTimeProvider();
        var client = new StunClient(fake);
        var stopwatch = Stopwatch.StartNew();

        Task<StunResult?> query = client.QueryAsync($"127.0.0.1:{port}", timeoutMs: 1000);
        for (int i = 0; i < 1000 && !fake.HasPendingTimers; i++)
        {
            await Task.Delay(5); // 等待被测代码注册超时定时器(真实时间仅数毫秒)
        }

        fake.Advance(TimeSpan.FromMilliseconds(1001)); // 越过 1000ms 截止时间
        StunResult? result = await query;
        stopwatch.Stop();

        Assert.Null(result);
        Assert.True(stopwatch.ElapsedMilliseconds <= 1000 + 500, $"实际耗时 {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task UnresolvableHost_ReturnsNull()
    {
        var client = new StunClient();

        StunResult? result = await client.QueryAsync("no-such-host.invalid:3478", timeoutMs: 2000);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExternalCancellation_ReturnsNull()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); // 静默服务器
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var client = new StunClient();
        using var cts = new CancellationTokenSource(200);
        var stopwatch = Stopwatch.StartNew();

        StunResult? result = await client.QueryAsync($"127.0.0.1:{port}", timeoutMs: 10_000, cts.Token);
        stopwatch.Stop();

        Assert.Null(result);
        Assert.True(stopwatch.ElapsedMilliseconds < 5_000, $"实际耗时 {stopwatch.ElapsedMilliseconds}ms");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-port")]
    [InlineData(":3478")]
    [InlineData("127.0.0.1:")]
    [InlineData("127.0.0.1:notaport")]
    [InlineData("127.0.0.1:99999")]
    public async Task InvalidServerString_ReturnsNull(string server)
    {
        Assert.Null(await new StunClient().QueryAsync(server, timeoutMs: 200));
    }

    // -------------------------------------------------------------- 假 STUN 服务器

    /// <summary>回环假服务器:收到请求后以请求的 tid 回填,手工构造合法响应(不访问真实网络)。</summary>
    private sealed class FakeStunServer : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private FakeStunServer(Func<byte[], byte[]> respond)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _loop = LoopAsync(respond, _cts.Token);
        }

        public int Port { get; }

        /// <summary>置 true 时首个响应的 tid 被改坏(测跳过逻辑)。</summary>
        public bool CorruptTidOfFirstReply { get; set; }

        public static FakeStunServer Standard(IPEndPoint reported, IPEndPoint? other, IPEndPoint? changed) =>
            new(tid => StunMessage.BuildResponse(tid, reported, other, changed));

        public static FakeStunServer MappedOnly(IPEndPoint mapped) =>
            new(tid => StunMessage.BuildResponse(tid, mapped: mapped));

        private async Task LoopAsync(Func<byte[], byte[]> respond, CancellationToken ct)
        {
            bool firstReply = true;
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult request;
                try
                {
                    request = await _udp.ReceiveAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (request.Buffer.Length < 20)
                {
                    continue;
                }

                byte[] tid = request.Buffer[8..20];
                byte[] response = respond(tid);
                if (firstReply && CorruptTidOfFirstReply)
                {
                    byte[] corrupted = [.. response];
                    corrupted[8] ^= 0xFF; // 改坏 tid 首字节
                    await _udp.SendAsync(corrupted, request.RemoteEndPoint); // 先发坏包,再发合法包
                }

                firstReply = false;
                await _udp.SendAsync(response, request.RemoteEndPoint);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _loop.Wait(500);
            }
            catch (Exception)
            {
                // 关闭期间的套接字异常可忽略
            }

            _udp.Dispose();
            _cts.Dispose();
        }
    }
}
