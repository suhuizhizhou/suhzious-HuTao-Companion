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
    private readonly KnowledgeRuntimeOptions _knowledgeOptions;
    private readonly Func<PersonaProfile,ILLMProvider>? _llmFactory;

    public AgentRuntimeFactory(
        string repoRoot,
        Func<CharacterDefinition, ITtsEngine?>? ttsFactory = null,
        Action<string>? log = null,
        Func<ToolApproval, CancellationToken, Task<bool>>? approveTool = null,
        KnowledgeRuntimeOptions? knowledgeOptions=null,Func<PersonaProfile,ILLMProvider>? llmFactory=null)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
        _ttsFactory = ttsFactory;
        _log = log;
        _approveTool = approveTool;
        _knowledgeOptions=knowledgeOptions??KnowledgeRuntimeOptions.FromEnvironment(_repoRoot);_llmFactory=llmFactory;
        if(!Enum.IsDefined(_knowledgeOptions.Mode))throw new ArgumentException("Unknown knowledge runtime mode.");
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
        var scope=string.IsNullOrWhiteSpace(memoryScope)?"desktop":memoryScope;
        _=HuTao.Knowledge.Retrieval.KnowledgeAccessContext.ForSession(_knowledgeOptions.UserId,character.Id,scope,[]);
        var sqlWrites=_knowledgeOptions.Mode is KnowledgeRuntimeMode.Sql or KnowledgeRuntimeMode.SqlGraph;
        var hostPaths=KnowledgeHostPaths.Resolve(_repoRoot,character,_knowledgeOptions.UserId,scope);
        var authorityPath=hostPaths.Authority;
        var fence=new KnowledgeAuthorityFence(authorityPath);
        using var activationLease=fence.Acquire(legacy:!sqlWrites);
        if(File.Exists(authorityPath+".rollback-pending.json"))
            throw new InvalidOperationException("Rollback installation is pending; recover or complete it before starting a runtime.");
        if(File.Exists(authorityPath+".legacy-authority.json"))
        {
            var legacyAuthority=JsonSerializer.Deserialize<KnowledgeRollbackInstallation.LegacyAuthority>(File.ReadAllBytes(authorityPath+".legacy-authority.json"));
            if(legacyAuthority?.Namespace!=HuTao.Knowledge.Retrieval.KnowledgeAccessContext.ForSession(_knowledgeOptions.UserId,character.Id,scope,[]).MemoryNamespace)
                throw new InvalidDataException("Legacy authority belongs to a different conversation.");
            if(sqlWrites || _knowledgeOptions.Mode==KnowledgeRuntimeMode.Shadow)
                throw new InvalidOperationException("Legacy owns this conversation after rollback; explicitly migrate its latest state before selecting SQL or shadow.");
        }
        using var stoppedLegacy=sqlWrites?fence.RequireNoLegacyRuntime():null;
        string? expectedLineageId=null;
        if(!sqlWrites && File.Exists(authorityPath))throw new InvalidOperationException("SQL owns this conversation. Export and explicitly roll back before selecting legacy/shadow.");
        if(sqlWrites && File.Exists(authorityPath))
        {
            using var authority=JsonDocument.Parse(File.ReadAllText(authorityPath));
            if(authority.RootElement.GetProperty("space").GetString()!=HuTao.Knowledge.Retrieval.KnowledgeAccessContext.ForSession(_knowledgeOptions.UserId,character.Id,scope,[]).MemoryNamespace)
                throw new InvalidOperationException("SQL authority belongs to another conversation identity.");
            if(authority.RootElement.TryGetProperty("lineageId",out var lineage))
                expectedLineageId=lineage.GetString()??throw new InvalidDataException("SQL authority lineage is null.");
            if(!string.Equals(authority.RootElement.GetProperty("database").GetString(),Path.GetFullPath(_knowledgeOptions.DatabasePath),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Configured database differs from this conversation's SQL authority.");
        }
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
        var memoryPath=hostPaths.Memory;
        if(scope=="desktop" && (!sqlWrites || !File.Exists(authorityPath)) &&
            (File.Exists(Path.Combine(_repoRoot,"data",$"memory_{character.Id}_desktop.json")) ||
             File.Exists(Path.Combine(_repoRoot,"data",$"memory_{character.Id}_desktop.archive.jsonl"))))
            throw new InvalidOperationException("An older explicit-desktop memory file requires reconciliation with the canonical desktop file before startup.");
        var llm = BuildLlm(persona);
        // 情境台词（兜底/提醒/剧情降级）缺专属台词时按人设补一条并持久化。
        // 回填而不是构造时传入：建 LLM 要 persona，会成环（见 ScriptedLines.AttachGenerator）。
        if (llm is not MockLlmProvider)
        {
            var generatorLlm = llm;
            persona.Scripted.AttachGenerator((situation, ct) =>
                GenerateScriptedLineAsync(generatorLlm, persona, situation, ct));
        }
        var knowledge=_knowledgeOptions.Mode==KnowledgeRuntimeMode.Legacy?null:
            new KnowledgeRuntime(_knowledgeOptions,character.Id,scope,character.StoryCorpusDirectory,_log,expectedLineageId,llm);
        try
        {
        var sqlMemory=sqlWrites?new SqlConversationMemoryRepository(knowledge!.Database,knowledge.Context.Access,memoryOptions.MaxActiveRecords):null;
        if(sqlWrites && !File.Exists(authorityPath) && File.Exists(memoryPath) &&
            !new HuTao.Knowledge.Import.MemoryJsonImporter(sqlMemory!).Import(memoryPath,dryRun:true).AlreadyImported)
        {
            throw new InvalidOperationException("JSON memory differs from SQL. Back up and migrate a current snapshot before SQL becomes authoritative.");
        }
        var memoryStore = memoryOptions.Enabled
            ? new ConversationMemoryStore(
                // 记忆归属：谁说的。旧数据没有这个标签，检索端会把它当"来源不明"，
                // 不再当成当前角色说过的话（否则别的角色会模仿其自称）。
                memoryPath, memoryOptions with { Persona = persona.Name },
                LocalDiagnosticLog.Default,sqlMemory,sqlWrites?null:()=>fence.Acquire(legacy:true))
            : null;
        if (memoryStore is not null && !sqlWrites)
            ImportChatLogHistory(memoryStore, character, scope);

        IAgentConversation agent = new ReactAgent(
            persona,
            llm,
            tts,
            tools,
            refAudio: voiceAvailable ? refAudio : null,
            refText: voiceAvailable ? character.ReferenceText : null,
            importantMemory: CreateMemory(character, persona,scope,sqlWrites?knowledge:null,File.Exists(authorityPath),sqlWrites?null:()=>fence.Acquire(legacy:true)),
            emotionReferences: emotionReferences,
            originalVoices: originalVoices,
            eventSink: eventSink,
            observationCollector: new ToolObservationCollector(llm, _approveTool,
                allowAppAwareness, allowSideEffects: string.IsNullOrWhiteSpace(memoryScope)),
            memory: memoryStore,knowledge:sqlWrites?knowledge!.Context:null,runtimeIsCurrent:sqlWrites?null:fence.LegacyIsCurrent);
        if(_knowledgeOptions.Mode==KnowledgeRuntimeMode.Shadow)agent=new ShadowKnowledgeConversation(agent,knowledge!);
        var reader = tools.OfType<DocumentReadingTool>().Single();

        var maintenance = memoryStore is null
            ? null
            : new MemoryMaintenance(
                memoryStore,
                new MemoryConsolidator(memoryStore, llm, memoryOptions, LocalDiagnosticLog.Default),
                memoryOptions);

        if(sqlWrites)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
            var temporary=authorityPath+".tmp-"+Guid.NewGuid().ToString("N");
            try {File.WriteAllText(temporary,JsonSerializer.Serialize(new {database=Path.GetFullPath(_knowledgeOptions.DatabasePath),space=knowledge!.Context.Access.MemoryNamespace,lineageId=HuTao.Knowledge.Storage.KnowledgeRecovery.ReadLineage(knowledge.Database)}));File.Move(temporary,authorityPath,true);}
            finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        var legacyLifetime=sqlWrites?null:fence.RegisterLegacyRuntime();
        var sqlLifetime=sqlWrites?fence.RegisterSqlRuntime():null;
        var runtime=new AgentRuntime(
            character,
            persona,
            agent,
            reader,
            tts,
            emotionReferences,
            originalVoices,
            tools,
            maintenance,
            memoryStore,knowledge,legacyLifetime,sqlLifetime,scope);
        try {knowledge?.Activate();}
        catch {legacyLifetime?.Dispose();sqlLifetime?.Dispose();throw;}
        return runtime;
        }
        catch
        {
            knowledge?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>
    /// 把功能上线之前的聊天记录补进记忆库。游标存在记忆文件里，
    /// 所以反复启动不会重复导入，也不会把用户已有的历史丢掉。
    /// </summary>
    private void ImportChatLogHistory(ConversationMemoryStore store, CharacterDefinition character, string scope)
    {
        try
        {
            var logPath = KnowledgeHostPaths.Resolve(_repoRoot,character,_knowledgeOptions.UserId,scope).ChatLog!;
            if (!File.Exists(logPath))
                return;
            // Use the same privacy boundary as the host display. A SQL-stamped log
            // must be explicitly converted during rollback, never imported as legacy text.
            var entries = new ChatLogStore(logPath).Load();
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
        var storyRag = _knowledgeOptions.Mode is KnowledgeRuntimeMode.Sql or KnowledgeRuntimeMode.SqlGraph?null:GetStoryRag(character, lexicon);
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
    private ImportantMemoryStore? CreateMemory(CharacterDefinition character, PersonaProfile persona,string scope,KnowledgeRuntime? knowledge,bool existingSqlAuthority,Func<IDisposable>? accessLease)
    {
        if (string.IsNullOrWhiteSpace(character.ImportantMemoryFile))
            return null;
        var path=KnowledgeHostPaths.Resolve(_repoRoot,character,_knowledgeOptions.UserId,scope).ImportantItems!;
        var repository=knowledge is null?null:new SqlImportantItemRepository(knowledge.Database,knowledge.Context.Access);
        if(repository is not null && !existingSqlAuthority && File.Exists(path))
        {
            var legacy=JsonSerializer.Deserialize<List<ImportantMemory>>(File.ReadAllText(path))
                ??throw new InvalidDataException("Invalid important-item JSON.");
            static DateTimeOffset? Milliseconds(DateTimeOffset? value)=>value is null?null:DateTimeOffset.FromUnixTimeMilliseconds(value.Value.ToUnixTimeMilliseconds());
            var expected=legacy.Select(i=>new ImportantItem(i.Id,i.Content,Milliseconds(i.CreatedAt)!.Value,Milliseconds(i.DueAt),Milliseconds(i.LastOfferedAt))).OrderBy(i=>i.Id);
            if(!expected.SequenceEqual(repository.Load().OrderBy(i=>i.Id)))
                throw new InvalidOperationException("Important-item JSON differs from SQL. Migrate a current snapshot before activation.");
        }
        return new ImportantMemoryStore(path,
            // 唤醒词来自角色词表：默认只认角色名本身；胡桃的「本堂主 / 桃桃」等来自 lexicon.json。
            wakeWords: persona.EffectiveLexicon.ImportantMemoryWakeWords(persona.Name),repository:repository,accessLease:accessLease);
    }

    private async Task WarmStoryAsync(StoryRagService service, string characterId)
    {
        try { await service.WarmupAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log?.Invoke($"[story] {characterId} 语料预热失败: {ex.GetType().Name}"); }
    }

    /// <summary>
    /// 按人设生成一条情境台词。**只给这一句话、温度压到 0.4**：
    /// 兜底台词是"安全网"，不该像创作一样每跑一次就变个样。
    /// 失败一律返回 null —— 上层会退回中性语气，聊天不该因为兜底台词挂掉。
    /// </summary>
    private async Task<string?> GenerateScriptedLineAsync(
        ILLMProvider llm, PersonaProfile persona,
        HuTao.Persona.ScriptedLines.Situation situation, CancellationToken ct)
    {
        try
        {
            var prompt = persona.Scripted.BuildPrompt(situation);
            var reply = await llm.CompleteAsync(
                prompt, [new ChatMessage("user", "（轮到你说话了。）")], ct).ConfigureAwait(false);
            var text = (reply ?? "").Trim().Trim('"', '「', '」');
            if (text.Length == 0)
                return null;
            // 生成物也得守出戏底线：自称 AI、客服腔一律不要。
            var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            _log?.Invoke($"[scripted] {persona.Name} · {situation} → {flat}");
            return flat;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke($"[scripted] 生成失败（{persona.Name} · {situation}）：{ex.GetType().Name}");
            return null;
        }
    }

    private ILLMProvider BuildLlm(PersonaProfile persona)    {
        if(_llmFactory is not null)return _llmFactory(persona);
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
