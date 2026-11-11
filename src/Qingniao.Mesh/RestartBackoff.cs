namespace Qingniao.Mesh;

/// <summary>
/// 看门狗重启退避(纯逻辑,无 I/O):失败次数 n → 2^(n-1) 秒,封顶 60 秒。
/// 由 YggdrasilHost 在每次重启调度时调用 <see cref="NextDelay"/>;稳定运行 ≥5 分钟后调用 <see cref="Reset"/> 归零。
/// </summary>
public sealed class RestartBackoff
{
    /// <summary>首枚延迟(秒)。</summary>
    private const int BaseSeconds = 1;

    /// <summary>封顶延迟(秒)。</summary>
    private const int MaxSeconds = 60;

    private const int MaxShift = 6; // 2^6 = 64 → 封顶 60

    private int _failures;

    /// <summary>time 参数按接口约定注入(纯逻辑当前不依赖时钟,为将来加入抖动/jitter 预留)。</summary>
    public RestartBackoff(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _ = time;
    }

    /// <summary>下一次重启延迟:1s,2s,4s,… 封顶 60s。</summary>
    public TimeSpan NextDelay()
    {
        var failures = Interlocked.Increment(ref _failures);
        var seconds = failures > MaxShift + 1
            ? MaxSeconds
            : Math.Min(BaseSeconds << (failures - 1), MaxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>归零退避序列(稳定运行 ≥5 分钟后由 Host 调用)。</summary>
    public void Reset() => Interlocked.Exchange(ref _failures, 0);
}
