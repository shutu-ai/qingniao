using System.Text;
using Qingniao.Core;
using Qingniao.LinkManager;
using Qingniao.Mesh;
using Qingniao.MeshDaemon;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // 控制台编码设置失败(如重定向环境不支持)时忽略,继续运行。
}

var configPath = "configs/sample.json";
var scanIntervalSeconds = 30;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("错误: --config 需要一个路径参数");
                return 2;
            }

            configPath = args[++i];
            break;
        case "--scan-interval":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("错误: --scan-interval 需要一个秒数参数");
                return 2;
            }

            if (!int.TryParse(args[++i], out scanIntervalSeconds) || scanIntervalSeconds < 1)
            {
                Console.Error.WriteLine("错误: --scan-interval 需要 ≥1 的整数(秒)");
                return 2;
            }

            break;
        default:
            Console.Error.WriteLine(
                $"错误: 无法识别的参数 \"{args[i]}\"(支持 --config <path> 与 --scan-interval <sec>)");
            return 2;
    }
}

// 缺文件回落默认配置;文件存在但非法时报错退出(与 SelfCheck 同口径)
MeshConfig config;
try
{
    config = ConfigStore.LoadOrDefault(configPath);
}
catch (ConfigException ex)
{
    Console.Error.WriteLine($"错误: {ex.Message}");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // 接管 Ctrl+C,走优雅停止
    cts.Cancel();
};

// ------------------------------------------------ 组装真实实现(R15 接口 + 生产实现)
await using var host = new YggdrasilHost(config);
using var admin = new AdminSocketClient(port: config.AdminPort);
var orchestrator = new LinkOrchestrator(admin);
await using var advertiser = new MdnsAdvertiser(Environment.MachineName, config.MeshPort); // R2: nodeName = MachineName
await using var discovery = new MdnsDiscovery();
var scanner = new VpnCidrScanner();
var runtime = new MeshRuntime(config, host, advertiser, discovery, scanner, orchestrator);

orchestrator.PeerAdded += (_, link) =>
    Console.WriteLine($"[qng] 已添加 {link.Endpoint} ({link.Source})");
host.Exited += (_, e) =>
    Console.Error.WriteLine($"[qng] yggdrasil 进程意外退出 (pid={e.ProcessId}),看门狗将按退避自动重启");

Console.WriteLine(
    $"[qng] 启动:mesh={config.MeshPort} admin={config.AdminPort} " +
    $"配置={configPath} 扫描间隔={scanIntervalSeconds}s 节点名={Environment.MachineName}");

try
{
    await runtime.StartAsync(cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[qng] 启动失败: {ex.Message}");
    return 1;
}

// ------------------------------------------------ 周期扫描循环:立即一轮,再按间隔循环(Ctrl+C 优雅停止)
try
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(scanIntervalSeconds));
    do
    {
        try
        {
            await runtime.RunVpnScanCycleAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            // 单轮扫描失败(如网段配置非法)不杀守护进程,下一轮再试
            Console.Error.WriteLine($"[qng] VPN 扫描轮失败: {ex.Message}");
        }
    }
    while (await timer.WaitForNextTickAsync(cts.Token));
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    // 等待下一个周期时收到 Ctrl+C
}

Console.WriteLine("[qng] 正在停止…");
await runtime.StopAsync();
Console.WriteLine("[qng] 已退出");
return 0;
