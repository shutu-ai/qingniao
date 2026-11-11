using System.Net;
using System.Net.Sockets;
using Qingniao.LinkManager;

namespace Qingniao.LinkManager.Tests;

/// <summary>VpnCidrScanner 测试:仅回环(127.0.0.1)上的真实 TCP 连接,不触外网。</summary>
public sealed class VpnCidrScannerTests
{
    private readonly VpnCidrScanner _scanner = new();

    /// <summary>绑定 127.0.0.1 随机端口并启动监听;测试结束后释放。</summary>
    private static TcpListener StartLoopbackListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    /// <summary>取一个当前空闲(绑定后立即关闭)的回环端口,用于“无人监听”场景。</summary>
    private static int TakeFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<List<ProbeResult>> MaterializeAsync(
        IAsyncEnumerable<ProbeResult> stream, CancellationToken ct = default)
    {
        var results = new List<ProbeResult>();
        await foreach (var result in stream.WithCancellation(ct).ConfigureAwait(false))
        {
            results.Add(result);
        }
        return results;
    }

    [Fact]
    public async Task ScanAsync_OpenListener_ReportsOpenWithNonNegativeLatency()
    {
        var listener = StartLoopbackListener(out var port);
        try
        {
            var results = await MaterializeAsync(_scanner.ScanAsync(["127.0.0.1/32"], port));

            var single = Assert.Single(results);
            Assert.True(single.IsOpen);
            Assert.Equal(IPAddress.Loopback, single.Address);
            Assert.True(single.LatencyMs >= 0);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ScanAsync_ClosedPort_ReportsClosedWithNonNegativeLatency()
    {
        int port = TakeFreeLoopbackPort();

        var results = await MaterializeAsync(_scanner.ScanAsync(["127.0.0.1/32"], port));

        var single = Assert.Single(results);
        Assert.False(single.IsOpen);
        Assert.True(single.LatencyMs >= 0);
    }

    [Fact]
    public async Task ScanAsync_EmptyCidrList_YieldsEmptyStream()
    {
        var results = await MaterializeAsync(_scanner.ScanAsync([], 80));

        Assert.Empty(results);
    }

    [Fact]
    public async Task ScanAsync_NullCidrList_ThrowsArgumentNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await MaterializeAsync(_scanner.ScanAsync(null!, 80)));
    }

    [Fact]
    public async Task ScanAsync_InvalidCidr_PropagatesArgumentException()
    {
        // 编程错误(CIDR 非法)应向外传播,而不是吞掉当作“不可达”。
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await MaterializeAsync(_scanner.ScanAsync(["10.0.0.0/15"], 80)));
    }

    [Fact]
    public async Task ScanAsync_CancelledAfterFirstResult_TerminatesStreamWithOperationCanceled()
    {
        int port = TakeFreeLoopbackPort();
        using var cts = new CancellationTokenSource();

        var results = new List<ProbeResult>();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var result in _scanner
                .ScanAsync(["127.0.0.1/32", "127.0.0.1/32", "127.0.0.1/32"], port, cts.Token)
                .ConfigureAwait(false))
            {
                results.Add(result);
                cts.Cancel(); // 收到首个结果后立即取消
            }
        });

        Assert.Single(results); // 取消后不再调度/产出新结果
    }
}
