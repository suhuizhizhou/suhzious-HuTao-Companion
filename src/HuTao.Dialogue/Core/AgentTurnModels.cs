using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Core;

/// <summary>一条聊天气泡及其不展示给用户的语音元数据。</summary>
public sealed record SpeechSegment(
    string Text,
    string Emotion,
    double Intensity,
    string? OriginalAudioPath = null,
    string? OriginalVoiceId = null);

/// <summary>Reason → Observe → Think 的文字结果。</summary>
public sealed record TextResult(
    string Reason,
    string Observation,
    string Reply,
    IReadOnlyList<SpeechSegment> Segments)
{
    public HuTao.Knowledge.Rag.StoryRagResult? Story { get; init; }
    public HuTao.Knowledge.Rag.StoryAnswerResult? StoryAnswer { get; init; }

    /// <summary>
    /// observe 阶段最后一版的沉浸判定结论（含未通过时的违规类型）。
    ///
    /// **为什么必须回传**：判定本身只改内部状态，外部此前只能看到被替换过的台词——
    /// 于是「闸门到底判过没有、判的是什么」在观测侧完全不可见，在线评测只能靠重新实现一遍
    /// 规则层去猜。回传的是**闸门自己的结论**，不是评测另写的一套判据，
    /// 这样「闸门内判」与「闸门外独立判官」的差异才是可比的。
    /// 未接线（闸门关闭）时为 null，调用方不得把它当作「通过」。
    /// </summary>
    public HuTao.Dialogue.Immersion.ImmersionVerdict? Immersion { get; init; }

    /// <summary>
    /// 闸门这一轮实际走的路径（clean / rules-only / critic-repaired / critic-blocked / blocked …）。
    ///
    /// **不加这一项，最重要的在线事实就看不见了**：闸门修复后会再用规则层复核一遍，
    /// 于是 <see cref="Immersion"/> 里留下的是**复核后的**规则结论、Source 也变成 "rules"，
    /// 「评审层当场抓到了、然后被改写」这段过程被完全抹平。实测三例全被评审层判为
    /// Coherence 违规并触发重写，而只看 Verdict 会读成「三例一次通过」。
    /// </summary>
    public string? ImmersionPath { get; init; }

    /// <summary>
    /// 本轮的 ReAct 检索过程（分轮、每轮查询、每轮用的臂、是否足够）。
    /// 「多跳只能由在线多轮查询完成」，所以轮次与逐轮查询必须随结果回传，
    /// 否则在线指标只剩一个最终证据池，看不出多级多次查询究竟发生了没有。
    /// </summary>
    public ReactRetrievalResult? Retrieval { get; init; }
}

/// <summary>完整 ReAct 循环结果。</summary>
public sealed record AgentTurnResult(
    string Reason,
    string Observation,
    string Reply,
    TtsResult? Audio,
    string Action)
{
    public HuTao.Knowledge.Rag.StoryRagResult? Story { get; init; }
    public HuTao.Knowledge.Rag.StoryAnswerResult? StoryAnswer { get; init; }
    public HuTao.Dialogue.Immersion.ImmersionVerdict? Immersion { get; init; }
    public string? ImmersionPath { get; init; }
    public ReactRetrievalResult? Retrieval { get; init; }
}

/// <summary>跨 UI、Agent 和 TTS 复用的文本判定规则。</summary>
public static class SpeechText
{
    public static bool IsAction(string? text)
    {
        var value = text?.Trim() ?? "";
        return value.Length >= 2 &&
               ((value.StartsWith('（') && value.EndsWith('）')) ||
                (value.StartsWith('(') && value.EndsWith(')')));
    }
}
