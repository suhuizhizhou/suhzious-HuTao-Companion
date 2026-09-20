using HuTao.Dialogue.Core;
using HuTao.Dialogue.Immersion;
using HuTao.Dialogue.Tools;
using HuTao.Foundation.Abstractions;
using HuTao.Knowledge.Memory;
using HuTao.Knowledge.Rag;
using HuTao.Persona;

internal static class RecallChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var checks = new List<CheckResult>();
        void Check(string name, bool pass, string detail = "") => checks.Add(new(name, pass, detail));
        var root = Path.Combine(Path.GetTempPath(), "hutao-recall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.Now;
            var store = new ConversationMemoryStore(Path.Combine(root, "memory.json"), new MemoryOptions());
            var seed = store.ObserveTurn("user", "我喝咖啡会心悸，只喝无糖乌龙茶。", now.AddDays(-2))!;
            var retriever = new MemoryRetriever(store);
            var planner = new FixedLlm("""
                {"intent":"挑饮品","memory_queries":["咖啡 乌龙茶"],"story_tasks":[]}
                """);
            var recall = new ConversationRecall(planner, StoryLexicon.Empty, [new MemoryRecallStrategy(retriever)]);
            var result = await recall.RunAsync("写累了，喝点什么？", [], now, default);
            Check("recall_paraphrase_finds_personal_preference", result.Memories.Any(h => h.Record.Id == seed.Id));
            Check("recall_query_trace_has_source_ids", result.Steps.Any(s => s.Query == "咖啡 乌龙茶" && s.EvidenceIds.Contains("memory:" + seed.Id)));
            Check("recall_no_story_for_personal_question", result.Story is null);
            var semantic = new MemoryRecallStrategy(retriever, new FixedLlm($"{{\"ids\":[\"{seed.Id}\",\"invented\"]}}"));
            var semanticHit = await semantic.RecallAsync(new("饮品禁忌", [], now, new HashSet<string>()), default);
            Check("recall_semantic_selection_only_returns_real_ids", semanticHit.Memories.Count == 1 &&
                semanticHit.Memories[0].Record.Id == seed.Id && semanticHit.Memories[0].Reason.Contains("semantic-selection"));
            var semanticExcluded = await semantic.RecallAsync(new("饮品禁忌", [], now,
                new HashSet<string> { seed.Fingerprint }), default);
            Check("recall_semantic_selection_respects_exclusions", semanticExcluded.Memories.Count == 0);
            var excluded = await recall.RunAsync("喝点什么", [new("user", seed.Text)], now, default);
            Check("recall_does_not_duplicate_visible_history", excluded.Memories.Count == 0);
            var malformed = new ConversationRecall(new FixedLlm("{}"), StoryLexicon.Empty, [new MemoryRecallStrategy(retriever)]);
            var fallback = await malformed.RunAsync("咖啡", [], now, default);
            Check("recall_bad_plan_retains_seed_memory", fallback.Planning.StartsWith("fallback:") && fallback.Memories.Count > 0);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await recall.RunAsync("咖啡", [], now, cancellation.Token); Check("recall_cancellation", false); }
            catch (OperationCanceledException) { Check("recall_cancellation", true); }

            var lore = new FixtureStory();
            var loop = new ReactRetrievalLoop(new StoryKnowledgeTool(lore), null, new ReactRetrievalOptions { EnableLlmPlanner = false });
            var joint = new ConversationRecall(new FixedLlm("""
                {"intent":"一起聊手工","memory_queries":["咖啡 乌龙茶"],"story_tasks":[{"query":"胡桃 帽子","strategy":"Baseline"}]}
                """), StoryLexicon.Empty, [new MemoryRecallStrategy(retriever), new StoryRecallStrategy(loop)]);
            var both = await joint.RunAsync("歇会，聊聊你的帽子吧", [], now, default);
            Check("recall_executes_both_strategies", both.Memories.Count > 0 && both.Story?.EvidencePool.Evidence.Count > 0);
            var grounded = both.Story!.EvidencePool with { ConversationMemories = both.Memories };
            var response = "{\"answerability\":\"supported\",\"segments\":[" +
                $"{{\"text\":\"你只喝无糖乌龙茶。\",\"kind\":\"fact\",\"evidence_ids\":[\"memory:{seed.Id}\"]}}," +
                $"{{\"text\":\"帽子是我亲手改的。\",\"kind\":\"fact\",\"evidence_ids\":[\"{grounded.Evidence[0].Id}\"]}}]}}";
            Check("recall_joint_answer_accepts_both_provenances", StoryAnswerComposer.Validate(response, grounded).Validated);
            Check("recall_memory_cannot_be_forged", !StoryAnswerComposer.Validate(response.Replace(seed.Id, "missing"), grounded).Validated);
            Check("recall_source_speaker_and_time_present", StoryAnswerComposer.Prompt(grounded).Contains("observed_at") &&
                retriever.BuildPromptSection(both.Memories, now).Contains("用户说"));

            var held = new RetrievalMemory(1);
            held.Record(new("first", grounded.Evidence, ["old fact"], "Answer", true));
            held.Record(new("second", [], [], "NotFound", false));
            Check("recall_eviction_removes_evidence_facts_queries", held.HeldEvidenceIds.Count == 0 && !held.HoldsFact("old fact") && !held.HasAsked("first"));
            await loop.RunAsync("胡桃 帽子", []);
            var switched = await loop.RunAsync("写代码", []);
            Check("recall_topic_switch_does_not_inherit_evidence", switched?.EvidencePool.Evidence.Count == 0);
            Check("recall_bypass_stops_without_gap_rounds", switched?.Steps.Count == 1);

            var persona = new PersonaProfile { Root = root, Name = "胡桃", SystemPrompt = "胡桃", Catchphrases = new(), Lore = "", Quotes = [] };
            var actor = new FixedLlm(planner.Text, "你喝咖啡会心悸，还是无糖乌龙茶吧。");
            var agent = new ReactAgent(persona, actor, null, [], memory: store,
                immersionOptions: new ImmersionOptions { EnableCritic = false, EnableRuleGate = false });
            var turn = await agent.GenerateTextAsync("写累了，喝点什么？", false);
            Check("recall_real_agent_injects_personal_evidence", turn.Recall?.Memories.Any(h => h.Record.Id == seed.Id) == true && actor.LastPrompt.Contains(seed.Text));
        }
        finally { Directory.Delete(root, true); }
        return checks;
    }

    private sealed class FixedLlm(string text, string? reply = null) : ILLMProvider
    {
        public string Text => text;
        public string Name => "recall-fixture";
        public string LastPrompt { get; private set; } = "";
        public Task<string> CompleteAsync(string prompt, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); LastPrompt = prompt;
            return Task.FromResult(reply is null || prompt.Contains("Conversation Recall Planner") ? text : reply);
        }
    }

    private sealed class FixtureStory : IStoryRagService
    {
        public Task WarmupAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<StoryRagResult> RetrieveAsync(string input, IReadOnlyList<ChatMessage>? history = null,
            CancellationToken ct = default, RetrievalStrategy? strategy = null, IReadOnlyList<string>? chapterScope = null)
        {
            var line = new StoryDialogueLine { ChapterId = "hat", Speaker = "胡桃", Text = "帽子是我亲手改的。", LineId = 1, Resolved = true };
            var hit = input.Contains("帽子");
            var plan = new StoryQueryPlan(input, input, [input], [], hit ? StoryRoute.Retrieve : StoryRoute.Bypass,
                false, true, false, "fixture", "胡桃");
            return Task.FromResult(new StoryRagResult(plan, hit ? StoryStatus.Answer : StoryStatus.Bypass,
                hit ? [new(line.EvidenceId, line, [line], 1, 1, true, "fixture", StoryPerspective.Personal, "fixture")] : [],
                [], new("fixture", 0, false, hit ? 1 : 0, 0, "fixture", [])));
        }
    }
}
