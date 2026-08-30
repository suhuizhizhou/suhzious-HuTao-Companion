namespace HuTao.Agent.Core.Abstractions;

/// <summary>
/// 语音合成引擎的统一接口。
/// 设计意图：Agent 只依赖这个接口，不关心底层是 GPT-SoVITS、CosyVoice 还是未来微调后的模型。
/// 切换引擎 = 换一个实现类 + 改一行配置，业务逻辑（ReAct、调度、对话）完全不动。
/// </summary>
public interface ITtsEngine
{
    /// <summary>引擎名，用于日志与配置选择。</summary>
    string Name { get; }

    /// <summary>把一段文本合成音频（可能用参考音频做 few-shot 音色克隆）。</summary>
    Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct = default);
}

/// <summary>合成请求。参考音频/参考文本用于 few-shot 音色克隆；传 null 表示用引擎默认音色。</summary>
public sealed record TtsRequest(
    string Text,
    string? RefAudioPath = null,
    string? RefText = null,
    string Language = "中文");

/// <summary>合成结果。</summary>
public sealed record TtsResult(
    string AudioPath,
    int SampleRate,
    double DurationSeconds);
