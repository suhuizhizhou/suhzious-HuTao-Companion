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

        // 兜底规范化：动作括号会被去掉、破折号会变成可切句的停顿。
        // 放在这个咽喉点而不是只放在调用方，是因为「整段是动作」以外的形态
        // （括号夹在台词中间）只有规范化能处理，而任何调用方都可能忘记做。
        var speakable = SpeechText.ForSpeech(text, SpeechText.DashPause);
        if (speakable.Length == 0)
            return null;

        var style = _emotionReferences?.Select(emotion, intensity);
        return await _tts.SynthesizeAsync(
            new TtsRequest(
                speakable,
                style?.RefAudioPath ?? _fallbackAudio,
                style?.RefText ?? _fallbackText,
                SpeedFactor: style?.SpeedFactor ?? 1.0,
                Temperature: style?.Temperature ?? 0.6),
            ct).ConfigureAwait(false);
    }
}
