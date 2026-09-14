using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Immersion;
using HuTao.Knowledge.Memory;
using HuTao.Persona;

/// <summary>
/// 长期记忆四阶段的离线回归。全部用可注入时钟，不依赖真实模型与真实时间。
/// </summary>
internal static class MemoryChecks
{
    private static readonly DateTimeOffset Now =
        new(2026, 3, 15, 10, 0, 0, TimeSpan.FromHours(8));

    public static async Task<List<CheckResult>> RunAsync()
    {
        var checks = new List<CheckResult>();
        void Check(string name, bool ok, string detail = "") => checks.Add(new(name, ok, detail));
        var root = Path.Combine(Path.GetTempPath(), "hutao-memory-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckTemporal(Check);
            CheckStore(root, Check);
            CheckRetrieval(root, Check);
            await CheckConsolidationAsync(root, Check);
            await CheckMaintenanceAsync(root, Check);
            await CheckCriticSeesMemoryAsync(Check);
        }
        catch (Exception ex)
        {
            checks.Add(new("memory_unexpected", false, ex.GetType().Name + ": " + ex.Message));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        return checks;
    }

    // ── 阶段 2：时间解析 ──────────────────────────────────────────────────

    private static void CheckTemporal(Action<string, bool, string> check)
    {
        var tomorrow = TemporalExpression.Parse("明天把稿子发你", Now);
        check("temporal_tomorrow_expires_end_of_day",
            tomorrow.ValidUntil is { } t1 && t1.Date == Now.Date.AddDays(1),
            tomorrow.ValidUntil?.ToString("O") ?? "null");

        var inThreeDays = TemporalExpression.Parse("三天后交论文", Now);
        check("temporal_relative_days",
            inThreeDays.ValidUntil is { } t2 && t2.Date == Now.Date.AddDays(3),
            inThreeDays.ValidUntil?.ToString("O") ?? "null");

        var twoWeeks = TemporalExpression.Parse("两周内搞定这个功能", Now);
        check("temporal_relative_weeks",
            twoWeeks.ValidUntil is { } t3 && t3.Date == Now.Date.AddDays(14),
            twoWeeks.ValidUntil?.ToString("O") ?? "null");

        var nextWeek = TemporalExpression.Parse("下周要考试", Now);
        check("temporal_next_week_is_future",
            nextWeek.ValidUntil is { } t4 && t4 > Now.AddDays(3) && t4 < Now.AddDays(16),
            nextWeek.ValidUntil?.ToString("O") ?? "null");

        // 过去的时间不给失效点，只给口径——不能把已经发生的事判成「将来会过期」。
        var yesterday = TemporalExpression.Parse("昨天见了朋友", Now);
        check("temporal_past_has_no_expiry", yesterday.ValidUntil is null && yesterday.HasScope,
            yesterday.Hint);

        // 含糊表述不给期限，避免系统自作主张把仍然有效的记忆判死。
        var vague = TemporalExpression.Parse("以后有空再聊", Now);
        check("temporal_vague_has_no_expiry", vague.ValidUntil is null, vague.ValidUntil?.ToString("O") ?? "null");

        var plain = TemporalExpression.Parse("今天天气不错", Now);
        check("temporal_plain_statement_still_scoped", plain.ValidUntil is not null, "今天应给出当日结束");

        var nonsense = TemporalExpression.Parse("随便说点什么吧", Now);
        check("temporal_no_scope_for_plain_chat", !nonsense.HasScope, nonsense.Hint);

        var badDate = TemporalExpression.Parse("2月30日再说", Now);
        check("temporal_invalid_date_does_not_throw", badDate.ValidUntil is null, badDate.Hint);

        check("temporal_describe_age_days",
            TemporalExpression.DescribeAge(Now.AddDays(-3), Now) == "3 天前",
            TemporalExpression.DescribeAge(Now.AddDays(-3), Now));
        check("temporal_describe_age_hours",
            TemporalExpression.DescribeAge(Now.AddHours(-2), Now) == "2 小时前",
            TemporalExpression.DescribeAge(Now.AddHours(-2), Now));
    }

    // ── 存储：去重、导入游标、取代、持久化 ────────────────────────────────

    private static void CheckStore(string root, Action<string, bool, string> check)
    {
        var path = Path.Combine(root, "store.json");
        var store = new ConversationMemoryStore(path, new MemoryOptions());

        var first = store.ObserveTurn("user", "我在做一个桌宠项目", Now);
        var duplicate = store.ObserveTurn("user", "我在做一个桌宠项目", Now.AddSeconds(30));
        check("memory_dedup_within_window", first is not null && duplicate is null, $"count={store.Count}");

        var otherSpeaker = store.ObserveTurn("assistant", "我在做一个桌宠项目", Now.AddSeconds(30));
        check("memory_dedup_is_speaker_specific", otherSpeaker is not null, $"count={store.Count}");

        var noise = store.ObserveTurn("user", "嗯嗯", Now);
        check("memory_rejects_noise", noise is null, $"count={store.Count}");

        check("memory_importance_prefers_personal_facts",
            ConversationMemoryStore.EstimateImportance("记住我最喜欢猫", "user")
            > ConversationMemoryStore.EstimateImportance("今天天气怎么样？", "user"),
            "记住我喜欢… 应高于 随口提问");

        // 导入游标：重复导入不产生重复记忆，只补增量。
        var log = new List<(string, string, string)>
        {
            ("2026-03-01 09:00:00", "user", "我上周开始学吉他"),
            ("2026-03-01 09:01:00", "assistant", "那可得好好练呀"),
            ("2026-03-02 20:00:00", "user", "下周要去一趟杭州"),
        };
        var importedFirst = store.ImportChatLog(log, Now);
        var importedAgain = store.ImportChatLog(log, Now);
        var beforeIncrement = store.Count;
        log.Add(("2026-03-03 08:00:00", "user", "我把吉他带去公司了"));
        var importedIncrement = store.ImportChatLog(log, Now);
        check("memory_import_cursor_is_idempotent",
            importedFirst == 3 && importedAgain == 0 && importedIncrement == 1,
            $"first={importedFirst}; again={importedAgain}; increment={importedIncrement}");
        check("memory_import_increment_added_one", store.Count == beforeIncrement + 1, $"count={store.Count}");

        // 取代：旧记录保留在磁盘上，但不参与常规检索。
        var target = store.ObserveTurn("user", "我住在上海", Now)!;
        var replacement = store.AddConsolidated(
            "用户住在杭州", MemoryKind.Fact, 0.8, [target.Id], Now, Now, null, target.Id);
        var reloaded = store.FindById(target.Id)!;
        check("memory_supersede_marks_old_record",
            reloaded.Superseded && reloaded.SupersededById == replacement.Id,
            $"superseded_by={reloaded.SupersededById}");

        // 持久化往返：新实例读回同样的条数。
        store.Flush();
        var reopened = new ConversationMemoryStore(path, new MemoryOptions());
        check("memory_persists_roundtrip", reopened.Count == store.Count,
            $"before={store.Count}; after={reopened.Count}");
        check("memory_persists_supersede", reopened.FindById(target.Id)?.Superseded == true, "取代关系应落盘");
    }

    // ── 阶段 1：检索与打分 ────────────────────────────────────────────────

    private static void CheckRetrieval(string root, Action<string, bool, string> check)
    {
        var path = Path.Combine(root, "retrieval.json");
        var store = new ConversationMemoryStore(path, new MemoryOptions());
        store.ObserveTurn("user", "我的猫叫大咪，特别爱吃鱼", Now.AddDays(-2));
        // 一批「最近」干扰项：让「最近」成为低 idf 的常见词，而「大咪」保持高 idf。
        // 小语料里 IDF 无法区分常见词与专有名词，这正是真实语料要替我们做的事。
        for (var i = 0; i < 7; i++)
            store.ObserveTurn("user", $"我最近在忙第{i}个项目的事", Now.AddDays(-1).AddMinutes(i));
        store.ObserveTurn("user", "周末想去爬山，你有推荐吗", Now.AddHours(-3));
        store.ObserveTurn("user", "今天中午吃了拉面", Now.AddMinutes(-30));

        var retriever = new MemoryRetriever(store);

        var aboutCat = retriever.Retrieve(new MemoryQuery("大咪最近怎么样", [], Now, MaxResults: 3));
        check("memory_retrieval_finds_relevant",
            aboutCat.Count > 0 && aboutCat[0].Record.Text.Contains("大咪"),
            aboutCat.Count == 0 ? "no hits" : aboutCat[0].Record.Text);

        // 专有名词应当压过只是共享「最近」这个常见词的更新记录：
        // 相关度先赢，新鲜度不能翻盘。
        check("memory_relevance_outranks_recency",
            aboutCat.Count > 0 && aboutCat[0].Score > (aboutCat.Count > 1 ? aboutCat[1].Score : 0) &&
            aboutCat[0].Relevance > (aboutCat.Count > 1 ? aboutCat[1].Relevance : 0),
            string.Join(" | ", aboutCat.Select(h => $"{h.Record.Text[..Math.Min(8, h.Record.Text.Length)]}:{h.Score:F2}/rel{h.Relevance:F2}")));

        // 无关问题不该硬凑记忆。
        var irrelevant = retriever.Retrieve(new MemoryQuery("量子纠缠的数学定义", [], Now, MaxResults: 3));
        check("memory_retrieval_gates_irrelevant", irrelevant.Count == 0,
            $"hits={irrelevant.Count}");

        // 已经在当前窗口里的内容不重复注入。
        var fingerprint = ConversationMemoryStore.Fingerprint("user", "我的猫叫大咪，特别爱吃鱼");
        var excluded = retriever.Retrieve(new MemoryQuery(
            "大咪最近怎么样", [], Now, new HashSet<string>([fingerprint]), MaxResults: 3));
        check("memory_retrieval_excludes_current_window",
            excluded.All(hit => !hit.Record.Text.Contains("大咪")),
            $"hits={excluded.Count}");

        // 同样相关时，新的排在前面。
        var pair = new ConversationMemoryStore(Path.Combine(root, "recency.json"), new MemoryOptions());
        pair.ObserveTurn("user", "我在做桌宠项目甲", Now.AddDays(-20));
        pair.ObserveTurn("user", "我在做桌宠项目乙", Now.AddHours(-1));
        var ranked = new MemoryRetriever(pair)
            .Retrieve(new MemoryQuery("桌宠项目进展", [], Now, MaxResults: 2));
        check("memory_retrieval_prefers_recent",
            ranked.Count == 2 && ranked[0].Record.ObservedAt > ranked[1].Record.ObservedAt,
            string.Join(" | ", ranked.Select(h => $"{h.Record.Text}@{h.Score:F2}")));

        // 过时信息保留可召回但明确标注并降权——隐藏会让角色更容易前后矛盾。
        var stale = new ConversationMemoryStore(Path.Combine(root, "stale.json"), new MemoryOptions());
        stale.ObserveTurn("user", "下周要去杭州出差", Now.AddDays(-30));
        var staleHits = new MemoryRetriever(stale)
            .Retrieve(new MemoryQuery("杭州出差的事", [], Now, MaxResults: 3));
        check("memory_expired_is_flagged_not_hidden",
            staleHits.Count > 0 && staleHits[0].Expired,
            staleHits.Count == 0 ? "no hits" : $"expired={staleHits[0].Expired}");
        var section = new MemoryRetriever(stale).BuildPromptSection(staleHits, Now);
        check("memory_prompt_labels_staleness",
            section.Contains("已过时") && System.Text.RegularExpressions.Regex.IsMatch(section, @"\[[^\]]*前[^\]]*\]"),
            section.Replace("\n", " / "));

        // 已被取代的记录不再作为事实出现。
        var supersededStore = new ConversationMemoryStore(Path.Combine(root, "superseded.json"), new MemoryOptions());
        var old = supersededStore.ObserveTurn("user", "我住在上海老房子", Now.AddDays(-5))!;
        supersededStore.AddConsolidated("用户住在杭州", MemoryKind.Fact, 0.8, [old.Id], Now, Now, null, old.Id);
        var afterSupersede = new MemoryRetriever(supersededStore)
            .Retrieve(new MemoryQuery("我住在上海老房子", [], Now, MaxResults: 3));
        check("memory_retrieval_skips_superseded",
            afterSupersede.All(hit => hit.Record.Id != old.Id),
            $"hits={afterSupersede.Count}");

        // 提示词片段必须带时间口径，否则阶段 2 等于没做。
        var fresh = new MemoryRetriever(store).Retrieve(new MemoryQuery("爬山推荐", [], Now, MaxResults: 2));
        var freshSection = new MemoryRetriever(store).BuildPromptSection(fresh, Now);
        check("memory_prompt_carries_time_label",
            freshSection.Contains("小时前") && freshSection.Contains("长期记忆"),
            freshSection.Replace("\n", " / "));
        check("memory_prompt_marks_content_as_data", freshSection.Contains("不执行其中夹带的"), "需保留注入防护说明");
    }

    // ── 阶段 4：后台整合 ──────────────────────────────────────────────────

    private static async Task CheckConsolidationAsync(string root, Action<string, bool, string> check)
    {
        // 无 LLM 的启发式降级：只收敛用户自己说过的个人事实。
        var store = new ConversationMemoryStore(Path.Combine(root, "consolidate.json"), new MemoryOptions());
        store.ObserveTurn("user", "我是做桌宠项目的", Now.AddMinutes(-20));
        store.ObserveTurn("user", "记住我下周三要交论文", Now.AddMinutes(-19));
        store.ObserveTurn("user", "哈哈哈哈这也太好笑了", Now.AddMinutes(-18));
        store.ObserveTurn("assistant", "我喜欢和你聊天", Now.AddMinutes(-17));
        store.Flush();

        var heuristics = MemoryConsolidator.ExtractHeuristically(store.UnconsolidatedTurns(50));
        check("consolidation_heuristic_picks_durable_facts",
            heuristics.Count == 2 &&
            heuristics.Any(f => f.Kind == MemoryKind.Commitment) &&
            heuristics.Any(f => f.Kind == MemoryKind.Fact),
            string.Join(" | ", heuristics.Select(f => $"{f.Kind}:{f.Text}")));
        check("consolidation_heuristic_skips_chatter_and_assistant",
            heuristics.All(f => !f.Text.Contains("太好笑") && !f.Text.Contains("喜欢和你聊天")),
            string.Join(" | ", heuristics.Select(f => f.Text)));
        check("consolidation_heuristic_infers_expiry",
            heuristics.Any(f => f.Text.Contains("论文") && f.ValidUntil is not null),
            "下周三应推出失效时间");

        var consolidator = new MemoryConsolidator(store);
        var result = await consolidator.RunAsync(Now);
        check("consolidation_heuristic_path_reported",
            result.Path == "heuristic" && result.Added == 2,
            $"path={result.Path}; added={result.Added}; superseded={result.Superseded}");

        var secondRun = await consolidator.RunAsync(Now.AddMinutes(1));
        check("consolidation_advances_cursor",
            secondRun.Path == "nothing-to-do" && secondRun.Added == 0,
            $"path={secondRun.Path}");

        // LLM 回包解析：字段容错、来源可追溯、无效来源丢弃。
        var turns = store.Snapshot().Where(r => r.Kind == MemoryKind.Turn).ToArray();
        var payload = """
            {"facts":[
              {"text":"用户在做桌宠项目","kind":"fact","importance":0.8,
               "sources":["SOURCE_ID"],"valid_until":null,"supersedes":null},
              {"text":"用户下周三要交论文","kind":"commitment","importance":0.9,
               "sources":["MADE_UP"],"valid_until":"2026-03-18","supersedes":null},
              {"text":"","kind":"fact","importance":0.5,"sources":[]}
            ]}
            """.Replace("SOURCE_ID", turns[0].Id);
        var parsed = MemoryConsolidator.ParseFacts(payload, turns, Now);
        check("consolidation_parses_llm_contract",
            parsed.Count == 2 &&
            parsed[0].Kind == MemoryKind.Fact &&
            parsed[1].Kind == MemoryKind.Commitment &&
            Math.Abs(parsed[0].Importance - 0.8) < 0.001,
            string.Join(" | ", parsed.Select(f => $"{f.Kind}:{f.Text}:{f.Importance}")));
        check("consolidation_drops_unknown_source_ids",
            parsed[1].SourceIds.Count == 0, $"sources={parsed[1].SourceIds.Count}");
        check("consolidation_keeps_explicit_valid_until",
            parsed[1].ValidUntil is not null, parsed[1].ValidUntil?.ToString("O") ?? "null");

        // supersedes 生效：旧事实被标记取代而不是删除。
        var supStore = new ConversationMemoryStore(Path.Combine(root, "supersede.json"), new MemoryOptions());
        var oldTurn = supStore.ObserveTurn("user", "我住在上海", Now.AddDays(-10))!;
        var oldFact = supStore.AddConsolidated(
            "用户住在上海", MemoryKind.Fact, 0.7, [oldTurn.Id], Now.AddDays(-10), Now.AddDays(-10), null, null);
        supStore.ObserveTurn("user", "我搬到杭州了", Now.AddMinutes(-5));
        supStore.Flush();
        var supTurns = supStore.UnconsolidatedTurns(50);
        var supPayload = $$"""
            {"facts":[{"text":"用户住在杭州","kind":"fact","importance":0.8,
              "sources":["{{supTurns[0].Id}}"],"valid_until":null,"supersedes":"{{oldFact.Id}}"}]}
            """;
        var facts = MemoryConsolidator.ParseFacts(supPayload, supTurns, Now);
        supStore.AddConsolidated(
            facts[0].Text, facts[0].Kind, facts[0].Importance, facts[0].SourceIds,
            facts[0].ObservedAt, Now, facts[0].ValidUntil, facts[0].SupersedesId);
        check("consolidation_supersede_keeps_old_on_disk",
            supStore.FindById(oldFact.Id) is { Superseded: true } &&
            supStore.Snapshot().Any(r => r.Text.Contains("杭州")),
            $"old_superseded={supStore.FindById(oldFact.Id)?.Superseded}");

        // 整合失败不推进游标，下次空闲可以重试。
        var failing = new ConversationMemoryStore(Path.Combine(root, "failing.json"), new MemoryOptions());
        failing.ObserveTurn("user", "我是做桌宠项目的", Now);
        failing.Flush();
        var failConsolidator = new MemoryConsolidator(failing, new ThrowingLlm());
        var failResult = await failConsolidator.RunAsync(Now);
        check("consolidation_failure_does_not_advance_cursor",
            failResult.Path == "failed" && failing.UnconsolidatedTurnCount == 1,
            $"path={failResult.Path}; pending={failing.UnconsolidatedTurnCount}");
    }

    // ── 阶段 4：调度边界 ──────────────────────────────────────────────────

    private static async Task CheckMaintenanceAsync(string root, Action<string, bool, string> check)
    {
        var options = new MemoryOptions
        {
            MinTurnsToConsolidate = 3,
            ConsolidationIdle = TimeSpan.FromMinutes(3),
            ConsolidationCooldown = TimeSpan.FromMinutes(20),
        };
        var store = new ConversationMemoryStore(Path.Combine(root, "maintenance.json"), options);
        var maintenance = new MemoryMaintenance(store, new MemoryConsolidator(store), options);

        store.ObserveTurn("user", "我是做桌宠项目的", Now);
        check("maintenance_blocked_by_idle",
            !maintenance.ShouldRun(TimeSpan.FromMinutes(1), Now), "空闲不足不该整合");
        check("maintenance_blocked_by_pending_count",
            !maintenance.ShouldRun(TimeSpan.FromMinutes(10), Now), "待提炼太少不该整合");

        store.ObserveTurn("user", "我喜欢在深夜写代码", Now);
        store.ObserveTurn("user", "记住下周三要交论文", Now);
        check("maintenance_runs_when_conditions_met",
            maintenance.ShouldRun(TimeSpan.FromMinutes(10), Now), "条件满足应触发");
        check("maintenance_not_due_returns_null",
            await maintenance.RunIfDueAsync(TimeSpan.FromSeconds(10), Now) is null, "不满足时返回 null");

        var ran = await maintenance.RunIfDueAsync(TimeSpan.FromMinutes(10), Now);
        check("maintenance_runs_and_reports",
            ran is { Added: > 0 }, ran is null ? "null" : $"added={ran.Added}");

        store.ObserveTurn("user", "我是做桌宠项目的二期", Now.AddMinutes(1));
        store.ObserveTurn("user", "我喜欢在深夜写代码二期", Now.AddMinutes(1));
        store.ObserveTurn("user", "记住下周三要交论文二期", Now.AddMinutes(1));
        check("maintenance_blocked_by_cooldown",
            !maintenance.ShouldRun(TimeSpan.FromMinutes(10), Now.AddMinutes(5)), "冷却期内不该再整合");
        check("maintenance_recovers_after_cooldown",
            maintenance.ShouldRun(TimeSpan.FromMinutes(10), Now.AddMinutes(30)), "冷却结束后应恢复");

        var disabled = new MemoryOptions { EnableConsolidation = false };
        var disabledStore = new ConversationMemoryStore(Path.Combine(root, "disabled.json"), disabled);
        var disabledMaintenance = new MemoryMaintenance(disabledStore, new MemoryConsolidator(disabledStore), disabled);
        check("maintenance_respects_disable_switch",
            !disabledMaintenance.Enabled && !disabledMaintenance.ShouldRun(TimeSpan.FromDays(1), Now),
            "关闭后不应触发");
    }

    // ── 阶段 3：审查员看得到长期记忆 ──────────────────────────────────────

    private static async Task CheckCriticSeesMemoryAsync(Action<string, bool, string> check)
    {
        var llm = new CapturingLlm();
        var critic = new ImmersionCritic(llm, 0.6);
        var parser = new SpeechSegmentParser();
        var request = new ImmersionRequest(
            "胡桃", "你是胡桃", "记得呀，你之前说过住在上海。", parser.Parse("记得呀，你之前说过住在上海。"),
            [new ChatMessage("user", "我们之前聊到哪了")], "我们之前聊到哪了", false, "唔…",
            "【长期记忆】\n- [3 天前] 用户说自己搬到杭州了\n");

        var verdict = await critic.ReviewAsync(request, CancellationToken.None);
        check("critic_receives_long_term_memory",
            llm.LastSystem.Contains("用户说自己搬到杭州了"),
            "审查员提示词应包含本轮召回的记忆");
        check("critic_prompt_explains_staleness_rule",
            llm.LastSystem.Contains("已过时") && llm.LastSystem.Contains("长期记忆"),
            "审查员需知道已过时记忆不作为矛盾依据");
        check("critic_still_returns_verdict_with_memory",
            verdict.Verdict is { Passed: true },
            verdict.Verdict?.KindsSummary ?? verdict.Reason);

        // 没有记忆时不应凭空塞入记忆段落。
        var plain = new CapturingLlm();
        await new ImmersionCritic(plain, 0.6).ReviewAsync(
            request with { MemoryContext = "" }, CancellationToken.None);
        check("critic_omits_memory_section_when_empty",
            !plain.LastSystem.Contains("本轮同时提供给角色的长期记忆"),
            "无记忆时不应出现记忆段落");

        // 记忆原文要真的交给演员：确认提示词构建会带上它。
        var profile = new PersonaProfile
        {
            Root = Path.GetTempPath(), Name = "胡桃", SystemPrompt = "你是胡桃",
            Catchphrases = new(), Lore = "", Quotes = [],
        };
        var built = new AgentPromptBuilder().Build(new AgentPromptContext(
            profile, "观察", "", [], false, LongTermMemory: "【长期记忆】\n- [1 天前] 用户喜欢猫\n"));
        check("actor_prompt_includes_long_term_memory",
            built.Contains("用户喜欢猫"), "演员提示词应包含长期记忆片段");
    }

    private sealed class CapturingLlm : ILLMProvider
    {
        public string Name => "capture";
        public string LastSystem { get; private set; } = "";
        public Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
            CancellationToken ct = default)
        {
            LastSystem = systemPrompt + "\n" + string.Join('\n', history.Select(h => h.Content));
            return Task.FromResult("{\"ooc\":false,\"coherence\":0.95,\"problems\":[]}");
        }
    }

    private sealed class ThrowingLlm : ILLMProvider
    {
        public string Name => "throwing";
        public Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatMessage> history,
            CancellationToken ct = default)
            => throw new HttpRequestException("offline");
    }
}
