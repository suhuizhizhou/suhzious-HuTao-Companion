using HuTao.Voice;

namespace HuTao.Knowledge.DocumentReading;

/// <summary>把朗读计划中的情绪标签转换为 TTS 参考声线。</summary>
public sealed class ReadingEmotionSelector : IReadingEmotionSelector
{
    private readonly EmotionReferenceCatalog? _catalog;
    private readonly string? _fallbackAudio;
    private readonly string? _fallbackText;

    public ReadingEmotionSelector(
        EmotionReferenceCatalog? catalog,
        string? fallbackAudio,
        string? fallbackText)
    {
        _catalog = catalog;
        _fallbackAudio = fallbackAudio;
        _fallbackText = fallbackText;
    }

    public EmotionVoiceStyle Select(ReadingChunk chunk)
        => _catalog?.Select(chunk.Emotion, chunk.Intensity)
           ?? new EmotionVoiceStyle(
               chunk.Emotion,
               _fallbackAudio ?? "",
               _fallbackText ?? "",
               1.0,
               0.6);

}
