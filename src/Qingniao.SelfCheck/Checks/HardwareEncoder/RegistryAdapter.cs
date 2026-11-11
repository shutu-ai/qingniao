using Microsoft.Win32;

namespace Qingniao.SelfCheck.Checks.HardwareEncoder;

/// <summary>
/// 注册表只读适配器(可注入,离线测试)。约定:
/// <list type="bullet">
/// <item><c>keyPath</c> 为相对 HKLM 根的子键路径,不含 hive 前缀(适配器固定知道 hive),
/// 如 <c>SOFTWARE\Microsoft\Windows Media Foundation\HardwareMFT</c>。</item>
/// <item><c>GetSubKeyNames</c>:键不存在 → 返回空数组,不抛。</item>
/// <item><c>GetValue</c>:值不存在 → 返回 null;<c>valueName</c> 传 null 或空串读键的<b>默认值</b>。</item>
/// <item>键/值缺失是正常返回值;但注册表级故障(如 <see cref="System.Security.SecurityException"/>、
/// IOException)原样抛出,由上层(R10)映射为 Unknown。</item>
/// </list>
/// </summary>
public interface IRegistryAdapter
{
    string[] GetSubKeyNames(string keyPath);

    string? GetValue(string keyPath, string valueName);
}

/// <summary>
/// 真实注册表适配器:固定以 <see cref="RegistryView.Registry64"/> 打开 HKLM
/// (32 位进程也能看到 64 位 MFT 注册;HardwareMFT 只有 64 位视图下完整)。
/// 打开的键全部 dispose;非 Windows 平台按"键不存在"返回(本项目目标 Windows,纯防御)。
/// </summary>
public sealed class RegistryAdapter : IRegistryAdapter
{
    public string[] GetSubKeyNames(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(keyPath);
        return key?.GetSubKeyNames() ?? [];
    }

    public string? GetValue(string keyPath, string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(keyPath);
        if (key is null)
        {
            return null;
        }

        // 默认值的注册值名为空串;非字符串类型(REG_BINARY/DWORD 等)对友好名无意义 → null
        return key.GetValue(string.IsNullOrEmpty(valueName) ? string.Empty : valueName) as string;
    }
}
