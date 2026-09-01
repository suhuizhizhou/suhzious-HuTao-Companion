using System.Text.Json;

namespace HuTao.Agent.Core.Storage;

/// <summary>一条聊天记录：用户或胡桃的一句话，胡桃的带语音路径。</summary>
public sealed record ChatEntry(string Time, string Role, string Text, string? Audio);

/// <summary>
/// 聊天记录 + 语音记录的持久化存储：
///   - 保存/加载完整聊天记录（重开可重放）
///   - 语音保存区大小上限（超限删除最旧音频）
///   - 手动清空
/// 同时作为 Agent 的短期对话上下文；用户明确托付的重要事项由 ImportantMemoryStore 独立保存。
/// </summary>
public sealed class ChatLogStore
{
    private readonly string _logPath;
    private readonly long _audioSizeLimitBytes;

    public ChatLogStore(string logPath, long audioSizeLimitBytes = 512L * 1024 * 1024)
    {
        _logPath = logPath;
        _audioSizeLimitBytes = audioSizeLimitBytes;
    }

    public List<ChatEntry> Load()
    {
        if (!File.Exists(_logPath))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<ChatEntry>>(File.ReadAllText(_logPath)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<ChatEntry> entries)
    {
        var dir = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_logPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Clear()
    {
        if (File.Exists(_logPath))
            File.Delete(_logPath);
    }

    /// <summary>统计所有语音文件总大小（字节）。</summary>
    public static long SumAudioBytes(IEnumerable<ChatEntry> entries)
    {
        long total = 0;
        foreach (var e in entries)
        {
            if (e.Audio is null || !File.Exists(e.Audio))
                continue;
            total += new FileInfo(e.Audio).Length;
        }
        return total;
    }

    /// <summary>
    /// 若语音总大小超上限，返回应从磁盘删除的最旧音频路径列表。
    /// 调用方删除文件后，把对应条目的 Audio 置 null 再 Save。
    /// </summary>
    public List<string> ComputePrunableAudio(List<ChatEntry> entries)
    {
        var prunable = new List<string>();
        var ordered = entries
            .Where(e => e.Audio is not null && File.Exists(e.Audio))
            .OrderBy(e => e.Time)
            .ToList();

        var total = SumAudioBytes(entries);
        foreach (var e in ordered)
        {
            if (total <= _audioSizeLimitBytes)
                break;
            total -= new FileInfo(e.Audio!).Length;
            prunable.Add(e.Audio!);
        }
        return prunable;
    }
}
