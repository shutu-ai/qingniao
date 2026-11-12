using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;

namespace Qingniao.SelfCheck.Checks;

/// <summary>
/// 带宽自检:未配置端点时枚举非 loopback 网卡的链路速率取最大(brief 的 Pass 文案,
/// 明示"未测实际带宽");配置 BandwidthEndpoint 时对该 URL 上/下行各一个时间窗实测吞吐。
/// </summary>
/// <remarks>
/// R10 契约:<see cref="RunAsync"/> 永不抛出。降级口径(取舍见任务报告):
/// 网卡枚举失败/无有效速率 → Unknown(证据不可得);端点测速失败(不可达、超时、非 2xx)
/// → Warn(端点是可选增强,失败不否定本机可用性,原因进 Detail);外部取消 → Unknown。
/// 测速窗口默认 3s(上/下行各一窗),测试可经 measureWindow 缩短。
/// </remarks>
public sealed class BandwidthCheck : IEnvironmentCheck
{
    public const string CheckName = "bandwidth";

    private const string CancelledSummary = "带宽测速被取消";
    private const string FailedSummary = "带宽测速失败,未取得实测吞吐";

    private const int DownloadBufferBytes = 64 * 1024;
    private const int UploadChunkBytes = 256 * 1024;

    private readonly string? _endpoint;
    private readonly Func<IEnumerable<(string Name, long BitsPerSecond)>> _listNics;
    private readonly HttpMessageHandler? _httpHandler;
    private readonly TimeSpan _measureWindow;

    /// <summary>注入假网卡枚举/假 HttpMessageHandler 用于测试;缺省时枚举真实网卡、用默认 HttpClient 栈。</summary>
    public BandwidthCheck(
        string? endpoint = null,
        Func<IEnumerable<(string Name, long BitsPerSecond)>>? listNics = null,
        HttpMessageHandler? httpHandler = null,
        TimeSpan? measureWindow = null)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint;
        _listNics = listNics ?? ListNetworkInterfaces;
        _httpHandler = httpHandler;
        _measureWindow = measureWindow ?? TimeSpan.FromSeconds(3);
    }

    public string Name => CheckName;

    public async Task<CheckResult> RunAsync(CancellationToken ct)
    {
        if (_endpoint is null)
        {
            return RunNicSpeed(); // 只读本机,自含降级路径
        }

        try
        {
            (double downMbps, double upMbps) =
                await MeasureThroughputAsync(ct).ConfigureAwait(false);
            return new CheckResult(
                Name, CheckStatus.Pass,
                $"实测带宽: 下行 {downMbps:F1} Mbps,上行 {upMbps:F1} Mbps",
                $"测速端点 {_endpoint},单向上/下行各 {_measureWindow.TotalSeconds} 秒窗口");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new CheckResult(Name, CheckStatus.Unknown, CancelledSummary);
        }
        catch (Exception ex)
        {
            return new CheckResult(Name, CheckStatus.Warn, FailedSummary, ex.Message);
        }
    }

    /// <summary>无端点路径:非 loopback 网卡最大链路速率;枚举失败或无有效速率降级 Unknown。</summary>
    private CheckResult RunNicSpeed()
    {
        IEnumerable<(string Name, long BitsPerSecond)> nics;
        try
        {
            nics = _listNics();
        }
        catch (Exception ex)
        {
            return new CheckResult(Name, CheckStatus.Unknown, "网卡枚举失败", ex.Message);
        }

        (string Name, long BitsPerSecond) best = (string.Empty, 0);
        foreach ((string name, long bitsPerSecond) in nics)
        {
            if (bitsPerSecond > best.BitsPerSecond)
            {
                best = (name, bitsPerSecond);
            }
        }

        if (best.BitsPerSecond <= 0)
        {
            return new CheckResult(
                Name, CheckStatus.Unknown, "未获取到网卡链路速率",
                "所有非 loopback 网卡的链路速率均为 0 或未知");
        }

        return new CheckResult(
            Name, CheckStatus.Pass,
            $"网卡链路速率: {best.BitsPerSecond / 1_000_000.0:F1} Mbps(未测实际带宽)",
            $"取最大值网卡: {best.Name}");
    }

    /// <summary>端点路径:下行按响应流读满一个窗口,上行按 256KB 分块 POST 到窗口耗尽。</summary>
    private async Task<(double DownMbps, double UpMbps)> MeasureThroughputAsync(CancellationToken ct)
    {
        using var client = _httpHandler is not null
            ? new HttpClient(_httpHandler)
            : new HttpClient();

        double downMbps = await MeasureDownAsync(client, ct).ConfigureAwait(false);
        double upMbps = await MeasureUpAsync(client, ct).ConfigureAwait(false);
        return (downMbps, upMbps);
    }

    /// <summary>下行:流式读取响应直到流结束或窗口耗尽,按实际耗时换算 Mbps。</summary>
    private async Task<double> MeasureDownAsync(HttpClient client, CancellationToken ct)
    {
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        windowCts.CancelAfter(_measureWindow);
        long startTimestamp = Stopwatch.GetTimestamp();

        using HttpResponseMessage response = await client
            .GetAsync(_endpoint, HttpCompletionOption.ResponseHeadersRead, windowCts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(windowCts.Token)
            .ConfigureAwait(false);

        long totalBytes = 0;
        var buffer = new byte[DownloadBufferBytes];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, windowCts.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                totalBytes += read;
                if (Stopwatch.GetElapsedTime(startTimestamp) >= _measureWindow)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
            when (windowCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // 窗口耗尽中断读取:用已收字节计算,不算失败
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        if (totalBytes == 0)
        {
            throw new InvalidOperationException("下行测速窗口内未收到数据");
        }

        return ToMbps(totalBytes, elapsed);
    }

    /// <summary>上行:循环 POST 256KB 分块直到窗口耗尽,按已完成分块字节换算 Mbps。</summary>
    private async Task<double> MeasureUpAsync(HttpClient client, CancellationToken ct)
    {
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        windowCts.CancelAfter(_measureWindow);
        long startTimestamp = Stopwatch.GetTimestamp();
        long totalBytes = 0;
        var payload = new byte[UploadChunkBytes];

        try
        {
            while (Stopwatch.GetElapsedTime(startTimestamp) < _measureWindow)
            {
                using var content = new ByteArrayContent(payload);
                using HttpResponseMessage response = await client
                    .PostAsync(_endpoint, content, windowCts.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                totalBytes += payload.Length;
            }
        }
        catch (OperationCanceledException)
            when (windowCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // 窗口耗尽(某个 POST 被打断):用已完成分块计算,不算失败
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        if (totalBytes == 0)
        {
            throw new InvalidOperationException("上行测速窗口内未发出数据");
        }

        return ToMbps(totalBytes, elapsed);
    }

    /// <summary>字节量/耗时 → Mbps(bits / 1e6)。</summary>
    private static double ToMbps(long bytes, TimeSpan elapsed) =>
        bytes * 8.0 / elapsed.TotalSeconds / 1_000_000.0;

    /// <summary>真实网卡枚举:非 loopback 全部纳入(速率 ≤0 的条目在取最大值时自然被过滤)。</summary>
    private static IEnumerable<(string Name, long BitsPerSecond)> ListNetworkInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => (n.Name, n.Speed));
}
