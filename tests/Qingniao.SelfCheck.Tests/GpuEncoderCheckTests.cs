using System.Security;
using Qingniao.SelfCheck.Checks;
using Qingniao.SelfCheck.Checks.HardwareEncoder;

namespace Qingniao.SelfCheck.Tests;

/// <summary>
/// GpuEncoderCheck 三态判定与 R10 永不抛出契约测试(全部注入内存假注册表,无真实注册表 I/O;
/// Live 用例除外,单独 <c>Category=Live</c> 跑)。
/// </summary>
public class GpuEncoderCheckTests
{
    private const string HardwareMftPath = @"SOFTWARE\Microsoft\Windows Media Foundation\HardwareMFT";
    private const string ClsidRoot = @"SOFTWARE\Classes\CLSID";

    /// <summary>构造返回固定编码器列表的假注册表工厂。</summary>
    private static Func<IRegistryAdapter> RegistryWith(params (string Clsid, string Name)[] encoders)
    {
        var friendlyNames = encoders.ToDictionary(
            e => ClsidRoot + @"\" + e.Clsid, e => (string?)e.Name);
        var registry = new FakeRegistry(
            new Dictionary<string, string[]> { [HardwareMftPath] = encoders.Select(e => e.Clsid).ToArray() },
            friendlyNames);
        return () => registry;
    }

    private static readonly Func<IRegistryAdapter> EmptyRegistry =
        RegistryWith();

    // -------------------------------------------------------------- 基本契约

    [Fact]
    public void Name_IsGpuEncoder()
    {
        Assert.Equal("gpu-encoder", new GpuEncoderCheck(EmptyRegistry).Name);
    }

    // -------------------------------------------------------------- 三态判定

    [Fact]
    public async Task EncodersFound_ReturnsPassWithJoinedNames()
    {
        var check = new GpuEncoderCheck(RegistryWith(
            ("{1111}", "NVIDIA H264 Encoder MFT"),
            ("{2222}", "Intel HEVC Encoder MFT")));

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal("gpu-encoder", result.Name);
        Assert.Equal(CheckStatus.Pass, result.Status);
        Assert.Equal("硬件编码器: NVIDIA H264 Encoder MFT, Intel HEVC Encoder MFT", result.Summary);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task NoEncoders_ReturnsWarnWithExactCopy()
    {
        var check = new GpuEncoderCheck(EmptyRegistry);

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Warn, result.Status);
        Assert.Equal("未发现硬件 H264/HEVC 编码器,串流将退回软编,性能受限", result.Summary);
        Assert.Null(result.Detail);
    }

    // -------------------------------------------------------------- R10:读取失败 ≠ 没有编码器

    /// <summary>抛注册表访问异常的假适配器(模拟真实 SecurityException/IOException)。</summary>
    private sealed class ThrowingRegistry : IRegistryAdapter
    {
        public string[] GetSubKeyNames(string keyPath) =>
            throw new SecurityException("拒绝访问注册表项");

        public string? GetValue(string keyPath, string valueName) =>
            throw new InvalidOperationException("注册表句柄已释放");
    }

    [Fact]
    public async Task AdapterThrows_ReturnsUnknownWithExceptionMessage_NeverWarn()
    {
        var check = new GpuEncoderCheck(() => new ThrowingRegistry());

        var result = await check.RunAsync(CancellationToken.None);

        // R10:读取失败必须与"没有编码器"区分——Unknown + 异常消息,而非 Warn
        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Equal("注册表读取失败", result.Summary);
        Assert.Equal("拒绝访问注册表项", result.Detail);
    }

    [Fact]
    public async Task AdapterThrowsOnLateRead_StillUnknown_NeverThrows()
    {
        // 枚举子键成功但随后读名称失败 → 同样走 Unknown(异常发生在 Find 中段)
        var registry = new FlakyAfterEnumerateRegistry();
        var check = new GpuEncoderCheck(() => registry);

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(CheckStatus.Unknown, result.Status);
        Assert.Equal("注册表读取失败", result.Summary);
        Assert.Equal("读取 CLSID 默认值失败", result.Detail);
    }

    /// <summary>枚举正常、读值抛异常的假适配器(覆盖 Find 中段失败的降级路径)。</summary>
    private sealed class FlakyAfterEnumerateRegistry : IRegistryAdapter
    {
        public string[] GetSubKeyNames(string keyPath) => ["{1111}"];

        public string? GetValue(string keyPath, string valueName) =>
            throw new InvalidOperationException("读取 CLSID 默认值失败");
    }

    // -------------------------------------------------------------- Live(真机注册表,单跑)

    [Fact]
    [Trait("Category", "Live")]
    public async Task RealRegistry_RunAsync_ReturnsAnsweredStatus_NeverThrows()
    {
        var result = await new GpuEncoderCheck().RunAsync(CancellationToken.None);

        Assert.Equal("gpu-encoder", result.Name);
        Assert.NotEqual(CheckStatus.Fail, result.Status); // 永不抛出,也不会凭空 Fail
        if (result.Status == CheckStatus.Pass)
        {
            Assert.StartsWith("硬件编码器: ", result.Summary);
        }
        if (result.Status == CheckStatus.Unknown)
        {
            Assert.NotNull(result.Detail); // 失败细节可诊断
        }
    }
}
