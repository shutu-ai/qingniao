using Qingniao.Core;

namespace Qingniao.Mesh.Tests;

public class YggdrasilConfigWriterTests
{
    [Fact]
    public void Generate_ProducesMinimalDeterministicYaml() {
        var yaml = YggdrasilConfigWriter.Generate(new MeshConfig());
        Assert.Equal(
            "AdminApi:\n  Enabled: true\n  Listen: tcp://127.0.0.1:13372\n" +
            "Listen:\n  - tcp://0.0.0.0:13371\n", yaml);
    }
}
