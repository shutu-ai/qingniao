namespace Qingniao.SelfCheck.Checks.HardwareEncoder;

/// <summary>一个硬件 MFT 条目。Clsid 为 HardwareMFT 子键(GUID),Name 为 CLSID 默认值(友好名)。</summary>
public sealed record EncoderInfo(string Clsid, string Name);

/// <summary>
/// 硬件 MFT 枚举器:对注入的注册表适配器做纯函数式枚举(HardwareMFT 子键 → CLSID 默认值),
/// 不含任何 I/O 实现,全部注册表访问经由 <see cref="IRegistryAdapter"/>。
/// 过滤规则(R13):仅保留名称含 H264/HEVC/H265 且不含 "decoder" 的条目(均不区分大小写)
/// ——HardwareMFT 键同时登记硬件解码器,名称同样带编解码关键词,只按关键词匹配会把
/// 解码器误报成编码器,得出假 Pass。
/// </summary>
public static class HardwareMftFinder
{
    /// <summary>硬件 MFT 注册位置:每个子键为一个 CLSID。</summary>
    public const string HardwareMftPath = @"SOFTWARE\Microsoft\Windows Media Foundation\HardwareMFT";

    /// <summary>COM CLSID 根:以 CLSID 子键的默认值承载 MFT 友好名。</summary>
    public const string ClsidRootPath = @"SOFTWARE\Classes\CLSID";

    /// <summary>
    /// 枚举硬件视频编码器。保持 HardwareMFT 的枚举顺序;无默认值/空名称的 CLSID 跳过;
    /// 注册表故障由适配器抛出,不由本方法吞掉(上层按 R10 映射 Unknown)。
    /// </summary>
    public static IReadOnlyList<EncoderInfo> Find(IRegistryAdapter registry)
    {
        List<EncoderInfo> encoders = [];
        foreach (var clsid in registry.GetSubKeyNames(HardwareMftPath))
        {
            // 适配器约定:null 或空 valueName 读默认值;接口形参不可空,这里用空串
            var name = registry.GetValue($@"{ClsidRootPath}\{clsid}", valueName: "");
            if (string.IsNullOrWhiteSpace(name) || !IsEncoderName(name))
            {
                continue;
            }

            encoders.Add(new EncoderInfo(clsid, name));
        }

        return encoders;
    }

    /// <summary>R13:含 H264/HEVC/H265(编码器特征)且不含 "decoder"(排除解码器),不区分大小写。</summary>
    private static bool IsEncoderName(string name) =>
        (name.Contains("H264", StringComparison.OrdinalIgnoreCase)
            || name.Contains("HEVC", StringComparison.OrdinalIgnoreCase)
            || name.Contains("H265", StringComparison.OrdinalIgnoreCase))
        && !name.Contains("Decoder", StringComparison.OrdinalIgnoreCase);
}
