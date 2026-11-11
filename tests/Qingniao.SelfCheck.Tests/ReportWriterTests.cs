namespace Qingniao.SelfCheck.Tests;

public class ReportWriterTests
{
    [Fact]
    public void Render_TitleContainsHostName_AndEachLineHasPrefixNameAndSummary()
    {
        var results = new List<CheckResult>
        {
            new("ipv6", CheckStatus.Pass, "IPv6 可用"),
            new("nat", CheckStatus.Fail, "NAT 受限", "Symmetric NAT"),
        };

        var report = ReportWriter.Render(results, "DESKTOP-ABC");
        var lines = report.Replace("\r\n", "\n").Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.Contains("DESKTOP-ABC", lines[0]);
        Assert.Contains("[PASS]", lines[1]);
        Assert.Contains("ipv6", lines[1]);
        Assert.Contains("IPv6 可用", lines[1]);
        Assert.Contains("[FAIL]", lines[2]);
        Assert.Contains("nat", lines[2]);
        Assert.Contains("NAT 受限", lines[2]);
    }

    [Fact]
    public void Render_MapsEveryStatusToItsPrefix()
    {
        var expected = new Dictionary<CheckStatus, string>
        {
            [CheckStatus.Pass] = "[PASS]",
            [CheckStatus.Warn] = "[WARN]",
            [CheckStatus.Fail] = "[FAIL]",
            [CheckStatus.Unknown] = "[UNKNOWN]",
        };

        foreach (var (status, prefix) in expected)
        {
            var report = ReportWriter.Render([new("x", status, "摘要")], "H");
            Assert.Contains(prefix, report);
        }
    }
}
