using Qingniao.SelfCheck.Checks.HardwareEncoder;

namespace Qingniao.SelfCheck.Checks;

/// <summary>
/// 硬件编码器自检:枚举 HKLM HardwareMFT 注册的硬件 MFT(<see cref="HardwareMftFinder.Find"/>),
/// 回答 M0 的"这台机器有没有 H264/HEVC 硬件编码器"。
/// ≥1 → Pass(列名称);0 → Warn(退回软编,性能受限);读取失败 → Unknown(R10:读失败 ≠ 没有编码器)。
/// </summary>
public sealed class GpuEncoderCheck : IEnvironmentCheck
{
    public const string CheckName = "gpu-encoder";

    private const string WarnSummary = "未发现硬件 H264/HEVC 编码器,串流将退回软编,性能受限";
    private const string UnknownSummary = "注册表读取失败";

    private readonly Func<IRegistryAdapter> _createAdapter;

    /// <summary>注入假注册表工厂用于测试;缺省时用真实注册表适配器(64 位视图)。</summary>
    public GpuEncoderCheck(Func<IRegistryAdapter>? createAdapter = null) =>
        _createAdapter = createAdapter ?? (() => new RegistryAdapter());

    public string Name => CheckName;

    public Task<CheckResult> RunAsync(CancellationToken ct)
    {
        // 只读本地注册表,毫秒级完成:无取消点,ct 按接口契约仅接受并透传,不使用
        IReadOnlyList<EncoderInfo> encoders;
        try
        {
            encoders = HardwareMftFinder.Find(_createAdapter());
        }
        catch (Exception ex)
        {
            // R10:读取失败必须与"没有编码器"区分——降级 Unknown 而非 Warn,细节进 Detail
            return Task.FromResult(
                new CheckResult(Name, CheckStatus.Unknown, UnknownSummary, ex.Message));
        }

        return Task.FromResult(encoders.Count == 0
            ? new CheckResult(Name, CheckStatus.Warn, WarnSummary)
            : new CheckResult(Name, CheckStatus.Pass, $"硬件编码器: {string.Join(", ", encoders.Select(e => e.Name))}"));
    }
}
