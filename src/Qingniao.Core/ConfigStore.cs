using System.Text.Json;

namespace Qingniao.Core;

/// <summary>
/// Mesh 配置的持久化加载与保存。文件缺失时 <see cref="LoadOrDefault"/> 回落默认值;
/// 文件缺失(<see cref="Load"/>)或 JSON 非法时抛 <see cref="ConfigException"/>,消息含路径。
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new(ReadOptions)
    {
        WriteIndented = true,
    };

    private static readonly MeshConfig Defaults = new();

    public static MeshConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"配置文件不存在: {path}");
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"配置文件读取失败: {path}", ex);
        }

        MeshConfigDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<MeshConfigDto>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"配置文件不是合法 JSON: {path}", ex);
        }

        if (dto is null)
        {
            throw new ConfigException($"配置文件内容为空: {path}");
        }

        return FromDto(dto);
    }

    public static MeshConfig LoadOrDefault(string path) =>
        File.Exists(path) ? Load(path) : new MeshConfig();

    public static void Save(MeshConfig config, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        File.WriteAllText(path, JsonSerializer.Serialize(config, WriteOptions));
    }

    /// <summary>将 DTO 映射到 record;JSON 中缺失的字段回落到默认值。</summary>
    private static MeshConfig FromDto(MeshConfigDto dto) => new()
    {
        MeshPort = dto.MeshPort ?? Defaults.MeshPort,
        AdminPort = dto.AdminPort ?? Defaults.AdminPort,
        YggdrasilExePath = dto.YggdrasilExePath ?? Defaults.YggdrasilExePath,
        VpnCidrs = dto.VpnCidrs ?? [.. Defaults.VpnCidrs],
        StunServers = dto.StunServers ?? [.. Defaults.StunServers],
        BandwidthEndpoint = dto.BandwidthEndpoint,
    };

    /// <summary>可变反序列化目标,避免直接反序列化到只读集合属性。</summary>
    private sealed class MeshConfigDto
    {
        public int? MeshPort { get; set; }

        public int? AdminPort { get; set; }

        public string? YggdrasilExePath { get; set; }

        public List<string>? VpnCidrs { get; set; }

        public List<string>? StunServers { get; set; }

        public string? BandwidthEndpoint { get; set; }
    }
}

/// <summary>配置文件缺失或内容非法时抛出。</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }

    public ConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
