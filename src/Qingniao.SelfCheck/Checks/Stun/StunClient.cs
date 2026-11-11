using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Qingniao.SelfCheck.Checks.Stun;

/// <summary>
/// 一次 STUN 查询的结论。<see cref="XorMapped"/> 承载“当前已知的最佳映射”(优先 XOR-MAPPED-ADDRESS,
/// 服务器仅回 MAPPED-ADDRESS 时回退使用之,字段名保持接口契约);<see cref="Other"/> 为备用服务器端点
/// (OTHER-ADDRESS 优先,缺失时回退 CHANGED-ADDRESS——RFC 3489 / RFC 5780 两种服务器都覆盖)。
/// </summary>
public sealed record StunResult(IPEndPoint XorMapped, IPEndPoint? Other);

/// <summary>
/// 最小 STUN 客户端(RFC 5389 Binding 请求):发一次请求,在截止时间内循环收包,
/// 取第一个事务 ID 匹配的响应构造 <see cref="StunResult"/>。
/// 契约:超时 / DNS 失败 / 套接字错误 / 不可解析输入 / 外部取消 一律返回 null,绝不抛出(fire-and-forget 语义)。
/// 超时通过 <see cref="TimeProvider"/> 驱动(测试可注入虚拟时钟)。
/// </summary>
public sealed class StunClient
{
    private const int TransactionIdLength = 12;
    private const int MinDatagramLength = 20;
    private const int MaxDatagramLength = 65535; // UDP 单包上限,按实收长度截取

    private readonly TimeProvider _time;

    public StunClient(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>查询 server("host:port")的公网映射。任何失败 → null。</summary>
    public async Task<StunResult?> QueryAsync(string server, int timeoutMs = 1500, CancellationToken ct = default)
    {
        try
        {
            (string Host, int Port)? endpoint = ParseServerEndpoint(server);
            if (endpoint is null || timeoutMs <= 0)
            {
                return null;
            }

            // 设计约定:仅尝试 IPv4 地址(STUN 自检场景;IPv6 检查由 ipv6 check 负责)
            IPAddress? ipv4 = (await Dns.GetHostAddressesAsync(endpoint.Value.Host, ct).ConfigureAwait(false))
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is null)
            {
                return null;
            }

            byte[] tid = new byte[TransactionIdLength];
            RandomNumberGenerator.Fill(tid);
            byte[] request = StunMessage.BuildBindingRequest(transactionId: tid);

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // net10 CreateTimer 签名:(callback, state, dueTime, period);到期回调取消链路 token
            using ITimer timer = _time.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                deadline,
                TimeSpan.FromMilliseconds(timeoutMs),
                Timeout.InfiniteTimeSpan);

            await socket.ConnectAsync(new IPEndPoint(ipv4, endpoint.Value.Port), deadline.Token).ConfigureAwait(false);
            await socket.SendAsync(request, SocketFlags.None, deadline.Token).ConfigureAwait(false);

            byte[] buffer = new byte[MaxDatagramLength];
            while (!deadline.IsCancellationRequested)
            {
                int received;
                try
                {
                    received = await socket.ReceiveAsync(
                        buffer.AsMemory(0, buffer.Length), SocketFlags.None, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null; // 超时或外部取消
                }
                catch (SocketException)
                {
                    return null; // 套接字错误(含超长包被截断)
                }

                if (received < MinDatagramLength)
                {
                    continue; // 非法短包 → 继续等
                }

                StunMessage? message = StunMessage.Parse(buffer[..received], tid);
                if (message is null)
                {
                    continue; // tid 不匹配或残缺 → 取下一个包
                }

                IPEndPoint? mapped = message.XorMapped ?? message.Mapped;
                if (mapped is null)
                {
                    continue; // 无地址属性 → 继续等(截止时间兜底)
                }

                return new StunResult(mapped, message.Other ?? message.Changed);
            }

            return null;
        }
        catch
        {
            // 契约:绝不抛出——外部取消、DNS 解析异常等一律降级为 null(fire-and-forget)
            return null;
        }
    }

    /// <summary>解析 "host:port"(最后一个冒号切分;host/port 任一非法 → null)。</summary>
    private static (string Host, int Port)? ParseServerEndpoint(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return null;
        }

        int colon = server.LastIndexOf(':');
        if (colon <= 0 || colon >= server.Length - 1)
        {
            return null;
        }

        if (!ushort.TryParse(server[(colon + 1)..], out ushort port))
        {
            return null;
        }

        return (server[..colon], port);
    }
}
