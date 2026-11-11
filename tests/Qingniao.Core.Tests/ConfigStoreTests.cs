namespace Qingniao.Core.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void MinimalJson_YieldsDefaults()
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(p, "{}");
        var c = ConfigStore.Load(p);
        Assert.Equal(13371, c.MeshPort);
        Assert.Equal(13372, c.AdminPort);
        Assert.Equal(2, c.StunServers.Count);
        Assert.Equal("tools/yggdrasil/yggdrasil.exe", c.YggdrasilExePath);
        Assert.Empty(c.VpnCidrs);
        Assert.Contains("stun1.l.google.com:19302", c.StunServers);
        Assert.Contains("stun.cloudflare.com:3478", c.StunServers);
        Assert.Null(c.BandwidthEndpoint);
        File.Delete(p);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var original = new MeshConfig
        {
            MeshPort = 20000,
            AdminPort = 20001,
            YggdrasilExePath = "custom/yggdrasil.exe",
            VpnCidrs = ["10.66.0.0/24", "fd00::/64"],
            StunServers = ["stun.example.com:3478"],
            BandwidthEndpoint = "https://bw.example.com/report",
        };
        ConfigStore.Save(original, p);
        var loaded = ConfigStore.Load(p);
        Assert.Equal(original.MeshPort, loaded.MeshPort);
        Assert.Equal(original.AdminPort, loaded.AdminPort);
        Assert.Equal(original.YggdrasilExePath, loaded.YggdrasilExePath);
        Assert.Equal(original.VpnCidrs, loaded.VpnCidrs);
        Assert.Equal(original.StunServers, loaded.StunServers);
        Assert.Equal(original.BandwidthEndpoint, loaded.BandwidthEndpoint);
        File.Delete(p);
    }

    [Fact]
    public void MissingFile_LoadThrowsWithPath_LoadOrDefaultReturnsDefaults()
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        var ex = Assert.Throws<ConfigException>(() => ConfigStore.Load(p));
        Assert.Contains(p, ex.Message);

        var defaults = ConfigStore.LoadOrDefault(p);
        Assert.Equal(13371, defaults.MeshPort);
        Assert.Equal(13372, defaults.AdminPort);
        Assert.Equal(2, defaults.StunServers.Count);
    }

    [Fact]
    public void InvalidJson_ThrowsConfigException()
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(p, "{ this is not valid json");
        var ex = Assert.Throws<ConfigException>(() => ConfigStore.Load(p));
        Assert.Contains(p, ex.Message);
        Assert.NotNull(ex.InnerException);
        File.Delete(p);
    }
}
