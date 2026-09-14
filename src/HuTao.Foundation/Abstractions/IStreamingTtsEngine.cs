namespace HuTao.Foundation.Abstractions;

/// <summary>流式音频分块，供低延迟播放、嘴型驱动和 WebSocket 转发。</summary>
public sealed record AudioChunk(
    ReadOnlyMemory<byte> Data,
    int SampleRate,
    int Channels,
    string MediaType,
    bool IsFinal);

/// <summary>支持首包播放的 TTS 扩展；非流式引擎继续只实现 ITtsEngine。</summary>
public interface IStreamingTtsEngine : ITtsEngine
{
    IAsyncEnumerable<AudioChunk> StreamAsync(
        TtsRequest request,
        CancellationToken ct = default);
}
