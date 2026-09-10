using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Configuration;
using HuTao.Agent.Core.Core;
using HuTao.Agent.Core.Llm;
using HuTao.Agent.Core.Persona;
using HuTao.Agent.Core.Rag;
using HuTao.Agent.Core.Storage;
using HuTao.Agent.Core.Tools;
using HuTao.Agent.Core.Tts;

namespace HuTao.Agent.Core.Runtime;

/// <summary>
/// 统一装配角色运行时。
/// 具体 LLM/TTS 通过委托注入，Core 只负责组合业务依赖；宿主可以选择常驻或按需 TTS。
/// </summary>
public sealed class AgentRuntimeFactory
{
    private readonly string _repoRoot;
    private readonly Func<CharacterDefinition, ITtsEngine?>? _ttsFactory;
    private readonly Action<string>? _log;
    private readonly Lazy<StoryVectorStore> _storySummaries;
    private readonly Lazy<StoryRagService> _storyRag;

    public AgentRuntimeFactory(
        string repoRoot,
        Func<CharacterDefinition, ITtsEngine?>? ttsFactory = null,
        Action<string>? log = null)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
        _ttsFactory = ttsFactory;
        _log = log;
        _storySummaries = new Lazy<StoryVectorStore>(
            () => StoryVectorStore.Load(Path.Combine(_repoRoot, "data", "story", "index.json")),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _storyRag = new Lazy<StoryRagService>(
            () => new StoryRagService(Path.Combine(_repoRoot, "data", "story", "dialogue"), _storySummaries.Value,
                semantic: Uri.TryCreate(Environment.GetEnvironmentVariable("HU_TAO_STORY_SEMANTIC_URL"), UriKind.Absolute, out var url)
                    ? new LocalStorySemanticSearch(url) : null),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public AgentRuntime Create(
        CharacterDefinition character,
        ITtsEngine? sharedTts = null,
        Func<bool>? allowAppAwareness = null,
        Action<string>? documentProgress = null,
        IAgentEventSink? eventSink = null)
    {
        var persona = PersonaLoader.Load(Path.Combine(_repoRoot, character.PersonaDirectory));
        var refAudio = ResolveReferenceAudio(character);
        var emotionReferences = EmotionReferenceCatalog.Load(
            Path.Combine(_repoRoot, character.EmotionCatalog),
            refAudio ?? "",
            character.ReferenceText);
        var voiceAvailable = File.Exists(refAudio) || emotionReferences.HasUsableReference;
        var tts = voiceAvailable
            ? sharedTts ?? _ttsFactory?.Invoke(character)
            : null;

        if (voiceAvailable && tts is null)
            _log?.Invoke($"[TTS] 未找到可用引擎，{character.Name} 使用文字模式");

        var tools = CreateTools(character, allowAppAwareness, documentProgress, tts,
            refAudio, emotionReferences);
        var originalVoices = OriginalVoiceCatalog.Load(
            Path.Combine(_repoRoot, "data", "voice", character.Id, "manifest.jsonl"),
            persona.Quotes);
        var agent = new ReactAgent(
            persona,
            BuildLlm(persona),
            tts,
            tools,
            refAudio: voiceAvailable ? refAudio : null,
            refText: voiceAvailable ? character.ReferenceText : null,
            importantMemory: CreateMemory(character),
            emotionReferences: emotionReferences,
            originalVoices: originalVoices,
            eventSink: eventSink);
        var reader = tools.OfType<DocumentReadingTool>().Single();

        return new AgentRuntime(
            character,
            persona,
            agent,
            reader,
            tts,
            emotionReferences,
            originalVoices,
            tools);
    }

    private IReadOnlyList<IAgentTool> CreateTools(
        CharacterDefinition character,
        Func<bool>? allowAppAwareness,
        Action<string>? documentProgress,
        ITtsEngine? tts,
        string? refAudio,
        EmotionReferenceCatalog emotionReferences)
    {
        var tools = new List<IAgentTool>
        {
            new TimeTool(),
            new ActiveWindowTool(allowAppAwareness ?? (() => false)),
            new IdleTool(),
        };

        // 剧情档案是胡桃的第四面墙能力，其他角色不共享该工具。
        if (character.Id.Equals("hutao", StringComparison.OrdinalIgnoreCase))
        {
            tools.Add(new StoryKnowledgeTool(
                _storyRag.Value));
            _ = WarmStoryAsync(_storyRag.Value);
        }

        tools.Add(new DocumentReadingTool(
            tts,
            refAudio,
            character.ReferenceText,
            emotionReferences,
            Path.Combine(_repoRoot, "data"),
            Path.Combine(_repoRoot, "voice", "GPT-SoVITS-main", "ffmpeg.exe"),
            character.Name,
            documentProgress));
        return tools;
    }

    private ImportantMemoryStore? CreateMemory(CharacterDefinition character)
        => character.Id.Equals("hutao", StringComparison.OrdinalIgnoreCase)
            ? new ImportantMemoryStore(Path.Combine(
                _repoRoot, "data", "important_memories_hutao.json"))
            : null;

    private async Task WarmStoryAsync(StoryRagService service)
    {
        try { await service.WarmupAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log?.Invoke($"[story] 预热失败: {ex.GetType().Name}"); }
    }

    private ILLMProvider BuildLlm(PersonaProfile persona)
    {
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            _log?.Invoke("[llm] 已接入 DeepSeek");
            return new DeepSeekLlmProvider(key);
        }
        _log?.Invoke("[llm] 未检测到 DEEPSEEK_API_KEY，使用 Mock");
        return new MockLlmProvider(persona);
    }

    private string? ResolveReferenceAudio(CharacterDefinition character)
    {
        var configured = Environment.GetEnvironmentVariable("HU_TAO_REF_AUDIO");
        if (character.Id.Equals("hutao", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var fullPath = Path.Combine(_repoRoot, character.ReferenceAudio);
        return File.Exists(fullPath) ? fullPath : null;
    }
}
