namespace Qingniao.SelfCheck;

/// <summary>单项环境检查。Name 为稳定英文 ID,供 --only 过滤与后续任务匹配。</summary>
public interface IEnvironmentCheck
{
    string Name { get; }

    Task<CheckResult> RunAsync(CancellationToken ct);
}
