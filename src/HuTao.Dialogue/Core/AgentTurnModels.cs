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
    public IReadOnlyList<HuTao.Dialogue.Tools.ToolRunResult> ToolRuns { get; init; } = [];
    public ConversationRecallResult? Recall { get; init; }
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
    /// 闸门这一轮实际走的路径（clean / rules-only / critic-blocked / rule-repaired …
    /// 重试过的轮次会带后缀 <c>gen-retry-N</c>（同一份证据重做）或 <c>observe-retry-N</c>（换检索词重走 RAG））。
    ///
    /// **不加这一项，最重要的在线事实就看不见了**：只看 <see cref="Immersion"/> 的 Passed/KindsSummary
    /// 只能知道"最后过没过"，看不出「拦下过几次、重做了几轮、换没换过检索词」——
    /// 而这三件事的成本差一个数量级（一次重生成 vs 一整套检索）。
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
    public IReadOnlyList<HuTao.Dialogue.Tools.ToolRunResult> ToolRuns { get; init; } = [];
    public ConversationRecallResult? Recall { get; init; }
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

    /// <summary>夹在台词中间的括号动作/旁白，如「（撑着下巴）客官今日来得早啊。」里的前半段。</summary>
    private static readonly System.Text.RegularExpressions.Regex InlineAction = new(
        @"（[^（）]{0,80}）|\([^()]{0,80}\)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 句内用作停顿的破折号。中文排版里 —— 表示明显停顿/拖长，
    /// 但 GPT-SoVITS 的 `cut5` 只按**中文标点**切句，标点表里没有破折号，
    /// 于是它被原样读过去、听起来完全没有停顿。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex Dash =
        new(@"\s*—{2,}\s*|\s*--+\s*",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>紧邻标点的收尾清理：避免出现「，。」这类叠标点。</summary>
    private static readonly System.Text.RegularExpressions.Regex DoubledPunctuation = new(
        @"([，。！？；：、…])\s*([，。！？；：、])",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex SpaceBeforePunctuation = new(
        @"\s+([，。！？；：、…）】」』])",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// **送进 TTS 之前的文本规范化**。两件事都是「引擎读不对、由我们读对」：
    ///
    /// 1. **去掉括号内的动作/旁白**——它只该显示在气泡里，绝不该被念出来。
    ///    只判断「整段是不是括号」不够，真正的失效形态是括号夹在台词中间：
    ///    `（撑着下巴）客官今日来得早啊。` 整段不算动作，于是「撑着下巴」被念了出来。
    /// 2. **把破折号换成会被切句的标点**——`cut5` 按标点切句、段间有自然停顿；
    ///    破折号不在标点表里，所以 `我——不是那个意思` 读起来一口气，没有停顿。
    ///    默认换逗号（停顿自然、不破坏韵律）；想要更长停顿传句号，或用
    ///    `HU_TAO_TTS_DASH_PAUSE=。` 全局改。
    ///
    /// 返回空串表示「这一段没有可读内容」，调用方必须**跳过合成**而不是把空文本送给引擎。
    /// </summary>
    public static string ForSpeech(string? text, string dashPause = "，")
    {
        var value = text ?? "";
        if (value.Length == 0) return "";

        var stripped = InlineAction.Replace(value, " ");
        var paused = Dash.Replace(stripped, dashPause);
        paused = DoubledPunctuation.Replace(paused, "$1");
        var cleaned = System.Text.RegularExpressions.Regex.Replace(paused, @"[ \t]+", " ").Trim();
        cleaned = SpaceBeforePunctuation.Replace(cleaned, "$1");

        // 只剩标点也算没内容——引擎对纯标点会产出噪声或空音频。
        return cleaned.Any(char.IsLetterOrDigit) ? cleaned : "";
    }

    /// <summary>破折号停顿用的替换标点，可由 <c>HU_TAO_TTS_DASH_PAUSE</c> 覆盖。</summary>
    public static string DashPause
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("HU_TAO_TTS_DASH_PAUSE")?.Trim();
            return string.IsNullOrEmpty(configured) ? "，" : configured;
        }
    }
}
