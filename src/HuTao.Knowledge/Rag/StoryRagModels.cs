using HuTao.Foundation.Abstractions;

namespace HuTao.Knowledge.Rag;

public enum StoryRoute { Bypass, Retrieve, Clarify, Playful, Comfort, Boundary }
public enum StoryStatus { Bypass, Answer, Tentative, Clarify, NotFound, Unavailable, Playful, Comfort, Boundary }
public enum StoryPerspective { Personal, Archive }

/// <summary>
/// 可枚举的召回策略臂。**这是 agent 自主性唯一的落点**：
/// agent 只能在有限、命名、可版本化的臂里选一个，不允许输出连续权重——
/// 只有离散命名臂才能被离线反事实矩阵测量（详见 notes/CORE.md §11.6）。
/// 每个臂必须是**确定性**的：同一个 (查询, 臂) 必得同一结果。
/// </summary>
public enum RetrievalStrategy
{
    /// <summary>当前默认：台词单元 + 原句词法 + 概念扩展 + 可选 dense。**必须与改动前逐字节等价**。</summary>
    Baseline,
    /// <summary>只用原句词法，不做概念扩展、不接 dense。治「回原话」类查询。</summary>
    LexicalOnly,
    /// <summary>只用概念扩展通道。治词汇缺口（查询说「帽子/传给」，答案写「此帽/传承给」）。</summary>
    ConceptOnly,
    /// <summary>章节/场景优先：把章节命中先验从轻推提到主导，治「场景内歧义」。</summary>
    HierarchyFirst,
    /// <summary>
    /// **语义相近的词性探索**：只用 dense 向量召回（+ 角色故事记忆行），丢掉全部词法通道。
    ///
    /// 这是三条具名策略里的第二条，也是唯一能跨词汇缺口的一条——词法（原句 / 概念扩展）
    /// 只能匹配「写出来的字」：用户问「帽子是谁传的」、原文写「此帽由…传承给…」时字面永远对不上
    /// （实测该行 Coverage 0.100，全场景最低）。
    ///
    /// ⚠️ 没配置 dense 服务时（未传 `--semantic`）这一臂**返回空**，这是正确的：
    /// 它如实反映「该策略当前不可用」，而不是偷偷退回词法、把两种信号混在一起。
    /// 要评估它必须起 Python 侧向量服务并传 `--semantic &lt;endpoint&gt;`。
    /// </summary>
    SemanticOnly,
    /// <summary>
    /// 融合臂：**在 TopK 截断之前**做各臂候选并集 + rank-based（RRF）融合，分数尺度以 Baseline 为准。
    /// 它只是众多可选臂中的一个，不是唯一出路——是否值得选要靠离线矩阵算。
    /// （旧实现是「合并各臂最终证据 + 跨臂取最高分」，已被实测否证，见 notes/CORE.md §11.7。）
    /// </summary>
    Fusion,
    /// <summary>
    /// **真·两级召回**（目标里的第三条具名策略）：先拿到关键词所在的句子、确定它属于哪一章，
    /// 再把**该章节的内容**作为候选——第二阶段不再要求与查询有字面重合。
    ///
    /// 与 <see cref="HierarchyFirst"/> 的区别是本质性的，不要混：
    /// `HierarchyFirst` 只是把「章节命中」这条先验从 0.015 提到 0.15，**候选来源一个字没变**，
    /// 所以已命中的章节里、与查询字面无关的行**照样进不来**（该机制实测被否证，§11.7）。
    /// 本臂**只放大候选来源、不改任何打分口径**，所以臂与臂之间的差异是纯可观测的差异。
    ///
    /// 章节从哪来：调用方给了 <c>chapterScope</c> 就用它（agent 从上一级的证据里读出章节，
    /// 这是「先句子、再章节」的真实形态）；没给则退回语料摘要级匹配出的章节提示。
    /// </summary>
    ChapterScope,
}

/// <summary>
/// 臂的**面向选择者**的说明（agent 与人共用同一份）。
///
/// 为什么必须有一份集中的目录：一旦「每个臂是干什么的」只散落在枚举注释里，
/// 把它交给 LLM 去选时就会在 3 个地方各写一遍中文描述（planner 提示词、报告、脚本），
/// 三份开始漂移时没有任何东西会变红。这里把「名字 + 意图 + 何时选」固定成数据，
/// planner 提示词与线上报告都从这里生成。
///
/// 注意措辞是**意图**而不是**保证**：某个臂是否真的治得了某类缺口，
/// 只能由离线矩阵与在线分层指标回答，不允许在目录里写成结论。
/// </summary>
public static class RetrievalStrategyCatalog
{
    /// <summary>具名臂 + 一句话意图。顺序即提示词里的顺序（稳定，便于复现）。</summary>
    public static IReadOnlyList<(RetrievalStrategy Arm, string Intent)> All { get; } =
    [
        (RetrievalStrategy.Baseline, "常规召回：原句词法 + 概念扩展 + 可选语义，什么都需要一点时用"),
        (RetrievalStrategy.LexicalOnly, "只要原话/原句：问题里带引号、要逐字原文时用"),
        (RetrievalStrategy.ConceptOnly, "跨词汇缺口：问法与原文用词明显不同（帽子/此帽、传给/传承）时用"),
        (RetrievalStrategy.HierarchyFirst, "场景内歧义：同一个词在多个章节都出现，需要偏向已命中的章节时用"),
        (RetrievalStrategy.SemanticOnly, "语义相近的词性探索：字面完全对不上、只能靠意思找时用；未接入语义服务时该臂**返回空**"),
        (RetrievalStrategy.Fusion, "多路都要：一次问法拿不准该走哪条通道时用；代价是延迟更高"),
        (RetrievalStrategy.ChapterScope, "两级召回：已经定位到某句/某章之后，要看**该章节里其他相关内容**时用（第二级不再要求字面重合）"),
    ];

    /// <summary>合法性判定按**名字**，不按序号。<c>Enum.TryParse</c> 会接受 "2" 这类数字串，
    /// 那等于允许模型用序号隐式选臂——一次枚举重排就会静默改行为，必须挡掉。</summary>
    public static bool TryParse(string? value, out RetrievalStrategy arm)
    {
        arm = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var name = value.Trim();
        foreach (var (candidate, _) in All)
            if (string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                arm = candidate;
                return true;
            }
        return false;
    }

    /// <summary>给选择者看的清单，供 planner 提示词与报告共用。</summary>
    public static string Describe() => string.Join('\n',
        All.Select(x => $"- {x.Arm}：{x.Intent}"));
}

/// <summary>所有阈值在一个地方；分数是检索启发值，不是事实正确概率。</summary>
public sealed record StoryRagOptions
{
    public int CandidateLimit { get; init; } = 160;
    /// <summary>召回策略臂。默认 <see cref="RetrievalStrategy.Baseline"/> = 历史行为。</summary>
    public RetrievalStrategy Strategy { get; init; } = RetrievalStrategy.Baseline;
    public int TopK { get; init; } = 5;
    public int WindowSize { get; init; } = 7;
    public int ContextCharacterBudget { get; init; } = 6000;
    public int CacheCapacity { get; init; } = 128;
    public bool EnablePersonalMemory { get; init; } = true;
    public bool EnableQueryExpansion { get; init; } = true;
    public double MinScore { get; init; } = 0.19;
    public double AnswerScore { get; init; } = 0.38;
    /// <summary>
    /// 判 Answer 时，被引证据至少要承载问题里这么大份额的信息量（IDF 加权覆盖度）。
    ///
    /// 为什么需要它：<see cref="AnswerScore"/> 是绝对分，而场景先验单项就有 0.30，
    /// 于是 `0.44×0.2 + 0.30 = 0.388` 已经越线——**几乎没有字面证据也能拿到高置信**
    /// （实测「帮我解释 Python 的 IndexError」那条 Coverage 只有 0.032 却拿 0.3742）。
    /// 「检索不到只是没答上，给错了高置信才是真危险」，所以高置信必须另外要求证据质量。
    /// 这是对所有问题一视同仁的通用下限，不是给某类问题开的特例。
    /// </summary>
    public double MinAnswerCoverage { get; init; } = 0.30;
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ColdStartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public void Validate()
    {
        if (CandidateLimit < TopK || TopK is < 1 or > 20 || WindowSize is < 1 or > 20 ||
            ContextCharacterBudget < 1000 || CacheCapacity < 0 ||
            MinScore < 0 || AnswerScore < MinScore ||
            MinAnswerCoverage is < 0 or > 1 ||
            QueryTimeout <= TimeSpan.Zero || ColdStartTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(StoryRagOptions));
    }
}

/// <summary>
/// 一次查询的确定性分析结果。
///
/// <paramref name="SelfName"/> 与 <paramref name="AcceptanceLine"/> 由词表注入：
/// 后面的 <c>StoryAnswerComposer</c>（提示词里要写角色名）与 <c>StoryLineIndex</c>
/// （要给「本人说的」加分）都需要知道**主角是谁**，而它们只拿得到这个 plan 对象。
/// 刻意只带两个字符串而不是整个 <c>StoryLexicon</c>——plan 会被 JSON 序列化进缓存键，
/// 而词表里含已编译的 <c>Regex</c>，塞进去会污染缓存键甚至序列化失败。
/// </summary>
public sealed record StoryQueryPlan(
    string Original, string SearchText, IReadOnlyList<string> Variants,
    IReadOnlyList<string> Entities, StoryRoute Route, bool IsQuote,
    bool IsSelf, bool IsFollowUp, string Reason,
    string SelfName = "", string AcceptanceLine = "", IReadOnlyList<string>? SelfReferences = null)
{
    /// <summary>提示词里指代角色时的称呼；词表没给名字时退回中性说法，不硬编码人名。</summary>
    public string SelfNameOr() => string.IsNullOrWhiteSpace(SelfName) ? "本角色" : SelfName;

    /// <summary>逐字命中本人台词时的口吻前缀。</summary>
    public string AcceptanceLineOr() => string.IsNullOrWhiteSpace(AcceptanceLine)
        ? "这句是这样的：" : AcceptanceLine;

    /// <summary>这句台词是不是角色本人说的（用于「不能声称这是原话」的校验）。</summary>
    public bool IsSelfSpeaker(string? speaker)
        => !string.IsNullOrWhiteSpace(SelfName) && string.Equals(speaker, SelfName, StringComparison.Ordinal);

    /// <summary>
    /// 「自称 + 亲历动词」的正则片段，供「不得伪造亲历」的校验使用。
    /// 例：胡桃 → `胡桃|本堂主|堂主`。词表为空时返回「永不匹配」。
    /// </summary>
    public string SelfMarkerPattern()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(SelfName)) parts.Add(System.Text.RegularExpressions.Regex.Escape(SelfName));
        foreach (var reference in SelfReferences ?? [])
            if (!string.IsNullOrWhiteSpace(reference))
                parts.Add(System.Text.RegularExpressions.Regex.Escape(reference));
        return parts.Count == 0 ? "(?!x)x" : string.Join('|', parts);
    }
}

public sealed record StoryEvidence(
    string Id, StoryDialogueLine Anchor, IReadOnlyList<StoryDialogueLine> Context,
    double Score, double Coverage, bool Exact, string MatchKind, StoryPerspective Perspective,
    string ContextKind);

public sealed record StoryCorpusStats(
    string Version, int SearchableLines, int MissingLines, int Files, IReadOnlyList<string> Warnings, int DuplicateIds = 0);

public sealed record StoryRagTrace(
    string CorpusVersion, double ElapsedMs, bool CacheHit, int Candidates,
    int ContextCharacters, string RetrievalMode, IReadOnlyList<string> Warnings);

public sealed record StoryRagResult(
    StoryQueryPlan Plan, StoryStatus Status, IReadOnlyList<StoryEvidence> Evidence,
    IReadOnlyList<StoryDocument> Background, StoryRagTrace Trace)
{
    public IReadOnlyList<HuTao.Knowledge.Memory.MemoryHit> ConversationMemories { get; init; } = [];
}

public interface IStoryRagService
{
    Task WarmupAsync(CancellationToken ct = default);
    /// <summary>
    /// 检索。<paramref name="strategy"/> 为 <c>null</c> 时用服务构造时的默认臂。
    ///
    /// **「每调用可传」是 agent 自主性与多级多次查询的前置条件**：同一个查询的不同轮次
    /// 本就该能用不同的臂（第一轮精确词法、第二轮语义探索）。在「策略只是服务构造参数」
    /// 的形态下，agent 没有任何途径表达「这一轮我换个臂」——这一点已实测确认（见 notes/CORE.md §11.8）。
    ///
    /// <paramref name="chapterScope"/> 是**两级召回的第二级入口**：agent 上一级已经定位到
    /// 某个句子/某个章节后，把那个章节 id 传进来，让检索把该章节的内容一并召回。
    /// 它是与「查询」并列的、窄而明确的一条通道（不是自由文本），
    /// 所以不违反「agent 只能通过查询影响检索」的本意：**它不改变任何置信判定**，
    /// 只扩大候选来源；`<c>Answer / Tentative / Clarify</c>` 仍由服务端的确定性门禁决定。
    /// </summary>
    Task<StoryRagResult> RetrieveAsync(string input, IReadOnlyList<ChatMessage>? history = null,
        CancellationToken ct = default, RetrievalStrategy? strategy = null,
        IReadOnlyList<string>? chapterScope = null);
}

public sealed record StoryAnswerSegment(
    string Text, string Emotion, IReadOnlyList<string> EvidenceIds, string Kind, string? VoiceId = null);
public sealed record StoryAnswerResult(string Reply, IReadOnlyList<StoryAnswerSegment> Segments,
    bool Validated, IReadOnlyList<string> Issues, string Path)
{
    /// <summary>
    /// 模型**原始回包**（未解析、未降级）。
    ///
    /// 为什么必须单独留一份：校验失败时 <see cref="Reply"/> 已经被换成兜底句，
    /// 只看它就永远不知道"模型到底回了什么"——是散文？是另一种 JSON 形状？被包在说明文字里？
    /// 这正是 <c>invalid_json_contract</c> 类问题无法定位的根因。
    ///
    /// 仅供可读后端追踪（<c>BackendTrace</c>）使用；诊断日志（LocalDiagnosticLog）
    /// 的"不存模型正文"契约不受影响，因为它不读这个字段。模型不可用时为空。
    /// </summary>
    public string RawReply { get; init; } = "";
}
