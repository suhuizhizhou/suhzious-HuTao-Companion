using System.Text;
using System.Text.Json;

namespace HuTao.Knowledge.DocumentReading;

/// <summary>文件系统任务产物存储。未来可替换为数据库、对象存储或断点续读存储。</summary>
public sealed class FileReadingJobStore : IReadingJobStore
{
    private readonly string _outputRoot;

    public FileReadingJobStore(string outputRoot)
        => _outputRoot = Path.GetFullPath(outputRoot);

    public ReadingJobArtifacts Create(string sourcePath)
    {
        var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        var jobDirectory = Path.Combine(
            _outputRoot,
            "reading",
            $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
        var segmentDirectory = Path.Combine(jobDirectory, "segments");
        Directory.CreateDirectory(segmentDirectory);
        return new ReadingJobArtifacts(
            jobDirectory,
            segmentDirectory,
            Path.Combine(jobDirectory, $"{baseName}.mp3"));
    }

    public async Task SavePlanAsync(
        ReadingJobArtifacts job,
        IReadOnlyList<string> paragraphs,
        IReadOnlyList<ReadingChunk> chunks,
        CancellationToken ct = default)
    {
        await File.WriteAllTextAsync(
            Path.Combine(job.JobDirectory, "source.txt"),
            string.Join(Environment.NewLine + Environment.NewLine, paragraphs),
            new UTF8Encoding(false),
            ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(job.JobDirectory, "plan.json"),
            JsonSerializer.Serialize(chunks, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            ct).ConfigureAwait(false);
    }

    public string GetSegmentPath(ReadingJobArtifacts job, ReadingChunk chunk)
        => Path.Combine(job.SegmentDirectory, $"{chunk.Index:0000}_{chunk.Emotion}.wav");

    private static string SanitizeFileName(string value)
    {
        var invalid = new string(Path.GetInvalidFileNameChars());
        var safe = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "document" : safe;
    }
}
