using Qingniao.Core;

namespace Qingniao.Mesh;

/// <summary>
/// 生成确定性最小 yggdrasil 配置(R20):输出是 HJSON(hjson-go 解析,无缩进嵌套),
/// 键名与 v0.5.15 <c>-genconf</c> 参考一致——AdminListen(admin socket,单字符串,设为非 none 即启用)
/// 与 Listen(mesh 入站监听,字符串数组)。仅写我们管理的两个键,其余键走 yggdrasil 默认值
/// (含 MulticastInterfaces 组播发现)。已用随仓库 yggdrasil.exe(v0.5.15)实证可解析且两端口生效。
/// </summary>
public static class YggdrasilConfigWriter
{
    public static string Generate(MeshConfig config) =>
        $"AdminListen: tcp://127.0.0.1:{config.AdminPort}\n" +
        $"Listen: [\n  tcp://0.0.0.0:{config.MeshPort}\n]\n";
}
