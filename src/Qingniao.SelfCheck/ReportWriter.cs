namespace Qingniao.SelfCheck;

/// <summary>控制台人读报告渲染(纯函数,与控制台输出一一对应)。</summary>
public static class ReportWriter
{
    public static string Render(IReadOnlyList<CheckResult> results, string hostName)
    {
        var title = $"自检报告 [{hostName}]";
        var lines = new List<string>(results.Count + 1) { title };
        lines.AddRange(results.Select(r => $"{Prefix(r.Status)} {r.Name}: {r.Summary}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string Prefix(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "[PASS]",
        CheckStatus.Warn => "[WARN]",
        CheckStatus.Fail => "[FAIL]",
        CheckStatus.Unknown => "[UNKNOWN]",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未知检查状态"),
    };
}
