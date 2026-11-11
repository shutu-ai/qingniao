using Qingniao.SelfCheck.Checks.HardwareEncoder;

namespace Qingniao.SelfCheck.Tests;

/// <summary>
/// HardwareMftFinder 枚举/过滤规则测试(内存假注册表,离线):
/// 只保留名称含 H264/HEVC/H265 且不含 "decoder"(均不区分大小写)的条目(R13:排除解码器);
/// 无名称 CLSID 跳过;保持枚举顺序;空/缺失 HardwareMFT 键 → 空结果。
/// </summary>
public class HardwareMftFinderTests
{
    private const string HardwareMftPath = @"SOFTWARE\Microsoft\Windows Media Foundation\HardwareMFT";
    private const string ClsidRoot = @"SOFTWARE\Classes\CLSID";

    /// <summary>构造假注册表:HardwareMFT 子键列表 + 各 CLSID 路径的默认值(友好名)。</summary>
    private static FakeRegistry RegistryOf(
        string[] hwMftSubKeys,
        Dictionary<string, string?> friendlyNames) =>
        new(
            new Dictionary<string, string[]> { [HardwareMftPath] = hwMftSubKeys },
            friendlyNames.ToDictionary(p => p.Key, p => (string?)p.Value));

    // -------------------------------------------------------------- 核心矩阵(brief Step 1 + R13)

    [Fact]
    public void MixedRegistry_KeepsOnlyNamedEncoders_InOrder()
    {
        var registry = RegistryOf(
            hwMftSubKeys: ["{1111}", "{2222}", "{3333}", "{4444}", "{5555}", "{6666}"],
            friendlyNames: new Dictionary<string, string?>
            {
                // 两个真编码器:按枚举顺序保留
                [ClsidRoot + @"\{1111}"] = "NVIDIA H264 Encoder MFT",
                [ClsidRoot + @"\{2222}"] = "Intel HEVC Encoder MFT",
                // 含 "Decoder" 但无编解码关键词 → 名称过滤排除(brief 原)
                [ClsidRoot + @"\{3333}"] = "Foo Decoder MFT",
                // 含 H264 但是解码器 → R13 排除(防止把解码器误报成编码器)
                [ClsidRoot + @"\{4444}"] = "Intel H264 Decoder MFT",
                // 无名称 CLSID(null)→ 跳过(brief 原)
                // [ClsidRoot + @"\{5555}"] 故意不登记默认值
                // 空字符串名称 → 同样跳过
                [ClsidRoot + @"\{6666}"] = "",
            });

        var found = HardwareMftFinder.Find(registry);

        var expected = new[]
        {
            new EncoderInfo("{1111}", "NVIDIA H264 Encoder MFT"),
            new EncoderInfo("{2222}", "Intel HEVC Encoder MFT"),
        };
        Assert.Equal(expected, found);
    }

    [Fact]
    public void EmptyOrMissingHardwareMft_ReturnsEmpty()
    {
        // 假注册表完全无数据:HardwareMFT 键缺失与空表走同一"不存在"路径 → []
        var found = HardwareMftFinder.Find(
            new FakeRegistry(
                new Dictionary<string, string[]>(),
                new Dictionary<string, string?>()));

        Assert.Empty(found);
    }

    // -------------------------------------------------------------- 名称匹配细节

    [Fact]
    public void NameMatching_IsCaseInsensitive()
    {
        var registry = RegistryOf(
            hwMftSubKeys: ["{a}", "{b}", "{c}", "{d}"],
            friendlyNames: new Dictionary<string, string?>
            {
                // 全小写名称 → 保留
                [ClsidRoot + @"\{a}"] = "nvidia h264 encoder mft",
                // H265 同样算硬件编码(与 HEVC 同族)→ 保留
                [ClsidRoot + @"\{b}"] = "Intel H265 Encoder MFT",
                // 全大写 DECODER → 排除
                [ClsidRoot + @"\{c}"] = "AMD H264 DECODER MFT",
                // 小写 hevc 但语义为解码 → 排除
                [ClsidRoot + @"\{d}"] = "intel hevc decoder mft",
            });

        var found = HardwareMftFinder.Find(registry);

        var expected = new[]
        {
            new EncoderInfo("{a}", "nvidia h264 encoder mft"),
            new EncoderInfo("{b}", "Intel H265 Encoder MFT"),
        };
        Assert.Equal(expected, found);
    }

    [Fact]
    public void ClsidPath_IsUnderClassesClsidRoot()
    {
        // 夹具:友好名登记在错误路径下时找不到 → 结果为空;
        // 正确路径(SOFTWARE\Classes\CLSID\{clsid} 默认值)才能取到名称。
        var emptyOnWrongPath = RegistryOf(
            hwMftSubKeys: ["{1111}"],
            friendlyNames: new Dictionary<string, string?>
            {
                [@"SOFTWARE\Classes\Wow6432Node\CLSID\{1111}"] = "NVIDIA H264 Encoder MFT",
            });
        Assert.Empty(HardwareMftFinder.Find(emptyOnWrongPath));
    }
}
