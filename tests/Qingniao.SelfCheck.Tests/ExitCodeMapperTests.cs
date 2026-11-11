namespace Qingniao.SelfCheck.Tests;

public class ExitCodeMapperTests
{
    [Fact]
    public void AllPass_Returns0()
    {
        Assert.Equal(0, ExitCodeMapper.Compute(
        [
            new("ipv6", CheckStatus.Pass, "ok"),
            new("nat", CheckStatus.Pass, "ok"),
        ]));
    }

    [Fact]
    public void ContainsUnknown_Returns1()
    {
        Assert.Equal(1, ExitCodeMapper.Compute(
        [
            new("ipv6", CheckStatus.Pass, "ok"),
            new("gpu-encoder", CheckStatus.Unknown, "尚未实现"),
        ]));
    }

    [Fact]
    public void ContainsWarn_Returns1()
    {
        Assert.Equal(1, ExitCodeMapper.Compute(
        [
            new("nat", CheckStatus.Warn, "受限"),
        ]));
    }

    [Fact]
    public void ContainsFail_Returns2()
    {
        Assert.Equal(2, ExitCodeMapper.Compute(
        [
            new("bandwidth", CheckStatus.Fail, "不可达"),
        ]));
    }

    [Fact]
    public void FailTakesPrecedenceOverWarnAndUnknown_Returns2()
    {
        Assert.Equal(2, ExitCodeMapper.Compute(
        [
            new("ipv6", CheckStatus.Warn, "受限"),
            new("nat", CheckStatus.Unknown, "尚未实现"),
            new("bandwidth", CheckStatus.Fail, "不可达"),
        ]));
    }

    [Fact]
    public void EmptyResults_Returns0()
    {
        Assert.Equal(0, ExitCodeMapper.Compute([]));
    }
}
