using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.Immersion;
using HuTao.Knowledge.Memory;
using HuTao.Persona;
using HuTao.Dialogue.Storage;
using HuTao.Voice;
using HuTao.Knowledge.Rag;
using HuTao.Dialogue.Tools;
using System.Diagnostics;
using HuTao.Foundation.Diagnostics;

namespace HuTao.Dialogue.Core;

/// <summary>
/// ReAct 对话用例编排器。工具观察、提示词、回复解析、沉浸审查和语音合成都通过独立组件完成，
/// 本类只维护对话历史及 Reason → Observe → Think → Act 的调用顺序。
/// </summary>
public sealed class ReactAgent : IAgentConversation
{
    private readonly PersonaProfile _persona;
    private readonly ILLMProvider _llm;
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly ImportantMemoryStore? _importantMemory;
    private readonly OriginalVoiceCatalog? _originalVoices;
    private readonly OriginalVoiceRetriever? _voiceRetriever;
    private readonly IToolObservationCollector _observationCollector;
    private readonly IAgentPromptBuilder _promptBuilder;
    private readonly ISpeechSegmentParser _segmentParser;
    private readonly ICharacterSpeechSynthesizer _speechSynthesizer;
    private readonly IAgentEventSink _eventSink;
    private readonly ImmersionGate? _immersion;
    private readonly ImmersionOptions _immersionOptions;
    private readonly ConversationMemoryStore? _memoryStore;
    private readonly MemoryRetriever? _memoryRetriever;
    private readonly List<ChatMessage> _history = [];
    private readonly StoryKnowledgeTool? _storyTool;
    private readonly ReactRetrievalLoop? _reactRetrieval;
    private readonly StoryAnswerComposer _storyComposer = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly HuTao.Foundation.Diagnostics.LocalDiagnosticLog _diagnostics;

    public ReactAgent(
        PersonaProfile persona,
        ILLMProvider llm,
        ITtsEngine? tts,
        IEnumerable<IAgentTool> tools,
        string? refAudio = null,
        string? refText = null,
        ImportantMemoryStore? importantMemory = null,
        EmotionReferenceCatalog? emotionReferences = null,
        OriginalVoiceCatalog? originalVoices = null,
        IToolObservationCollector? observationCollector = null,
        IAgentPromptBuilder? promptBuilder = null,
        ISpeechSegmentParser? segmentParser = null,
        ICharacterSpeechSynthesizer? speechSynthesizer = null,
        IAgentEventSink? eventSink = null,
        HuTao.Foundation.Diagnostics.LocalDiagnosticLog? diagnostics = null,
        ImmersionOptions? immersionOptions = null,
        ConversationMemoryStore? memory = null)
    {
        _persona = persona;
        _llm = llm;
        _tools = tools.ToDictionary(tool => tool.Name);
        _storyTool = _tools.Values.OfType<StoryKnowledgeTool>().SingleOrDefault();
        _reactRetrieval = _storyTool is null
            ? null
            : new ReactRetrievalLoop(_storyTool, _llm, lexicon: persona.EffectiveLexicon);
        _importantMemory = importantMemory;
        _originalVoices = originalVoices;
        _voiceRetriever = originalVoices is null ? null : new OriginalVoiceRetriever(originalVoices);
        _observationCollector = observationCollector ?? new ToolObservationCollector();
        _promptBuilder = promptBuilder ?? new AgentPromptBuilder();
        _segmentParser = segmentParser ?? new SpeechSegmentParser(originalVoices);
        _speechSynthesizer = speechSynthesizer ?? new CharacterSpeechSynthesizer(
            tts,
            emotionReferences,
            refAudio,
            refText);
        _eventSink = eventSink ?? NullAgentEventSink.Instance;
        _diagnostics = diagnostics ?? HuTao.Foundation.Diagnostics.LocalDiagnosticLog.Default;
        var options = immersionOptions ?? ImmersionOptions.FromEnvironment();
        _immersionOptions = options;
        _immersion = options.EnableRuleGate || options.EnableCritic
            ? new ImmersionGate(_segmentParser, _llm, options, _diagnostics)
            : null;
        _memoryStore = memory;
        _memoryRetriever = memory is null ? null : new MemoryRetriever(memory);
    }

    public IReadOnlyList<ChatMessage> History => _history;

    public Task<AgentTurnResult> RespondAsync(
        string userInput,
        CancellationToken ct = default)
        => RunFullTurnAsync(userInput, isProactive: false, ct);

    public Task<AgentTurnResult> ProactiveAsync(CancellationToken ct = default)
        => RunFullTurnAsync(userInput: null, isProactive: true, ct);

    public Task<TextResult> GenerateTextAsync(
        string? userInput,
        bool isProactive,
        CancellationToken ct = default)
        => GenerateTextCoreAsync(userInput, isProactive, ct);

    public Task<TtsResult?> SynthesizeAsync(string text, CancellationToken ct)
        => SynthesizeAsync(text, "neutral", 0.5, ct);

    public Task<TtsResult?> SynthesizeAsync(
        string text,
        string emotion = "neutral",
        double intensity = 0.5,
        CancellationToken ct = default)
        => _speechSynthesizer.SynthesizeAsync(text, emotion, intensity, ct);

    public async Task<IReadOnlyList<string>> GenerateSegmentsAsync(
        string? userInput,
        bool isProactive,
        CancellationToken ct = default)
    {
        var result = await GenerateTextCoreAsync(userInput, isProactive, ct)
            .ConfigureAwait(false);
        return result.Segments.Select(segment => segment.Text).ToList();
    }

    public async Task<IReadOnlyList<SpeechSegment>> GenerateSpeechSegmentsAsync(
        string? userInput,
        bool isProactive,
        CancellationToken ct = default)
    {
        var result = await GenerateTextCoreAsync(userInput, isProactive, ct)
            .ConfigureAwait(false);
        return result.Segments;
    }

    public void RestoreHistory(IEnumerable<ChatMessage> history)
    {
        _history.Clear();
        _history.AddRange(history);
    }

    private async Task<AgentTurnResult> RunFullTurnAsync(
        string? userInput,
        bool isProactive,
        CancellationToken ct)
    {
        var text = await GenerateTextCoreAsync(userInput, isProactive, ct)
            .ConfigureAwait(false);
        var spoken = text.Segments
            .Where(segment => !SpeechText.IsAction(segment.Text))
            .ToList();

        TtsResult? audio = null;
        var action = "已生成台词";
        if (spoken.Count == 1 && spoken[0].OriginalAudioPath is { } originalPath)
        {
            audio = new TtsResult(originalPath, 0, 0);
            action = $"已直接采用角色原声 {spoken[0].OriginalVoiceId}";
        }
        else if (_speechSynthesizer.IsAvailable)
        {
            try
            {
                if (spoken.Count == 0)
                {
                    action = "仅包含动作气泡，已跳过语音合成";
                }
                else
                {
                    var first = spoken[0];
                    audio = await _speechSynthesizer.SynthesizeAsync(
                        string.Join('\n', spoken.Select(segment => segment.Text)),
                        first.Emotion,
                        first.Intensity,
                        ct).ConfigureAwait(false);
                    action = audio is null
                        ? "语音引擎未返回音频"
                        : $"已合成语音 {audio.AudioPath}";
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                HuTao.Foundation.Diagnostics.LocalDiagnosticLog.Default.Write("speech.full_turn", ex);
                action = "已生成文字";
            }
        }

        await PublishAsync(new AgentStageEvent(
            AgentStage.Act,
            action,
            DateTimeOffset.Now), ct).ConfigureAwait(false);
        if (audio is not null)
        {
            await PublishAsync(new AgentAudioEvent(
                audio.AudioPath,
                spoken.Count == 1 ? spoken[0].OriginalVoiceId : null,
                DateTimeOffset.Now), ct).ConfigureAwait(false);
        }

        return new AgentTurnResult(
            text.Reason,
            text.Observation,
            text.Reply,
            audio,
            action) { Story=text.Story, StoryAnswer=text.StoryAnswer, Immersion=text.Immersion, ImmersionPath=text.ImmersionPath, Retrieval=text.Retrieval };
    }

    private async Task<TextResult> GenerateTextCoreAsync(
        string? userInput,
        bool isProactive,
        CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        var turnId = Guid.NewGuid().ToString("N");
        var trigger = isProactive ? "proactive" : "user";
        _diagnostics.Turn(turnId, trigger, "start", "pending");
        try
        {
            var result = await GenerateTextTurnAsync(userInput, isProactive, turnId, ct).ConfigureAwait(false);
            _diagnostics.Turn(turnId, trigger, "complete",
                result.StoryAnswer?.Path ?? (isProactive ? "proactive" : "conversation"),
                result.Story?.Status.ToString(), result.Story?.Evidence.Count ?? 0,
                result.Segments.Count, result.StoryAnswer?.Issues);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { _diagnostics.Turn(turnId, trigger, "cancelled", "cancelled"); throw; }
        catch (Exception ex)
        { _diagnostics.Turn(turnId, trigger, "failed", ex.GetType().Name); throw; }
        finally { _turnGate.Release(); }
    }

    private async Task<TextResult> GenerateTextTurnAsync(string? userInput, bool isProactive, string turnId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var reason = isProactive
            ? "主动搭话：心跳到点，且用户不忙、冷却已过"
            : $"回应用户输入：{userInput}";
        await PublishAsync(new AgentStageEvent(
            AgentStage.Reason,
            reason,
            DateTimeOffset.Now), ct).ConfigureAwait(false);

        var ordinaryTools = _tools.Where(pair => pair.Value is not StoryKnowledgeTool)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var toolObservation = await _observationCollector
            .CollectAsync(ordinaryTools, userInput, ct)
            .ConfigureAwait(false);

        var memory = _importantMemory?.PreparePrompt(
            userInput,
            isProactive,
            DateTimeOffset.Now) ?? "";

        var now = DateTimeOffset.Now;
        // 原声召回要看的是「本轮用户消息进来之前」的视角，所以先留一份快照，
        // 否则这一轮的用户消息会被重复算进上下文。
        var historyBeforeTurn = _history.ToArray();
        if (userInput is not null)
        {
            // 长期记忆召回先落一条用户轮次，再用「用户这句话 + 本轮剧情场景 + 近期对话」
            // 去检索更早的往事。阶段 1 走纯 BM25，不增加任何 LLM 调用。
            _memoryStore?.ObserveTurn("user", userInput, now);
            _history.Add(new ChatMessage("user", userInput));
        }

        StoryRagResult? story = null;
        StoryAnswerResult? storyAnswer = null;
        // 最后一轮的检索过程与沉浸判定结论随结果回传：在线评测要能看见「多级多次查询」
        // 与「闸门判过什么」，而不是只能看到一个被替换过的最终台词。
        ReactRetrievalResult? lastRetrieval = null;
        IReadOnlyList<SpeechSegment> segments = [new SpeechSegment("……", "neutral", 0.3)];
        ImmersionVerdict? verdict = null;
        string? immersionPath = null;
        // 观察文本按轮次重建；保留最后一次尝试的版本给 TextResult。
        var observation = toolObservation;

        // 面向人阅读的后端追踪日志（agent + RAG 的完整运行叙事）。
        // 在这里开、在回合结束时一次性落盘：RAG 的每次调用会**自动并入这个回合**（见 BackendTrace），
        // 顺序即真实发生顺序，不需要事后从结果反推。
        var trace = BackendTrace.Default.BeginTurn(
            $"回合 {turnId[..8]}",
            $"触发 {(isProactive ? "proactive" : "user")} · 角色 {_persona.Name}");
        var stageClock = Stopwatch.StartNew();
        double composeMs = 0, gateMs = 0, planMs = 0;
        try
        {
        trace?.Section("输入");
        trace?.Key("用户", userInput ?? "（主动搭话，无用户输入）");
        trace?.Key("历史", $"{historyBeforeTurn.Length} 条");
        trace?.Key("记忆", memory.Length == 0 ? "无" : $"{memory.Length} 字");

        // observe 阶段：检索 → 起草 → 沉浸判定。判定不过就**换一个新的检索提示词重新走一遍 RAG**，
        // 再重新起草——出戏往往源于证据本身不对（检索到了系统/元信息口径的材料），
        // 只修最终那句话的字面治不了根。轮次有上限，用尽后退回闸门给的兜底台词。
        for (var attempt = 0; ; attempt++)
        {
            // 主动搭话不做剧情检索：没有用户提问就没有可校验的问题，检索结果只会变成自我加戏。
            // 剧情断言必须由用户明确提问，走同一条检索 / 校验链。
            var retrievalQuery = attempt == 0 ? userInput : ReviseRetrievalQuery(userInput, verdict);
            if (attempt > 0)
            {
                trace?.Section($"沉浸判定没过 → 换检索词重来（第 {attempt} 次重试）");
                trace?.Key("新查询", retrievalQuery ?? "");
            }
            var planWatch = Stopwatch.StartNew();
            var reactRetrieval = !isProactive && _storyTool is not null && retrievalQuery is not null
                ? await _reactRetrieval!.RunAsync(retrievalQuery, _history.TakeLast(8).ToArray(), ct).ConfigureAwait(false)
                : null;
            planWatch.Stop();
            planMs += planWatch.Elapsed.TotalMilliseconds;
            story = reactRetrieval?.EvidencePool;
            lastRetrieval = reactRetrieval;
            observation = toolObservation;
            if (reactRetrieval is not null)
                observation += $"\n{reactRetrieval.Observation}";
            if (story is not null && story.Status != StoryStatus.Bypass)
                observation += $"\n- story_archive: {story.Status}; {story.Trace.RetrievalMode}; {story.Evidence.Count} evidence; {story.Trace.ElapsedMs:F0}ms";
            if (attempt == 0)
                await PublishAsync(new AgentStageEvent(
                    AgentStage.Observe,
                    observation,
                    DateTimeOffset.Now), ct).ConfigureAwait(false);

            var longTermMemory = "";
            if (_memoryRetriever is not null)
            {
                // 当前窗口里已经有的内容不必重复注入，否则同一句话会在提示词里出现两遍。
                var exclude = _history
                    .Select(message => ConversationMemoryStore.Fingerprint(message.Role, message.Content))
                    .ToHashSet(StringComparer.Ordinal);
                var hits = _memoryRetriever.Retrieve(new MemoryQuery(
                    userInput,
                    CharacterEvidenceTexts(story),
                    now,
                    exclude,
                    MaxResults: 4));
                longTermMemory = _memoryRetriever.BuildPromptSection(hits, now);
            }

            // 原声召回用「整轮上下文」而不是「用户这一句话」：
            // 用户问「你和钟离什么关系」时，字面上跟任何一句台词都不重合，但本轮检索到的
            // 角色台词、近期对话合起来就能定位到本人录过的相关原声。
            var voiceCandidates = _voiceRetriever?.Retrieve(new OriginalVoiceQuery(
                userInput,
                CharacterEvidenceTexts(story),
                historyBeforeTurn.TakeLast(4).Select(message => message.Content).ToList(),
                // 用户在表达现实难过时不推台词库里的俏皮话，只保留本轮剧情逐字命中的原声。
                AllowLibrary: story?.Status != StoryStatus.Comfort)) ?? [];

            var prompt = _promptBuilder.Build(new AgentPromptContext(
                _persona,
                observation,
                memory,
                voiceCandidates,
                _tools.ContainsKey("document_reader"),
                IsProactive: isProactive,
                LongTermMemory: longTermMemory));

            storyAnswer = null;
            string rawReply;
            var composeWatch = Stopwatch.StartNew();
            if (story is not null && story.Status is not (StoryStatus.Bypass or StoryStatus.Playful or StoryStatus.Comfort))
            {
                storyAnswer = await _storyComposer
                    .ComposeAsync(_llm, prompt, _history, story, voiceCandidates, ct)
                    .ConfigureAwait(false);
                rawReply = storyAnswer.Reply;
            }
            else
            {
                var tone = story?.Status switch {
                    StoryStatus.Playful => "\n本轮是玩梗/假设。可以俏皮接话，明确这是想象，不声称官方发生过。",
                    StoryStatus.Comfort => "\n用户在表达现实的难过。温柔认真陪伴，不推销丧葬业务，不拿逝者开玩笑，不查剧情抢走话题。",
                    _ => "" };
                rawReply = await _llm.CompleteAsync(prompt + tone, _history, ct).ConfigureAwait(false);
            }
            composeWatch.Stop();
            composeMs += composeWatch.Elapsed.TotalMilliseconds;

            segments = _segmentParser.Parse(rawReply);
            if (segments.Count == 0)
                segments = [new SpeechSegment("……", "neutral", 0.3)];

            if (_immersion is null) break;

            // 沉浸判定：聊天窗口里绝不允许出现任何出戏内容。
            // 审查用的历史就是演员本轮看到的那段（含刚加入的用户消息），
            // 评审员在临时历史上工作，审查过程不会写回 _history。
            var gateWatch = Stopwatch.StartNew();
            var outcome = await _immersion.ReviewAsync(new ImmersionRequest(
                _persona.Name,
                prompt,
                rawReply,
                segments,
                _history.ToArray(),
                userInput,
                isProactive,
                SafeFallback(),
                longTermMemory), ct).ConfigureAwait(false);
            gateWatch.Stop();
            gateMs += gateWatch.Elapsed.TotalMilliseconds;
            segments = outcome.Segments;
            verdict = outcome.Verdict;
            immersionPath = attempt == 0 ? outcome.Path : $"{outcome.Path}/observe-retry-{attempt}";
            // 只记违规类型，不记任何文本片段——日志不得含模型正文或用户输入。
            _diagnostics.Turn(Guid.NewGuid().ToString("N"), isProactive ? "proactive" : "user",
                "immersion", attempt == 0 ? outcome.Path : $"{outcome.Path}/observe-retry-{attempt}",
                outcome.Verdict.KindsSummary, segmentCount: segments.Count,
                issues: outcome.Verdict.Violations.Select(v => v.Kind.ToString()).Distinct().ToArray());

            if (outcome.Verdict.Passed) break;
            if (attempt >= _immersionOptions.MaxObserveRetries) break;
        }

        var reply = string.Join('\n', segments.Select(segment => segment.Text));

        // ── 追踪日志的收尾段：证据池 / 作答 / 闸门 / 最终答复 / 耗时 ──
        // 这些是「agent 侧的后端结果」；RAG 侧的每一次调用已经在上面按发生顺序并进来了。
        if (trace is not null)
        {
            trace.Section("证据池（多轮检索合并后）");
            if (story is null)
                trace.Note("本轮没有走剧情检索（非剧情回合或未挂剧情工具）。");
            else
            {
                trace.Key("状态", story.Status.ToString());
                trace.Key("证据", $"{story.Evidence.Count} 条 · 检索模式 {story.Trace.RetrievalMode} · 候选 {story.Trace.Candidates}");
                trace.Key("多轮", lastRetrieval is null
                    ? "无"
                    : $"{lastRetrieval.Steps.Count} 步 / {lastRetrieval.Steps.Select(s => s.Query).Distinct().Count()} 条不同查询 / 补查 {lastRetrieval.Steps.Count(s => s.Query.Contains("补查", StringComparison.Ordinal))} 次");
                foreach (var warning in story.Trace.Warnings.Take(6))
                    trace.Note("⚠ " + warning);
            }

            trace.Section("作答（知识库契约）");
            if (storyAnswer is null)
                trace.Note("本轮不走剧情回答协议（旁路/玩梗/安慰），直接由演员生成。");
            else
            {
                // `Fallback` 返回的 `Validated` 是 true（兜底句本身是安全的），
                // 所以只印它会出现「路径=validation-fallback 且 校验=通过」这种自相矛盾的两行。
                // 日志里按**读的人关心的事**说：这一轮到底有没有按契约作答。
                var degraded = storyAnswer.Path.Contains("fallback", StringComparison.Ordinal);
                trace.Key("路径", storyAnswer.Path);
                trace.Key("校验", degraded ? "未通过 → 已降级为兜底句"
                    : storyAnswer.Validated ? "通过" : "未通过");
                if (storyAnswer.Issues.Count > 0)
                    // 同一个问题会在多段上各报一次；按名去重计数，否则一行里能出现六遍 unsupported_number。
                    trace.Key("问题", string.Join("、", storyAnswer.Issues
                        .GroupBy(issue => issue, StringComparer.Ordinal)
                        .Select(g => g.Count() > 1 ? $"{g.Key}×{g.Count()}" : g.Key)));
                trace.Key("草稿", storyAnswer.Reply);
            }

            trace.Section("沉浸闸门");
            if (verdict is null)
                trace.Note("闸门未接线（关闭或未启用）。");
            else
            {
                trace.Key("路径", immersionPath ?? "-");
                trace.Key("通过", verdict.Passed ? "是" : "否");
                trace.Key("违规", verdict.KindsSummary);
                trace.Key("连贯", verdict.Coherence.ToString("F2"));
                trace.Key("来源", verdict.Source);
            }

            trace.Section("最终答复（用户可见）");
            trace.Line(reply);
        }

        _history.Add(new ChatMessage("assistant", reply));
        // 角色说过的话同样进记忆库：她自己的承诺与说法也是长程一致性的依据。
        _memoryStore?.ObserveTurn("assistant", reply, now);
        await PublishAsync(new AgentStageEvent(
            AgentStage.Think,
            reply,
            DateTimeOffset.Now), ct).ConfigureAwait(false);
        return new TextResult(reason, observation, reply, segments)
            { Story = story, StoryAnswer = storyAnswer, Immersion = verdict, ImmersionPath = immersionPath, Retrieval = lastRetrieval };
        }
        finally
        {
            // **必须在 finally 里收尾**：回合中途抛异常时，前面已经发生的检索仍然要留下记录——
            // 出问题时最想看的恰恰是「走到哪一步炸的」，半途丢弃等于把最有用的那次运行丢掉。
            stageClock.Stop();
            var total = stageClock.Elapsed.TotalMilliseconds;
            if (trace is not null)
            {
                trace.Section("耗时");
                trace.Key("总", $"{total:F0}ms");
                trace.Key("编排", $"{planMs:F0}ms（规划 + 多轮检索）");
                trace.Key("起草", $"{composeMs:F0}ms");
                trace.Key("闸门", $"{gateMs:F0}ms");
                trace.Key("其它", $"{Math.Max(0, total - planMs - composeMs - gateMs):F0}ms");
                trace.End($"回合结束 · 总耗时 {total:F0}ms · 检索 {planMs:F0}ms · 起草 {composeMs:F0}ms · 闸门 {gateMs:F0}ms");
            }
        }
    }

    /// <summary>
    /// 只取本轮证据里**本角色本人**说过的台词。
    /// 必须按说话人过滤：剧情语料里「什么？！」这类短句被多个角色共用，
    /// 不过滤就会把别人的台词错配成本角色的原声。
    /// </summary>
    private IReadOnlyList<string> CharacterEvidenceTexts(StoryRagResult? story)
    {
        if (story is null || story.Evidence.Count == 0)
            return [];

        var texts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in story.Evidence)
        {
            foreach (var line in evidence.Context.Prepend(evidence.Anchor))
            {
                if (!line.Speaker.Contains(_persona.Name, StringComparison.Ordinal))
                    continue;
                if (string.IsNullOrWhiteSpace(line.Text) || !seen.Add(line.Text))
                    continue;
                texts.Add(line.Text);
            }
        }
        return texts;
    }

    /// <summary>
    /// 沉浸判定没过时，给 RAG 换一个新的检索提示词。
    ///
    /// 为什么不直接重检索同一个问题：同一句话检索回来的就是同一批证据，重新起草只会得到
    /// 同一类出戏。所以按**违规类型**给检索加一条明确的取材约束，让下一轮去取
    /// 「角色本人的、故事世界内的」材料。
    ///
    /// 这里只影响「怎么去找证据」，不参与任何通过/不通过的判定——
    /// 判定始终只由 <see cref="ImmersionGate"/> 做，这里不是给某类问题开后门。
    /// </summary>
    private static string? ReviseRetrievalQuery(string? input, ImmersionVerdict? verdict)
    {
        if (string.IsNullOrWhiteSpace(input) || verdict is null || verdict.Violations.Count == 0)
            return input;
        var kinds = verdict.Violations.Select(v => v.Kind).ToHashSet();
        var hints = new List<string>();
        // 出戏常见于「没有可用的世界内材料」，模型只好拿系统/元信息口径来凑。
        if (kinds.Overlaps([
                ImmersionViolationKind.AiSelfReference, ImmersionViolationKind.AssistantBoilerplate,
                ImmersionViolationKind.SystemLeak, ImmersionViolationKind.MetaReference,
                ImmersionViolationKind.RefusalTone]))
            hints.Add("角色本人的经历与原话");
        if (kinds.Contains(ImmersionViolationKind.Coherence)) hints.Add("与上文衔接的后续发展");
        if (kinds.Contains(ImmersionViolationKind.Repetition)) hints.Add("换一个还没提过的片段");
        if (kinds.Contains(ImmersionViolationKind.LanguageDrift)) hints.Add("中文原文台词");
        return hints.Count == 0 ? input : input + "；请只依据：" + string.Join('、', hints);
    }

    /// <summary>兜底台词也必须是角色本人会说的话——绝不能退回系统口吻。</summary>
    private string SafeFallback() => _persona.Name switch
    {
        "芙宁娜" => "唔…让我重新理一理。你方才说到哪儿了？",
        "可莉" => "唔……可莉刚刚走神啦，你再说一次好不好？",
        _ => "唔…让本堂主重新理一理。你刚才说到哪了？",
    };

    private async ValueTask PublishAsync(
        AgentEvent agentEvent,
        CancellationToken ct)
    {
        try
        {
            await _eventSink.PublishAsync(agentEvent, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 可观测通道是旁路，日志或 WebSocket 断开不能阻断对话。
        }
    }
}
