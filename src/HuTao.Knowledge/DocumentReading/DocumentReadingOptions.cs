namespace HuTao.Knowledge.DocumentReading;

/// <summary>朗读服务的可调参数，避免把魔法数字散落在业务代码中。</summary>
public sealed record DocumentReadingOptions
{
    public int MaxChunkLength { get; init; } = 180;
    public int ProgressEveryChunks { get; init; } = 5;
    public int Mp3BitrateKbps { get; init; } = 192;
}
