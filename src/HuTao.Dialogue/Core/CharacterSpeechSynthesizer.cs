using HuTao.Foundation.Abstractions;
using HuTao.Voice;

namespace HuTao.Dialogue.Core;

public interface ICharacterSpeechSynthesizer
{
    bool IsAvailable { get; }
    Task<TtsResult?> SynthesizeAsync(
        string text,
        string emotion,
        double intensity,
        CancellationToken ct = default);
}

/// <summary>将角色情绪参考选择与具体 ITtsEngine 隔离。</summary>
public sealed class CharacterSpeechSynthesizer : ICharacterSpeechSynthesizer
{
    private readonly ITtsEngine? _tts;
    private readonly EmotionReferenceCatalog? _emotionReferences;
    private readonly string? _fallbackAudio;
    private readonly string? _fallbackText;

    public CharacterSpeechSynthesizer(
        ITtsEngine? tts,
        EmotionReferenceCatalog? emotionReferences,
        string? fallbackAudio,
        string? fallbackText)
    {
        _tts = tts;
        _emotionReferences = emotionReferences;
        _fallbackAudio = fallbackAudio;
        _fallbackText = fallbackText;
    }

    public bool IsAvailable => _tts is not null;

    public async Task<TtsResult?> SynthesizeAsync(
        string text,
        string emotion,
        double intensity,
        CancellationToken ct = default)
    {
        if (_tts is null || SpeechText.IsAction(text))
            return null;

        var style = _emotionReferences?.Select(emotion, intensity);
        return await _tts.SynthesizeAsync(
            new TtsRequest(
                text,
                style?.RefAudioPath ?? _fallbackAudio,
                style?.RefText ?? _fallbackText,
                SpeedFactor: style?.SpeedFactor ?? 1.0,
                Temperature: style?.Temperature ?? 0.6),
            ct).ConfigureAwait(false);
    }
}
