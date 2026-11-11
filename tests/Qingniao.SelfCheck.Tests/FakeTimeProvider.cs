namespace Qingniao.SelfCheck.Tests;

/// <summary>
/// 测试用虚拟时钟:手动推进时间并同步触发到期定时器,使依赖超时的代码可确定性测试。
/// 等价于 Microsoft.Extensions.TimeProvider.Testing 的 FakeTimeProvider,但用箱内 API 自实现
/// (本任务约定不新增 NuGet 包)。
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _utcNow;

    public FakeTimeProvider()
        : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public FakeTimeProvider(DateTimeOffset start) => _utcNow = start;

    /// <summary>是否存在尚未触发的定时器(用于等待被测代码注册超时定时器)。</summary>
    public bool HasPendingTimers
    {
        get { lock (_gate) { return _timers.Count > 0; } }
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    /// <summary>推进虚拟时间,并按注册顺序同步触发所有到期定时器。</summary>
    public void Advance(TimeSpan delta)
    {
        FakeTimer[] due;
        lock (_gate)
        {
            _utcNow += delta;
            due = _timers.Where(t => t.DueTime <= _utcNow).ToArray();
        }

        foreach (FakeTimer timer in due)
        {
            timer.Fire(_utcNow);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    internal void Add(FakeTimer timer)
    {
        lock (_gate) { _timers.Add(timer); }
    }

    internal void Remove(FakeTimer timer)
    {
        lock (_gate) { _timers.Remove(timer); }
    }
}

/// <summary>虚拟定时器:到期的判定由 FakeTimeProvider.Advance 驱动(一次性定时器触发后自动移除)。</summary>
internal sealed class FakeTimer(FakeTimeProvider owner, TimerCallback? callback, object? state) : ITimer
{
    private readonly object _gate = new();
    private DateTimeOffset _dueTime;
    private TimeSpan _period;
    private bool _disposed;

    public DateTimeOffset DueTime
    {
        get { lock (_gate) { return _dueTime; } }
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                owner.Remove(this);
                return true;
            }

            _dueTime = owner.GetUtcNow() + dueTime;
            _period = period;
            owner.Add(this);
            return true;
        }
    }

    /// <summary>由 FakeTimeProvider.Advance 调用:到期则执行回调;一次性(period 无限)触发后移除。</summary>
    internal void Fire(DateTimeOffset now)
    {
        bool fire;
        lock (_gate)
        {
            fire = !_disposed && now >= _dueTime;
            if (fire && _period == Timeout.InfiniteTimeSpan)
            {
                owner.Remove(this);
            }
        }

        if (fire)
        {
            callback?.Invoke(state);
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        owner.Remove(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
