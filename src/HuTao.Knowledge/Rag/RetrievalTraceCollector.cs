using HuTao.Foundation.Diagnostics;

namespace HuTao.Knowledge.Rag;

/// <summary>
/// 一次检索的**全部中间量**收集器。
///
/// 为什么要有它：追踪日志原先只记「结果的一部分」——前 N 条证据、前 4 条短变体，
/// 而**过程**（各通道各出了多少候选、每条候选的分数由哪几项构成、谁被 MinScore 挡掉、
/// 谁被去重规则吃掉、TopK 在哪一步截断、锚点有没有被重选、上下文有没有被字符预算裁掉、
/// 充分性策略最后给了什么结论）**一条都没记**。于是日志只能回答「找到了什么」，
/// 回答不了「为什么是它 / 为什么没找到」——而后者才是排查检索问题的全部价值。
///
/// 设计约束：
/// - **传参而非全局**：`Retrieve(..., RetrievalTraceCollector? trace = null)`，
///   不开追踪时是 null，所有写入点都被 `?.` 短路，**零成本、零行为变化**。
/// - 每一段都是**人话字符串**，不做结构化建模：这份东西的用途是给人读，
///   结构化数据另有评测报告。别在这里追求字段完备性。
/// </summary>
public sealed class RetrievalTraceCollector
{
    /// <summary>查询理解：路由判据、检索文本、每个变体是否通过准入、实体与自称。</summary>
    public List<string> Plan { get; } = [];

    /// <summary>各召回通道各出了多少候选，以及并集/排序后的规模。</summary>
    public List<string> Channels { get; } = [];

    /// <summary>排序阶段的前若干候选与分数构成。</summary>
    public List<RetrievalTraceCandidate> Ranking { get; } = [];

    /// <summary>候选闸门：MinScore、同文本去重、记忆场景限量、锚点去重各挡掉了谁。</summary>
    public List<string> Gates { get; } = [];

    /// <summary>锚点重选日志（旧锚点 → 新锚点）。</summary>
    public List<string> Reselect { get; } = [];

    /// <summary>字符预算裁剪：哪些行因为超预算没能进上下文。</summary>
    public List<string> Budget { get; } = [];

    /// <summary>充分性策略的结论与理由。</summary>
    public List<string> Sufficiency { get; } = [];

    /// <summary>收尾用的自由文本（例如融合臂的分臂计数）。</summary>
    public List<string> Notes { get; } = [];

    /// <summary>一行候选的排版：`0.412/0.301 exact·原句` 这种，便于扫读。</summary>
    public static string Describe(RetrievalTraceCandidate candidate)
        => $"{candidate.Score:F3}/{candidate.Coverage:F3}" +
           (candidate.Exact ? " exact" : "") +
           $" · {candidate.Kind} · {candidate.Speaker}：{Shorten(candidate.Text, 60)}";

    internal static string Shorten(string? value, int limit)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var single = value.Replace('\r', ' ').Replace('\n', '⏎');
        return single.Length <= limit ? single : single[..limit] + "…";
    }
}
