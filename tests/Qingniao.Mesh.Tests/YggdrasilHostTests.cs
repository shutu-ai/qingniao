using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading; // HostFixture 清理重试用 Thread.Sleep
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

    /// <summary>长驻假子进程的进程名(不带扩展名),供泄漏检查。</summary>
    private const string LongLivedExeName = "qn-sleeper";

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
    public async Task StartAsync_AdminNeverReady_KillsChildAndUnblocksRestart()
    {
        // 长驻假子进程 + 静默 admin 端口(端口被占、永不响应):预算耗尽必须抛出,
        // 且存活子进程必须被清理——否则同一实例被残留 _process 卡死,永远无法再次启动
        using var fixture = new HostFixture(withAdminServer: false, childKind: ChildKind.LongLived);
        var host = fixture.CreateHost(startBudgetMs: 2000, pollIntervalMs: 200);

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("未就绪", first.Message);
        Assert.Null(host.CurrentProcessId); // 失败后不留活子进程的登记
        await fixture.AssertNoLeakedChildAsync();

        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.DoesNotContain("已启动", second.Message); // 修复前:此处置 "已启动或正在启动" 失败
        Assert.Contains("未就绪", second.Message); // 必须是预算耗尽,而非拒绝启动
        await host.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_AdminNeverReady_ThrowsWhenBudgetExhaustedWithLastError()
    {
        // admin 端口被静默假服务器占用(收请求、不回包):每次 GetSelf 超时;用短预算(真实时间)验证耗尽路径
        using var fixture = new HostFixture(withAdminServer: false);
        var host = fixture.CreateHost(startBudgetMs: 2000, pollIntervalMs: 200);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("admin", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("未就绪", exception.Message);
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
        using var server = FakeAdminServer.Start(_ => """
            {
              "status": "success",
              "response": {}
            }
            """);
        var config = new MeshConfig { AdminPort = server.Port, YggdrasilExePath = "no/such/exe.exe" };
        await using var host = new YggdrasilHost(config); // 未 StartAsync 也可转发(薄封装)

        var response = await host.AddPeerAsync("tcp://10.0.0.5:13371");

        Assert.True(response.Success);
        var sent = server.LastRequestLine;
        Assert.NotNull(sent);
        using var doc = JsonDocument.Parse(sent);
        Assert.Equal("addPeer", doc.RootElement.GetProperty("request").GetString());
        Assert.Equal(
            "tcp://10.0.0.5:13371",
            doc.RootElement.GetProperty("arguments").GetProperty("uri").GetString());
    }

    [Fact]
    public async Task ChildPrintsThenExitsNonZero_StdoutTailSurfacedOnExitedEvent()
    {
        // I5:崩溃子进程自身的错误文本(配置解析、wintun 加载、端口冲突)必须能到达操作者——
        // 假子进程打印一行后以非零码退出,Exited 事件参数携带单行尾迹,LastStdoutTail 同步可取
        using var fixture = new HostFixture(withAdminServer: false, childKind: ChildKind.PrintingExit);
        var host = fixture.CreateHost(startBudgetMs: 2000, pollIntervalMs: 200);
        var exited = new TaskCompletionSource<HostExitedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Exited += (_, args) => exited.TrySetResult(args);

        // admin 永不就绪 → StartAsync 预算耗尽抛出;期间子进程已自行退出并触发 Exited
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        var args = await AwaitOrTimeout(exited.Task, TimeSpan.FromSeconds(10));

        Assert.NotNull(args.StdoutTail);
        Assert.Contains("qn-fake-ygg-tail-probe stdout line", args.StdoutTail);
        Assert.DoesNotContain("\n", args.StdoutTail); // 尾迹必须单行化
        Assert.Equal(args.StdoutTail, host.LastStdoutTail);
        await host.DisposeAsync();
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

    /// <summary>假子进程种类:立即退出的 ping 副本、忽略参数的长驻编译进程,或打印后非零退出的编译进程。</summary>
    private enum ChildKind
    {
        Exe,
        LongLived,
        PrintingExit,
    }

    /// <summary>每个测试的隔离环境:临时目录(放假 exe)+ 可选的进程内假 admin 服务器。</summary>
    private sealed class HostFixture : IDisposable
    {
        private readonly string _dir;
        private readonly FakeAdminServer _server;

        public MeshConfig Config { get; }
        public string ConfPath { get; }
        public int AdminRequestCount => _server.AdminRequestCount;

        /// <param name="withAdminServer">
        /// true:应答固定 getSelf 成功;false:静默占位——端口被真实节点占用且永不响应的确定性替身(收请求、不回包)。
        /// </param>
        public HostFixture(bool withAdminServer, ChildKind childKind = ChildKind.Exe)
        {
            _dir = Directory.CreateTempSubdirectory("qingniao-host-test-").FullName;
            var exePath = childKind switch
            {
                ChildKind.LongLived => CompileLongLivedChild(_dir),
                ChildKind.PrintingExit => CompilePrintingExitChild(_dir),
                _ => CopyPing(_dir),
            };
            ConfPath = Path.Combine(_dir, "qingniao-ygg.conf");

            // 真实 yggdrasil 应答为多行 pretty JSON(SetIndent("", "  ")),假服务器同形(R21b)
            _server = FakeAdminServer.Start(_ => withAdminServer
                ? $$"""
                    {
                      "status": "success",
                      "response": {
                        "address": "{{FakeAddress}}",
                        "key": "{{FakeKey}}"
                      }
                    }
                    """
                : null);

            Config = new MeshConfig
            {
                AdminPort = _server.Port,
                YggdrasilExePath = exePath,
            };
        }

        public YggdrasilHost CreateHost(int startBudgetMs = 15000, int pollIntervalMs = 500) =>
            new(Config, null, startBudgetMs, pollIntervalMs); // 内部构造:测试可调短预算

        public void Dispose()
        {
            _server.Dispose();
            // 尽力而为:AV 扫描/进程句柄释放竞态可能短暂锁住新编译的 exe;清理失败交给系统临时目录回收,
            // 绝不能在 Dispose 中抛出(会掩盖测试本体异常)
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(_dir, recursive: true);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                Thread.Sleep(200);
            }
        }

        /// <summary>断言本夹具目录下的长驻子进程已全部退出(按镜像路径过滤,不受同名单独孤儿影响)。</summary>
        public async Task AssertNoLeakedChildAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (true)
            {
                var mine = 0;
                foreach (var process in Process.GetProcessesByName(LongLivedExeName))
                {
                    try
                    {
                        if (process.MainModule?.FileName?.StartsWith(_dir, StringComparison.OrdinalIgnoreCase) == true)
                            mine++;
                    }
                    catch (Exception)
                    {
                        // 镜像路径不可读(已退出竞态/权限)——不计入本夹具泄漏
                    }
                    process.Dispose();
                }
                if (mine == 0) return;
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"疑似泄漏 {mine} 个 {LongLivedExeName} 进程");
                await Task.Delay(100);
            }
        }

        private static string CopyPing(string dir)
        {
            var exePath = Path.Combine(dir, "ping.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exePath);
            return exePath;
        }

        /// <summary>用 .NET Framework 自带的 csc 编译一个忽略参数、睡眠 120s 的控制台 exe(长驻假子进程)。</summary>
        private static string CompileLongLivedChild(string dir) => CompileChild(
            dir,
            LongLivedExeName,
            "using System.Threading;\nstatic class P\n{\n    static void Main() { Thread.Sleep(120000); }\n}\n");

        /// <summary>编译一个打印一行后以非零码退出的控制台 exe(I5:验证 stdout 尾迹浮现)。</summary>
        private static string CompilePrintingExitChild(string dir) => CompileChild(
            dir,
            "qn-tail-probe",
            "using System;\n"
            + "static class P\n"
            + "{\n"
            + "    static void Main()\n"
            + "    {\n"
            + "        Console.WriteLine(\"qn-fake-ygg-tail-probe stdout line\");\n"
            + "        Environment.Exit(3);\n"
            + "    }\n"
            + "}\n");

        /// <summary>csc 编译公共路径(csc 4.8 仅支持 C# 5;缺失时响亮失败)。</summary>
        private static string CompileChild(string dir, string exeName, string source)
        {
            var csc = Path.GetFullPath(Path.Combine(
                Environment.SystemDirectory, "..", "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe"));
            Assert.True(File.Exists(csc), $"未找到 Framework csc:{csc}");
            var sourcePath = Path.Combine(dir, exeName + ".cs");
            var exePath = Path.Combine(dir, exeName + ".exe");
            File.WriteAllText(sourcePath, source);
            using var compiler = Process.Start(new ProcessStartInfo
            {
                FileName = csc,
                ArgumentList = { "/nologo", "/t:exe", $"/out:{exePath}", sourcePath },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(compiler);
            Assert.True(compiler!.WaitForExit(10000) && compiler.ExitCode == 0, $"假子进程 {exeName} 编译失败:{csc}");
            compiler.Dispose();
            return exePath;
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
                    catch (ObjectDisposedException) { }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task ServeAsync(TcpClient client, Func<string, string?> handler)
        {
            var stream = client.GetStream(); // 连接生命周期归 TcpClient,此处不释放
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync(_cts.Token);
            if (line is null) return;
            Interlocked.Increment(ref _requestCount);
            LastRequestLine = line;
            var response = handler(line);
            if (response is null) return; // 静默占位:不回包、不断开,客户端只能超时
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            await writer.WriteLineAsync(response.AsMemory(), _cts.Token);
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
