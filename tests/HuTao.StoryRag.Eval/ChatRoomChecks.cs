using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.ChatRoom;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Llm;
using HuTao.Persona;
using HuTao.Voice;

/// <summary>
/// 聊天室机制的离线回归。全程用桩 agent 与桩导演，不碰网络也不起 TTS，
/// 因此可以稳定断言「谁在什么时候说了什么」这类时序行为。
/// </summary>
internal static class ChatRoomChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var checks = new List<CheckResult>();
        void Check(string name, bool ok, string detail = "") => checks.Add(new(name, ok, detail));

        try
        {
            // ── 基本轮转：导演按序点名，两个角色交替说话 ──
            var a = new StubConversation("胡桃", "本堂主来啦。");
            var b = new StubConversation("安魂曲", "……你好。");
            var room = new ChatRoom(
                [new ChatRoomParticipant("hutao", "胡桃", a, Persona("胡桃")),
                 new ChatRoomParticipant("lacrimosa", "安魂曲", b, Persona("安魂曲"))],
                new ScriptedDirector(["hutao", "lacrimosa", "hutao", "lacrimosa", "hutao"]),
                new ChatRoomOptions { MinTurns = 2, MaxTurns = 6 });

            var turns = await room.RunAsync();
            Check("chatroom_alternates_speakers",
                turns.Count >= 2 && turns[0].SpeakerName == "胡桃" && turns[1].SpeakerName == "安魂曲",
                string.Join(" → ", turns.Select(t => t.SpeakerName)));

            // ── 每个发言者都收到了「场景 + 别人说过的话」──
            Check("chatroom_speaker_sees_transcript",
                b.LastInput.Contains("胡桃") && b.LastInput.Contains("本堂主来啦"),
                b.LastInput.Replace("\n", " / "));

            // 场景用全角括号旁白包裹，不出现「导演/系统」这类元指令，避免破坏沉浸。
            Check("chatroom_framing_is_in_world",
                b.LastInput.StartsWith('（') && !b.LastInput.Contains("导演") && !b.LastInput.Contains("系统"),
                b.LastInput[..Math.Min(24, b.LastInput.Length)]);

            // ── 用户插话会被下一位回应 ──
            var c = new StubConversation("胡桃", "哦呀。");
            var d = new StubConversation("安魂曲", "嗯。");
            var interjectRoom = new ChatRoom(
                [new ChatRoomParticipant("hutao", "胡桃", c, Persona("胡桃")),
                 new ChatRoomParticipant("lacrimosa", "安魂曲", d, Persona("安魂曲"))],
                new ScriptedDirector(["hutao"]),
                new ChatRoomOptions { MinTurns = 1 });
            interjectRoom.Interject("你们俩认识吗？");
            await interjectRoom.StepAsync();
            Check("chatroom_user_interjection_reaches_speaker",
                c.LastInput.Contains("你们俩认识吗") && c.LastInput.Contains("旁观"),
                c.LastInput.Replace("\n", " / "));
            Check("chatroom_transcript_records_user_turn",
                interjectRoom.Transcript.Any(t => t.IsUser && t.Text == "你们俩认识吗？"),
                $"turns={interjectRoom.Transcript.Count}");

            // ── 最小轮数保护：导演刚开场就喊 END 时不能被采纳 ──
            var e = new StubConversation("胡桃", "在呢。");
            var f = new StubConversation("安魂曲", "在。");
            var guarded = new ChatRoom(
                [new ChatRoomParticipant("hutao", "胡桃", e, Persona("胡桃")),
                 new ChatRoomParticipant("lacrimosa", "安魂曲", f, Persona("安魂曲"))],
                new ScriptedDirector(["hutao", "END", "lacrimosa"]),
                new ChatRoomOptions { MinTurns = 2, MaxTurns = 5 });
            var guardedTurns = await guarded.RunAsync();
            // 断言的是行为而不是具体轮数：提前 END 被忽略（所以不止 1 轮），
            // 且兜底轮转没有让同一个角色连说两次。
            Check("chatroom_respects_min_turns",
                guardedTurns.Count >= 2 && guardedTurns[1].SpeakerId != guardedTurns[0].SpeakerId,
                $"turns={guardedTurns.Count}; 顺序={string.Join("→", guardedTurns.Select(t => t.SpeakerName))}");

            // ── 达到 MaxTurns 必须停，不能无限对谈 ──
            var g = new StubConversation("胡桃", "嗯嗯。");
            var h = new StubConversation("安魂曲", "嗯。");
            var capped = new ChatRoom(
                [new ChatRoomParticipant("hutao", "胡桃", g, Persona("胡桃")),
                 new ChatRoomParticipant("lacrimosa", "安魂曲", h, Persona("安魂曲"))],
                new RotatingDirector(),   // 永远不 END，用来验证 MaxTurns 兜底
                new ChatRoomOptions { MinTurns = 1, MaxTurns = 3 });
            var cappedTurns = await capped.RunAsync();
            Check("chatroom_respects_max_turns", cappedTurns.Count == 3, $"turns={cappedTurns.Count}");

            // ── 单轮长度截断：聊天室不该出现长篇大论 ──
            var verbose = new StubConversation("胡桃", new string('长', 500));
            var shortOne = new StubConversation("安魂曲", "嗯。");
            var clamped = new ChatRoom(
                [new ChatRoomParticipant("hutao", "胡桃", verbose, Persona("胡桃")),
                 new ChatRoomParticipant("lacrimosa", "安魂曲", shortOne, Persona("安魂曲"))],
                new ScriptedDirector(["hutao"]),
                new ChatRoomOptions { MinTurns = 1, MaxTurnCharacters = 120 });
            var one = await clamped.StepAsync();
            Check("chatroom_clamps_turn_length",
                one is not null && one.Text.Length == 120,
                $"len={one?.Text.Length}");

            // ── 至少两位角色，否则拒绝构造 ──
            var solo = new StubConversation("胡桃", "自言自语。");
            var threw = false;
            try
            {
                _ = new ChatRoom(
                    [new ChatRoomParticipant("hutao", "胡桃", solo, Persona("胡桃"))],
                    new ScriptedDirector(["hutao"]));
            }
            catch (ArgumentException) { threw = true; }
            Check("chatroom_requires_two_participants", threw, "单人应拒绝构造");

            // ── 导演不可用时按顺序轮转，且不连说两次 ──
            var fallbackDirector = new LlmChatRoomDirector(new MockLlmProvider(Persona("胡桃")));
            var participants = new List<ChatRoomParticipant>
            {
                new("hutao", "胡桃", new StubConversation("胡桃", "一。"), Persona("胡桃")),
                new("lacrimosa", "安魂曲", new StubConversation("安魂曲", "二。"), Persona("安魂曲")),
            };
            var transcript = new List<ChatRoomTurn>
            {
                new("hutao", "胡桃", "上一句是我说的。", false, DateTimeOffset.Now),
            };
            // MockLlmProvider 不是真导演，会返回不可解析内容 → 走兜底轮转
            var decision = await fallbackDirector.DecideAsync(participants, transcript, null);
            Check("chatroom_director_fallback_avoids_repeat",
                decision.NextSpeakerId == "lacrimosa",
                $"next={decision.NextSpeakerId}; reason={decision.Reason}");

            await CheckSpeakerFailureIsSurvivable(Check);
        }
        catch (Exception ex)
        {
            checks.Add(new CheckResult("chatroom_unexpected", false, ex.GetType().Name + ": " + ex.Message));
        }

        return checks;
    }

    /// <summary>
    /// 一位角色的模型暂时不可用时，聊天室必须**换下一位继续**，而不是让异常冒泡把整个进程带走。
    ///
    /// 这条是实测逼出来的：跨作品演示跑到第三轮时 DeepSeek 连接被对端重置
    /// （`SocketException 10054`），`HttpRequestException` 一路穿过 ChatRoom 冒到 Main，
    /// 整个进程带着堆栈崩掉。长会话里网络抖动是常态，不该被当成致命错误。
    ///
    /// 同时守住反面：**非「模型不可用」的异常仍然要冒泡**——
    /// 把代码 bug 静默成「她今天不想说话」会让真正的问题永远查不出来。
    /// </summary>
    private static async Task CheckSpeakerFailureIsSurvivable(Action<string, bool, string> Check)
    {
        // 胡桃的模型挂了，安魂曲正常 → 这一轮应当由安魂曲补上。
        var failing = new ThrowingConversation(new LlmUnavailableException("模拟连接被重置"));
        var healthy = new StubConversation("安魂曲", "那我说吧。");
        var reported = new List<string>();
        var room = new ChatRoom(
            [
                new ChatRoomParticipant("hutao", "胡桃", failing, Persona("胡桃")),
                new ChatRoomParticipant("lacrimosa", "安魂曲", healthy, Persona("安魂曲")),
            ],
            // 导演每一轮都点名胡桃：全靠 ChatRoom 自己换人，才测得到降级路径。
            new ScriptedDirector(["hutao", "hutao", "hutao", "hutao"]),
            onSpeakerFailed: (name, _) => reported.Add(name));

        var turn = await room.StepAsync();
        Check("chatroom_survives_speaker_model_failure",
            turn is not null && turn.SpeakerId == "lacrimosa",
            $"speaker={turn?.SpeakerId ?? "(null)"}");
        Check("chatroom_reports_speaker_failure",
            reported.Contains("胡桃"), $"reported=[{string.Join(',', reported)}]");

        // 代码 bug 不能被当成「不想说话」吞掉。
        var buggy = new ThrowingConversation(new InvalidOperationException("模拟代码 bug"));
        var buggyRoom = new ChatRoom(
            [
                new ChatRoomParticipant("hutao", "胡桃", buggy, Persona("胡桃")),
                new ChatRoomParticipant("lacrimosa", "安魂曲", healthy, Persona("安魂曲")),
            ],
            new ScriptedDirector(["hutao"]));
        var bubbled = false;
        try { await buggyRoom.StepAsync(); }
        catch (InvalidOperationException) { bubbled = true; }
        Check("chatroom_does_not_swallow_real_bugs", bubbled, "非模型不可用的异常应当继续冒泡");
    }

    private static PersonaProfile Persona(string name) => new()
    {
        Root = Path.GetTempPath(), Name = name, SystemPrompt = $"你是{name}",
        Catchphrases = new(), Lore = "", Quotes = [],
    };

    /// <summary>
    /// 记录最后一次收到的输入，并回固定台词。
    /// 构造参数里的角色名只是为了让调用点读起来清楚（断言用不到它），
    /// 所以刻意不存字段，避免一个从没被读过的属性。
    /// </summary>
    private sealed class StubConversation(string name, string reply) : IAgentConversation
    {
        public string Name { get; } = name;
        public string LastInput { get; private set; } = "";
        public IReadOnlyList<ChatMessage> History { get; } = [];

        public Task<AgentTurnResult> RespondAsync(string userInput, CancellationToken ct = default)
        {
            LastInput = userInput;
            return Task.FromResult(new AgentTurnResult("", "", reply, null, ""));
        }

        public Task<AgentTurnResult> ProactiveAsync(CancellationToken ct = default)
            => Task.FromResult(new AgentTurnResult("", "", reply, null, ""));

        public Task<TextResult> GenerateTextAsync(string? userInput, bool isProactive, CancellationToken ct = default)
            => Task.FromResult(new TextResult("", "", reply, [new SpeechSegment(reply, "neutral", 0.5)]));

        public Task<IReadOnlyList<string>> GenerateSegmentsAsync(string? userInput, bool isProactive, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([reply]);

        public Task<IReadOnlyList<SpeechSegment>> GenerateSpeechSegmentsAsync(string? userInput, bool isProactive, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SpeechSegment>>([new SpeechSegment(reply, "neutral", 0.5)]);

        public Task<TtsResult?> SynthesizeAsync(string text, CancellationToken ct)
            => Task.FromResult<TtsResult?>(null);

        public Task<TtsResult?> SynthesizeAsync(string text, string emotion = "neutral", double intensity = 0.5, CancellationToken ct = default)
            => Task.FromResult<TtsResult?>(null);

        public void RestoreHistory(IEnumerable<ChatMessage> history) { }
    }

    /// <summary>发言必定抛指定异常——用来验证「模型不可用」的降级与「代码 bug」的不降级。</summary>
    private sealed class ThrowingConversation(Exception error) : IAgentConversation
    {
        public IReadOnlyList<ChatMessage> History { get; } = [];

        public void RestoreHistory(IEnumerable<ChatMessage> history) { }

        public Task<AgentTurnResult> RespondAsync(string userInput, CancellationToken ct = default)
            => Task.FromException<AgentTurnResult>(error);

        public Task<AgentTurnResult> ProactiveAsync(CancellationToken ct = default)
            => Task.FromException<AgentTurnResult>(error);

        public Task<TextResult> GenerateTextAsync(string? userInput, bool isProactive, CancellationToken ct = default)
            => Task.FromException<TextResult>(error);

        public Task<IReadOnlyList<string>> GenerateSegmentsAsync(string? userInput, bool isProactive, CancellationToken ct = default)
            => Task.FromException<IReadOnlyList<string>>(error);

        public Task<IReadOnlyList<SpeechSegment>> GenerateSpeechSegmentsAsync(string? userInput, bool isProactive, CancellationToken ct = default)
            => Task.FromException<IReadOnlyList<SpeechSegment>>(error);

        public Task<TtsResult?> SynthesizeAsync(string text, CancellationToken ct)
            => Task.FromResult<TtsResult?>(null);

        public Task<TtsResult?> SynthesizeAsync(string text, string emotion = "neutral", double intensity = 0.5, CancellationToken ct = default)
            => Task.FromResult<TtsResult?>(null);
    }

    /// <summary>永不收场，纯轮转——用来验证 MaxTurns 是真正的硬上限。</summary>
    private sealed class RotatingDirector : IChatRoomDirector
    {
        private int _index;
        public Task<DirectorDecision> DecideAsync(
            IReadOnlyList<ChatRoomParticipant> participants,
            IReadOnlyList<ChatRoomTurn> transcript,
            string? userInput,
            CancellationToken ct = default)
        {
            var next = participants[_index++ % participants.Count].Id;
            return Task.FromResult(new DirectorDecision(next, "轮转", false));
        }
    }

    /// <summary>按脚本点名；脚本用尽或遇到 END 就收场。</summary>
    private sealed class ScriptedDirector(string[] script) : IChatRoomDirector
    {
        private int _index;

        public Task<DirectorDecision> DecideAsync(
            IReadOnlyList<ChatRoomParticipant> participants,
            IReadOnlyList<ChatRoomTurn> transcript,
            string? userInput,
            CancellationToken ct = default)
        {
            var next = _index < script.Length ? script[_index++] : "END";
            return Task.FromResult(next == "END"
                ? DirectorDecision.End
                : new DirectorDecision(next, "脚本点名", false));
        }
    }
}
