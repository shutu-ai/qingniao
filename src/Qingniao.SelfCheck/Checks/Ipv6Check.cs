using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Qingniao.SelfCheck.Checks;

/// <summary>
/// IPv6 环境检查:枚举 up 且非 loopback 网卡的单播地址 → <see cref="AddressFilter.SelectGlobal"/>
/// 取全局地址;0 个 → Fail;有地址但出站探测失败 → Warn;出站可达 → Pass。
/// </summary>
/// <remarks>
/// R10 契约:<see cref="RunAsync"/> 永不抛出——每个失败模式都降级为 CheckResult:
/// 枚举异常 → Fail("无法枚举网络接口地址")、枚举被取消 → Unknown("检查已取消")、
/// 探测异常/取消 → 视同出站失败 → Warn;Detail 均带异常消息。
/// </remarks>
public sealed class Ipv6Check : IEnvironmentCheck
{
    public const string CheckName = "ipv6";

    private static readonly IPAddress ProbeTarget = IPAddress.Parse("2606:4700:4700::1111");
    private const int ProbePort = 443;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly Func<IEnumerable<IPAddress>> _enumerateAddresses;
    private readonly Func<Task<bool>>? _outboundProbe; // null → 用默认真实探测(链接 RunAsync 的 ct)

    /// <summary>注入假委托用于测试;两个参数均为 null 时使用默认真实实现。</summary>
    public Ipv6Check(
        Func<IEnumerable<IPAddress>>? enumerateAddresses = null,
        Func<Task<bool>>? outboundProbe = null)
    {
        _enumerateAddresses = enumerateAddresses ?? DefaultEnumerateAddresses;
        _outboundProbe = outboundProbe;
    }

    public string Name => CheckName;

    public async Task<CheckResult> RunAsync(CancellationToken ct)
    {
        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = AddressFilter.SelectGlobal(_enumerateAddresses());
        }
        catch (OperationCanceledException ex)
        {
            return new CheckResult(Name, CheckStatus.Unknown, "检查已取消", ex.Message);
        }
        catch (Exception ex)
        {
            // 裁定(R10):枚举失败无法判定 IPv6 有无,按 Fail + Detail 报告,交由人工诊断。
            return new CheckResult(Name, CheckStatus.Fail, "无法枚举网络接口地址", ex.Message);
        }

        if (addresses.Count == 0)
        {
            return new CheckResult(Name, CheckStatus.Fail, "未发现全局 IPv6(光猫/路由器可能未开 IPv6)");
        }

        bool reachable;
        try
        {
            reachable = _outboundProbe is not null
                ? await _outboundProbe()
                : await ProbeOutboundAsync(ct);
        }
        catch (Exception ex)
        {
            // 裁定(R10):探测异常视同出站失败 → Warn + Detail。
            return new CheckResult(Name, CheckStatus.Warn, "有全局 IPv6 但出站不可达(防火墙拦截?)", ex.Message);
        }

        if (!reachable)
        {
            return new CheckResult(Name, CheckStatus.Warn, "有全局 IPv6 但出站不可达(防火墙拦截?)");
        }

        return new CheckResult(Name, CheckStatus.Pass, $"全局 IPv6 可用: {addresses.Count} 个地址");
    }

    /// <summary>默认地址枚举:up 且非 loopback 网卡的全部单播地址(全局性过滤交给 SelectGlobal)。</summary>
    private static IEnumerable<IPAddress> DefaultEnumerateAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                yield return unicast.Address;
            }
        }
    }

    /// <summary>
    /// 默认出站探测:TCP 连 2606:4700:4700::1111:443,3s 超时(与外部 ct 取先到者)。
    /// 仅 IPv6 套接字,无 IPv4 回退;超时/无路由 → false。
    /// </summary>
    private static async Task<bool> ProbeOutboundAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetworkV6);
            await client.ConnectAsync(ProbeTarget, ProbePort, timeoutCts.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // 3s 超时 → 出站不可达
        }
        catch (SocketException)
        {
            return false; // 无 IPv6 路由等 → 出站不可达(快速失败)
        }
    }
}
