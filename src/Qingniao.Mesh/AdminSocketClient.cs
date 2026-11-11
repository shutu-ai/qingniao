using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Qingniao.Mesh;

/// <summary>一条 yggdrasil admin 请求:命令名与附加字段(如 addPeer 的 uri)。</summary>
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
/// yggdrasil admin socket 行式 JSON 客户端:每次 SendAsync 建一条连接,写一行 JSON 请求、读一行 JSON 响应。
/// 整次调用受 timeoutMs 约束(经 CancellationToken 传导);内部超时抛 TimeoutException,外部取消仍抛 OperationCanceledException。
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
        var line = await reader.ReadLineAsync(timeoutToken); // 对端不会半关闭,等一整行或超时
        if (line is null)
            throw new IOException("admin 连接在收到响应前被对端关闭");
        return ParseResponse(line);
    }

    private static string Serialize(AdminRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("request", request.Request);
        foreach (var field in request.Fields)
            writer.WriteString(field.Key, field.Value);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static AdminResponse ParseResponse(string line)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new FormatException($"admin 响应不是合法 JSON:{line}", exception);
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
