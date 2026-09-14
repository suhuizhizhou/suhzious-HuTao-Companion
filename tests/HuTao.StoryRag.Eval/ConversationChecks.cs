using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.Core;
using HuTao.Foundation.Diagnostics;
using HuTao.Dialogue.Immersion;
using HuTao.Persona;
using HuTao.Knowledge.Rag;

internal static class ConversationChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var checks = new List<CheckResult>();
        void Check(string name, bool ok, string detail = "") => checks.Add(new(name, ok, detail));
        var root = Path.Combine(Path.GetTempPath(), "hutao-conversation-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new TestClock();
            var idle = "空闲 100 秒";
            var scheduler = new ProactiveScheduler(60, 120, 30, timeProvider: clock, readIdle: () => idle);
            Check("proactive_startup_cooldown", !scheduler.ShouldTrigger());
            clock.Advance(120);
            Check("proactive_ready_after_cooldown", scheduler.ShouldTrigger());
            scheduler.NotifyConversationActivity(); // 用户发送
            clock.Advance(100);
            scheduler.NotifyConversationActivity(); // 很慢的语音结束
            clock.Advance(119);
            Check("proactive_cooldown_from_audio_end", !scheduler.ShouldTrigger());
            clock.Advance(1);
            Check("proactive_recovers_after_audio_cooldown", scheduler.ShouldTrigger());
            idle = "unknown";
            Check("proactive_unknown_idle_denies", !scheduler.ShouldTrigger());
            idle = "空闲 10 秒";
            Check("proactive_busy_denies", !scheduler.ShouldTrigger());
            var dnd = new ProactiveScheduler(60, 0, 0, () => true, clock, () => "100");
            Check("proactive_dnd_denies", !dnd.ShouldTrigger());

            var log = new LocalDiagnosticLog(Path.Combine(root, "runtime.log"));
            var llm = new GateLlm();
            // 本文件只验证对话编排与调用次数，因此关掉评审层（评审会再加一次调用）。
            // 规则层保持开启，确保闸门不会误伤正常台词。
            var options = new ImmersionOptions { EnableRuleGate = true, EnableCritic = false };
            var persona = new PersonaProfile { Root = root, Name = "胡桃", SystemPrompt = "胡桃", Catchphrases = new(), Lore = "", Quotes = [] };
            var agent = new ReactAgent(persona, llm, null, [], diagnostics: log, immersionOptions: options);
            var userTurn = agent.GenerateTextAsync("你好", false);
            await llm.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var proactive = agent.GenerateTextAsync(null, true);
            Check("conversation_turns_serialized", !proactive.IsCompleted);
            llm.Release.TrySetResult();
            await userTurn;
            var invitation = await proactive;

            // 主动搭话必须走 LLM：用户要求桌宠自己开口找话题，本地固定文案做不到这件事。
            Check("proactive_uses_llm", llm.Calls == 2, $"calls={llm.Calls}");
            Check("proactive_no_stale_question_reanswer", !invitation.Reply.Contains("钟离"));
            // 主动说的话同样要进历史，否则用户下一句就没有上文可连贯。
            Check("proactive_recorded_in_history", agent.History.Count == 3, $"history={agent.History.Count}");
            Check("proactive_prompt_marks_proactive", llm.LastPrompt.Contains("本轮是主动搭话"));
            // 主动搭话不做剧情检索，避免角色自己给自己塞未经校验的剧情断言。
            Check("proactive_skips_story_retrieval", !llm.LastPrompt.Contains("story_archive:"));

            foreach (var name in new[] { "胡桃", "芙宁娜", "可莉" })
            {
                var character = new PersonaProfile { Root = root, Name = name, SystemPrompt = name, Catchphrases = new(), Lore = "", Quotes = [] };
                var actor = new ReactAgent(character, llm, null, [], diagnostics: log, immersionOptions: options);
                actor.RestoreHistory([new("user", "喂，你知道钟离吗？"), new("assistant", "这件事我还没有可靠的依据")]);
                var before = llm.Calls;
                var output = await actor.GenerateTextAsync(null, true);
                Check("proactive_goes_through_llm_" + name, llm.Calls == before + 1,
                    $"before={before}; after={llm.Calls}");
                // 真实上界：stub 只回一句「你好呀。」，所以必须恰好切成 1 段且不空。
                // 之前这里被放宽成 Count > 0，那是没有依据的——它会让「回了个空段」也算过。
                Check("proactive_no_stale_story_" + name,
                    !output.Reply.Contains("钟离") &&
                    output.Segments.Count == 1 &&
                    output.Segments[0].Text.Trim().Length > 0,
                    $"segments={output.Segments.Count}; reply={output.Reply}");
            }
            var diagnostic = File.ReadAllText(log.FilePath);
            Check("conversation_log_has_trigger_id_path", diagnostic.Contains("turn_id") && diagnostic.Contains("proactive") && diagnostic.Contains("user"));
            Check("conversation_log_no_user_text", !diagnostic.Contains("钟离") && !diagnostic.Contains("你好"));
            var result = new StoryRagResult(StoryQueryAnalyzer.Plan("胡桃"), StoryStatus.NotFound, [], [],
                new StoryRagTrace("test", 0, false, 0, 0, "none", []));
            var noData = StoryAnswerComposer.Fallback(result, [], "deterministic-boundary");
            var failed = StoryAnswerComposer.Fallback(result, ["missing_citation"], "validation-fallback");
            Check("generation_failure_not_claimed_as_missing_knowledge", noData.Reply.Contains("可靠的依据") && !failed.Reply.Contains("可靠的依据"));
            Check("generation_failure_keeps_diagnostic_reason", failed.Issues.Contains("missing_citation") && failed.Path == "validation-fallback");
        }
        catch (Exception ex) { checks.Add(new("conversation_unexpected", false, ex.GetType().Name)); }
        finally { Directory.Delete(root, recursive: true); } // 仅本次拥有的 GUID 临时目录
        return checks;
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
    private sealed class GateLlm : ILLMProvider
    {
        public string Name => "offline-gated";
        public int Calls;
        public string LastPrompt { get; private set; } = "";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> CompleteAsync(string prompt, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
        {
            Calls++;
            LastPrompt = prompt;
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return "你好呀。";
        }
    }
}
