using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Qingniao.LinkManager;

/// <summary>VPN 网段扫描的最小抽象(R15,Task 14):仅暴露 MeshRuntime 组装所需的 ScanAsync。</summary>
public interface IVpnCidrScanner
{
    /// <summary>扫描全部 CIDR,逐个产出存活探测结果(完成顺序)。取消经 <paramref name="ct"/> 传播。</summary>
    IAsyncEnumerable<ProbeResult> ScanAsync(
        IReadOnlyList<string> cidrs, int port, CancellationToken ct = default);
}

/// <summary>单台主机的 TCP 存活探测结果。<see cref="LatencyMs"/> 恒 ≥ 0(未连通也报告实际耗时)。</summary>
/// <param name="Address">被探测的主机地址。</param>
/// <param name="IsOpen">TCP 连接是否成功建立。</param>
/// <param name="LatencyMs">本次连接尝试的实测耗时(毫秒),含失败与超时。</param>
public sealed record ProbeResult(IPAddress Address, bool IsOpen, int LatencyMs);

/// <summary>
/// VPN 网段 TCP 存活扫描器:枚举各 CIDR 的主机地址并发起单次 TCP 连接探测。
/// </summary>
/// <remarks>
/// 并发上限 64(有界任务窗口);单点探测超时 500ms。主机不可达(拒绝、超时、套接字错误)只产生
/// <see cref="ProbeResult"/>(<c>IsOpen=false</c>),不会使扫描中断;CIDR 字符串非法属于调用方编程错误,原样抛出。
/// 结果按完成顺序产出(不保证地址顺序)。取消令牌触发后立即停止调度新探测并终止流。
/// </remarks>
public sealed class VpnCidrScanner : IVpnCidrScanner
{
    /// <summary>同时在途的探测任务数上限。</summary>
    private const int MaxConcurrency = 64;

    /// <summary>单次 TCP 连接尝试的超时(毫秒)。</summary>
    private const int ProbeTimeoutMs = 500;

    /// <summary>扫描全部 CIDR,逐个产出存活探测结果(完成顺序)。</summary>
    /// <param name="cidrs">CIDR 字符串列表;空列表产出空流,null 抛 ArgumentNullException。</param>
    /// <param name="port">探测的 TCP 端口。</param>
    /// <param name="ct">取消令牌:取消后停止调度新探测并使流以 OperationCanceledException 终止。</param>
    public async IAsyncEnumerable<ProbeResult> ScanAsync(
        IReadOnlyList<string> cidrs, int port, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cidrs);

        var inFlight = new List<Task<ProbeResult>>();
        try
        {
            foreach (string cidr in cidrs)
            {
                ct.ThrowIfCancellationRequested();
                foreach (IPAddress address in CidrRange.EnumerateHosts(cidr))
                {
                    ct.ThrowIfCancellationRequested();
                    if (inFlight.Count >= MaxConcurrency)
                    {
                        yield return await AwaitOne(inFlight, ct).ConfigureAwait(false);
                    }
                    inFlight.Add(ProbeAsync(address, port, ct));
                }
            }

            while (inFlight.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                yield return await AwaitOne(inFlight, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // 流被中途放弃(异常/取消/提前退出)时,排空在途任务,避免未观察异常与句柄泄漏。
            try
            {
                await Task.WhenAll(inFlight).ConfigureAwait(false);
            }
            catch
            {
                // 扫描已中止,在途探测的失败(含取消)不影响调用方看到的异常。
            }
        }
    }

    /// <summary>等待在途窗口中最早完成的探测并出窗;其结果交由调用方产出。</summary>
    private static async Task<ProbeResult> AwaitOne(List<Task<ProbeResult>> inFlight, CancellationToken ct)
    {
        Task<ProbeResult> completed = await Task.WhenAny(inFlight).WaitAsync(ct).ConfigureAwait(false);
        inFlight.Remove(completed);
        return await completed.ConfigureAwait(false);
    }

    /// <summary>对单台主机发起一次 TCP 连接:成功 → IsOpen=true;超时/拒绝/套接字错误 → IsOpen=false;外层取消 → 抛出。</summary>
    private static async Task<ProbeResult> ProbeAsync(IPAddress address, int port, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeoutMs);
        long startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, timeoutCts.Token).ConfigureAwait(false);
            return new ProbeResult(address, true, ElapsedMs(startTimestamp));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ProbeResult(address, false, ElapsedMs(startTimestamp));
        }
    }

    private static int ElapsedMs(long startTimestamp)
    {
        return (int)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
    }
}
