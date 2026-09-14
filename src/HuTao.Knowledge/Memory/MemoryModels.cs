using System.Text.Json;
using System.Text.Json.Serialization;

namespace HuTao.Knowledge.Memory;

/// <summary>记忆的类型。事实类可失效，情节类保留语气，承诺类需要跨轮兑现。</summary>
public enum MemoryKind
{
    /// <summary>原始对话轮次，未经提炼。检索的主力来源，零 LLM 成本。</summary>
    Turn,
    /// <summary>从多轮里提炼出的离散事实（「用户在做桌宠项目」）。</summary>
    Fact,
    /// <summary>用户的稳定偏好（「用户喜欢深夜写代码」）。</summary>
    Preference,
    /// <summary>角色或用户许下的承诺（「答应过要提醒他交论文」）。</summary>
    Commitment,
    /// <summary>有场景的一段经历（保留原话与情绪）。</summary>
    Episode,
}

/// <summary>
/// 一条长期记忆。
///
/// 双时态设计（借鉴 Zep/Graphiti 的思路）：
/// - <see cref="ObservedAt"/> 是事件发生时间；
/// - <see cref="RecordedAt"/> 是系统写入时间。
/// 两者分开，才能在「这条当时成立吗」和「我们什么时候知道的」之间作答。
///
/// 失效而不是删除：<see cref="ValidUntil"/> 到期或 <see cref="SupersededById"/> 非空时，
/// 记录仍然留在磁盘上，只是不再参与常规检索。
/// </summary>
public sealed class MemoryRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public MemoryKind Kind { get; set; } = MemoryKind.Turn;
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    /// <summary>user / assistant / consolidation。</summary>
    [JsonPropertyName("speaker")] public string Speaker { get; set; } = "";
    [JsonPropertyName("observed_at")] public DateTimeOffset ObservedAt { get; set; }
    [JsonPropertyName("recorded_at")] public DateTimeOffset RecordedAt { get; set; }
    /// <summary>推断或显式声明的失效时间；null 表示长期有效。</summary>
    [JsonPropertyName("valid_until")] public DateTimeOffset? ValidUntil { get; set; }
    /// <summary>被哪条新记忆取代。非空即视为已失效，但记录保留。</summary>
    [JsonPropertyName("superseded_by")] public string? SupersededById { get; set; }
    [JsonPropertyName("importance")] public double Importance { get; set; }
    /// <summary>提炼来源的可追溯 id（记忆里的来源思想：能说清「为什么我记得这个」）。</summary>
    [JsonPropertyName("sources")] public List<string> Sources { get; set; } = [];
    [JsonPropertyName("access_count")] public int AccessCount { get; set; }
    [JsonPropertyName("last_accessed_at")] public DateTimeOffset? LastAccessedAt { get; set; }
    /// <summary>去重键：说话人 + 归一化文本。</summary>
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";
    /// <summary>被哪次整合处理过；null 表示尚未提炼。</summary>
    [JsonPropertyName("consolidated_at")] public DateTimeOffset? ConsolidatedAt { get; set; }

    [JsonIgnore] public bool Superseded => !string.IsNullOrEmpty(SupersededById);
}

/// <summary>一次记忆检索的输入。ContextTexts 用来补足「用户这句话没说全」的话题。</summary>
public sealed record MemoryQuery(
    string? UserInput,
    IReadOnlyList<string> ContextTexts,
    DateTimeOffset Now,
    /// <summary>当前对话窗口里已经有的内容指纹，检索结果要排除，避免重复注入。</summary>
    IReadOnlySet<string>? ExcludeFingerprints = null,
    int MaxResults = 4,
    bool IncludeExpired = true);

/// <summary>一条召回结果，附带可解释的打分拆解，便于诊断和回归。</summary>
public sealed record MemoryHit(
    MemoryRecord Record,
    double Score,
    double Relevance,
    double Recency,
    double ImportanceScore,
    bool Expired,
    string Reason);

/// <summary>记忆层配置。默认全开；阶段 4 需要 LLM，可单独关闭。</summary>
public sealed record MemoryOptions
{
    /// <summary>
    /// 相关度权重。刻意给到 0.65：新鲜度对任何刚写入的记录都是满分，
    /// 若相关度和新鲜度等权，一条只是「碰巧最近提过、话题勉强沾边」的记忆
    /// 会压过真正精确命中的旧记忆。相关度必须先赢，新鲜度只在相关度接近时起作用。
    /// </summary>
    public double RelevanceWeight { get; init; } = 0.65;
    public double RecencyWeight { get; init; } = 0.20;
    public double ImportanceWeight { get; init; } = 0.15;
    /// <summary>新鲜度半衰期：越久越接近 0。</summary>
    public TimeSpan RecencyHalfLife { get; init; } = TimeSpan.FromDays(7);
    /// <summary>
    /// 相关度门槛：IDF 加权的查询覆盖率下限（命中内容词 idf ÷ 全部内容词 idf）。
    /// 低于此值的候选不进提示词，避免用不相关的旧事硬凑话题。
    /// </summary>
    public double MinScore { get; init; } = 0.18;
    /// <summary>活跃记录上限；超出部分归档而不是删除。</summary>
    public int MaxActiveRecords { get; init; } = 20_000;
    /// <summary>阶段 4：后台提炼。</summary>
    public bool EnableConsolidation { get; init; } = true;
    /// <summary>触发整合所需的最少未提炼轮次。</summary>
    public int MinTurnsToConsolidate { get; init; } = 8;
    /// <summary>触发整合所需的空闲时长。</summary>
    public TimeSpan ConsolidationIdle { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan ConsolidationCooldown { get; init; } = TimeSpan.FromMinutes(20);

    public bool Enabled { get; init; } = true;

    public void Validate()
    {
        if (RelevanceWeight < 0 || RecencyWeight < 0 || ImportanceWeight < 0 ||
            RelevanceWeight + RecencyWeight + ImportanceWeight <= 0 ||
            RecencyHalfLife <= TimeSpan.Zero || MaxActiveRecords < 100 ||
            MinTurnsToConsolidate < 1)
            throw new ArgumentOutOfRangeException(nameof(MemoryOptions));
    }

    /// <summary>HU_TAO_MEMORY=off 整体关闭；HU_TAO_MEMORY_CONSOLIDATE=false 只关阶段 4。</summary>
    public static MemoryOptions FromEnvironment()
    {
        if (!ReadFlag("HU_TAO_MEMORY", true))
            return new MemoryOptions { Enabled = false, EnableConsolidation = false };

        var halfLifeDays = 7.0;
        var configured = Environment.GetEnvironmentVariable("HU_TAO_MEMORY_HALFLIFE_DAYS")?.Trim();
        if (!string.IsNullOrEmpty(configured) &&
            double.TryParse(configured, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
            halfLifeDays = parsed;

        var options = new MemoryOptions
        {
            EnableConsolidation = ReadFlag("HU_TAO_MEMORY_CONSOLIDATE", true),
            RecencyHalfLife = TimeSpan.FromDays(halfLifeDays),
        };
        options.Validate();
        return options;
    }

    private static bool ReadFlag(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        if (string.IsNullOrEmpty(value))
            return defaultValue;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>记忆文件的落盘结构。单独包一层是为了携带导入游标等簿记信息。</summary>
public sealed class MemoryFileState
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    /// <summary>已经从旧聊天记录里导入到第几条，避免每次启动重复导入。</summary>
    [JsonPropertyName("imported_chat_entries")] public int ImportedChatEntries { get; set; }
    [JsonPropertyName("last_consolidated_at")] public DateTimeOffset? LastConsolidatedAt { get; set; }
    [JsonPropertyName("records")] public List<MemoryRecord> Records { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
