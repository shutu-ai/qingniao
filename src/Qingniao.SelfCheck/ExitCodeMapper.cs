namespace Qingniao.SelfCheck;

/// <summary>检查结果集合 → 进程退出码:有 Fail→2;无 Fail 但有 Warn/Unknown→1;否则 0。</summary>
public static class ExitCodeMapper
{
    public static int Compute(IReadOnlyList<CheckResult> results)
    {
        if (results.Any(r => r.Status == CheckStatus.Fail))
        {
            return 2;
        }

        return results.Any(r => r.Status is CheckStatus.Warn or CheckStatus.Unknown) ? 1 : 0;
    }
}
