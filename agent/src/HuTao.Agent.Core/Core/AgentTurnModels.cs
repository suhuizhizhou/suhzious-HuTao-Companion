using HuTao.Agent.Core.Abstractions;

namespace HuTao.Agent.Core.Core;

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
    public HuTao.Agent.Core.Rag.StoryRagResult? Story { get; init; }
    public HuTao.Agent.Core.Rag.StoryAnswerResult? StoryAnswer { get; init; }
}

/// <summary>完整 ReAct 循环结果。</summary>
public sealed record AgentTurnResult(
    string Reason,
    string Observation,
    string Reply,
    TtsResult? Audio,
    string Action)
{
    public HuTao.Agent.Core.Rag.StoryRagResult? Story { get; init; }
    public HuTao.Agent.Core.Rag.StoryAnswerResult? StoryAnswer { get; init; }
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
