using System.Text.RegularExpressions;

namespace HuTao.Knowledge.DocumentReading;

/// <summary>
/// 默认朗读规划器：标题切换阶段，句末切分，再按最大长度包装。
/// 可替换为 LLM 规划器而不影响文档解析和音频合并。
/// </summary>
public sealed class ReadingPlanBuilder : IReadingPlanner
{
    private static readonly Regex Heading = new(
        @"^(第[一二三四五六七八九十百千万0-9]+[章节部分篇卷]|[0-9一二三四五六七八九十]+[、.．]|【[^】]{1,30}】|标题：)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly DocumentReadingOptions _options;

    public ReadingPlanBuilder(DocumentReadingOptions? options = null)
        => _options = options ?? new DocumentReadingOptions();

    public IReadOnlyList<ReadingChunk> Build(IReadOnlyList<string> paragraphs)
    {
        var chunks = new List<ReadingChunk>();
        var stage = "自然";
        foreach (var paragraph in paragraphs)
        {
            if (Heading.IsMatch(paragraph) && paragraph.Length <= 50)
                stage = paragraph.Trim('【', '】', '：', ':');

            foreach (var sentence in SplitSentences(paragraph))
            {
                foreach (var part in Wrap(sentence.Trim(), _options.MaxChunkLength))
                {
                    if (part.Length == 0)
                        continue;
                    var emotion = InferEmotion(part);
                    chunks.Add(new ReadingChunk(
                        chunks.Count + 1,
                        stage,
                        part,
                        emotion,
                        InferIntensity(part, emotion)));
                }
            }
        }
        return chunks;
    }

    private static IEnumerable<string> SplitSentences(string paragraph)
    {
        var start = 0;
        for (var i = 0; i < paragraph.Length; i++)
        {
            if (paragraph[i] is not ('。' or '！' or '？' or '!' or '?' or ';' or '；' or '…'))
                continue;
            var end = i + 1;
            while (end < paragraph.Length &&
                   (paragraph[end] is '”' or '’' or '」' or '』'))
                end++;
            yield return paragraph[start..end];
            start = end;
            i = end - 1;
        }
        if (start < paragraph.Length)
            yield return paragraph[start..];
    }

    private IEnumerable<string> Wrap(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            yield return text;
            yield break;
        }

        for (var start = 0; start < text.Length; start += maxLength)
            yield return text.Substring(start, Math.Min(maxLength, text.Length - start));
    }

    private static string InferEmotion(string text)
    {
        if (ContainsAny(text, "晚安", "睡觉", "困了", "疲惫", "休息一下"))
            return "sleepy";
        if (ContainsAny(text, "抱歉", "对不起", "难过", "遗憾", "担心", "请注意", "但是"))
            return "concerned";
        if (ContainsAny(text, "禁止", "警告", "危险", "错误", "失败", "必须", "不许"))
            return "angry";
        if (ContainsAny(text, "嘿嘿", "哼哼", "当然啦", "你猜", "秘密"))
            return "teasing";
        if (ContainsAny(text, "欢迎", "谢谢", "开心", "成功", "好耶", "太棒", "祝贺"))
            return "cheerful";
        return "neutral";
    }

    private static double InferIntensity(string text, string emotion)
    {
        if (emotion == "neutral")
            return 0.35;
        var punctuation = text.Count(ch => ch is '！' or '!' or '？' or '?');
        return Math.Clamp(0.55 + punctuation * 0.08, 0.45, 0.9);
    }

    private static bool ContainsAny(string value, params string[] terms)
        => terms.Any(value.Contains);
}
