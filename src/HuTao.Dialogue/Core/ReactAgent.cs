using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.Immersion;
using HuTao.Knowledge.Memory;
using HuTao.Persona;
using HuTao.Dialogue.Storage;
using HuTao.Voice;
using HuTao.Knowledge.Rag;
using HuTao.Dialogue.Tools;
using System.Diagnostics;
using System.Text;
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
    private readonly ConversationRecall _recall;
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
        _observationCollector = observationCollector ?? new ToolObservationCollector(llm, diagnostics: diagnostics);
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
        var recallStrategies = new List<IConversationRecallStrategy>();
        if (_memoryRetriever is not null) recallStrategies.Add(new MemoryRecallStrategy(_memoryRetriever, llm));
        if (_reactRetrieval is not null) recallStrategies.Add(new StoryRecallStrategy(_reactRetrieval));
        _recall = new ConversationRecall(llm, persona.EffectiveLexicon, recallStrategies);
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
                    // 只送**可读部分**给引擎：括号里的动作/旁白去掉（整段是动作的已在上面滤掉，
                    // 但「（撑着下巴）客官今日来得早啊」这种夹在台词里的必须在这一层剥），
                    // 破折号换成会被 cut5 切句的标点，否则听起来完全没有停顿。
                    var speakable = spoken
                        .Select(segment => SpeechText.ForSpeech(segment.Text, SpeechText.DashPause))
                        .Where(line => line.Length > 0)
                        .ToArray();
                    if (speakable.Length == 0)
                    {
                        action = "仅包含动作与括号旁白，已跳过语音合成";
                    }
                    else
                    {
                    audio = await _speechSynthesizer.SynthesizeAsync(
                        string.Join('\n', speakable),
                        first.Emotion,
                        first.Intensity,
                        ct).ConfigureAwait(false);
                    action = audio is null
                        ? "语音引擎未返回音频"
                        : $"已合成语音 {audio.AudioPath}";
                    }
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
            action) { Story=text.Story, StoryAnswer=text.StoryAnswer, Immersion=text.Immersion, ImmersionPath=text.ImmersionPath, Retrieval=text.Retrieval, Recall=text.Recall, ToolRuns=text.ToolRuns };
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
            // Recall sees the previous conversation; persist this turn after answering so it cannot rank itself.
            _history.Add(new ChatMessage("user", userInput));
        }

        StoryRagResult? story = null;
        StoryAnswerResult? storyAnswer = null;
        // 最后一轮的检索过程与沉浸判定结论随结果回传：在线评测要能看见「多级多次查询」
        // 与「闸门判过什么」，而不是只能看到一个被替换过的最终台词。
        ReactRetrievalResult? lastRetrieval = null;
        ConversationRecallResult? recall = null;
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

        // ── 两层重试：先**生成级**（带证据、按闸门给的修改要求重做）→ 再**检索级**（换检索词重走 RAG）──
        //
        // 为什么必须分开：出戏的原因有两类，代价差一个数量级。
        //   · 表述问题（复读、语气、没接住问题）→ 同一份证据重做一次就够，一次 LLM 调用；
        //   · 证据本身不对（检索到系统/元信息口径的材料）→ 改措辞治不了根，必须重查，代价是一整套检索。
        // 旧实现把两者绑死：闸门内部先**无证据**地自由重写，重写完还不过才换检索词——
        // 于是最便宜的那条路反而绕开了证据与整套事实校验。
        //
        // 闸门在这里只出「修改要求」（ImmersionVerdict.Instructions），**重新生产由本方法负责**，
        // 因为只有它手里有本轮的证据。
        var constraints = new List<string>();
        var accepted = false;
        for (var retrievalAttempt = 0; !accepted; retrievalAttempt++)
        {
            // 主动搭话不做剧情检索：没有用户提问就没有可校验的问题，检索结果只会变成自我加戏。
            // 剧情断言必须由用户明确提问，走同一条检索 / 校验链。
            var retrievalQuery = retrievalAttempt == 0 ? userInput : ReviseRetrievalQuery(userInput, verdict);
            if (retrievalAttempt > 0)
            {
                trace?.Section($"检索级重试：换检索词重走 RAG（第 {retrievalAttempt} 次）");
                trace?.Key("新查询", retrievalQuery ?? "");
            }
            var planWatch = Stopwatch.StartNew();
            if (!isProactive && retrievalQuery is not null)
                recall = await _recall.RunAsync(retrievalQuery, historyBeforeTurn, now, ct).ConfigureAwait(false);
            var reactRetrieval = recall?.Story;
            planWatch.Stop();
            planMs += planWatch.Elapsed.TotalMilliseconds;
            story = reactRetrieval?.EvidencePool;
            lastRetrieval = reactRetrieval;
            observation = toolObservation;
            if (reactRetrieval is not null)
                observation += $"\n{reactRetrieval.Observation}";
            if (story is not null && story.Status != StoryStatus.Bypass)
                observation += $"\n- story_archive: {story.Status}; {story.Trace.RetrievalMode}; {story.Evidence.Count} evidence; {story.Trace.ElapsedMs:F0}ms";
            if (retrievalAttempt == 0)
                await PublishAsync(new AgentStageEvent(
                    AgentStage.Observe,
                    observation,
                    DateTimeOffset.Now), ct).ConfigureAwait(false);

            var longTermMemory = _memoryRetriever?.BuildPromptSection(recall?.Memories ?? [], now, 1800) ?? "";
            if (story is not null)
                story = story with { ConversationMemories = recall?.Memories ?? [] };

            // 原声召回用「整轮上下文」而不是「用户这一句话」：
            // 用户问「你和钟离什么关系」时，字面上跟任何一句台词都不重合，但本轮检索到的
            // 角色台词、近期对话合起来就能定位到本人录过的相关原声。
            var voiceCandidates = _voiceRetriever?.Retrieve(new OriginalVoiceQuery(
                userInput,
                CharacterEvidenceTexts(story),
                historyBeforeTurn.TakeLast(4).Select(message => message.Content).ToList(),
                // 用户在表达现实难过时不推台词库里的俏皮话，只保留本轮剧情逐字命中的原声。
                AllowLibrary: story?.Status != StoryStatus.Comfort)) ?? [];

            // 证据定稿后不再变——生成级重试复用它，只有检索级重试才会换掉它。
            var basePrompt = _promptBuilder.Build(new AgentPromptContext(
                _persona,
                observation,
                memory,
                voiceCandidates,
                _tools.ContainsKey("document_reader"),
                IsProactive: isProactive,
                LongTermMemory: longTermMemory,
                RecallIntent: recall?.Intent ?? ""));

            // 严格协议用尽后允许**松协议重做一次**：格式失败只该损失"可校验性"，
            // 不该损失整条回答——否则用户停在恒定兜底句上。
            var relaxed = false;
            for (var generationAttempt = 0; ; generationAttempt++)
            {
                // 松协议那一次要去掉「只返回 JSON / 绑定 evidence_ids」这类**格式类**要求：
                // 它们与"不要输出 JSON"直接矛盾，模型收到打架的指令只会给一句最保险的短回答（实测 17 字）。
                // 内容类要求（回应问题、不编造、口吻）必须保留。
                var constraint = BuildRetryConstraint(
                    relaxed ? RelaxedConstraints(constraints) : constraints, userInput, isProactive, relaxed);
                var prompt = constraint.Length == 0 ? basePrompt : basePrompt + "\n\n" + constraint;

                storyAnswer = null;
                string rawReply;
                var composeWatch = Stopwatch.StartNew();
                if (story is not null && story.Status is not (StoryStatus.Bypass or StoryStatus.Playful or StoryStatus.Comfort))
                {
                    // 约束作为独立参数传入：Composer 内部拼成「人设 + 证据 JSON + 修改要求」，
                    // 保证重做时**证据和修改要求同时在场**。松协议那一次仍然带证据，只是不要 JSON。
                    storyAnswer = relaxed
                        ? await _storyComposer
                            .ComposeRelaxedAsync(_llm, basePrompt, _history, story, voiceCandidates, constraint, ct)
                            .ConfigureAwait(false)
                        : await _storyComposer
                            .ComposeAsync(_llm, basePrompt, _history, story, voiceCandidates, constraint, ct)
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

                // ── 一级修复：**本地确定性修复**（零 LLM）──
                // 结构 / 引用 / 格式类的契约问题（占实测失败的大头）本可以就地改好，
                // 不该占用生成级重试预算，更不该把整个回合拖进"重生成 → 换检索词 → 兜底"。
                // 这里只做确定性变换，修完内部会重新校验；修不动才交给下面重生成。
                if (storyAnswer is { Path: "validation-fallback", RawReply.Length: > 0 } && story is not null)
                {
                    var allowedVoices = voiceCandidates.Select(c => c.Clip.Id)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var repaired = StoryAnswerComposer.TryLocalRepair(storyAnswer.RawReply, story, allowedVoices);
                    if (repaired is not null)
                    {
                        trace?.Note($"本地确定性修复成功（原问题：{string.Join("、", storyAnswer.Issues)}）→ 不重生成");
                        storyAnswer = repaired;
                        rawReply = repaired.Reply;
                    }
                    else
                    {
                        trace?.Note($"本地修复无解（{string.Join("、", storyAnswer.Issues)}）→ 交给重生成");
                    }
                }

                segments = _segmentParser.Parse(rawReply);
                if (segments.Count == 0)
                    segments = [new SpeechSegment("……", "neutral", 0.3)];

                // **每次生成尝试都留痕**：闸门判定**之前**的稿子 + 模型原始回包。
                // 只记最终答复会让"改了三次到底改了什么"完全不可见；
                // 而校验失败时 storyAnswer.Reply 已经是降级后的兜底话，
                // 不看原始回包就只能猜模型是不是压根没按协议回。
                trace?.Section($"生成尝试 {generationAttempt + 1}" + (relaxed ? "（松协议）" : ""));
                trace?.Key("草稿", storyAnswer?.Reply ?? rawReply);
                if (storyAnswer is not null)
                    trace?.Key("作答路径", storyAnswer.Issues.Count == 0
                        ? storyAnswer.Path
                        : $"{storyAnswer.Path} · 校验 {string.Join("、", storyAnswer.Issues)}");
                trace?.Key("原始回包", DescribeRawReply(storyAnswer?.RawReply));

                // Composer 自己降级过（模型挂了 / 契约校验没过）→ **这一版不算数**。
                // 为什么必须单独判：那句兜底话本身不出戏，闸门会放行，于是重试机制根本不会触发，
                // 用户就永远看到「唔，让我再理一理」——实测在线 100 例里有 62 例是这么来的。
                // 这里把 Composer 的校验问题也变成修改要求，交给同一份证据重做。
                var degraded = storyAnswer is not null &&
                               storyAnswer.Path.Contains("fallback", StringComparison.Ordinal);

                ImmersionOutcome? outcome = null;
                if (_immersion is not null && degraded)
                {
                    // 契约已经判定这一版不可用，闸门再去评**降级后的兜底句**没有任何信息量：
                    // 那句恒定台词当然接不住用户问题（实测评审员给 0.05 —— 判得没错，只是评错了对象），
                    // 还白花一次评审调用，并往下一轮约束里塞进与内容无关的要求。
                    trace?.Key("闸门", "未运行（Composer 已按契约降级，评那句兜底话没有意义）");
                }
                else if (_immersion is not null)
                {
                    // 沉浸判定：聊天窗口里绝不允许出现任何出戏内容。
                    // 审查用的历史就是演员本轮看到的那段（含刚加入的用户消息），
                    // 评审员在临时历史上工作，审查过程不会写回 _history。
                    var gateWatch = Stopwatch.StartNew();
                    outcome = await _immersion.ReviewAsync(new ImmersionRequest(
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
                    var path = generationAttempt == 0 ? outcome.Path : $"{outcome.Path}/gen-retry-{generationAttempt}";
                    immersionPath = retrievalAttempt == 0 ? path : $"{path}/observe-retry-{retrievalAttempt}";
                    // 只记违规类型，不记任何文本片段——日志不得含模型正文或用户输入。
                    _diagnostics.Turn(Guid.NewGuid().ToString("N"), isProactive ? "proactive" : "user",
                        "immersion", immersionPath,
                        outcome.Verdict.KindsSummary, segmentCount: segments.Count,
                        issues: outcome.Verdict.Violations.Select(v => v.Kind.ToString()).Distinct().ToArray());
                    trace?.Key("闸门", $"{immersionPath} · {outcome.Verdict.KindsSummary} · 连贯 {outcome.Verdict.Coherence:F2}");
                }

                var passed = outcome?.Verdict.Passed ?? true;
                if (passed && !degraded) { accepted = true; break; }

                var blocking = outcome is not null &&
                               outcome.Verdict.Violations.Any(v => ImmersionGate.IsBlocking(v.Kind));
                if (generationAttempt >= _immersionOptions.MaxGenerationAttempts)
                {
                    // 严格协议用尽。若这次失败是"模型没按格式回"（validation-fallback）而不是出戏，
                    // 再给一次**松协议**机会：仍然带同一份证据，只是不要 JSON。
                    // 实测这条路径上"因为格式丢掉整条回答"是兜底句的最大来源。
                    if (degraded && !relaxed && !blocking && storyAnswer?.Path == "validation-fallback")
                    {
                        relaxed = true;
                        trace?.Note("严格协议用尽 → 松协议重做（仍带证据，不要求 JSON）");
                        continue;
                    }

                    // 生成级用尽：只有硬违规才继续往检索级走；
                    // 软违规、或"闸门放行但 Composer 降级成了兜底句"，都在这里收下
                    // ——一句略显重复的角色台词好过固定兜底，而兜底句再糟也只是最后手段。
                    if (!blocking)
                    {
                        accepted = true;
                        trace?.Note(degraded
                            ? $"生成级重试用尽，仍是兜底句（{storyAnswer?.Path}）→ 放行"
                            : $"生成级重试用尽，仅剩软违规（{outcome?.Verdict.KindsSummary}）→ 放行");
                    }
                    break;
                }

                // 修改要求**累积**而不是替换：只带最新一条会让两类问题来回震荡（A→B→A→B）。
                if (outcome is not null) constraints.AddRange(outcome.Verdict.Instructions);
                constraints.AddRange(DegradedInstructions(storyAnswer));
                trace?.Note($"→ 不采纳，重做第 {generationAttempt + 1} 次"
                    + (degraded ? $"（契约问题：{string.Join("、", storyAnswer!.Issues)}）" : "")
                    + (outcome is null ? "" : $"（闸门：{outcome.Verdict.KindsSummary}）"));
            }

            if (accepted) break;
            if (retrievalAttempt >= _immersionOptions.MaxRetrievalRetries) break;
        }

        // 两层都用尽仍然只有硬违规 → 角色化兜底台词（绝不把出戏内容放给用户）。
        if (!accepted)
            segments = [new SpeechSegment(SafeFallback(), "neutral", 0.35)];

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
        if (userInput is not null) _memoryStore?.ObserveTurn("user", userInput, now);
        _memoryStore?.ObserveTurn("assistant", reply, now);
        await PublishAsync(new AgentStageEvent(
            AgentStage.Think,
            reply,
            DateTimeOffset.Now), ct).ConfigureAwait(false);
        return new TextResult(reason, observation, reply, segments)
            { Story = story, StoryAnswer = storyAnswer, Immersion = verdict, ImmersionPath = immersionPath, Retrieval = lastRetrieval, Recall = recall,
                ToolRuns = (_observationCollector as ToolObservationCollector)?.LastResults ?? [] };
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

    /// <summary>
    /// 把闸门给的修改要求拼成一段生成约束。
    ///
    /// 最后那条「只依据给出的资料」是**关键**：重写曾经是无证据的自由重写，模型会退回预训练记忆
    /// ——对知名 IP 往往"看着对"，所以错得很隐蔽。带证据重做时必须显式禁止它引用资料之外的事实。
    /// </summary>
    private static string BuildRetryConstraint(
        IReadOnlyList<string> instructions, string? userInput, bool isProactive, bool relaxed)
    {
        var builder = new StringBuilder();
        if (relaxed)
        {
            // 松协议那一次：格式类要求已被 RelaxedConstraints 剔除，
            // 但**内容类要求必须留下**——否则模型只会给一句最保险的短回答（实测 17 字）。
            builder.Append("【本轮格式要求】上一版没有按输出格式返回，本轮改用宽松输出，内容要求不变。\n");
        }
        else
        {
            if (instructions.Count == 0)
                return "";
            builder.Append("【本轮修改要求】你上一版台词没有通过审查，必须重做。\n必须遵守：\n");
            foreach (var instruction in instructions)
                builder.Append($"- {instruction}\n");
        }

        builder.Append(isProactive || string.IsNullOrWhiteSpace(userInput)
            ? "本轮是你主动搭话，没有用户提问；保持主动关心或闲聊的意图，但要与上文连贯、不要复读。\n"
            : $"必须真正回应用户刚说的这句话：「{userInput}」。\n");
        builder.Append("只依据上面给出的资料作答，资料里没有的不要补。\n");
        builder.Append("不要解释、不要道歉、不要提到修改、审查、规则或模型这些字眼。");
        return builder.ToString();
    }

    /// <summary>
    /// 把 Composer 的校验问题翻译成下一版必须遵守的要求——**每个问题一条，不做拼接**。
    ///
    /// 为什么必须一条一条：松协议重做时要按条剔除格式类要求，
    /// 而拼接成一整块会让"只返回 JSON"这一条把整块带走，连"必须真正回应用户"也一起丢掉。
    /// </summary>
    private static IReadOnlyList<string> DegradedInstructions(StoryAnswerResult? answer)
    {
        if (answer is null || answer.Issues.Count == 0)
            return [];

        return answer.Issues.Select(issue => issue switch
        {
            "invalid_json_contract" => "只返回规定的 JSON（answerability + segments），不要输出任何多余文字",
            "missing_uncertainty" => "证据不足以完全确认时，必须明确说出哪一部分不确定",
            "unknown_citation" or "missing_citation" => "每条 fact/quote 必须绑定资料里给出的 evidence_ids，不得引用未给出的 id",
            "quote_not_verbatim" => "引用原话必须逐字照抄资料中的原句，不得改写",
            "unsupported_number" => "不要写出资料原文里没有的数字",
            "false_personal_experience" => "没亲历过的事不要用第一人称回忆，改用听闻或档案口吻",
            "citation_leak" => "不要把证据 id 写进台词正文",
            "text_length" => "每段不超过 100 字（原话引用不超过 240 字）",
            "segment_count" => "只输出 1~3 段",
            "action_format" => "动作旁白用全角括号，且不夹带事实",
            "fact_in_unattributed_segment" => "thought/action 段里不要夹带经历、数量或人物关系",
            "answerability" or "unknown_kind" or "emotion" => "严格按回答协议里的取值作答",
            "missing_segments" or "segment_not_object" => "按协议用 segments 数组返回，每段是一个对象",
            _ => "严格按上面的回答协议作答",
        }).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 模型原始回包的结构特征 + 截断样本，供可读后端追踪使用。
    ///
    /// 为什么要看它：校验失败时 <c>StoryAnswerResult.Reply</c> 已被换成兜底句，
    /// 只有原始回包能回答"模型到底回了什么"——是散文、是另一种 JSON 形状，
    /// 还是被包在说明文字里。截断到 160 字符并把换行折成 ⏎，
    /// 避免一条日志把整个协议 JSON（内含证据原文）灌进来。
    /// </summary>
    private static string DescribeRawReply(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "（无：本轮未调用模型）";
        var flat = raw.Replace('\r', ' ').Replace('\n', '⏎').Trim();
        var head = flat.Length <= 160 ? flat : flat[..160] + "…";
        return $"len={raw.Length} · 含花括号={raw.Contains('{')} · {head}";
    }

    /// <summary>
    /// 松协议重做时剔掉**格式类**要求（JSON / evidence_ids），保留内容类要求。
    /// 混在一起会让 prompt 自相矛盾，模型只能回一句最保险的短话。
    /// </summary>
    private static List<string> RelaxedConstraints(List<string> constraints)
        => constraints
            .Where(c => !c.Contains("JSON", StringComparison.Ordinal)
                     && !c.Contains("evidence_ids", StringComparison.Ordinal))
            .ToList();

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
