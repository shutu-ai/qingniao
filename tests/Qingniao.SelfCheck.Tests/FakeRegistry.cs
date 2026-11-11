using Qingniao.SelfCheck.Checks.HardwareEncoder;

namespace Qingniao.SelfCheck.Tests;

/// <summary>
/// 测试用内存假注册表:子键表 + 默认值表,无任何真实注册表 I/O(离线)。
/// 键路径与真实适配器约定一致:相对 HKLM 根的子键路径(不含 hive 前缀);
/// 默认值以 null/空 valueName 读取。未登记的键路径按"不存在"处理(空数组 / null)。
/// </summary>
internal sealed class FakeRegistry(
    IReadOnlyDictionary<string, string[]> subKeys,
    IReadOnlyDictionary<string, string?> defaultValues) : IRegistryAdapter
{
    public string[] GetSubKeyNames(string keyPath) =>
        subKeys.TryGetValue(keyPath, out var keys) ? keys : [];

    public string? GetValue(string keyPath, string valueName) =>
        string.IsNullOrEmpty(valueName) && defaultValues.TryGetValue(keyPath, out var value) ? value : null;
}
