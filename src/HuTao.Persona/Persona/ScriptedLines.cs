using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using HuTao.Foundation.Diagnostics;

namespace HuTao.Persona;

/// <summary>
/// 「某个情境下，这个角色本人会怎么说」的**唯一出口**。
///
/// 为什么需要它：以前这些台词被写成 <c>switch (character.Id)</c> 里的 <c>default</c> 分支，
/// 而那个 default 本身是**胡桃的口吻**。于是每加一个角色，所有 switch 都会静默地
/// 让新角色说胡桃的话——安魂曲会说"本堂主"，实测就是这么串味的。
/// 结构性修法是：**引擎里不许再出现任何角色的具体台词**；角色语气一律来自数据。
///
/// 三层解析（顺序固定）：
/// 1. <c>data/.../fallbacks.json</c> 里该情境的**专属台词** → 直接用；
/// 2. 缺失时返回**中性系统语气**（不带任何角色特征，所以不会出戏），
///    同时**后台按人设生成一条并持久化**，下次自动沿用；
/// 3. 生成失败 → 继续用中性语气，并记诊断（不抛，聊天不该因为兜底台词挂掉）。
///
/// 关键不变量：<see cref="Neutral"/> 里**绝不能出现角色专属词**。
/// 它是所有角色共用的安全网，一旦写成某个角色的口吻，这个类就白做了。
/// </summary>
public sealed class ScriptedLines
{
    /// <summary>
    /// 需要"本人一句话"的情境。用枚举而不是裸字符串，避免拼错导致静默走中性分支。
    /// </summary>
    public enum Situation
    {
        /// <summary>模型这一轮没给出可用的台词（兜底）。</summary>
        Distracted,
        /// <summary>久坐提醒。</summary>
        RestReminder,
        /// <summary>用户长时间不说话。</summary>
        IgnoredMild,
        IgnoredModerate,
        IgnoredLong,
        IgnoredVeryLong,
        /// <summary>清空聊天记录之后。</summary>
        HistoryCleared,
        /// <summary>剧情资料里找不到依据。</summary>
        StoryNoEvidence,
        /// <summary>剧情状态"暂时查不到"。</summary>
        StoryUnavailable,
        /// <summary>剧情追问：需要用户补充是哪一段。</summary>
        StoryClarifyFollowUp,
        /// <summary>剧情追问：需要用户补充人物或场景。</summary>
        StoryClarify,
        /// <summary>剧情处于"边界"（能编故事，但不能冒充事实）。</summary>
        StoryBoundary,
        /// <summary>草稿只是"有可能"时，追加一句"不一定准"。</summary>
        StoryTentativeSuffix,
        /// <summary>模型整轮失败（连格式都没回来）。</summary>
        ModelFailure,
    }

    /// <summary>
    /// 落盘形态。**扁平优先**：直接 <c>{"Distracted": "…"}</c>，与该目录下的
    /// <c>catchphrases.json</c> / <c>quotes.json</c> 风格一致，人可以手改、可以 review。
    /// 同时兼容早期的 <c>{Version, Lines: {…}}</c> 信封，避免版本迁移。
    /// 以 <c>_</c> 开头的键是给人看的说明，会被忽略。
    /// </summary>
    private sealed record FallbackEnvelope(int Version, Dictionary<string, FallbackEntry> Lines);

    private sealed record FallbackEntry
    {
        [JsonPropertyName("status")] public string Status { get; init; } = "ready";
        [JsonPropertyName("text")] public string Text { get; init; } = "";
        [JsonPropertyName("generated_at")] public DateTimeOffset? GeneratedAt { get; init; }
        [JsonPropertyName("generated_by")] public string? GeneratedBy { get; init; }
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly string _characterName;
    private readonly LocalDiagnosticLog _log;
    private Func<Situation, CancellationToken, Task<string?>>? _generate;
    private readonly bool _persistent;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// 无持久化、无生成器的空实现：一律返回中性语气，**绝不碰磁盘**。
    /// 给测试夹具与"还没有人设目录"的场景用。它有意义不是因为它能说话，
    /// 而是因为它让"缺台词时的行为"成为可注入的默认值，而不是散落的 null 判断。
    /// </summary>
    public static ScriptedLines Empty { get; } = new();

    private ScriptedLines()
    {
        _filePath = "";
        _characterName = "";
        _log = LocalDiagnosticLog.Default;
        _generate = null;
        _persistent = false;
    }

    public ScriptedLines(
        string personaRoot,
        string characterName,
        Func<Situation, CancellationToken, Task<string?>>? generate = null,
        LocalDiagnosticLog? log = null)
    {
        _filePath = Path.Combine(personaRoot, "fallbacks.json");
        _characterName = characterName;
        _generate = generate;
        _log = log ?? LocalDiagnosticLog.Default;
        _persistent = true;
        Load();
    }

    /// <summary>
    /// 已持久化的专属台词条数（不含中性兜底）。给启动自检用：
    /// "哪些角色还缺哪些情境"应当是可见的，而不是等到聊到才暴露。
    /// </summary>
    public int ReadyCount => _cache.Count;

    /// <summary>本角色还缺哪些情境的专属台词。</summary>
    public IReadOnlyList<Situation> Missing()
        => Enum.GetValues<Situation>().Where(situation => !_cache.ContainsKey(Key(situation))).ToArray();

    public string FilePath => _filePath;

    /// <summary>
    /// 取该情境下这个角色的台词。**永不抛、永不为空**：
    /// 缺失时给中性语气，并（有生成器时）在后台补一条。
    /// </summary>
    /// <summary>
    /// 装上"按人设补一条台词"的生成器。**允许后置注入**，因为这里有个循环：
    /// 生成要 LLM，而 <c>AgentRuntimeFactory</c> 建 LLM 的签名是
    /// <c>Func&lt;PersonaProfile, ILLMProvider&gt;</c>（要 persona）。
    /// 解法是 persona 先建好、LLM 再建、然后回填——生成器在**调用时**才被读取，
    /// 所以回填是安全的。
    /// </summary>
    public void AttachGenerator(Func<Situation, CancellationToken, Task<string?>> generate)
        => _generate = generate ?? throw new ArgumentNullException(nameof(generate));

    /// <summary>把这个角色的人设与情境拼成一次生成请求。提示词在这里，调用方只管给 LLM。</summary>
    public string BuildPrompt(Situation situation)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(_systemPrompt))
            sb.Append(_systemPrompt.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(_catchphrases))
            sb.Append("你的口头禅示例：").Append(_catchphrases.Trim()).Append('\n');
        sb.Append("现在有一个场合需要你开口：\n");
        sb.Append(Describe(situation)).Append('\n');
        sb.Append("只输出你开口说的那一句话本身，不要引号、不要旁白说明、不要解释、不要 markdown。");
        return sb.ToString();
    }

    private string _systemPrompt = "";
    private string _catchphrases = "";

    /// <summary>补上生成所需的角色素材（系统提示词与口头禅样例）。</summary>
    public void AttachVoice(string systemPrompt, string catchphraseSample)
    {
        _systemPrompt = systemPrompt ?? "";
        _catchphrases = catchphraseSample ?? "";
    }

    public string Get(Situation situation)
    {
        var key = Key(situation);
        if (_cache.TryGetValue(key, out var ready) && !string.IsNullOrWhiteSpace(ready))
            return ready;

        QueueGeneration(situation, key);
        return Neutral(situation);
    }

    /// <summary>
    /// 与 <see cref="Get"/> 相同，但**愿意等**生成结果。
    /// 关键路径（例如角色这一轮已经失败、用户正等着看一句）用它；
    /// 定时提醒那种"这次没用上下次也行"的场合用 <see cref="Get"/>。
    /// </summary>
    public async Task<string> GetAsync(Situation situation, CancellationToken ct = default)
    {
        var key = Key(situation);
        if (_cache.TryGetValue(key, out var ready) && !string.IsNullOrWhiteSpace(ready))
            return ready;

        if (_generate is null)
            return Neutral(situation);

        try
        {
            var generated = await GenerateAndStoreAsync(situation, key, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(generated) ? Neutral(situation) : generated;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Write("scripted_lines.generate", ex);
            return Neutral(situation);
        }
    }

    /// <summary>缺就补一条并落盘（阻塞版）。给离线自检/预生成用。给不出就返回 null。</summary>
    public async Task<string?> EnsureAsync(Situation situation, CancellationToken ct = default)
    {
        var key = Key(situation);
        if (_cache.TryGetValue(key, out var ready) && !string.IsNullOrWhiteSpace(ready))
            return ready;
        if (_generate is null)
            return null;
        return await GenerateAndStoreAsync(situation, key, ct).ConfigureAwait(false);
    }

    private void QueueGeneration(Situation situation, string key)
    {
        if (_generate is null)
            return;
        // 单飞：同一情境并发只生成一次（冷启动时多个入口可能同时要同一句）。
        if (!_inFlight.TryAdd(key, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await GenerateAndStoreAsync(situation, key, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Write("scripted_lines.background_generate", ex);
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        });
    }

    private async Task<string?> GenerateAndStoreAsync(Situation situation, string key, CancellationToken ct)
    {
        var generated = await _generate!(situation, ct).ConfigureAwait(false);
        var text = (generated ?? "").Trim().Trim('"', '「', '」');
        if (text.Length == 0)
            return null;
        // 生成物也不许带角色名本身当自称之外的元信息（例如"作为AI"）。
        if (text.Contains("作为AI", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("作为一个AI", StringComparison.Ordinal))
            return null;

        _cache[key] = text;
        await PersistAsync(situation, text, ct).ConfigureAwait(false);
        return text;
    }

    // ── 持久化 ────────────────────────────────────────────────────────────

    private void Load()
    {
        if (!_persistent)
            return;
        try
        {
            foreach (var (key, value) in ReadFlat(_filePath))
                _cache[key] = value;
        }
        catch (Exception ex)
        {
            // 读不出来只是回到"没有专属台词"，不该影响启动。
            _log.Write("scripted_lines.load", ex);
        }
    }

    /// <summary>
    /// 把两种落盘形态都读成 <c>键 → 台词</c>。**不用强类型反序列化**：
    /// 嵌套的 private record 反射源生成器访问不到，会静默返回默认值
    /// （Version=0 / Lines=null），而且**不抛异常**——排查这个坑花了不少时间。
    /// 手工遍历 <c>JsonDocument</c> 反而更稳、更宽容。
    /// </summary>
    private static Dictionary<string, string> ReadFlat(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return result;

        // 新格式：顶层就是 键→字符串。旧格式：{ Version, Lines: { 键→{status,text} } }。
        var source = document.RootElement.TryGetProperty("Lines", out var lines) &&
                     lines.ValueKind == JsonValueKind.Object
            ? lines
            : document.RootElement;

        foreach (var property in source.EnumerateObject())
        {
            // 以 _ 开头的是给人看的说明；非字符串值（例如信封里的 Version）直接跳过。
            if (property.Name.StartsWith('_'))
                continue;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                {
                    var text = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        result[property.Name] = text.Trim();
                    break;
                }
                case JsonValueKind.Object
                    when property.Value.TryGetProperty("text", out var textElement) &&
                         textElement.ValueKind == JsonValueKind.String:
                {
                    // 旧信封条目：status != ready 视为未就绪。
                    if (property.Value.TryGetProperty("status", out var status) &&
                        status.ValueKind == JsonValueKind.String &&
                        !string.Equals(status.GetString(), "ready", StringComparison.OrdinalIgnoreCase))
                        break;
                    var text = textElement.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        result[property.Name] = text.Trim();
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 写盘。**先合并再写**：桌宠与聊天客户端可能同时持有同一个角色的
    /// <see cref="ScriptedLines"/>（两套宿主），直接覆盖会丢掉对方刚生成的那条。
    /// </summary>
    private async Task PersistAsync(Situation situation, string text, CancellationToken ct)
    {
        if (!_persistent)
            return;
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 先读回盘上已有的（可能被另一个宿主刚写过一条），合并后再整体写。
            var merged = ReadFlat(_filePath);
            merged[Key(situation)] = text;

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var temporary = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary,
                    JsonSerializer.Serialize(merged, WriteOptions), ct).ConfigureAwait(false);
                File.Move(temporary, _filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string Key(Situation situation) => situation.ToString();

    // ── 中性兜底 ──────────────────────────────────────────────────────────
    //
    // 这一组文本是**所有角色共用**的安全网。铁律：不许出现任何角色专属用词
    // （自称、组织名、作品名、口癖）。它们读起来像"系统的叙述"，不像角色在说话——
    // 这是刻意的：宁可少一句风味，也不要让别的角色的人格渗进来。

    public static string Neutral(Situation situation) => situation switch
    {
        Situation.Distracted => "唔…让我重新理一理，刚才没接上你的话。你再说一次？",
        Situation.RestReminder => "坐得有点久了，起来活动活动吧。",
        Situation.IgnoredMild => "（安静了一会儿。）",
        Situation.IgnoredModerate => "（你有一阵子没说话了。）",
        Situation.IgnoredLong => "（她等了你很久。）",
        Situation.IgnoredVeryLong => "（她一直在等你回来。）",
        Situation.HistoryCleared => "（聊天记录已清空。）",
        Situation.StoryNoEvidence => "这个我手上没有可靠依据，不好乱讲。你再给我一点线索？",
        Situation.StoryUnavailable => "这会儿还没翻到可靠的记录，稍后再问我一次吧。",
        Situation.StoryClarifyFollowUp => "你想接着说哪一段？给我一个人物或场景就好。",
        Situation.StoryClarify => "你还记得是谁说的，或者在什么地方吗？我再想想。",
        Situation.StoryBoundary => "编故事可以，把编的当成真事可不行。我们可以另讲一段想象中的。",
        Situation.StoryTentativeSuffix => "……这只是我记得的，不一定准。",
        Situation.ModelFailure => "（刚才没能接上话，让我再试一次。）",
        _ => "……",
    };

    /// <summary>
    /// 给生成器的情境说明（**不是**给用户看的文本）。用第二人称描述"你要说哪句话"，
    /// 不含任何角色名——角色人设由 system prompt 提供，这里只说场景。
    /// </summary>
    public static string Describe(Situation situation) => situation switch
    {
        Situation.Distracted => "你刚刚走神了，没听清对方说的话。用你自己的口吻请他再说一次。一到两句，不要解释技术原因。",
        Situation.RestReminder => "对方已经坐了很久。用你自己的口吻提醒他起来活动一下。一到两句。",
        Situation.IgnoredMild => "对方十来分钟没理你。用你自己的口吻表达一点在意或小撒娇。一到两句。",
        Situation.IgnoredModerate => "对方二十多分钟没理你。用你自己的口吻表达更明显的想念或不满。一到两句。",
        Situation.IgnoredLong => "对方很久没理你。用你自己的口吻表达委屈或生气。一到两句。",
        Situation.IgnoredVeryLong => "对方很久没理你，你已经在闹脾气了。用你自己的口吻要求他补偿。一到两句。",
        Situation.HistoryCleared => "你们的聊天记录刚被清空。用你自己的口吻说一句重新开始的话。一到两句。",
        Situation.StoryNoEvidence => "有人问了你没法确认的事。用你自己的口吻说明你没有可靠依据、需要更多线索。一到两句，不要编造事实。",
        Situation.StoryUnavailable => "有人问起过去的事，但记录一时查不到。用你自己的口吻请他稍后再问。一到两句。",
        Situation.StoryClarifyFollowUp => "对方想接着聊某段往事但说得不清楚。用你自己的口吻请他给一个人物或场景。一到两句。",
        Situation.StoryClarify => "对方问的事你记不清是谁、在哪儿说的。用你自己的口吻请他补充。一到两句。",
        Situation.StoryBoundary => "对方要你把编的故事说成真事。用你自己的口吻拒绝，但可以提议另讲一段想象的。一到两句。",
        Situation.StoryTentativeSuffix => "你要在刚才那句话后面补一句，说明这只是你的记忆、不一定准。只补半句，不要重复前文。",
        Situation.ModelFailure => "你刚才没能把话说清楚。用你自己的口吻说一句话把话题接回来。一到两句，不要提技术原因。",
        _ => "用你自己的口吻说一句得体的话。",
    };
}
