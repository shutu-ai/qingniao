using Qingniao.Core;

namespace Qingniao.Mesh;

/// <summary>生成确定性最小 yggdrasil 配置(纯字符串插值,仅 AdminApi 与 Listen,其余交由 yggdrasil 默认值)。</summary>
public static class YggdrasilConfigWriter
{
    public static string Generate(MeshConfig config) =>
        $"AdminApi:\n  Enabled: true\n  Listen: tcp://127.0.0.1:{config.AdminPort}\n" +
        $"Listen:\n  - tcp://0.0.0.0:{config.MeshPort}\n";
}
