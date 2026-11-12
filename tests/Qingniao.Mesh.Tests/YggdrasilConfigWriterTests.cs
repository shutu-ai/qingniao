using Qingniao.Core;

namespace Qingniao.Mesh.Tests;

/// <summary>
/// yggdrasil 配置 golden(R20):输出必须是 HJSON(hjson-go 解析,无缩进嵌套),键名与
/// v0.5.15 <c>-genconf</c> 参考一致——AdminListen(admin socket,单字符串)与 Listen(mesh 监听,
/// 字符串数组)。仅含我们管理的键,其余键走 yggdrasil 默认值。
/// 已用随仓库的 yggdrasil.exe(v0.5.15)实证:该配置可解析,admin 与 mesh 端口均生效。
/// </summary>
public class YggdrasilConfigWriterTests
{
    [Fact]
    public void Generate_ProducesMinimalDeterministicHjson()
    {
        var hjson = YggdrasilConfigWriter.Generate(new MeshConfig());
        Assert.Equal(
            "AdminListen: tcp://127.0.0.1:13372\n" +
            "Listen: [\n  tcp://0.0.0.0:13371\n]\n", hjson);
    }

    [Fact]
    public void Generate_NonDefaultPorts_AppearInConfig()
    {
        // 台账 T3-1:非默认端口必须原样落进配置
        var hjson = YggdrasilConfigWriter.Generate(
            new MeshConfig { MeshPort = 23371, AdminPort = 23372 });
        Assert.Contains("AdminListen: tcp://127.0.0.1:23372", hjson);
        Assert.Contains("tcp://0.0.0.0:23371", hjson);
        Assert.DoesNotContain("13371", hjson);
        Assert.DoesNotContain("13372", hjson);
    }
}
