namespace HuTao.Knowledge.DocumentReading;

/// <summary>长文朗读任务的一段可复现计划。</summary>
public sealed record ReadingChunk(
    int Index,
    string Stage,
    string Text,
    string Emotion,
    double Intensity);

/// <summary>一次朗读任务的结果。分段 WAV 会保留，便于排查和重新合并。</summary>
public sealed record DocumentReadingResult(
    bool Success,
    string SourcePath,
    string? OutputMp3,
    string JobDirectory,
    int ChunkCount,
    string? Error = null,
    bool Cancelled = false);

/// <summary>一个朗读任务在磁盘上的产物位置。</summary>
public sealed record ReadingJobArtifacts(
    string JobDirectory,
    string SegmentDirectory,
    string Mp3Path);
