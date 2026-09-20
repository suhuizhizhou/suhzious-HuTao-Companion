using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;
using HuTao.Persona;
using HuTao.Knowledge.Rag;
using HuTao.Dialogue.Tools;
using System.Text.Json;

namespace HuTao.Dialogue.Core;

/// <summary>受控的 ReAct 检索循环：拆分子问题、逐个观察证据并在覆盖足够时停止。</summary>
public sealed record ReactRetrievalOptions
{
    public int MaxRounds { get; init; } = 6;
    public int MaxSubQueries { get; init; } = 6;
    public int MaxEvidencePerRound { get; init; } = 5;
    public double MinimumCoverage { get; init; } = 0.66;
    public bool EnableLlmPlanner { get; init; } = true;
    public int MaxRepairRounds { get; init; } = 2;
    /// <summary>
    /// 任务跑完后若证据仍不足，是否再追加一轮检索（默认开）。
    /// 关掉即为旧行为：只跑 planner 给出的任务，跑完就结束——实测那会让
    /// 「planner 只给 1 个任务」的用例在证据不足时直接收工，多级多次查询形同不存在。
    /// </summary>
    public bool EnableGapFollowUp { get; init; } = true;
    /// <summary>
    /// 首轮使用的召回臂。<c>null</c> = **交给服务默认臂**（`StoryRagOptions.Strategy`）。
    ///
    /// **默认必须是 null，不能是 Baseline**：循环过去总是把 <c>Baseline</c> 显式传给工具层，
    /// 于是服务级配置的臂被逐调用参数**悄悄覆盖**——`--strategy ConceptOnly` 跑在线时
    /// 实际每轮仍然走 Baseline，臂矩阵在在线侧物理上不可能成立。
    /// 「循环没有自己的偏好」就应该表达为「不传」，而不是「传一个恰好等于 Baseline 的值」。
    /// </summary>
    public RetrievalStrategy? FirstRoundStrategy { get; init; }
    /// <summary>
    /// 后续轮次使用的臂。<c>null</c> = 服务默认臂（不换臂 = 历史行为）。
    ///
    /// **这是「多级多次查询交给 agent」的落点**：同一个查询的不同轮次本就可以用不同的臂——
    /// 首轮「直接准确的词语召回」，后续轮次转「语义相近的词性探索」去跨词汇缺口。
    /// 真正给自主性时由 agent 逐轮填；当前先做成可配置的确定性策略，以便离线断言。
    /// </summary>
    public RetrievalStrategy? FollowUpStrategy { get; init; }
}

public sealed record ReactSubTask(string Id, string Query, IReadOnlyList<string> RequiredFacts,
    IReadOnlyList<string> DependsOn,
    /// <summary>
    /// 这个子任务点名要用的召回臂（<c>null</c> = 没点名，按轮次配置或服务默认）。
    ///
    /// **这就是「给 agent 自主性」在数据结构上的落点**：臂由 planner（agent 的规划步骤）
    /// 针对**每一个子问题**决定，而不是全局一个配置。只在
    /// <see cref="RetrievalStrategyCatalog"/> 的具名臂里取值，不接受连续权重。
    /// </summary>
    RetrievalStrategy? Strategy = null);

/// <summary>
/// 一步召回的**一条证据**（紧凑形态）。
///
/// 为什么要有它：召回链路的每一步此前只留下「几条证据、覆盖率多少」这类**计数**，
/// 具体召回了哪几行、各自多少分、来自哪一章全部丢失。于是一份轨迹只能被读成摘要，
/// 既没法离线复盘「这一步到底找回了什么」，也没法做任何可视化。
/// 这里刻意只留展示与判读需要的字段（不含整段 Context），避免报告膨胀。
/// </summary>
public sealed record RetrievalStepEvidence(
    string Id, string Speaker, string Text, double Score, double Coverage, string Chapter, bool Exact,
    string Perspective, string MatchKind);

public sealed record ReactRetrievalStep(
    int Round, string Query, StoryStatus Status, int EvidenceCount,
    double Coverage, bool Sufficient, string Reason,
    /// <summary>本轮实际生效的臂（<c>null</c> = 服务默认臂）。</summary>
    RetrievalStrategy? Strategy = null,
    /// <summary>臂是谁定的：<c>agent</c>（planner 点名）/ <c>configured</c>（轮次配置）/ <c>default</c>（服务默认）。</summary>
    string StrategySource = "default",
    /// <summary>这一步真正召回的证据（锚点行）。补检索步可能为空。</summary>
    IReadOnlyList<RetrievalStepEvidence>? Evidence = null,
    /// <summary>两级召回的章节范围大小（0 = 这一步没做第二级）。</summary>
    int ChapterScopeSize = 0,
    /// <summary>这一步对应哪个子任务（gap=循环自己合成的补差轮）。</summary>
    string TaskId = "",
    /// <summary>这个子任务依赖哪些前置任务——多级查询的拓扑就靠它读出来。</summary>
    IReadOnlyList<string>? DependsOn = null,
    /// <summary>子任务点名要求覆盖的事实。</summary>
    IReadOnlyList<string>? RequiredFacts = null)
{
    /// <summary>把一步的检索结果压成落盘用的紧凑证据列表。</summary>
    public static IReadOnlyList<RetrievalStepEvidence> Compress(StoryRagResult result) =>
        result.Evidence.Select(e => new RetrievalStepEvidence(
            e.Id, e.Anchor.Speaker, e.Anchor.Text, e.Score, e.Coverage, e.Anchor.ChapterId, e.Exact,
            e.Perspective.ToString(), e.MatchKind)).ToArray();
}

public sealed record ReactRetrievalResult(
    StoryRagResult Primary,
    StoryRagResult EvidencePool,
    IReadOnlyList<StoryRagResult> Results,
    IReadOnlyList<ReactRetrievalStep> Steps,
    string Observation);

public sealed class ReactRetrievalLoop
{
    private readonly StoryKnowledgeTool _tool;
    private readonly ReactRetrievalOptions _options;
    private readonly ILLMProvider? _llm;
    private readonly StoryLexicon _lexicon;
    private readonly RetrievalMemory _memory;

    /// <summary>
    /// agent 的检索记忆（跨轮持久）。同一个 loop 实例被 agent 长期持有，
    /// 所以上一轮对话拿到的证据与事实覆盖，下一轮仍然可用。
    /// </summary>
    public RetrievalMemory Memory => _memory;

    public ReactRetrievalLoop(StoryKnowledgeTool tool, ILLMProvider? llm = null, ReactRetrievalOptions? options = null,
        StoryLexicon? lexicon = null, RetrievalMemory? memory = null)
    {
        _tool = tool;
        _llm = llm;
        _options = options ?? new ReactRetrievalOptions();
        _lexicon = lexicon ?? StoryLexicon.Empty;
        _memory = memory ?? new RetrievalMemory();
    }

    public async Task<ReactRetrievalResult?> RunAsync(
        string input,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default, IReadOnlyList<ReactSubTask>? plannedTasks = null)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var tasks = plannedTasks?.ToList() ?? await PlanAsync(input, history, ct).ConfigureAwait(false);
        // 规划结果写进当前回合的追踪日志（如果有回合在跑）。
        // 这一段是「agent 的决策」与「RAG 的执行」之间的接缝：先看到 agent 点了哪个臂、谁依赖谁，
        // 紧接着才是那次调用的真实结果，因果关系不必靠事后反推。
        if (BackendTrace.Current is { } plannerTrace)
        {
            plannerTrace.Section("检索编排（agent 规划）",
                $"拆出 {tasks.Count} 个子任务；{tasks.Count(t => t.Strategy is not null)} 个点名了召回通道");
            for (var i = 0; i < tasks.Count && i < 8; i++)
            {
                var task = tasks[i];
                plannerTrace.Tree(i == tasks.Count - 1 ? "└" : "├",
                    $"{task.Id}  {(task.Strategy is { } declared ? declared.ToString() : "（未点名，走默认臂）")}" +
                    (task.DependsOn.Count == 0 ? "  ← —" : "  ← " + string.Join(',', task.DependsOn)));
                plannerTrace.Tree("│ ", $"query ▸ {task.Query}");
                if (task.RequiredFacts.Count > 0)
                    plannerTrace.Tree("│ ", $"待验事实 ▸ {string.Join('、', task.RequiredFacts)}");
            }
        }
        var results = new List<StoryRagResult>();
        var steps = new List<ReactRetrievalStep>();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        // 子任务 → 它的检索结果。两级召回第二级要靠它把上一级命中的章节读出来。
        var byTask = new Dictionary<string, StoryRagResult>(StringComparer.Ordinal);

        // 两级召回第二级的章节来源：**上一级（depends_on）证据实际落在的章节**。
        // 这才是「先召回关键词的句子和具体出现的章节，再召回该章节的全部相关内容」——
        // 章节由第一级的命中决定，不是再拿查询去匹配一遍摘要。
        // 上限 3 个章节：依赖链可能命中很多章节，无限扩会把 TopK 全占满、反而挤掉字面证据。
        IReadOnlyList<string>? ScopeFor(ReactSubTask task)
        {
            if (task.Strategy != RetrievalStrategy.ChapterScope) return null;
            var chapters = new List<string>();
            foreach (var dependency in task.DependsOn)
            {
                if (!byTask.TryGetValue(dependency, out var upstream)) continue;
                foreach (var line in upstream.Evidence.SelectMany(e => e.Context.Prepend(e.Anchor)))
                    if (!string.IsNullOrWhiteSpace(line.ChapterId) && !chapters.Contains(line.ChapterId) &&
                        chapters.Count < 3)
                        chapters.Add(line.ChapterId);
            }
            return chapters.Count == 0 ? null : chapters;
        }

        for (var round = 0; round < _options.MaxRounds; round++)
        {
            var ready = tasks.Where(t => !completed.Contains(t.Id) && t.DependsOn.All(completed.Contains)).ToArray();
            if (ready.Length == 0) ready = tasks.Where(t => !completed.Contains(t.Id)).Take(1).ToArray();
            if (ready.Length == 0)
            {
                // 同一条问题最多追加 MaxRepairRounds 轮，重复问不会带来新证据。
                var followUps = tasks.Count(t => t.Id.StartsWith("gap", StringComparison.Ordinal));
                if (steps.Count == 0 || steps[^1].Sufficient ||
                    steps[^1].Status is StoryStatus.Bypass or StoryStatus.Comfort or StoryStatus.Playful or StoryStatus.Boundary or StoryStatus.Clarify) break;
                if (!_options.EnableGapFollowUp || followUps >= _options.MaxRepairRounds) break;
                // 连续两轮发同一句查询 → 不会有新证据（服务端还会命中缓存），立刻停。
                if (steps.Count >= 2 && string.Equals(steps[^1].Query, steps[^2].Query, StringComparison.Ordinal)) break;
                var followUp = new ReactSubTask($"gap{round + 1}", input, [], []);
                tasks.Add(followUp);
                ready = [followUp];
                BackendTrace.Current?.Line(
                    $"▶ 证据仍不足 → 追加补差轮 {followUp.Id}（循环自己合成，沿用服务默认臂；问题按缺口改写）");
                BackendTrace.SetCallLabel($"补差轮 {followUp.Id} · 服务默认臂 · 循环合成");
            }
            // 用**检索记忆**改写本轮查询：记忆还没覆盖的事实组补进查询（补差），
            // 已经问过且事实也没新增的原样复用。查询不再是开局一次性定死的——
            // 这是「多级多次查询交给 agent」在检索侧的落点。
            var queries = ready.Select(RewriteQuery).ToArray();
            // 每轮用哪个臂，优先级三层：
            //   1. **agent 点名**（planner 给这个子任务填了 strategy）——这是自主性的落点；
            //   2. 轮次配置（FirstRoundStrategy / FollowUpStrategy）；
            //   3. 服务默认臂（`--strategy` / StoryRagOptions.Strategy）。
            // 注意是**逐子任务**取臂
            var roundStrategy = round == 0 ? _options.FirstRoundStrategy : _options.FollowUpStrategy;
            var arms = ready.Select(t => t.Strategy ?? roundStrategy).ToArray();
            var armSources = ready.Select(t => t.Strategy is not null ? "agent"
                : roundStrategy is not null ? "configured" : "default").ToArray();
            // **两级召回的第二级**：agent 点名 ChapterScope 时，章节取自它 depends_on 的那些任务的证据
            // ——「先召回关键词的句子和它出现的章节，再召回该章节的全部相关内容」就是这条。
            // 取不到依赖证据时给 null，让服务端退回摘要级匹配（如实降级，不偷换信号）。
            var scopes = ready.Select(ScopeFor).ToArray();
            // 每个调用贴自己的身份（`AsyncLocal` 会随各子任务分别捕获），
            // 于是并发的同一轮调用在日志里也各自能认出是哪一步，而不是一堆无头无尾的块。
            var batch = await Task.WhenAll(queries.Select((q, i) =>
            {
                BackendTrace.SetCallLabel($"R{round + 1} · {ready[i].Id} · {arms[i]?.ToString() ?? "服务默认臂"} · " +
                    $"{SourceLabel(armSources[i])}" +
                    (scopes[i] is { Count: > 0 } scopedForLog ? $" · 扩章节 {scopedForLog.Count}" : ""));
                return _tool.RetrieveAsync(q, history, ct, arms[i], scopes[i]);
            })).ConfigureAwait(false);
            for (var i = 0; i < ready.Length; i++)
            {
                var task = ready[i]; var result = batch[i]; results.Add(result); completed.Add(task.Id);
                byTask[task.Id] = result;
                var coverage = result.Evidence.Count == 0 ? 0 : result.Evidence.Average(e => e.Coverage);
                var sufficient = result.Status is StoryStatus.Answer or StoryStatus.Tentative &&
                                 (coverage >= _options.MinimumCoverage || result.Evidence.Count >= _options.MaxEvidencePerRound);
                // 落进检索记忆：本轮拿到的证据本体与事实覆盖，下一轮和下一轮对话都能复用。
                _memory.Record(new RetrievalTurn(queries[i], result.Evidence,
                    task.RequiredFacts.Where(f => EvidenceCovers(result, f)).ToArray(),
                    result.Status.ToString(), sufficient));
                steps.Add(new ReactRetrievalStep(round + 1, queries[i], result.Status, result.Evidence.Count, coverage, sufficient,
                    $"{(sufficient ? "证据足够" : "证据可能缺失，后续执行补检索")}{ArmLabel(arms[i], armSources[i])}" +
                    (scopes[i] is { Count: > 0 } scoped ? $"（扩章节 {scoped.Count} 个）" : ""),
                    arms[i], armSources[i], ReactRetrievalStep.Compress(result), scopes[i]?.Count ?? 0,
                    task.Id, task.DependsOn, task.RequiredFacts));
            }
        }

        var primary = results.OrderByDescending(r => Score(r)).FirstOrDefault();
        if (primary is null) return null;
        var pool = Merge(primary, results);
        // 把**检索记忆里已持有的证据**并进池子。
        // 这是「利用之前查询的信息」的落点：上一轮（甚至上一次对话）已经拿到的东西，
        // 这一轮直接复用，而不是重新检索一遍——多级多次查询因此才有积累效应。
        var currentPlan = StoryQueryAnalyzer.Plan(input, _lexicon, history);
        var held = currentPlan.IsFollowUp && currentPlan.Route == StoryRoute.Retrieve
            ? _memory.HeldEvidence().Where(e => pool.Evidence.Any(p => p.Anchor.ChapterId == e.Anchor.ChapterId)).Take(5).ToArray()
            : [];
        if (held.Length > 0)
        {
            pool = pool with
            {
                Evidence = pool.Evidence.Concat(held)
                    .GroupBy(e => e.Id, StringComparer.Ordinal)
                    .Select(g => g.OrderByDescending(e => e.Score).First())
                    .OrderByDescending(e => e.Score).ToArray(),
            };
        }
        pool = await RepairMissingFactsAsync(input, tasks, history, pool, results, steps, ct).ConfigureAwait(false);
        pool = VerifyClaims(pool);
        var observation = string.Join("\n", steps.Select(step =>
            $"- react[{step.Round}] {step.Status}: {step.Query}; evidence={step.EvidenceCount}; coverage={step.Coverage:F2}; {step.Reason}"));
        return new ReactRetrievalResult(primary, pool, results, steps, observation);
    }

    private async Task<List<ReactSubTask>> PlanAsync(string input, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        // 旁路/玩梗/安慰和纯原文引用不应消耗 Planner 调用；它们必须保持原有低延迟路径。
        var route = StoryQueryAnalyzer.Plan(input, _lexicon, history).Route;
        var direct = route != StoryRoute.Retrieve ||
                     (input.Contains('“') && input.Contains('”') && input.Contains("原文", StringComparison.Ordinal));
        if (!direct && _options.EnableLlmPlanner && _llm is not null)
        {
            try
            {
                // 规划步骤同时决定**每个子问题该走哪条召回通道**——这就是「给 agent 自主性」：
                // 臂不再由全局配置写死，由 agent 在有限具名臂里为每个子问题点名。
                // 仍然只允许具名臂，**不允许输出权重**：连续权重让每次改动不可证伪、
                // 两次运行不可比、置信门禁失去意义（notes/CORE.md §11.6）。
                var prompt =
                    "你是 Story RAG Query Planner。只返回 JSON：{\"tasks\":[{\"id\":\"t1\",\"query\":\"...\"," +
                    "\"required_facts\":[\"...\"],\"depends_on\":[],\"strategy\":\"Baseline\"}]}。" +
                    "把用户问题拆成最多6个可独立检索的事实子任务；因果和时间解释任务依赖其前置事实；不要回答问题。\n" +
                    "每个子任务可以点名一条召回通道（strategy），取值只能是下面这些具名臂之一，**不要输出权重或分数**：\n" +
                    RetrievalStrategyCatalog.Describe() + "\n" +
                    "选择要点：问题里带引号要逐字原文 → LexicalOnly；问法与原文用词明显不同 → ConceptOnly；" +
                    "同一个词在多个章节都出现过、需要偏向已命中的章节 → HierarchyFirst；" +
                    "字面完全对不上只能靠意思找 → SemanticOnly；拿不准走哪条 → Fusion 或 Baseline。" +
                    "需要分几步查时，把它拆成多个任务并用 depends_on 串起来（后面的任务才看得到前面的结果）；" +
                    "典型两步：先定位到某句话/某个章节，再要看该章节里的其他内容 → 第二个任务 depends_on 第一个" +
                    "并用 ChapterScope（第二级的章节范围由第一级实际命中的章节决定，不是再拿查询匹配一遍）。" +
                    "没把握就**省略 strategy**（省略即按默认臂），不要为了填满而乱选。\n" +
                    "用户问题：" + input;
                var raw = await _llm.CompleteAsync(prompt, history, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(raw.Trim().Trim('`'));
                var rows = doc.RootElement.GetProperty("tasks").EnumerateArray().Take(_options.MaxSubQueries).ToArray();
                var parsed = rows.Select((row, i) => new ReactSubTask(
                    row.TryGetProperty("id", out var id) ? id.GetString() ?? $"t{i + 1}" : $"t{i + 1}",
                    row.GetProperty("query").GetString() ?? input,
                    row.TryGetProperty("required_facts", out var facts) ? facts.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [],
                    row.TryGetProperty("depends_on", out var deps) ? deps.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [],
                    ParseArm(row))).ToList();
                if (parsed.Count > 0) return parsed;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            { /* 降级到确定性规划 */ }
        }
        return BuildSubQueries(input).Select((query, i) => new ReactSubTask($"t{i + 1}", query, [], i == 0 ? [] : [$"t{i}"])).ToList();
    }

    /// <summary>
    /// 臂的留痕文案。<c>agent</c> 与 <c>configured</c> 必须能分辨出来：
    /// 「planner 点名了 ConceptOnly」和「配置要求所有后续轮用 ConceptOnly」在报告里
    /// 长得一样的话，「agent 有没有真的在选」就永远查不出来。
    /// </summary>
    private static string ArmLabel(RetrievalStrategy? arm, string source) => arm is not { } value
        ? "（按服务默认臂）"
        : source switch
        {
            "agent" => $"（臂 {value}·agent 选）",
            "configured" => $"（臂 {value}·轮次配置）",
            _ => $"（臂 {value}）",
        };

    /// <summary>「谁定的这个臂」的人话版本，追踪日志与步理由共用同一套措辞。</summary>
    private static string SourceLabel(string source) => source switch
    {
        "agent" => "agent 选",
        "configured" => "轮次配置",
        _ => "默认臂",
    };

    /// <summary>
    /// 读 planner 给某个子任务点名的臂。**任何一步不合法都只当作「没点名」**，
    /// 绝不抛异常：一个非法臂名把整轮规划打回确定性拆句，代价远大于忽略它。
    /// 特别地，<c>"2"</c> 这类序号串必须被拒——那等于允许模型用序号隐式选臂，
    /// 一次枚举重排就会静默改行为（<see cref="RetrievalStrategyCatalog.TryParse"/> 按名字判）。
    /// </summary>
    private static RetrievalStrategy? ParseArm(JsonElement row)
    {
        if (!row.TryGetProperty("strategy", out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return RetrievalStrategyCatalog.TryParse(value.GetString(), out var arm) ? arm : null;
    }

    private async Task<StoryRagResult> RepairMissingFactsAsync(string input, IReadOnlyList<ReactSubTask> tasks,
        IReadOnlyList<ChatMessage> history, StoryRagResult pool, List<StoryRagResult> results,
        List<ReactRetrievalStep> steps, CancellationToken ct)
    {
        var text = string.Join('\n', pool.Evidence.SelectMany(e => new[] { e.Anchor }.Concat(e.Context)).Select(x => x.Text));
        var missing = tasks.SelectMany(t => t.RequiredFacts).Distinct().Where(f => !text.Contains(f, StringComparison.OrdinalIgnoreCase)).Take(_options.MaxRepairRounds).ToArray();
        if (missing.Length == 0) return pool;
        var repairResults = await Task.WhenAll(missing.Select(f =>
        {
            BackendTrace.SetCallLabel($"定向补检索 · 事实「{f}」· 服务默认臂");
            return _tool.RetrieveAsync(input + "；请只查：" + f, history, ct);
        })).ConfigureAwait(false);
        results.AddRange(repairResults);
        foreach (var repair in repairResults)
            steps.Add(new ReactRetrievalStep(steps.Count + 1, input + "；补查", repair.Status, repair.Evidence.Count,
                repair.Evidence.Count == 0 ? 0 : repair.Evidence.Average(e => e.Coverage), repair.Evidence.Count > 0,
                "缺失事实组定向补检索（按服务默认臂）", null, "default", ReactRetrievalStep.Compress(repair)));
        var merged = Merge(pool, results);
        var remaining = missing.Where(f => !string.Join('\n', merged.Evidence.Select(e => e.Anchor.Text)).Contains(f, StringComparison.OrdinalIgnoreCase)).ToArray();
        var warning = merged.Trace.Warnings.Concat(remaining.Length == 0 ? [] : ["证据边界：仍缺失事实组：" + string.Join('、', remaining)]).Distinct().ToArray();
        return merged with { Status = remaining.Length == 0 ? merged.Status : StoryStatus.Tentative, Trace = merged.Trace with { Warnings = warning } };
    }

    private static StoryRagResult VerifyClaims(StoryRagResult pool)
    {
        var warnings = pool.Trace.Warnings.ToList();
        var all = string.Join('\n', pool.Evidence.SelectMany(e => new[] { e.Anchor }.Concat(e.Context)).Select(x => x.Text));
        if (pool.Plan.Original.Contains("为什么") && !all.Contains("因为") && !all.Contains("所以") && !all.Contains("由于"))
            warnings.Add("证据校验：因果方向缺少明确连接词，回答不得把相关性写成确定因果");
        if (pool.Plan.Original.Contains("后来") && !all.Contains("后来") && !all.Contains("之后") && !all.Contains("回家"))
            warnings.Add("证据校验：时间顺序锚点不足，回答需保留时间不确定性");
        return pool with { Trace = pool.Trace with { Warnings = warnings.Distinct().ToArray() } };
    }

    private static StoryRagResult Merge(StoryRagResult primary, IReadOnlyList<StoryRagResult> results)
    {
        var evidence = results.SelectMany(r => r.Evidence)
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(e => e.Score).First())
            .OrderByDescending(e => e.Score).ThenByDescending(e => e.Coverage)
            .ToArray();
        var background = results.SelectMany(r => r.Background).DistinctBy(x => x.Id).Take(8).ToArray();
        var warnings = results.SelectMany(r => r.Trace.Warnings).Distinct(StringComparer.Ordinal).ToArray();
        var trace = primary.Trace with
        {
            Candidates = results.Sum(r => r.Trace.Candidates),
            ContextCharacters = results.Sum(r => r.Trace.ContextCharacters),
            RetrievalMode = primary.Trace.RetrievalMode + "+react-subqueries",
            Warnings = warnings
        };
        var status = evidence.Length == 0 ? primary.Status :
            evidence.Any(e => e.Exact || e.Score >= 0.38) ? StoryStatus.Answer : StoryStatus.Tentative;
        return primary with { Status = status, Evidence = evidence, Background = background, Trace = trace };
    }

    private static double Score(StoryRagResult result) =>
        (result.Status is StoryStatus.Answer ? 2 : result.Status is StoryStatus.Tentative ? 1 : 0) +
        result.Evidence.Sum(e => e.Coverage) + Math.Min(result.Evidence.Count, 5) * 0.05;

    /// <summary>
    /// 用检索记忆改写查询。**当前是确定性实现**（所以机制可离线断言）：
    /// · 记忆还没覆盖的事实组 → 补进查询（这就是「补差」）；
    /// · 已经问过、且没有新缺口 → 原样复用，避免把同一句问两遍。
    /// 后续升级点：把同一个接口换成 LLM 改写，判据与记忆都不变。
    /// </summary>
    private string RewriteQuery(ReactSubTask task)
    {
        var missing = task.RequiredFacts.Where(f => !_memory.HoldsFact(f)).Take(3).ToArray();
        if (missing.Length == 0) return task.Query;
        return task.Query + "；另需：" + string.Join('、', missing);
    }

    /// <summary>
    /// 这批证据的文本里是否逐字出现了该事实组。
    /// 沿用补检索原来的「逐字命中」判据，保证记忆里的事实覆盖与补检索判定口径一致。
    /// </summary>
    private static bool EvidenceCovers(StoryRagResult result, string fact)
    {
        if (string.IsNullOrWhiteSpace(fact)) return false;
        return result.Evidence.SelectMany(e => new[] { e.Anchor }.Concat(e.Context))
            .Any(l => l.Text.Contains(fact, StringComparison.OrdinalIgnoreCase));
    }

    private List<string> BuildSubQueries(string input)
    {
        var parts = input.Split(['。', '？', '?', '！', '!', ';', '；'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 1).ToList();
        if (parts.Count <= 1) return [input];
        var result = new List<string>();
        foreach (var part in parts)
        {
            if (!result.Contains(part, StringComparer.Ordinal)) result.Add(part);
            if (result.Count >= _options.MaxSubQueries) break;
        }
        return result;
    }
}
