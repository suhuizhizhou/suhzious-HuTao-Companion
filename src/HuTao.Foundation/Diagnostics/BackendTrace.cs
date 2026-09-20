using System.Text;

namespace HuTao.Foundation.Diagnostics;

/// <summary>一条随证据一起进上下文的行（模型实际会看到的原文）。</summary>
public sealed record RetrievalTraceContextLine(string Id, string Speaker, string Text, bool IsAnchor);

/// <summary>一次检索用到的查询文本：原句变体 / 概念扩展，以及它是否通过准入。</summary>
public sealed record RetrievalTraceVariant(string Text, string Source, bool Admitted);

/// <summary>一次 RAG 调用里被召回的一条证据（追踪日志用，只留可读所需字段）。</summary>
public sealed record RetrievalTraceEvidence(
    string Id, string Speaker, string Text, double Score, double Coverage, string Chapter,
    bool Exact, string MatchKind,
    /// <summary>这条证据携带的上下文窗口——**模型真正读到的东西**，不只是锚点那一行。</summary>
    IReadOnlyList<RetrievalTraceContextLine>? Context = null,
    /// <summary>分数由哪几项构成（精确/覆盖/RRF/自称/记忆先验/实体/章节先验）。</summary>
    IReadOnlyList<string>? ScoreParts = null);

/// <summary>
/// 一次 RAG 调用的**全部过程与结果**。这是追踪日志的基本单位：
/// 无论调用来自 agent 的某一轮、来自 <c>--query</c> 还是来自将来的别的调用方，
/// 都从 <see cref="StoryRagService"/> 这一个咽喉点流出来，所以不会漏记。
///
/// **「全部」是字面意思**：不只是找到了什么，还包括每一段中间过程——
/// 各通道各出了多少候选、排序前几名的分数由哪几项构成、谁被哪条闸门挡下、
/// TopK 在哪一条截断、锚点有没有被重选、哪些上下文的行被字符预算裁掉、充分性给了什么结论。
/// 只记结果不记过程时，日志能回答「找到了什么」，回答不了「为什么是它 / 为什么没找到」。
/// </summary>
public sealed record RetrievalTraceRecord(
    string Input,
    string Route,
    string RouteReason,
    string Status,
    string Strategy,
    int ChapterScopeSize,
    bool CacheHit,
    int Candidates,
    double ElapsedMs,
    IReadOnlyList<RetrievalTraceVariant> Variants,
    IReadOnlyList<RetrievalTraceEvidence> Evidence,
    IReadOnlyList<string> Warnings,
    /// <summary>查询理解：路由判据、检索文本、自称/引号/追问标记、实体。</summary>
    IReadOnlyList<string>? Plan = null,
    /// <summary>各召回通道的候选数与并集/排序规模。</summary>
    IReadOnlyList<string>? Channels = null,
    /// <summary>排序阶段前若干候选与分数构成。</summary>
    IReadOnlyList<RetrievalTraceCandidate>? Ranking = null,
    /// <summary>候选闸门：谁被 MinScore / 去重 / 限量 / TopK 挡下。</summary>
    IReadOnlyList<string>? Gates = null,
    /// <summary>锚点重选日志。</summary>
    IReadOnlyList<string>? Reselect = null,
    /// <summary>字符预算裁剪日志。</summary>
    IReadOnlyList<string>? Budget = null,
    /// <summary>充分性策略的结论。</summary>
    IReadOnlyList<string>? Sufficiency = null,
    /// <summary>其它需要说明的过程（例如融合臂的分臂计数）。</summary>
    IReadOnlyList<string>? Notes = null);

/// <summary>排序阶段的一个候选（含分数构成）。</summary>
public sealed record RetrievalTraceCandidate(
    int Rank, string Id, string Speaker, string Text,
    double Score, double Coverage, bool Exact, string Kind,
    IReadOnlyList<string>? Parts = null);

/// <summary>
/// 一次 RAG 调用的**后端结果**。这是追踪日志的基本单位：
/// 无论调用来自 agent 的某一轮、来自 <c>--query</c> 还是来自将来的别的调用方，
/// 都从 <see cref="StoryRagService"/> 这一个咽喉点流出来，所以不会漏记。
/// </summary>

/// <summary>
/// 面向阅读的后端追踪日志（agent + RAG 的运行结果）。
///
/// **与 <see cref="LocalDiagnosticLog"/> 的分工，别混**：
/// - `LocalDiagnosticLog` 是**技术诊断**：JSON 行、只记阶段/路径/异常类型/计数，
///   契约是「不存用户输入、模型正文、密钥」——它的用途是排查崩溃与统计。
/// - 本类相反：它是**给人读的运行叙事**，会包含用户输入、召回到的原文、草稿与最终答复。
///   默认只写本机（`%LOCALAPPDATA%\HuTaoCompanion\logs\`）、有大小上限与轮转、
///   可用 `HU_TAO_TRACE=off` 整体关掉，且**绝不进入角色历史或任何对外通道**。
///
/// 之所以要单独有这么一份：出问题时真正要看的是「这一轮到底检索了什么、召回了哪几行、
/// 闸门改没改、最后答了什么」，而这些在诊断日志里被**故意**丢掉了，
/// 在评测报告里又只存在于跑了评测的那一次。平时聊天出问题，只有这份日志能复盘。
///
/// 环境变量：
/// - <c>HU_TAO_TRACE</c>：<c>off/false/0</c> 关闭；缺省开启。
/// - <c>HU_TAO_TRACE_PATH</c>：自定义文件路径。
/// - <c>HU_TAO_TRACE_EVIDENCE</c>：每条 RAG 调用打印多少行证据（默认 3）。
/// - <c>HU_TAO_TRACE_MAX_MB</c>：单文件上限，超过即轮转为 <c>.1</c>（默认 8）。
/// </summary>
public sealed class BackendTrace
{
    private static readonly AsyncLocal<TurnTrace?> AmbientTurn = new();
    private static readonly AsyncLocal<string?> AmbientLabel = new();
    private static readonly AsyncLocal<string?> CallLabel = new();
    private readonly object _gate = new();

    private BackendTrace(string path, bool enabled, int evidenceLines, long maxBytes)
    {
        FilePath = path;
        Enabled = enabled;
        // 0 = **不截断，全部列出**。默认就是 0：这份日志的用途是排查检索过程，
        // 「只列前 3 条证据」会把「第 4 条其实是关键行」这种情况直接藏掉。
        // 嫌长可以显式设 HU_TAO_TRACE_EVIDENCE=3。
        EvidenceLines = Math.Clamp(evidenceLines, 0, 200);
        MaxBytes = maxBytes;
    }

    /// <summary>进程默认实例。宿主可在启动时用 <see cref="Configure"/> 改路径（例如指到评测输出目录）。</summary>
    public static BackendTrace Default { get; private set; } = FromEnvironment();

    public bool Enabled { get; }
    public string FilePath { get; }
    public int EvidenceLines { get; }
    public long MaxBytes { get; }

    /// <summary>当前正在记录的回合；为空表示这次 RAG 调用不在任何回合里，应当单独成块。</summary>
    public static TurnTrace? Current => AmbientTurn.Value;

    /// <summary>
    /// 给接下来的记录贴一个外部标签（评测里是「哪个用例」，宿主里可以是「哪个会话」）。
    /// 没有它，一晚上的日志里几百个回合会长得一模一样，只有时间戳能区分。
    /// </summary>
    public static void SetTurnLabel(string? label) => AmbientLabel.Value = label;

    public static string? TurnLabel => AmbientLabel.Value;

    /// <summary>
    /// 给**下一次** RAG 调用贴身份（「R1 · t1 · Fusion · agent 选」）。
    ///
    /// 不用参数传是有原因的：调用链是 `agent → loop → tool → service`，
    /// 而 service 是唯一能拿到最终结果的地方。把「这次调用是谁发起的」沿路加参数
    /// 会为了日志污染检索契约；`AsyncLocal` 在并发的 `Task.WhenAll` 里也会随每个子任务
    /// 各自捕获，正好符合「每个调用自己的标签」，且用完即清，不会串到下一个调用。
    /// </summary>
    public static void SetCallLabel(string? label) => CallLabel.Value = label;

    public static string? CurrentCallLabel => CallLabel.Value;

    internal static string? TakeCallLabel()
    {
        var label = CallLabel.Value;
        CallLabel.Value = null;
        return label;
    }

    public static BackendTrace FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("HU_TAO_TRACE_PATH")?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HuTaoCompanion", "logs", "backend-trace.log");
        return new BackendTrace(path, ReadFlag("HU_TAO_TRACE"), ReadInt("HU_TAO_TRACE_EVIDENCE", 0),
            ReadInt("HU_TAO_TRACE_MAX_MB", 8) * 1024L * 1024L);
    }

    /// <summary>显式配置（宿主/评测用）。<paramref name="enabled"/> 为 null 时沿用当前实例的设置。</summary>
    public static void Configure(string path, bool? enabled = null, long? maxBytes = null)
    {
        var current = Default;
        Default = new BackendTrace(path, enabled ?? current.Enabled, current.EvidenceLines, maxBytes ?? current.MaxBytes);
    }

    /// <summary>测试用：恢复为按环境变量构造的实例。</summary>
    public static void ResetToEnvironment() => Default = FromEnvironment();

    internal static void ClearAmbient() => AmbientTurn.Value = null;

    /// <summary>
    /// 开一个回合。<see cref="TurnTrace"/> 在内存里攒完整段叙事，<see cref="TurnTrace.End"/> 一次性落盘
    /// ——这样并发的调用不会互相插行，半个回合也不会留在文件里。
    /// 关闭追踪或已有回合在跑时返回 null，调用方只需判空。
    /// </summary>
    public TurnTrace? BeginTurn(string title, string? subtitle = null)
    {
        if (!Enabled || AmbientTurn.Value is not null)
            return null;
        var turn = new TurnTrace(this, title, subtitle);
        AmbientTurn.Value = turn;
        return turn;
    }

    /// <summary>
    /// 记一次 RAG 调用。**有回合在跑就并入该回合**（保持时序），否则单独成块——
    /// 后者覆盖 `--query` 这类不经过 agent 的调用，否则「rag 的后端结果」会缺一块。
    /// </summary>
    public void Retrieval(RetrievalTraceRecord record)
    {
        if (!Enabled)
            return;
        if (AmbientTurn.Value is { } ambient)
        {
            ambient.Retrieval(record);
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine(Rule('─'));
        var tag = string.Join(" · ", new[] { TurnLabel, TakeCallLabel() }.Where(x => !string.IsNullOrWhiteSpace(x)));
        sb.AppendLine($" RAG 调用{(tag.Length == 0 ? "" : " · " + tag)} · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}");
        AppendRetrieval(sb, record, EvidenceLines, prefix: " ");
        sb.AppendLine(Rule('─'));
        Write(sb.ToString());
    }

    internal static void AppendRetrieval(StringBuilder sb, RetrievalTraceRecord record,
        int evidenceLines, string prefix)
    {
        sb.AppendLine($"{prefix}查询 ▸ {Shorten(record.Input, 240)}");
        sb.AppendLine($"{prefix}路由 ▸ {record.Route} · 判据 {Shorten(record.RouteReason, 24)}   状态 {record.Status}");
        var scope = record.ChapterScopeSize > 0 ? $" · 扩章节 {record.ChapterScopeSize}" : "";
        var cache = record.CacheHit ? " · 命中缓存" : "";
        sb.AppendLine($"{prefix}召回 ▸ 臂 {record.Strategy}{scope} · 候选 {record.Candidates} · " +
                      $"用时 {record.ElapsedMs:F1}ms{cache}");

        // ── ① 查询理解 ──
        foreach (var line in record.Plan ?? [])
            sb.AppendLine($"{prefix}  {line}");
        // ── ② 检索词：**全部列出、不再过滤**，并标明它是原句变体还是概念扩展 ──
        if (record.Variants.Count > 0)
        {
            sb.AppendLine($"{prefix}检索词 ▸ 共 {record.Variants.Count} 条" +
                          (record.Variants.Any(v => !v.Admitted) ? "（含未通过准入的）" : ""));
            foreach (var variant in record.Variants)
                sb.AppendLine($"{prefix}  {(variant.Admitted ? "✔" : "✘")} [{variant.Source}] {Shorten(variant.Text, 160)}");
        }
        // ── ③ 通道贡献 ──
        foreach (var line in record.Channels ?? [])
            sb.AppendLine($"{prefix}通道 ▸ {line}");
        // ── ④ 排序与分数构成：回答「为什么是它」 ──
        if (record.Ranking is { Count: > 0 })
        {
            sb.AppendLine($"{prefix}排序前 {record.Ranking.Count} 名（分数构成）:");
            foreach (var candidate in record.Ranking)
                sb.AppendLine($"{prefix}  {candidate.Rank,2}. {candidate.Score:F3}/{candidate.Coverage:F3}" +
                              (candidate.Exact ? " exact" : "") +
                              $"  {candidate.Speaker}：{Shorten(candidate.Text, 60)}" +
                              $"\n{prefix}      = {string.Join(" + ", candidate.Parts ?? [])}");
        }
        // ── ⑤ 闸门：谁被挡下、为什么 ──
        if (record.Gates is { Count: > 0 })
        {
            sb.AppendLine($"{prefix}闸门 ▸ 挡下 {record.Gates.Count} 条:");
            foreach (var line in record.Gates)
                sb.AppendLine($"{prefix}  ✘ {line}");
        }
        // ── ⑥ 锚点重选 ──
        foreach (var line in record.Reselect ?? [])
            sb.AppendLine($"{prefix}重锚 ▸ {line}");
        // ── ⑦ 证据：**默认全部**（evidenceLines<=0 表示不截断）＋ 每条的实际上下文 ──
        var shown = evidenceLines <= 0 ? record.Evidence : record.Evidence.Take(evidenceLines).ToArray();
        sb.AppendLine($"{prefix}证据 {record.Evidence.Count} 条"
                      + (shown.Count < record.Evidence.Count ? $"（只列前 {shown.Count} 条）" : "（全部）"));
        foreach (var item in shown)
        {
            var exact = item.Exact ? " ·exact" : "";
            sb.AppendLine($"{prefix}  {item.Score:F3} / {item.Coverage:F3}{exact}  {item.Speaker}：{Shorten(item.Text, 150)}");
            sb.AppendLine($"{prefix}      [{item.Id}] 章节 {item.Chapter} · 命中 {item.MatchKind}");
            if (item.ScoreParts is { Count: > 0 })
                sb.AppendLine($"{prefix}      分数构成 = {string.Join(" + ", item.ScoreParts)}");
            // 上下文是**模型真正读到的内容**：锚点单独标出，其余是窗口邻行。
            foreach (var line in item.Context ?? [])
                sb.AppendLine($"{prefix}      {(line.IsAnchor ? "▸锚点" : "  上下文")} [{line.Id}] {line.Speaker}：{Shorten(line.Text, 120)}");
        }
        // ── ⑧ 字符预算裁剪 / 充分性结论 / 其它说明 ──
        foreach (var line in record.Budget ?? [])
            sb.AppendLine($"{prefix}预算 ▸ {line}");
        foreach (var line in record.Sufficiency ?? [])
            sb.AppendLine($"{prefix}充分性 ▸ {line}");
        foreach (var line in record.Notes ?? [])
            sb.AppendLine($"{prefix}说明 ▸ {line}");
        foreach (var warning in record.Warnings)
            sb.AppendLine($"{prefix}  ⚠ {Shorten(warning, 200)}");
    }

    internal void Write(string text)
    {
        try
        {
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length + text.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, text, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 记日志失败绝不能影响对话——与 LocalDiagnosticLog 同一条纪律。
        }
    }

    internal static string Rule(char glyph, int width = 96) => new(glyph, width);

    internal static string Shorten(string? value, int limit)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var single = value.Replace('\r', ' ').Replace('\n', '⏎');
        return single.Length <= limit ? single : single[..limit] + "…";
    }

    /// <summary>密钥脱敏复用诊断日志那一套（同一个程序集，保证两处规则不会各写一遍）。</summary>
    internal static string Redact(string value) => LocalDiagnosticLog.Redact(value);

    private static bool ReadFlag(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        if (string.IsNullOrEmpty(value))
            return true;
        return !(value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                 value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase));
    }

    private static int ReadInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed) && parsed >= 0 ? parsed : fallback;
}

/// <summary>
/// 一个回合的追踪缓冲。方法名刻意用「章节 / 键值 / 行」这种叙事词——
/// 这份日志的目标是可读，不是结构化：结构化另有 LocalDiagnosticLog 与评测报告。
/// </summary>
public sealed class TurnTrace
{
    private readonly BackendTrace _owner;
    private readonly StringBuilder _sb = new();
    private readonly string _title;
    private int _section;
    private bool _ended;

    internal TurnTrace(BackendTrace owner, string title, string? subtitle)
    {
        _owner = owner;
        _title = title;
        var label = BackendTrace.TurnLabel;
        var tail = string.Join(" · ", new[] { label, subtitle }.Where(x => !string.IsNullOrWhiteSpace(x)));
        _sb.AppendLine(BackendTrace.Rule('═'));
        _sb.AppendLine($" {title}{(tail.Length == 0 ? "" : " · " + tail)} · " +
                       $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}");
        _sb.AppendLine(BackendTrace.Rule('─'));
    }

    /// <summary>下一段。序号自动递增，便于在长日志里数到「哪一步之后出问题」。</summary>
    public void Section(string title, string? detail = null)
    {
        _section++;
        var marker = SectionGlyph(_section);
        Line($"{marker} {title}{(string.IsNullOrWhiteSpace(detail) ? "" : "  " + detail)}");
    }

    public void Line(string text) => _sb.AppendLine("  " + BackendTrace.Redact(text));

    public void Raw(string text) => _sb.AppendLine(BackendTrace.Redact(text));

    /// <summary>标签 + 值，标签对齐，扫读时不必逐个找冒号。</summary>
    public void Key(string label, string? value)
        => _sb.AppendLine("    " + label.PadRight(6) + " ▸ " + BackendTrace.Redact(value ?? ""));

    public void Note(string text) => _sb.AppendLine("    · " + BackendTrace.Redact(text));

    /// <summary>缩进的树形条目（多级查询的拓扑用它画）。</summary>
    public void Tree(string prefix, string text) => _sb.AppendLine("    " + prefix + " " + BackendTrace.Redact(text));

    public void Retrieval(RetrievalTraceRecord record)
    {
        var tag = BackendTrace.TakeCallLabel();
        Line($"┌ 一次 RAG 调用{(string.IsNullOrWhiteSpace(tag) ? "" : " · " + tag)}");
        var sb = new StringBuilder();
        BackendTrace.AppendRetrieval(sb, record, _owner.EvidenceLines, prefix: "  │ ");
        _sb.Append(BackendTrace.Redact(sb.ToString()));
        Line("└");
    }

    /// <summary>收尾并落盘。重复调用无效果，避免 finally 与正常路径各写一次。</summary>
    public void End(string? footer = null)
    {
        if (_ended)
            return;
        _ended = true;
        if (!string.IsNullOrWhiteSpace(footer))
        {
            _sb.AppendLine(BackendTrace.Rule('─'));
            _sb.AppendLine(" " + footer);
        }
        _sb.AppendLine(BackendTrace.Rule('═'));
        _owner.Write(_sb.ToString());
        if (ReferenceEquals(BackendTrace.Current, this))
            BackendTrace.ClearAmbient();
    }

    private static string SectionGlyph(int index) => index switch
    {
        >= 1 and <= 20 => ((char)('①' + index - 1)).ToString(),
        _ => $"({index})",
    };
}
