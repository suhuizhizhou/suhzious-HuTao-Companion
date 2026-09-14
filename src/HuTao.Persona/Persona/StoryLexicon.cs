using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HuTao.Persona;

/// <summary>
/// 剧情检索的词表。**这是「RAG 与具体角色解耦」的落点。**
///
/// 背景：`StoryQueryAnalyzer` 原本把「胡桃 / 本堂主 / 桃桃 / 胡堂主 / 大咪 / 二咪 /
/// 钟离 / 七七 / 白术 …」「原神 / 提瓦特 / 璃月 / 往生堂 …」这些词**写死在正则里**。
/// 结果是这套检索只对原神角色好使——异环角色挂上自己的语料后，带剧情标记能检索，
/// 但自然语言提问（「安魂曲是谁」）永远路由不到，因为实体表和别名表是原神的。
///
/// 拆分原则：
/// - **引擎留在代码里**：疑问句式（`第.+代`、`我.{0,4}(主题).{0,6}(丢|不吃|坏)`）、
///   通用时间词（当时/以前/曾经）、追问词（然后呢/那后来）。这些是中文问句的形态，与角色无关。
/// - **词汇进词表**：自称、别名、实体名、作品专属词、与角色密切相关的主题、同义词组。
///
/// 两张词表在运行时合并：
/// - `data/persona/&lt;id&gt;/lexicon.json` —— **角色**词表（自称、别名、私人话题、同义词组）
/// - `&lt;语料根&gt;/lexicon.json` —— **语料**词表（这部作品里有哪些实体、有哪些专属词）
///
/// 分开的理由很实际：实体表（钟离/七七/白术…）是**语料**的属性，不是胡桃的属性——
/// 换角色不换语料时它不该变。
/// </summary>
public sealed class StoryLexicon
{
    /// <summary>角色本人的名字，用于给召回查询加权、判断「这句是不是她说的」。</summary>
    [JsonPropertyName("self_name")] public string SelfName { get; set; } = "";

    /// <summary>
    /// 作品名（原神 / 异环…）。属于**语料**侧：它只用来生成给模型看的工具说明
    /// （「翻阅有出处的&lt;作品&gt;台词」），写死会让新作品的检索工具在提示词里显得莫名其妙。
    /// </summary>
    [JsonPropertyName("work_name")] public string WorkName { get; set; } = "";

    /// <summary>自称与昵称（本堂主 / 桃桃 / 胡堂主 / 大咪 / 二咪）。命中即认为用户在说她。</summary>
    [JsonPropertyName("self_references")] public List<string> SelfReferences { get; set; } = [];

    /// <summary>
    /// 自称识别需要的**额外正则片段**（可选）。
    ///
    /// 这是词表里唯一允许出现正则的地方，用得很克制，理由说清楚：
    /// 胡桃原来有一条 `堂主[，,]`——只在「堂主，」这种称呼后跟逗号时才认作自称，
    /// 目的是不把「第七十五代堂主是谁」这种**询问**当成她在自称。
    /// 用纯字面量表达不了这个「后跟逗号」的条件。
    ///
    /// 如果直接把它简化成字面量 `堂主`，100 场景路由回归里那批「第 N 代堂主」问句
    /// 会从 Bypass 翻成 Retrieve——那是**静默改变行为**，比多一个字段危险得多。
    /// 所以宁可留一个显式的逃生口，并要求：**只写这一条例外，不要拿它当通用配置。**
    /// </summary>
    [JsonPropertyName("self_pattern_extra")] public string SelfPatternExtra { get; set; } = "";

    /// <summary>别名 → 正式名。用户在问「胡堂主」时按「胡桃」检索。</summary>
    [JsonPropertyName("aliases")] public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.Ordinal);

    /// <summary>语料里出现过的实体名。用来判断「这是不是在问剧情」。</summary>
    [JsonPropertyName("entities")] public List<string> Entities { get; set; } = [];

    /// <summary>作品专属词（原神 / 提瓦特 / 璃月 / 往生堂 / 神之眼 …）。命中即走剧情检索。</summary>
    [JsonPropertyName("story_markers")] public List<string> StoryMarkers { get; set; } = [];

    /// <summary>与角色身世密切相关的主题（爷爷 / 帽子 / 石狮 / 梅花 / 生死 …）。</summary>
    [JsonPropertyName("personal_topics")] public List<string> PersonalTopics { get; set; } = [];

    /// <summary>只有用户本人会用的私人说法（我的帽子 / 我养的宠物），用来识别「他在聊自己的事」。</summary>
    [JsonPropertyName("intimate_topics")] public List<string> IntimateTopics { get; set; } = [];

    /// <summary>同义词组，用于查询扩展（爷爷/祖父/胡老/老胡头 是一组）。</summary>
    [JsonPropertyName("concept_groups")] public List<List<string>> ConceptGroups { get; set; } = [];

    /// <summary>要额外从查询里剥掉的角色专属填充词（本堂主 / 胡桃）。</summary>
    [JsonPropertyName("self_stop_words")] public List<string> SelfStopWords { get; set; } = [];

    /// <summary>
    /// 角色个人档案在语料里的相对路径（如 `character/hutao.json`）。
    /// 留空表示这部语料没有「角色档案」这个来源。
    /// </summary>
    [JsonPropertyName("character_story_file")] public string CharacterStoryFile { get; set; } = "";

    /// <summary>
    /// 角色档案在索引里的章节名。会参与场景键（SceneKey）的组成，所以**改名会改变上下文窗口的切分**。
    ///
    /// 保留这个字段而不是在代码里写死，是因为它必须**可配**：把某角色的档案并到别的章节名下，
    /// 是语料作者会做的事。留空则用中性默认值 `character-story`。
    /// </summary>
    [JsonPropertyName("character_chapter_id")] public string CharacterChapterId { get; set; } = "";

    /// <summary>逐字命中自己台词时的口吻前缀（提醒模型这是她自己的原话）。</summary>
    [JsonPropertyName("acceptance_line")] public string AcceptanceLine { get; set; } = "";

    /// <summary>没有任何词表时的中性兜底：引擎照常工作，只是不做角色/作品专属判断。</summary>
    public static StoryLexicon Empty { get; } = new();

    public bool HasAnyVocabulary =>
        SelfReferences.Count > 0 || Entities.Count > 0 || StoryMarkers.Count > 0 ||
        PersonalTopics.Count > 0 || ConceptGroups.Count > 0;

    /// <summary>
    /// 合并两张词表。**角色词表优先**：同名字段两边都有时，角色侧的排前面（正则按长度排序，不靠顺序，但去重时保留先出现的）。
    /// </summary>
    public static StoryLexicon Merge(StoryLexicon? persona, StoryLexicon? corpus)
    {
        if (persona is null) return corpus ?? Empty;
        if (corpus is null) return persona;

        var aliases = new Dictionary<string, string>(corpus.Aliases, StringComparer.Ordinal);
        foreach (var (key, value) in persona.Aliases)
            aliases[key] = value;

        return new StoryLexicon
        {
            SelfName = string.IsNullOrWhiteSpace(persona.SelfName) ? corpus.SelfName : persona.SelfName,
            WorkName = string.IsNullOrWhiteSpace(persona.WorkName) ? corpus.WorkName : persona.WorkName,
            SelfReferences = Union(persona.SelfReferences, corpus.SelfReferences),
            SelfPatternExtra = string.IsNullOrWhiteSpace(persona.SelfPatternExtra)
                ? corpus.SelfPatternExtra : persona.SelfPatternExtra,
            Aliases = aliases,
            Entities = Union(persona.Entities, corpus.Entities),
            StoryMarkers = Union(persona.StoryMarkers, corpus.StoryMarkers),
            PersonalTopics = Union(persona.PersonalTopics, corpus.PersonalTopics),
            IntimateTopics = Union(persona.IntimateTopics, corpus.IntimateTopics),
            ConceptGroups = [.. persona.ConceptGroups, .. corpus.ConceptGroups],
            SelfStopWords = Union(persona.SelfStopWords, corpus.SelfStopWords),
            CharacterStoryFile = string.IsNullOrWhiteSpace(persona.CharacterStoryFile)
                ? corpus.CharacterStoryFile : persona.CharacterStoryFile,
            CharacterChapterId = string.IsNullOrWhiteSpace(persona.CharacterChapterId)
                ? corpus.CharacterChapterId : persona.CharacterChapterId,
            AcceptanceLine = string.IsNullOrWhiteSpace(persona.AcceptanceLine)
                ? corpus.AcceptanceLine : persona.AcceptanceLine,
        };
    }

    private static List<string> Union(IEnumerable<string> first, IEnumerable<string> second)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in first.Concat(second))
            if (!string.IsNullOrWhiteSpace(item) && seen.Add(item))
                result.Add(item);
        return result;
    }

    /// <summary>读词表文件；文件不存在或格式不对都返回 null（词表是可选的，缺了只是功能退化）。</summary>
    public static StoryLexicon? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<StoryLexicon>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>语料根目录下的词表约定位置。</summary>
    public static string CorpusLexiconPath(string corpusRoot) => Path.Combine(corpusRoot, "lexicon.json");

    // ── 由词表编译出的正则（惰性，只编译一次）──────────────────────────────
    //
    // 全部用 Regex.Escape 转义后拼装，避免词表里的字符被当成正则元字符。
    // 按长度降序排列，保证「胡堂主」先于「胡桃」匹配。

    private Regex? _selfPattern;
    private Regex? _storyPattern;
    private Regex? _personalPattern;

    /// <summary>
    /// 命中即认为用户在说角色本人。
    ///
    /// ⚠️ **必须包含 <see cref="SelfName"/> 本身**：原引擎的正则是
    /// `胡桃|本堂主|堂主[，,]|你|大咪|二咪`，第一个就是角色名。
    /// 只拼 <see cref="SelfReferences"/> 会漏掉最常出现的那个词——
    /// 症状是路由准确率从 100% 掉到 80%，而且两个结构用例会挂
    /// （「她几岁」不再被当成在问她，闲聊也不再走 Bypass），
    /// 看起来像检索退化，其实是词表少了一项。
    /// </summary>
    public Regex SelfPattern => _selfPattern ??=
        Build(SelfName.Length > 0 ? [SelfName, .. SelfReferences] : SelfReferences,
              suffix: "|你", extra: SelfPatternExtra);

    /// <summary>
    /// 命中即认为在问剧情/档案。**只有作品专属词与剧情标记，不含 <see cref="Entities"/>。**
    ///
    /// 这一条区分得很细，别顺手把 Entities 并进来：原引擎的 `explicitStory` 里
    /// 有「白术/七七/行秋/重云/钟离」这些**剧情相关的 NPC**，但**没有「胡桃」**——
    /// 因为「胡桃早安」这种日常招呼不该被当成在问剧情。
    /// 把 Entities（含角色本人的名字）并进来，会让 `react_smalltalk_one_call` 直接挂：
    /// 「胡桃早安」被判成 Retrieve 而不是 Bypass。
    ///
    /// 「剧情/档案/台词/原文」这类与作品无关的标记由引擎补上，不在这里重复。
    /// </summary>
    public Regex StoryPattern => _storyPattern ??= Build(StoryMarkers);

    /// <summary>命中即认为在问角色身世；结构部分（第.+代、当时、以前）留在引擎里。</summary>
    public Regex PersonalPattern => _personalPattern ??= Build(PersonalTopics);

    /// <summary>「我的帽子 / 我养的宠物」这类只有用户本人会用的说法。</summary>
    public Regex IntimatePattern => _intimatePattern ??= Build(IntimateTopics);
    private Regex? _intimatePattern;

    /// <summary>要从查询里剥掉的角色专属填充词。</summary>
    public Regex StopWordPattern => _stopWordPattern ??= Build(SelfStopWords);
    private Regex? _stopWordPattern;

    /// <summary>把词表拼成正则；空词表返回「永不匹配」而不是「匹配一切」。</summary>
    private static Regex Build(IEnumerable<string> terms, string suffix = "", string extra = "")
    {
        var parts = terms
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(t => t.Length)
            .Select(Regex.Escape)
            .ToList();
        if (suffix.Length > 0)
            parts.Add(suffix.TrimStart('|'));
        if (!string.IsNullOrWhiteSpace(extra))
            parts.Add(extra);
        if (parts.Count == 0)
            return NeverMatch;
        return new Regex(string.Join('|', parts), RegexOptions.Compiled);
    }

    /// <summary>空词表用的「永不匹配」。用不可能的字符序列，避免误伤正常输入。</summary>
    private static readonly Regex NeverMatch = new(@"(?!x)x", RegexOptions.Compiled);

    /// <summary>把一个词表拼成 `(a|b|c)` 分组，供引擎嵌入更大的句式正则。</summary>
    public static string Group(IEnumerable<string> terms)
    {
        var parts = terms
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(t => t.Length)
            .Select(Regex.Escape)
            .ToList();
        return parts.Count == 0 ? "(?!x)x" : "(" + string.Join('|', parts) + ")";
    }

    /// <summary>把别名按「长词优先」替换成正式名。</summary>
    public string ApplyAliases(string text)
    {
        foreach (var (alias, name) in Aliases.OrderByDescending(kv => kv.Key.Length))
            if (!string.IsNullOrWhiteSpace(alias))
                text = text.Replace(alias, name, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>
    /// 这句台词是不是角色本人说的。用于给「本人说的原话」加分、以及判断
    /// 「不能声称这是她说的原话」。
    /// </summary>
    public bool IsSelfSpeaker(string? speaker)
        => !string.IsNullOrWhiteSpace(SelfName) &&
           string.Equals(speaker, SelfName, StringComparison.Ordinal);

    /// <summary>提示词里指代角色时的称呼；词表没写名字时退回中性说法，不硬编码人名。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(SelfName) ? "本角色" : SelfName;

    /// <summary>
    /// 「记住…/忘掉…」这类指令允许出现在前面的称呼。
    /// 角色名始终算一个（最容易想到的说法），再加词表里的自称与别名。
    /// </summary>
    public IReadOnlyList<string> ImportantMemoryWakeWords(string? fallbackName = null)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value)) result.Add(value);
        }
        Add(SelfName);
        Add(fallbackName);
        foreach (var reference in SelfReferences) Add(reference);
        foreach (var alias in Aliases.Keys) Add(alias);
        return result;
    }

    /// <summary>
    /// 词表没写 <see cref="SelfName"/> 时，用角色的名字兜底。
    ///
    /// 为什么需要：角色名是**最常出现的自称**，缺了它「胡桃早安」这类输入会从
    /// 闲聊（Bypass）翻成剧情检索，然后返回 NotFound。测试里手搭的
    /// <c>PersonaProfile</c> 不带词表文件，正好会踩到这个坑。
    /// 有一份能用的词表时原样返回，不做任何覆盖。
    /// </summary>
    public StoryLexicon WithSelfNameFallback(string? name)
    {
        if (!string.IsNullOrWhiteSpace(SelfName) || string.IsNullOrWhiteSpace(name))
            return this;
        return new StoryLexicon
        {
            SelfName = name,
            WorkName = WorkName,
            SelfReferences = SelfReferences,
            SelfPatternExtra = SelfPatternExtra,
            Aliases = Aliases,
            Entities = Entities,
            StoryMarkers = StoryMarkers,
            PersonalTopics = PersonalTopics,
            IntimateTopics = IntimateTopics,
            ConceptGroups = ConceptGroups,
            SelfStopWords = SelfStopWords,
            CharacterStoryFile = CharacterStoryFile,
            CharacterChapterId = CharacterChapterId,
            AcceptanceLine = AcceptanceLine,
        };
    }
}
