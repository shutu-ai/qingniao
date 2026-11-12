using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Qingniao.Mesh;

/// <summary>一条 yggdrasil admin 请求:命令名与 arguments 参数对象(如 addPeer 的 uri)。</summary>
public sealed record AdminRequest(string Request, IReadOnlyDictionary<string, string> Fields)
{
    private static readonly IReadOnlyDictionary<string, string> NoFields = new Dictionary<string, string>();

    public static AdminRequest GetSelf() => new("getSelf", NoFields);

    public static AdminRequest GetPeers() => new("getPeers", NoFields);

    public static AdminRequest AddPeer(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return new AdminRequest("addPeer", new Dictionary<string, string> { ["uri"] = uri });
    }
}

/// <summary>一次 admin 调用结果:<c>status</c> 映射为 Success/Error,<c>response</c> 字段作为 payload。</summary>
public sealed record AdminResponse(bool Success, string? Error, JsonElement Payload)
{
    /// <summary>从 payload 顶层取字符串字段;缺失或非字符串返回 null。</summary>
    public string? GetString(string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        return Payload.ValueKind == JsonValueKind.Object
            && Payload.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

/// <summary>
/// yggdrasil admin socket 客户端(对齐真实 v0.5.15 行为,R21):每次 SendAsync 建一条连接,
/// 写一行 JSON 请求(request + arguments 信封),再累计读取响应——真实响应是多行 pretty JSON
/// (SetIndent("", "  ")),故每次读取后尝试解析,成功即返回;EOF 后仍不可解析走 FormatException。
/// 一请求一连接,读至解析成功是安全的(对端应答后关闭)。整次调用受 timeoutMs 约束
/// (经 CancellationToken 传导);内部超时抛 TimeoutException,外部取消仍抛 OperationCanceledException。
/// </summary>
public sealed class AdminSocketClient : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private TcpClient? _inFlight;
    private bool _disposed;

    public AdminSocketClient(string host = "127.0.0.1", int port = 13372, int timeoutMs = 2000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        _host = host;
        _port = port;
        _timeoutMs = timeoutMs;
    }

    public async Task<AdminResponse> SendAsync(AdminRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeoutMs);
        try
        {
            return await SendOnceAsync(request, timeoutCts.Token);
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"yggdrasil admin ({_host}:{_port}) 在 {_timeoutMs}ms 内未响应", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inFlight?.Dispose(); // 若有进行中的调用,断开连接使其尽快失败
    }

    /// <summary>单次问答:每次一条全新连接(超时后的连接不可复用)。</summary>
    private async Task<AdminResponse> SendOnceAsync(AdminRequest request, CancellationToken timeoutToken)
    {
        using var client = new TcpClient();
        _inFlight = client;
        try
        {
            await client.ConnectAsync(_host, _port, timeoutToken);
            return await ExchangeAsync(client, request, timeoutToken);
        }
        finally
        {
            _inFlight = null;
        }
    }

    private static async Task<AdminResponse> ExchangeAsync(TcpClient client, AdminRequest request, CancellationToken timeoutToken)
    {
        await using var stream = client.GetStream();
        await using var writer = new StreamWriter(stream, Utf8NoBom) { NewLine = "\n", AutoFlush = true };
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false);
        await writer.WriteLineAsync(Serialize(request).AsMemory(), timeoutToken);

        // 真实响应是多行 pretty JSON:累计读取,每次尝试解析,成功即返回;EOF 后交给 ParseResponse 收口
        var accumulated = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutToken);
            if (read == 0)
                break; // EOF:yggdrasil 应答后关闭连接
            accumulated.Append(buffer, 0, read);
            if (ParsesAsJson(accumulated))
                return ParseResponse(accumulated.ToString());
        }

        if (accumulated.Length == 0)
            throw new IOException("admin 连接在收到响应前被对端关闭");
        return ParseResponse(accumulated.ToString());
    }

    /// <summary>累计缓冲目前是否已构成一份完整 JSON 文档(部分响应解析必失败,故成功即完整)。</summary>
    private static bool ParsesAsJson(StringBuilder text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text.ToString());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Serialize(AdminRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("request", request.Request);
        if (request.Fields.Count > 0)
        {
            // R21(a):真实 yggdrasil 从 arguments 对象取 handler 参数,顶层字段会被忽略;
            // 无参数请求省略该键(与 Go 侧 arguments,omitempty 语义一致)
            writer.WriteStartObject("arguments");
            foreach (var field in request.Fields)
                writer.WriteString(field.Key, field.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static AdminResponse ParseResponse(string text)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new FormatException($"admin 响应不是合法 JSON:{text}", exception);
        }

        if (GetStringProperty(root, "status") == "success")
        {
            var payload = root.TryGetProperty("response", out var response) ? response.Clone() : default(JsonElement);
            return new AdminResponse(Success: true, Error: null, Payload: payload);
        }

        return new AdminResponse(Success: false, Error: GetStringProperty(root, "error") ?? "未知的 admin 错误", Payload: default);
    }

    private static string? GetStringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
