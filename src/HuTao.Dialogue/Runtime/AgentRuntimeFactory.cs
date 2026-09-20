using System.Text.Json;
using HuTao.Foundation.Abstractions;
using HuTao.Persona;
using HuTao.Dialogue.Core;
using HuTao.Foundation.Diagnostics;
using HuTao.Dialogue.Llm;
using HuTao.Knowledge.Memory;
using HuTao.Knowledge.Rag;
using HuTao.Dialogue.Storage;
using HuTao.Dialogue.Tools;
using HuTao.Voice;

namespace HuTao.Dialogue.Runtime;

/// <summary>
/// 统一装配角色运行时。
/// 具体 LLM/TTS 通过委托注入，Core 只负责组合业务依赖；宿主可以选择常驻或按需 TTS。
/// </summary>
public sealed class AgentRuntimeFactory
{
    private readonly string _repoRoot;
    private readonly Func<CharacterDefinition, ITtsEngine?>? _ttsFactory;
    private readonly Action<string>? _log;
    private readonly Func<ToolApproval, CancellationToken, Task<bool>>? _approveTool;
    /// <summary>按语料目录缓存检索服务：胡桃一份、异环一份，互不干扰也互不重复构建。</summary>
    private readonly Dictionary<string, StoryRagService> _storyRags = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _storyGate = new();

    public AgentRuntimeFactory(
        string repoRoot,
        Func<CharacterDefinition, ITtsEngine?>? ttsFactory = null,
        Action<string>? log = null,
        Func<ToolApproval, CancellationToken, Task<bool>>? approveTool = null)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
        _ttsFactory = ttsFactory;
        _log = log;
        _approveTool = approveTool;
    }

    /// <summary>
    /// 取该角色语料的检索服务；未声明语料时返回 null（该角色不挂剧情档案工具）。
    /// 语料目录由 CharacterDefinition 决定，所以「原神角色用原神语料、异环角色用异环语料」
    /// 只是两条配置，检索链路本身完全共用。
    ///
    /// <paramref name="lexicon"/> 是**角色的词表**：检索要认识「本堂主」「桃桃」这类自称，
    /// 才知道用户在问谁。语料自己的词表（实体表、作品专属词）由 StoryRagService
    /// 从语料目录自动加载。
    /// </summary>
    private StoryRagService? GetStoryRag(CharacterDefinition character, StoryLexicon? lexicon)
    {
        if (string.IsNullOrWhiteSpace(character.StoryCorpusDirectory))
            return null;

        var corpusRoot = Path.Combine(_repoRoot, character.StoryCorpusDirectory);
        var dialogueRoot = Path.Combine(corpusRoot, "dialogue");
        if (!Directory.Exists(Path.Combine(dialogueRoot, "chapters")))
        {
            _log?.Invoke($"[story] {character.Name} 的语料目录不存在，跳过：{dialogueRoot}");
            return null;
        }

        // 缓存键必须带上角色 id：检索行为由「语料 + 角色词表」共同决定，
        // 只用语料目录做键会让两个共用同一语料的角色互相复用词表（静默串味）。
        var cacheKey = dialogueRoot + "\u001f" + character.Id;
        lock (_storyGate)
        {
            if (_storyRags.TryGetValue(cacheKey, out var cached))
                return cached;

            var summaries = StoryVectorStore.Load(Path.Combine(corpusRoot, "index.json"));
            var service = new StoryRagService(dialogueRoot, summaries,
                semantic: Uri.TryCreate(Environment.GetEnvironmentVariable("HU_TAO_STORY_SEMANTIC_URL"), UriKind.Absolute, out var url)
                    ? new LocalStorySemanticSearch(url) : null,
                characterLexicon: lexicon);
            _storyRags[cacheKey] = service;
            _log?.Invoke($"[story] {character.Name} 使用语料 {dialogueRoot}" +
                         (lexicon?.HasAnyVocabulary == true ? "（含角色词表）" : "（无角色词表）"));
            return service;
        }
    }

    public AgentRuntime Create(
        CharacterDefinition character,
        ITtsEngine? sharedTts = null,
        Func<bool>? allowAppAwareness = null,
        Action<string>? documentProgress = null,
        IAgentEventSink? eventSink = null,
        string? memoryScope = null)
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

        var tools = CreateTools(character, persona.EffectiveLexicon, allowAppAwareness, documentProgress, tts,
            refAudio, emotionReferences);
        var originalVoices = OriginalVoiceCatalog.Load(
            Path.Combine(_repoRoot, character.VoiceDirectory ?? Path.Combine("data", "voice", character.Id),
                "manifest.jsonl"),
            persona.Quotes);

        // 长期记忆：每个角色一份，人设与聊天记录本来就互相独立。
        // memoryScope 用于「同一个角色的第二现场」（聊天室窗口）：两个内存中的 store
        // 指向同一个 json 会互相覆盖写入，所以聊天室另开一份而不是共用。
        var memoryOptions = MemoryOptions.FromEnvironment();
        var memoryStore = memoryOptions.Enabled
            ? new ConversationMemoryStore(
                Path.Combine(_repoRoot, "data",
                    $"memory_{character.Id}{(string.IsNullOrWhiteSpace(memoryScope) ? "" : "_" + memoryScope)}.json"),
                memoryOptions, LocalDiagnosticLog.Default)
            : null;
        if (memoryStore is not null)
            ImportChatLogHistory(memoryStore, character);

        var llm = BuildLlm(persona);
        var agent = new ReactAgent(
            persona,
            llm,
            tts,
            tools,
            refAudio: voiceAvailable ? refAudio : null,
            refText: voiceAvailable ? character.ReferenceText : null,
            importantMemory: CreateMemory(character, persona),
            emotionReferences: emotionReferences,
            originalVoices: originalVoices,
            eventSink: eventSink,
            observationCollector: new ToolObservationCollector(llm, _approveTool,
                allowAppAwareness, allowSideEffects: string.IsNullOrWhiteSpace(memoryScope)),
            memory: memoryStore);
        var reader = tools.OfType<DocumentReadingTool>().Single();

        var maintenance = memoryStore is null
            ? null
            : new MemoryMaintenance(
                memoryStore,
                new MemoryConsolidator(memoryStore, llm, memoryOptions, LocalDiagnosticLog.Default),
                memoryOptions);

        return new AgentRuntime(
            character,
            persona,
            agent,
            reader,
            tts,
            emotionReferences,
            originalVoices,
            tools,
            maintenance,
            memoryStore);
    }

    /// <summary>
    /// 把功能上线之前的聊天记录补进记忆库。游标存在记忆文件里，
    /// 所以反复启动不会重复导入，也不会把用户已有的历史丢掉。
    /// </summary>
    private void ImportChatLogHistory(ConversationMemoryStore store, CharacterDefinition character)
    {
        try
        {
            var logPath = Path.Combine(_repoRoot, "data", $"chat_log_{character.Id}.json");
            if (!File.Exists(logPath))
                return;
            var entries = JsonSerializer.Deserialize<List<ChatEntry>>(File.ReadAllText(logPath)) ?? [];
            if (entries.Count == 0)
                return;
            var imported = store.ImportChatLog(
                entries.Select(e => (e.Time, e.Role, e.Text)), DateTimeOffset.Now);
            if (imported > 0)
                _log?.Invoke($"[memory] 已从聊天记录补入 {imported} 条历史（共 {store.Count} 条记忆）");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[memory] 历史导入失败: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 装配这一轮的工具。
    ///
    /// 工具集合**由角色包声明**（<see cref="CharacterDefinition.Tools"/>，留空则用默认集），
    /// 构造器登记在 <see cref="AgentToolRegistry"/> 里。这样加工具、换工具、
    /// 给某个角色单独配工具都不需要改这个工厂——它只负责把依赖递进去。
    /// </summary>
    private IReadOnlyList<IAgentTool> CreateTools(
        CharacterDefinition character,
        StoryLexicon lexicon,
        Func<bool>? allowAppAwareness,
        Action<string>? documentProgress,
        ITtsEngine? tts,
        string? refAudio,
        EmotionReferenceCatalog emotionReferences)
    {
        // 剧情检索按「有没有语料」装配，而不是按角色 id 硬编码。
        // 这里先取出来是为了同时注册工具与预热索引。
        var storyRag = GetStoryRag(character, lexicon);
        if (storyRag is not null)
            _ = WarmStoryAsync(storyRag, character.Id);

        var registry = new AgentToolRegistry()
            .Register("time", () => new TimeTool())
            // 前台感知默认开启：桌宠需要知道用户在做什么才谈得上「陪伴」。
            // 宿主仍可通过委托随时关闭（桌宠面板的 ◉/○ 开关），关闭后工具不读取任何窗口信息。
            .Register("active_window", () => new ActiveWindowTool(allowAppAwareness ?? (() => true)))
            .Register("idle", () => new IdleTool())
            .Register("story_archive", () => storyRag is null ? null : new StoryKnowledgeTool(storyRag))
            .Register("document_reader", () => new DocumentReadingTool(
                tts,
                refAudio,
                character.ReferenceText,
                emotionReferences,
                Path.Combine(_repoRoot, "data"),
                Path.Combine(_repoRoot, "voice", "GPT-SoVITS-main", "ffmpeg.exe"),
                character.Name,
                documentProgress));

        foreach (var builtin in BuiltinToolCatalog.Create(Path.Combine(_repoRoot, "data", "tool-workspace")))
            registry.Register(builtin.Name, () => builtin);

        // CLI profiles constrain executable, subcommand, arguments and working directory.
        // 没配就是没有——**不会**去扫 PATH 或让模型自由指定程序。
        foreach (var cli in character.CliTools ?? [])
        {
            var tool = CliTool.TryCreate(cli.Name, cli.Description, ResolvePath(cli.Executable),
                cli.Arguments, _log, Path.Combine(_repoRoot, "data", "tool-workspace"));
            if (tool is not null)
                registry.Register(cli.Name, () => tool);
        }

        return registry.Resolve(character.Tools, _log);
    }

    /// <summary>把角色包里的相对路径解析到仓库根下；绝对路径原样返回。</summary>
    private string ResolvePath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(_repoRoot, path);

    /// <summary>
    /// 重要事项彩蛋（「记住…」/「忘掉…」）。**由角色包声明要不要挂**，不再按 id 写死。
    ///
    /// 原来是 `character.Id.Equals("hutao") ? new ImportantMemoryStore("…hutao.json") : null`——
    /// 加一个新角色想要这个能力，就得回来改这行代码。现在只看
    /// <see cref="CharacterDefinition.ImportantMemoryFile"/> 有没有配。
    /// </summary>
    private ImportantMemoryStore? CreateMemory(CharacterDefinition character, PersonaProfile persona)
    {
        if (string.IsNullOrWhiteSpace(character.ImportantMemoryFile))
            return null;
        return new ImportantMemoryStore(
            Path.Combine(_repoRoot, character.ImportantMemoryFile),
            // 唤醒词来自角色词表：默认只认角色名本身；胡桃的「本堂主 / 桃桃」等来自 lexicon.json。
            wakeWords: persona.EffectiveLexicon.ImportantMemoryWakeWords(persona.Name));
    }

    private async Task WarmStoryAsync(StoryRagService service, string characterId)
    {
        try { await service.WarmupAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log?.Invoke($"[story] {characterId} 语料预热失败: {ex.GetType().Name}"); }
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

    /// <summary>
    /// 解析参考音频。**开发期覆盖对任何角色都生效**，不再只对胡桃生效。
    ///
    /// 原来是 `if (character.Id == "hutao" && HU_TAO_REF_AUDIO 有值)`——这既是一处角色特例，
    /// 又让「换个参考音频试试」这个调试动作对异环角色静默失效（设了环境变量却不生效，
    /// 而且没有任何提示）。现在语义统一：**环境变量优先于角色包里的路径**，
    /// 配了就用，文件不存在就报出来而不是悄悄忽略。
    /// </summary>
    private string? ResolveReferenceAudio(CharacterDefinition character)
    {
        var configured = Environment.GetEnvironmentVariable("HU_TAO_REF_AUDIO");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
                return Path.GetFullPath(configured);
            _log?.Invoke($"[TTS] HU_TAO_REF_AUDIO 指向的文件不存在，忽略：{configured}");
        }

        if (string.IsNullOrWhiteSpace(character.ReferenceAudio))
            return null;
        var fullPath = Path.Combine(_repoRoot, character.ReferenceAudio);
        return File.Exists(fullPath) ? fullPath : null;
    }
}
