using Microsoft.Extensions.Time.Testing;
using Qingniao.Mesh;

namespace Qingniao.Mesh.Tests;

/// <summary>RestartBackoff 纯逻辑测试:FakeTimeProvider 驱动,断言指数序列与重置语义。</summary>
public class RestartBackoffTests
{
    [Fact]
    public void NextDelay_ConsecutiveCalls_YieldsDoublingSequenceCappedAt60s()
    {
        var backoff = new RestartBackoff(new FakeTimeProvider());

        var delays = Enumerable.Range(0, 8).Select(_ => backoff.NextDelay()).ToArray();

        Assert.Equal(
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(32),
            TimeSpan.FromSeconds(60), // 64 → 封顶 60
            TimeSpan.FromSeconds(60),
        ], delays);
    }

    [Fact]
    public void NextDelay_NeverExceeds60Seconds_EvenAfterManyFailures()
    {
        var backoff = new RestartBackoff(new FakeTimeProvider());

        var max = Enumerable.Range(0, 20).Select(_ => backoff.NextDelay()).Max();

        Assert.Equal(TimeSpan.FromSeconds(60), max);
    }

    [Fact]
    public void Reset_AfterFailures_RestartsSequenceAt1s()
    {
        var backoff = new RestartBackoff(new FakeTimeProvider());
        backoff.NextDelay();
        backoff.NextDelay();

        backoff.Reset();
        var afterReset = backoff.NextDelay();

        Assert.Equal(TimeSpan.FromSeconds(1), afterReset);
    }
}
