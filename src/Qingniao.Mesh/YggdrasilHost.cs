using System.ComponentModel;
using System.Diagnostics;
using Qingniao.Core;

namespace Qingniao.Mesh;

/// <summary>子进程退出参数(计划内 Stop 不触发 <see cref="YggdrasilHost.Exited"/> 事件)。</summary>
public sealed record HostExitedEventArgs(int ProcessId);

/// <summary>
/// yggdrasil 子进程托管:写配置、拉起进程、轮询 admin 健康检查,并以指数退避看门狗在进程意外退出后自动重启。
/// 语义约定:<see cref="Exited"/> 仅在进程意外退出时触发(计划内 <see cref="StopAsync"/> 不触发);
/// 重启失败的错误不冒泡——看门狗通过杀掉残留子进程使其退出,借 <see cref="Exited"/> 链路排入下一轮退避。
/// </summary>
public sealed class YggdrasilHost : IAsyncDisposable
{
    private const string ConfFileName = "qingniao-ygg.conf";

    /// <summary>稳定运行 ≥ 该时长后,看门狗退避序列归零。</summary>
    private static readonly TimeSpan StabilityResetAfter = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly MeshConfig _config;
    private readonly TimeProvider _time;
    private readonly RestartBackoff _backoff;
    private readonly int _startBudgetMs;
    private readonly int _pollIntervalMs;

    private Process? _process;
    private Task? _stdoutDrain;
    private ITimer? _restartTimer;
    private ITimer? _stabilityTimer;
    private CancellationTokenSource _lifetimeCts = new();

    /// <summary>_lifetimeCts.Token 的快照:重启周期读它,避免与 Dispose 竞争访问已释放的 CTS。</summary>
    private CancellationToken _lifetimeToken;
    private bool _starting; // 初始 StartAsync 进行中(防并发重复启动)
    private bool _started;  // 初始 StartAsync 已成功,看门狗自此接管
    private bool _stopping;
    private bool _disposed;

    public YggdrasilHost(MeshConfig config, TimeProvider? time = null)
        : this(config, time, startBudgetMs: 15_000, pollIntervalMs: 500)
    {
    }

    /// <summary>内部构造:测试可用更短的启动预算/轮询间隔。</summary>
    internal YggdrasilHost(MeshConfig config, TimeProvider? time, int startBudgetMs, int pollIntervalMs)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startBudgetMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pollIntervalMs);
        _config = config;
        _time = time ?? TimeProvider.System;
        _backoff = new RestartBackoff(_time);
        _startBudgetMs = startBudgetMs;
        _pollIntervalMs = pollIntervalMs;
        _lifetimeToken = _lifetimeCts.Token;
    }

    /// <summary>当前子进程的自身 yggdrasil 地址(IPv6 字符串);进程退出或未就绪时为 null。</summary>
    public string? SelfAddress { get; private set; }

    /// <summary>当前子进程的自身公钥;进程退出或未就绪时为 null。</summary>
    public string? SelfKey { get; private set; }

    /// <summary>当前子进程号;未运行时为 null(供守护进程日志与测试使用)。</summary>
    public int? CurrentProcessId { get; private set; }

    /// <summary>子进程意外退出时触发(计划内 Stop 不触发)。事件处理器内的异常会被隔离,不影响看门狗。</summary>
    public event EventHandler<HostExitedEventArgs>? Exited;

    /// <summary>
    /// 写配置文件到 exe 同目录、启动 yggdrasil 子进程,并轮询 admin getSelf 至预算耗尽。
    /// 就绪后设置 <see cref="SelfAddress"/>/<see cref="SelfKey"/>,并武装看门狗。
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_starting || _started || _process is not null)
                throw new InvalidOperationException("YggdrasilHost 已启动或正在启动,不可重复 StartAsync");
            if (_lifetimeCts.IsCancellationRequested)
            {
                // StopAsync 后允许再次 Start:取消过的 CTS 无法复用,须换新
                _lifetimeCts.Dispose();
                _lifetimeCts = new CancellationTokenSource();
            }
            _lifetimeToken = _lifetimeCts.Token;
            _starting = true;
            _stopping = false;
        }

        try
        {
            var (pid, address, key) = await StartChildAndAwaitAdminAsync(ct);
            lock (_gate)
            {
                _starting = false;
                if (_stopping)
                    throw new InvalidOperationException("YggdrasilHost 在启动轮询期间被停止");
                if (CurrentProcessId != pid)
                    throw new InvalidOperationException($"yggdrasil 进程(pid={pid})在就绪后立即退出,本次启动失败");
                SelfAddress = address;
                SelfKey = key;
                _started = true;
                ArmStabilityTimerLocked();
            }
        }
        catch
        {
            // 启动失败(含轮询预算耗尽):同步清理登记状态,并杀掉可能仍在运行的子进程,
            // 否则泄漏的存活子进程会让同一实例永远无法再次 StartAsync
            Process? process;
            Task? drain;
            lock (_gate)
            {
                process = _process;
                drain = _stdoutDrain;
                _process = null;
                _stdoutDrain = null;
                CurrentProcessId = null;
                SelfAddress = null;
                SelfKey = null;
                _starting = false;
            }
            if (process is not null)
            {
                await KillProcessAsync(process, timeoutMs: 3000);
                await AwaitStdoutDrainAsync(drain);
                process.Dispose();
            }
            throw;
        }
    }

    /// <summary>向当前 yggdrasil 转发 addPeer(薄封装;未启动时 admin 调用会自然失败)。</summary>
    public async Task<AdminResponse> AddPeerAsync(string uri, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var client = CreateAdminClient();
        return await client.SendAsync(AdminRequest.AddPeer(uri), ct);
    }

    /// <summary>停止托管:取消看门狗、杀死子进程树并清理句柄与定时器。幂等。</summary>
    public async Task StopAsync()
    {
        Process? process;
        Task? drain;
        lock (_gate)
        {
            if (_stopping && _process is null) return; // 已完成停止
            _stopping = true;
            _started = false;
            DisposeTimersLocked();
            process = _process;
            drain = _stdoutDrain;
            _process = null;
            _stdoutDrain = null;
            CurrentProcessId = null;
            SelfAddress = null;
            SelfKey = null;
        }

        _lifetimeCts.Cancel(); // 打断可能仍在进行的启动/重启轮询
        _backoff.Reset();
        if (process is not null)
        {
            await KillProcessAsync(process, timeoutMs: 5000);
            await AwaitStdoutDrainAsync(drain);
            process.Dispose();
        }
    }

    /// <summary>等价于 <see cref="StopAsync"/> 后释放全部托管资源。幂等(重复调用安全)。</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lock (_gate) { _disposed = true; }
        _lifetimeCts.Dispose(); // CancellationTokenSource.Dispose 幂等
    }

    // ---------------------------------------------------------- 进程生命周期

    /// <summary>启动子进程并轮询 admin 至就绪;预算耗尽抛 <see cref="InvalidOperationException"/>(含最后一次错误)。</summary>
    private async Task<(int Pid, string Address, string Key)> StartChildAndAwaitAdminAsync(CancellationToken ct)
    {
        var exePath = Path.GetFullPath(_config.YggdrasilExePath);
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"未找到 yggdrasil 可执行文件:{exePath}", exePath);
        var confPath = WriteConfFile();

        var pid = StartProcess(exePath, confPath);

        string? lastError = null;
        var deadline = _time.GetTimestamp() + (long)_startBudgetMs * TimeSpan.TicksPerMillisecond;
        while (true)
        {
            lock (_gate)
            {
                if (_stopping) throw new InvalidOperationException("YggdrasilHost 在启动轮询期间被停止");
            }
            ct.ThrowIfCancellationRequested();

            try
            {
                using var client = CreateAdminClient();
                var response = await client.SendAsync(AdminRequest.GetSelf(), ct);
                var address = response.GetString("address");
                var key = response.GetString("key");
                if (response.Success && address is not null && key is not null)
                    return (pid, address, key);
                lastError = response.Error ?? "getSelf 响应缺少 address/key";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) // 连接被拒/超时/坏响应:记录并继续轮询
            {
                lastError = exception.Message;
            }

            if (_time.GetTimestamp() >= deadline)
                throw new InvalidOperationException(
                    $"yggdrasil admin 在 {_startBudgetMs}ms 内未就绪,最后一次错误:{lastError ?? "timeout"}");
            await Task.Delay(TimeSpan.FromMilliseconds(_pollIntervalMs), _time, ct);
        }
    }

    /// <summary>拉起子进程,登记句柄并武装退出监听;返回进程号。先登记再启用事件,保证 Exited 事件不丢失。</summary>
    private int StartProcess(string exePath, string confPath)
    {
        var info = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true, // 必须持续排空,否则子进程写满管道缓冲后会卡死
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-useconffile");
        info.ArgumentList.Add(confPath);
        info.ArgumentList.Add("-logto");
        info.ArgumentList.Add("stdout");

        var process = new Process { StartInfo = info };
        process.Exited += OnProcessExited;
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"yggdrasil 进程启动失败:{exePath}");
        }
        catch
        {
            process.Exited -= OnProcessExited;
            process.Dispose();
            throw;
        }

        var drain = process.StandardOutput.ReadToEndAsync(); // 后台排空日志,防管道背压
        lock (_gate)
        {
            _process = process;
            _stdoutDrain = drain;
            CurrentProcessId = process.Id;
        }
        // 进程若已在此前退出,此处赋值 true 会立刻触发 Exited;此时 _process 已登记,事件不会丢
        process.EnableRaisingEvents = true;
        return process.Id;
    }

    /// <summary>子进程意外退出:清空自省属性、通知订阅者、回收句柄,再由看门狗按退避调度重启。</summary>
    private void OnProcessExited(object? sender, EventArgs e)
    {
        Process? process;
        Task? drain;
        lock (_gate)
        {
            process = _process;
            if (process is null || _stopping)
                return; // 计划内 Stop 已接管状态,此处无事可做
            _process = null;
            drain = _stdoutDrain;
            _stdoutDrain = null;
            CurrentProcessId = null;
            SelfAddress = null;
            SelfKey = null;
            DisposeTimersLocked(); // 稳定计时器随进程死亡一并作废
        }

        try
        {
            Exited?.Invoke(this, new HostExitedEventArgs(process.Id));
        }
        catch
        {
            // 订阅者异常就地隔离:事件契约是单向通知,看门狗必须继续运行(守护进程阶段接入日志后改为记录)
        }

        _ = AwaitStdoutDrainAsync(drain); // 排空任务自带 3s 上限与异常收口,不阻塞看门狗
        process.Dispose(); // 退出后立即回收句柄,避免依赖终结器

        lock (_gate)
        {
            if (_started && !_stopping)
                ScheduleRestartLocked();
        }
    }

    /// <summary>按当前退避延迟安排一次重启。仅由 <see cref="OnProcessExited"/> 调用,单链不重叠。</summary>
    private void ScheduleRestartLocked()
    {
        DisposeTimersLocked();
        var delay = _backoff.NextDelay();
        _restartTimer = _time.CreateTimer(
            _ => _ = RestartCycleAsync(), null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>重启一轮:拉起子进程 + 等待 admin 就绪。失败时确保子进程死亡,由其 Exited 事件排入下一轮退避。</summary>
    private async Task RestartCycleAsync()
    {
        lock (_gate)
        {
            _restartTimer?.Dispose();
            _restartTimer = null;
            if (_stopping || !_started) return;
        }

        try
        {
            var (pid, address, key) = await StartChildAndAwaitAdminAsync(_lifetimeToken);
            lock (_gate)
            {
                if (_stopping || CurrentProcessId != pid) return; // 期间被停止或进程已再死:Exited 链路已接管
                SelfAddress = address;
                SelfKey = key;
                ArmStabilityTimerLocked();
            }
        }
        catch
        {
            // 重启失败(admin 不就绪/启动失败):杀掉残留子进程 → 其 Exited 事件排入下一轮退避,错误不冒泡
            await KillCurrentChildAsync();
        }
    }

    /// <summary>稳定运行 ≥5 分钟后归零退避;进程在此期间死亡则计时器已作废。</summary>
    private void ArmStabilityTimerLocked() =>
        _stabilityTimer = _time.CreateTimer(
            _ => _backoff.Reset(), null, StabilityResetAfter, Timeout.InfiniteTimeSpan);

    // ------------------------------------------------------------- 资源清理

    private async Task KillCurrentChildAsync()
    {
        Process? process;
        lock (_gate) { process = _process; }
        if (process is not null)
            await KillProcessAsync(process, timeoutMs: 3000);
    }

    private static async Task KillProcessAsync(Process process, int timeoutMs)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true); // yggdrasil 无已知子进程,整树终止以防万一
        }
        catch (InvalidOperationException)
        {
            // 进程恰好已自行退出,无需再杀
        }
        catch (Win32Exception)
        {
            // 访问被拒等系统级拒绝:不冒泡打断 StopAsync/清理路径,等待与 Dispose 兜底
        }

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) { } // 超时未等到:交由 Dispose 收尾
    }

    /// <summary>等待子进程 stdout 排空任务收尾(管道随进程死亡断开属正常现象)。</summary>
    private static async Task AwaitStdoutDrainAsync(Task? drain)
    {
        if (drain is null) return;
        try
        {
            await drain.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (TimeoutException) { }
    }

    private void DisposeTimersLocked()
    {
        _restartTimer?.Dispose();
        _restartTimer = null;
        _stabilityTimer?.Dispose();
        _stabilityTimer = null;
    }

    private AdminSocketClient CreateAdminClient() =>
        new(port: _config.AdminPort, timeoutMs: Math.Min(_pollIntervalMs, 2000));

    private string WriteConfFile()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_config.YggdrasilExePath));
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException($"无法确定 yggdrasil 可执行文件目录:{_config.YggdrasilExePath}");
        var confPath = Path.Combine(dir, ConfFileName);
        File.WriteAllText(confPath, YggdrasilConfigWriter.Generate(_config));
        return confPath;
    }
}
