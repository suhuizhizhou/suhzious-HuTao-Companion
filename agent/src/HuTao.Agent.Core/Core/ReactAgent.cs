using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Persona;
using HuTao.Agent.Core.Storage;
using HuTao.Agent.Core.Tts;
using HuTao.Agent.Core.Rag;
using HuTao.Agent.Core.Tools;

namespace HuTao.Agent.Core.Core;

/// <summary>
/// ReAct 对话用例编排器。工具观察、提示词、回复解析和语音合成都通过独立组件完成，
/// 本类只维护对话历史及 Reason → Observe → Think → Act 的调用顺序。
/// </summary>
public sealed class ReactAgent : IAgentConversation
{
    private readonly PersonaProfile _persona;
    private readonly ILLMProvider _llm;
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly ImportantMemoryStore? _importantMemory;
    private readonly OriginalVoiceCatalog? _originalVoices;
    private readonly IToolObservationCollector _observationCollector;
    private readonly IAgentPromptBuilder _promptBuilder;
    private readonly ISpeechSegmentParser _segmentParser;
    private readonly ICharacterSpeechSynthesizer _speechSynthesizer;
    private readonly IAgentEventSink _eventSink;
    private readonly List<ChatMessage> _history = [];
    private readonly StoryKnowledgeTool? _storyTool;
    private readonly ReactRetrievalLoop? _reactRetrieval;
    private readonly StoryAnswerComposer _storyComposer = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly HuTao.Agent.Core.Diagnostics.LocalDiagnosticLog _diagnostics;

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
        HuTao.Agent.Core.Diagnostics.LocalDiagnosticLog? diagnostics = null)
    {
        _persona = persona;
        _llm = llm;
        _tools = tools.ToDictionary(tool => tool.Name);
        _storyTool = _tools.Values.OfType<StoryKnowledgeTool>().SingleOrDefault();
        _reactRetrieval = _storyTool is null ? null : new ReactRetrievalLoop(_storyTool, _llm);
        _importantMemory = importantMemory;
        _originalVoices = originalVoices;
        _observationCollector = observationCollector ?? new ToolObservationCollector();
        _promptBuilder = promptBuilder ?? new AgentPromptBuilder();
        _segmentParser = segmentParser ?? new SpeechSegmentParser(originalVoices);
        _speechSynthesizer = speechSynthesizer ?? new CharacterSpeechSynthesizer(
            tts,
            emotionReferences,
            refAudio,
            refText);
        _eventSink = eventSink ?? NullAgentEventSink.Instance;
        _diagnostics = diagnostics ?? HuTao.Agent.Core.Diagnostics.LocalDiagnosticLog.Default;
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
                HuTao.Agent.Core.Diagnostics.LocalDiagnosticLog.Default.Write("speech.full_turn", ex);
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
            action) { Story=text.Story, StoryAnswer=text.StoryAnswer };
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
            var result = await GenerateTextTurnAsync(userInput, isProactive, ct).ConfigureAwait(false);
            _diagnostics.Turn(turnId, trigger, "complete",
                result.StoryAnswer?.Path ?? (isProactive ? "proactive-local" : "conversation"),
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

    private async Task<TextResult> GenerateTextTurnAsync(string? userInput, bool isProactive, CancellationToken ct)
    {
        // 心跳不是一次新的用户提问，不把旧问题交给无检索的 LLM 重答。
        // 主动闲聊限定为无剧情断言的本地邀请；剧情内容必须由用户明确提问走同一检索/校验链。
        if (isProactive)
        {
            ct.ThrowIfCancellationRequested();
            var invitation = _persona.Name switch
            {
                "芙宁娜" => "要不要歇一会儿，陪我聊聊？",
                "可莉" => "可莉在这里哦！要不要一起聊点开心的事？",
                _ => "本堂主在这儿呢，要不要歇一会儿，聊点什么？"
            };
            return new TextResult("主动邀请", "", invitation,
                [new SpeechSegment(invitation, "neutral", 0.35)]);
        }
        var reason = isProactive
            ? "主动搭话：心跳到点，且用户不忙、冷却已过"
            : $"回应用户输入：{userInput}";
        await PublishAsync(new AgentStageEvent(
            AgentStage.Reason,
            reason,
            DateTimeOffset.Now), ct).ConfigureAwait(false);
        var ordinaryTools = _tools.Where(pair => pair.Value is not StoryKnowledgeTool)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var observation = await _observationCollector
            .CollectAsync(ordinaryTools, userInput, ct)
            .ConfigureAwait(false);
        var reactRetrieval = _storyTool is not null && userInput is not null
            ? await _reactRetrieval!.RunAsync(userInput, _history.TakeLast(8).ToArray(), ct).ConfigureAwait(false)
            : null;
        var story = reactRetrieval?.EvidencePool;
        if (reactRetrieval is not null)
            observation += $"\n{reactRetrieval.Observation}";
        if (story is not null && story.Status != StoryStatus.Bypass)
            observation += $"\n- story_archive: {story.Status}; {story.Trace.RetrievalMode}; {story.Evidence.Count} evidence; {story.Trace.ElapsedMs:F0}ms";
        await PublishAsync(new AgentStageEvent(
            AgentStage.Observe,
            observation,
            DateTimeOffset.Now), ct).ConfigureAwait(false);
        var memory = _importantMemory?.PreparePrompt(
            userInput,
            isProactive,
            DateTimeOffset.Now) ?? "";

        if (userInput is not null)
            _history.Add(new ChatMessage("user", userInput));

        var prompt = _promptBuilder.Build(new AgentPromptContext(
            _persona,
            observation,
            memory,
            _originalVoices?.FindCandidates(userInput) ?? [],
            _tools.ContainsKey("document_reader")));
        StoryAnswerResult? storyAnswer = null;
        string rawReply;
        if (story is not null && story.Status is not (StoryStatus.Bypass or StoryStatus.Playful or StoryStatus.Comfort))
        {
            storyAnswer = await _storyComposer.ComposeAsync(_llm, prompt, _history, story, ct).ConfigureAwait(false);
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
        var segments = _segmentParser.Parse(rawReply);
        if (segments.Count == 0)
            segments = [new SpeechSegment("……", "neutral", 0.3)];

        var reply = string.Join('\n', segments.Select(segment => segment.Text));
        _history.Add(new ChatMessage("assistant", reply));
        await PublishAsync(new AgentStageEvent(
            AgentStage.Think,
            reply,
            DateTimeOffset.Now), ct).ConfigureAwait(false);
        return new TextResult(reason, observation, reply, segments) { Story = story, StoryAnswer = storyAnswer };
    }

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
