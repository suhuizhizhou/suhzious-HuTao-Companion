using HuTao.Voice;

namespace HuTao.Knowledge.DocumentReading;

/// <summary>从支持的文档格式提取标准化段落。</summary>
public interface IDocumentTextExtractor
{
    IReadOnlyList<string> Extract(string path);
}

/// <summary>把段落转换为带阶段与情绪元数据的朗读片段。</summary>
public interface IReadingPlanner
{
    IReadOnlyList<ReadingChunk> Build(IReadOnlyList<string> paragraphs);
}

/// <summary>为片段选择参考音频及推理参数。</summary>
public interface IReadingEmotionSelector
{
    EmotionVoiceStyle Select(ReadingChunk chunk);
}

/// <summary>创建任务目录并持久化源文档与朗读计划。</summary>
public interface IReadingJobStore
{
    ReadingJobArtifacts Create(string sourcePath);
    Task SavePlanAsync(
        ReadingJobArtifacts job,
        IReadOnlyList<string> paragraphs,
        IReadOnlyList<ReadingChunk> chunks,
        CancellationToken ct = default);
    string GetSegmentPath(ReadingJobArtifacts job, ReadingChunk chunk);
}

/// <summary>将分段 WAV 合并编码为最终音频。</summary>
public interface IAudioSegmentMerger
{
    Task MergeToMp3Async(
        IReadOnlyList<string> wavs,
        string outputPath,
        CancellationToken ct = default);
}

/// <summary>长文朗读应用服务，供 UI 和 Agent Tool 共用。</summary>
public interface IDocumentReadingService
{
    Task<DocumentReadingResult> ReadAsync(
        string documentPath,
        CancellationToken ct = default);
}
