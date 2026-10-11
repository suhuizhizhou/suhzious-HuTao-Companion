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
public sealed partial class ReactAgent : IAgentConversation
{
    private readonly PersonaProfile _persona;
    private readonly ILLMProvider _llm;
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly IReadOnlyDictionary<string, IAgentTool> _ordinaryTools;
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
    private readonly KnowledgeConversationContext? _knowledge;
    private readonly Func<bool>? _runtimeIsCurrent;
    private HuTao.Knowledge.Retrieval.EvidenceBundle? _historyPrivacy;
    private HuTao.Knowledge.Retrieval.EvidenceBundle? _turnPrivacy;
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
        ConversationMemoryStore? memory = null,
        KnowledgeConversationContext? knowledge = null, Func<bool>? runtimeIsCurrent = null)
    {
        _persona = persona;
        _knowledge=knowledge;
        _runtimeIsCurrent=runtimeIsCurrent;
        _historyPrivacy=knowledge?.PrivacyStamp();
        _llm = llm;
        _tools = tools.ToDictionary(tool => tool.Name);
        _ordinaryTools = _tools.Where(pair => pair.Value is not StoryKnowledgeTool)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
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
        _recall = new ConversationRecall(llm, persona.EffectiveLexicon, recallStrategies,knowledge);
    }

    public IReadOnlyList<ChatMessage> History => _runtimeIsCurrent?.Invoke()==false || (_historyPrivacy is not null && _knowledge?.PrivacyIsCurrent(_historyPrivacy)!=true)?[]:_history.ToArray();
    public ConversationPrivacyStamp? PrivacyStamp=>_knowledge is null?null:
        new(_knowledge.Access.MemoryNamespace,_knowledge.PrivacyStamp().PrivacyEpochs[_knowledge.Access.MemoryNamespace]);

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
    {
        if(_runtimeIsCurrent?.Invoke()==false)throw new KnowledgeChangedException();
        return SynthesizeCurrentAsync(text,emotion,intensity,ct);
    }

    private async Task<TtsResult?> SynthesizeCurrentAsync(string text,string emotion,double intensity,CancellationToken ct)
    {
        var audio=await _speechSynthesizer.SynthesizeAsync(text,emotion,intensity,ct).ConfigureAwait(false);
        if(_runtimeIsCurrent?.Invoke()==false)throw new KnowledgeChangedException();
        return audio;
    }

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
        _historyPrivacy=_knowledge?.PrivacyStamp();
        // Unversioned legacy chat files cannot prove that they exclude a prior forgotten fact.
        if(_historyPrivacy is null || _historyPrivacy.PrivacyEpochs.Values.All(epoch=>epoch==0))_history.AddRange(history);
    }

    public void RestoreHistory(IEnumerable<ChatMessage> history,ConversationPrivacyStamp? privacy)
    {
        _history.Clear();_historyPrivacy=_knowledge?.PrivacyStamp();
        if(privacy==PrivacyStamp)_history.AddRange(history);
    }

    /// <summary>
    /// 交接快照。刻意走 <see cref="History"/> 而不是直接拷 _history：
    /// 那个属性已经处理了「运行时已失效」与「隐私世代已过期」两种情况（此时它返回空）。
    /// 交接的意义是**把角色此刻真正看得见的对话交给存储层**——如果世代已经不可信，
    /// 交出去的应该是空，而不是把一份不该再被引用的历史写进新会话的视角文件。
    /// </summary>
    public ConversationContext? CaptureContext()=>new(History,PrivacyStamp);

    private async Task<TextResult> GenerateTextCoreAsync(
        string? userInput,
        bool isProactive,
        CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        var turnId = Guid.NewGuid().ToString("N");
        var trigger = isProactive ? "proactive" : "user";
        var turnClock=Stopwatch.StartNew();
        var trace=BackendTrace.Default.BeginTurn($"回合 {turnId[..8]}",$"触发 {trigger} · 角色 {_persona.Name}");
        HuTao.Knowledge.Retrieval.EvidenceBundle? entryPrivacy=null;
        _diagnostics.Turn(turnId, trigger, "start", "pending");
        try
        {
            if(_runtimeIsCurrent?.Invoke()==false)throw new KnowledgeChangedException();
            if(_knowledge is not null)
            {
                if(_historyPrivacy is not null && !_knowledge.PrivacyIsCurrent(_historyPrivacy))_history.Clear();
                _turnPrivacy=_knowledge.PrivacyStamp();_historyPrivacy=_turnPrivacy;
                entryPrivacy=_turnPrivacy;
            }
            var result = await GenerateTextTurnAsync(userInput, isProactive, turnId, ct,trace).ConfigureAwait(false);
            DemandTurnPrivacy();
            if(_knowledge is not null && _turnPrivacy is { } stamp)
                result=result with {Segments=result.Segments.Select(s=>s with {DeliveryAllowed=()=>_knowledge.PrivacyIsCurrent(stamp)}).ToArray()};
            if(_runtimeIsCurrent is not null)
                result=result with {Segments=result.Segments.Select(s=>s with {DeliveryAllowed=()=>_runtimeIsCurrent() && (s.DeliveryAllowed?.Invoke()!=false)}).ToArray()};
            _diagnostics.Turn(turnId, trigger, "complete",
                result.StoryAnswer?.Path ?? (isProactive ? "proactive" : "conversation"),
                result.Story?.Status.ToString(), result.Story?.Evidence.Count ?? 0,
                result.Segments.Count, result.StoryAnswer?.Issues);
            return result;
        }
        catch(KnowledgeChangedException)
        {
            trace?.Discard();
            _history.Clear();
            _diagnostics.Turn(turnId,trigger,"cancelled","knowledge-changed");
            return new TextResult("知识状态已变化","knowledge-changed","刚才的依据有变化，我得重新确认一下。",
                [new("刚才的依据有变化，我得重新确认一下。","neutral",0.3)]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { trace?.Note("回合取消");_diagnostics.Turn(turnId, trigger, "cancelled", "cancelled"); throw; }
        catch (Exception ex)
        { trace?.Note("回合失败: "+ex.GetType().Name);_diagnostics.Turn(turnId, trigger, "failed", ex.GetType().Name); throw; }
        finally
        {
            try
            {
                if(_runtimeIsCurrent?.Invoke()==false || (entryPrivacy is not null && _knowledge?.PrivacyIsCurrent(entryPrivacy)!=true))trace?.Discard();
                trace?.Key("文字回合总耗时",$"{turnClock.Elapsed.TotalMilliseconds:F0}ms（含事项、写回、工具、检索与生成；不含 TTS）");
                trace?.End("文字回合结束");
            }
            finally{_turnGate.Release();}
        }
    }

    private void DemandTurnPrivacy()
    {if(_runtimeIsCurrent?.Invoke()==false || (_turnPrivacy is not null && _knowledge?.PrivacyIsCurrent(_turnPrivacy)!=true))throw new KnowledgeChangedException();}

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
    /// 兜底台词也必须是角色本人会说的话——绝不能退回系统口吻。
    ///
    /// 台词**不再写在这里**：以前是 <c>_persona.Name switch</c>，而 default 分支
    /// 本身就是胡桃的口吻，于是新角色一律说"本堂主"（安魂曲实测串味）。
    /// 现在一律问 <see cref="ScriptedLines"/>：有专属台词就用，没有就中性语气 +
    /// 后台按人设补一条并持久化。
    ///
    /// 这里**愿意等**生成结果：用户此刻正盯着屏幕等一句话，
    /// 宁可多花几百毫秒，也不要先甩一句不像她的话再换。
    /// </summary>
    private async Task<string> SafeFallbackAsync(CancellationToken ct)
        => await _persona.Scripted
            .GetAsync(ScriptedLines.Situation.Distracted, ct)
            .ConfigureAwait(false);

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
