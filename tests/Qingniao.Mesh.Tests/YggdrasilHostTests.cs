using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Qingniao.Core;
using Qingniao.Mesh;

namespace Qingniao.Mesh.Tests;

/// <summary>
/// YggdrasilHost 测试。Non-Live:用复制到临时目录的 ping.exe 当假子进程(带未知参数立即退出)
/// + 进程内假 admin 服务器,驱动完整的启动/看门狗重启/停止链路,不需要管理员权限。
/// Live(<c>Category=Live</c>)拉起真实 yggdrasil,需要管理员权限(wintun 装虚拟网卡)。
/// </summary>
public class YggdrasilHostTests
{
    private const string FakeAddress = "200:abcd:ef01:2345:6789:abcd:ef01:2345";
    private const string FakeKey = "pubkey-fake0123456789";

    // ---------------------------------------------------------------- Live

    [Fact]
    [Trait("Category", "Live")]
    public async Task StartAsync_RealYggdrasil_SelfAddressPopulatedWithin15s()
    {
        await using var host = CreateRealHost();

        await host.StartAsync();

        Assert.NotNull(host.SelfAddress);
        Assert.Contains(":", host.SelfAddress); // IPv6 字符串
        Assert.NotNull(host.SelfKey);
        await host.StopAsync();
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task KillChildProcess_WatchdogRestarts_NewProcessIdWithin5s()
    {
        await using var host = CreateRealHost();
        await host.StartAsync();
        var firstId = host.CurrentProcessId;
        Assert.True(firstId > 0);

        var exited = new TaskCompletionSource<HostExitedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Exited += (_, args) => exited.TrySetResult(args);

        Process.GetProcessById(firstId!.Value).Kill(entireProcessTree: true);

        var args = await AwaitOrTimeout(exited.Task, TimeSpan.FromSeconds(5));
        Assert.Equal(firstId.Value, args.ProcessId);

        // 重启后:新进程号 + SelfAddress 恢复非 null
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (host.CurrentProcessId is null || host.CurrentProcessId == firstId || host.SelfAddress is null)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("看门狗 15s 内未恢复 SelfAddress");
            await Task.Delay(200);
        }
        Assert.NotEqual(firstId.Value, host.CurrentProcessId);
        await host.StopAsync();
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task StopAsync_RealYggdrasil_ChildProcessExits()
    {
        var host = CreateRealHost();
        await host.StartAsync();
        var id = host.CurrentProcessId;
        Assert.True(id > 0);

        await host.StopAsync();

        AssertProcessGone(id!.Value);
        await host.DisposeAsync();
    }

    // ------------------------------------------------------------ Non-Live

    [Fact]
    public async Task StartAsync_WritesConfigFile_LaunchesChild_PopulatesSelfProperties()
    {
        using var fixture = new HostFixture(withAdminServer: true);
        var host = fixture.CreateHost();

        try
        {
            await host.StartAsync();

            Assert.Equal(FakeAddress, host.SelfAddress);
            Assert.Equal(FakeKey, host.SelfKey);
            Assert.True(host.CurrentProcessId > 0);
            Assert.True(File.Exists(fixture.ConfPath), "qingniao-ygg.conf 未写入 exe 同目录");
            Assert.Equal(YggdrasilConfigWriter.Generate(fixture.Config), File.ReadAllText(fixture.ConfPath));
        }
        finally
        {
            await host.StopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_ExeMissing_ThrowsFileNotFoundException()
    {
        var host = new YggdrasilHost(new MeshConfig { YggdrasilExePath = "no/such/dir/missing.exe" });

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => host.StartAsync());

        Assert.Contains("missing.exe", exception.Message);
    }

    [Fact]
    public async Task StartAsync_AdminNeverReady_ThrowsWhenBudgetExhaustedWithLastError()
    {
        // admin 端口上没有任何服务:每次 GetSelf 都连接被拒;用短预算(真实时间)验证耗尽路径
        using var fixture = new HostFixture(withAdminServer: false);
        var host = fixture.CreateHost(startBudgetMs: 2000, pollIntervalMs: 200);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("admin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChildSelfExit_WatchdogRestarts_RestartChainObserved()
    {
        using var fixture = new HostFixture(withAdminServer: true);
        var host = fixture.CreateHost();
        var exitedIds = new List<int>();
        var exitedSignal = new SemaphoreSlim(0);
        host.Exited += (_, args) =>
        {
            lock (exitedIds) { exitedIds.Add(args.ProcessId); }
            exitedSignal.Release();
        };

        try
        {
            await host.StartAsync();
            var firstId = host.CurrentProcessId;
            Assert.True(firstId > 0);
            Assert.Equal(FakeAddress, host.SelfAddress); // 初始就绪

            // ping.exe 带未知参数立即自行退出 → 第 1 次 Exited
            await AwaitSignalAsync(exitedSignal, TimeSpan.FromSeconds(10));
            Assert.Equal(firstId, exitedIds[0]);
            Assert.Null(host.SelfAddress); // 进程退出后清空

            // 看门狗按退避(首枚 1s)重启 → 新子进程 → 再次自行退出 → 第 2 次 Exited(证明重启发生)
            await AwaitSignalAsync(exitedSignal, TimeSpan.FromSeconds(10));
            lock (exitedIds) { Assert.True(exitedIds.Distinct().Count() >= 2, $"退出进程号应含新子进程:{string.Join(",", exitedIds)}"); }
            // 假子进程存活仅十几毫秒,重启轮询是否来得及完成不确定;重启后的 SelfAddress 恢复由 Live 测试覆盖
            Assert.True(fixture.AdminRequestCount >= 1, "初始启动应完成至少一次 admin getSelf");
        }
        finally
        {
            await host.StopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopAsync_Twice_IsIdempotent()
    {
        using var fixture = new HostFixture(withAdminServer: false);
        var host = fixture.CreateHost();

        await host.StopAsync();
        await host.StopAsync();

        Assert.Null(host.CurrentProcessId);
        Assert.Null(host.SelfAddress);
        Assert.Null(host.SelfKey);
    }

    [Fact]
    public async Task DisposeAsync_AfterStart_StopsAndIsIdempotent()
    {
        using var fixture = new HostFixture(withAdminServer: true);
        var host = fixture.CreateHost();
        await host.StartAsync();
        Assert.True(host.CurrentProcessId > 0);

        await host.DisposeAsync();
        await host.DisposeAsync(); // 二次释放不抛

        Assert.Null(host.CurrentProcessId);
        Assert.Null(host.SelfAddress);
    }

    [Fact]
    public async Task StartAfterStop_WatchdogStillFunctions_SecondLifecycleRestarts()
    {
        using var fixture = new HostFixture(withAdminServer: true);
        var host = fixture.CreateHost();
        var signal = new SemaphoreSlim(0);
        host.Exited += (_, _) => signal.Release();

        try
        {
            await host.StartAsync();
            await host.StopAsync(); // 第一次生命周期结束
            await host.StartAsync(); // 同一 host 复用:验证已取消的 CTS 被重建
            Assert.Equal(FakeAddress, host.SelfAddress);

            // 第二生命周期的子进程退出 → 看门狗仍接管:退避 1s 后进入重启轮询(admin 请求数 ≥3:两次启动 + 一轮重启)
            await AwaitSignalAsync(signal, TimeSpan.FromSeconds(10));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (fixture.AdminRequestCount < 3)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"重启轮询未执行,admin 请求次数 = {fixture.AdminRequestCount}");
                await Task.Delay(100);
            }
        }
        finally
        {
            await host.StopAsync();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddPeerAsync_ForwardsAddPeerRequestViaAdminSocket()
    {
        using var server = FakeAdminServer.Start(_ =>
            "{\"status\":\"success\",\"response\":{}}");
        var config = new MeshConfig { AdminPort = server.Port, YggdrasilExePath = "no/such/exe.exe" };
        await using var host = new YggdrasilHost(config); // 未 StartAsync 也可转发(薄封装)

        var response = await host.AddPeerAsync("tcp://10.0.0.5:13371");

        Assert.True(response.Success);
        var sent = server.LastRequestLine;
        Assert.NotNull(sent);
        Assert.Contains("\"request\":\"addPeer\"", sent);
        Assert.Contains("\"uri\":\"tcp://10.0.0.5:13371\"", sent);
    }

    // ------------------------------------------------------------- helpers

    /// <summary>Live 用:定位仓库根下的真实 yggdrasil(测试 cwd 在 bin 目录,需向上回溯)。</summary>
    private static YggdrasilHost CreateRealHost()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "yggdrasil", "yggdrasil.exe")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var exe = Path.Combine(dir!.FullName, "tools", "yggdrasil", "yggdrasil.exe");
        return new YggdrasilHost(new MeshConfig { YggdrasilExePath = exe });
    }

    private static async Task<T> AwaitOrTimeout<T>(Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task) throw new TimeoutException($"等待超时({timeout.TotalSeconds}s)");
        return await task;
    }

    private static async Task AwaitSignalAsync(SemaphoreSlim signal, TimeSpan timeout)
    {
        if (!await signal.WaitAsync(timeout)) throw new TimeoutException($"等待信号超时({timeout.TotalSeconds}s)");
    }

    private static void AssertProcessGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited || process.WaitForExit(5000), $"子进程 {pid} 在 5s 内未退出");
        }
        catch (ArgumentException)
        {
            // 进程已不存在 → 即为退出
        }
        catch (InvalidOperationException)
        {
            // 句柄存在但进程已退出
        }
    }

    /// <summary>每个测试的隔离环境:临时目录(放假 exe)+ 可选的进程内假 admin 服务器。</summary>
    private sealed class HostFixture : IDisposable
    {
        private readonly string _dir;
        private readonly FakeAdminServer? _server;

        public MeshConfig Config { get; }
        public string ConfPath { get; }
        public int AdminRequestCount => _server?.AdminRequestCount ?? 0;

        public HostFixture(bool withAdminServer)
        {
            _dir = Directory.CreateTempSubdirectory("qingniao-host-test-").FullName;
            var exePath = Path.Combine(_dir, "ping.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exePath);
            ConfPath = Path.Combine(_dir, "qingniao-ygg.conf");

            _server = withAdminServer ? FakeAdminServer.Start(_ =>
                $"{{\"status\":\"success\",\"response\":{{\"address\":\"{FakeAddress}\",\"key\":\"{FakeKey}\"}}}}") : null;

            Config = new MeshConfig
            {
                AdminPort = _server?.Port ?? 13372,
                YggdrasilExePath = exePath,
            };
        }

        public YggdrasilHost CreateHost(int startBudgetMs = 15000, int pollIntervalMs = 500) =>
            new(Config, null, startBudgetMs, pollIntervalMs); // 内部构造:测试可调短预算

        public void Dispose()
        {
            _server?.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } // 文件被占用时留给系统清理
        }
    }

    /// <summary>进程内 yggdrasil admin 假服务器:逐连接读一行,回一行固定 JSON(与 AdminSocketClientTests 同构)。</summary>
    private sealed class FakeAdminServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _connections = new();
        private readonly Task _acceptLoop;

        public int Port { get; }
        public string? LastRequestLine { get; private set; }
        public int AdminRequestCount => _requestCount;

        private int _requestCount;

        private FakeAdminServer(TcpListener listener, Func<string, string> handler)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint!).Port;
            _acceptLoop = AcceptLoopAsync(handler);
        }

        public static FakeAdminServer Start(Func<string, string> handler)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new FakeAdminServer(listener, handler);
        }

        private async Task AcceptLoopAsync(Func<string, string> handler)
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
                    catch (ObjectDisposedException) { }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient client, Func<string, string> handler)
        {
            var stream = client.GetStream(); // 连接生命周期归 TcpClient,此处不释放
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync(_cts.Token);
            if (line is null) return;
            Interlocked.Increment(ref _requestCount);
            LastRequestLine = line;
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            await writer.WriteLineAsync(handler(line).AsMemory(), _cts.Token);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            foreach (var connection in _connections)
                connection.Dispose();
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(1)); } catch { /* 良性收尾 */ }
            _cts.Dispose();
        }
    }
}
