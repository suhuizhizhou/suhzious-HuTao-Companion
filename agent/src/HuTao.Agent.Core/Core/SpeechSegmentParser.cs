using System.Globalization;
using System.Text.RegularExpressions;
using HuTao.Agent.Core.Persona;
using HuTao.Agent.Core.Tts;

namespace HuTao.Agent.Core.Core;

public interface ISpeechSegmentParser
{
    IReadOnlyList<SpeechSegment> Parse(string reply);
}

/// <summary>
/// 解析 LLM 的隐藏 emotion/voice 标签，并在播放原声前执行严格匹配。
/// UI 永远只接收已去除控制标签的 SpeechSegment。
/// </summary>
public sealed class SpeechSegmentParser : ISpeechSegmentParser
{
    private static readonly Regex EmotionTag = new(
        @"^\s*\[emotion=(?<emotion>[a-zA-Z\u4e00-\u9fff]+)(?:;intensity=(?<intensity>0(?:\.\d+)?|1(?:\.0+)?))?\]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OriginalVoiceTag = new(
        @"^\s*\[voice=(?<voice>[a-zA-Z0-9_-]+)\]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly OriginalVoiceCatalog? _originalVoices;

    public SpeechSegmentParser(OriginalVoiceCatalog? originalVoices = null)
        => _originalVoices = originalVoices;

    public IReadOnlyList<SpeechSegment> Parse(string reply)
        => reply
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .Select(ParseLine)
            .ToList();

    private SpeechSegment ParseLine(string line)
    {
        var remaining = line.Trim();
        string? voiceId = null;
        string? taggedEmotion = null;
        string? taggedIntensity = null;

        // 允许两个标签交换顺序，但每种最多消费一次。
        for (var i = 0; i < 2; i++)
        {
            var voiceMatch = OriginalVoiceTag.Match(remaining);
            if (voiceMatch.Success)
            {
                voiceId = voiceMatch.Groups["voice"].Value;
                remaining = remaining[voiceMatch.Length..].Trim();
                continue;
            }

            var emotionMatch = EmotionTag.Match(remaining);
            if (emotionMatch.Success)
            {
                taggedEmotion = emotionMatch.Groups["emotion"].Value;
                taggedIntensity = emotionMatch.Groups["intensity"].Value;
                remaining = remaining[emotionMatch.Length..].Trim();
                continue;
            }
            break;
        }

        if (SpeechText.IsAction(remaining))
            return new SpeechSegment(remaining, "neutral", 0);

        var emotion = taggedEmotion is not null
            ? SpeechEmotion.Normalize(taggedEmotion)
            : InferEmotion(remaining);
        var intensity = taggedIntensity is not null &&
                        double.TryParse(
                            taggedIntensity,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var parsed)
            ? Math.Clamp(parsed, 0, 1)
            : InferIntensity(remaining, emotion);
        var original = _originalVoices?.Resolve(voiceId, remaining)
                       ?? (voiceId is null
                           ? _originalVoices?.ResolveExactText(remaining)
                           : null);
        return new SpeechSegment(
            remaining,
            emotion,
            intensity,
            original?.AudioPath,
            original?.Id);
    }

    private static string InferEmotion(string text)
    {
        if (ContainsAny(text, "累", "困", "休息", "晚安"))
            return "sleepy";
        if (ContainsAny(text, "别担心", "没关系", "抱歉", "难过", "辛苦", "好不好"))
            return "concerned";
        if (ContainsAny(text, "生气", "讨厌", "不许", "够了", "岂有此理"))
            return "angry";
        if (ContainsAny(text, "嘿嘿", "哼哼", "优惠", "捉弄"))
            return "teasing";
        if (ContainsAny(text, "哈哈", "开心", "太好", "好耶"))
            return "cheerful";
        return "neutral";
    }

    private static double InferIntensity(string text, string emotion)
    {
        if (emotion == "neutral")
            return 0.35;
        var punctuationBoost = text.Count(ch => ch is '！' or '!' or '？' or '?');
        return Math.Clamp(0.55 + punctuationBoost * 0.1, 0, 0.9);
    }

    private static bool ContainsAny(string text, params string[] terms)
        => terms.Any(text.Contains);
}
