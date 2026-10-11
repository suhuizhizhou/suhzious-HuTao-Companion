/// <summary>
/// 一条结构检查结果。<paramref name="Skipped"/> 表示「未评估」——本机不具备评估条件
/// （公开仓库已 gitignore 掉的数据、未检出的语料）。它既不算通过也不算失败，
/// 不计入通过率的分母，这样「数据不在本机」就不会被读成绿灯。
/// </summary>
internal sealed record CheckResult(string Name, bool Passed, string Detail, bool Skipped = false)
{
    /// <summary>ok = null 记为跳过；否则按布尔值判定通过/失败。</summary>
    public static CheckResult Of(string name, bool? ok, string detail = "") =>
        ok is null
            ? new CheckResult(name, false, detail, Skipped: true)
            : new CheckResult(name, ok.Value, detail);
}

/// <summary>检查结果输出口。<c>ok = null</c> 表示这条未评估。</summary>
internal delegate void CheckSink(string name, bool? ok, string detail);
