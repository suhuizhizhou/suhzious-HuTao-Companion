using System.Text.Json;

namespace HuTao.Dialogue.Storage;

using HuTao.Dialogue.Core;

/// <summary>一条聊天记录：用户或胡桃的一句话，胡桃的带语音路径。</summary>
public sealed record ChatEntry(string Time, string Role, string Text, string? Audio)
{
    public ConversationPrivacyStamp? Privacy {get;init;}
    public bool OwnsGeneratedAudio {get;init;}
}

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
    private readonly Func<ConversationPrivacyStamp?>? _privacy;
    private readonly string? _generatedAudioRoot;
    private readonly object _gate=new();
    private sealed record PendingAudio(string Path,ConversationPrivacyStamp Privacy);

    public ChatLogStore(string logPath, long audioSizeLimitBytes = 512L * 1024 * 1024,
        Func<ConversationPrivacyStamp?>? privacy=null,string? generatedAudioRoot=null)
    {
        _logPath = logPath;
        _audioSizeLimitBytes = audioSizeLimitBytes;
        _privacy=privacy;
        _generatedAudioRoot=generatedAudioRoot is null?null:Path.GetFullPath(generatedAudioRoot);
    }

    public List<ChatEntry> Load()
    {
        lock(_gate)
        {
            try
            {
                FlushPendingAudio();
                if (!File.Exists(_logPath))return [];
                var entries=JsonSerializer.Deserialize<List<ChatEntry>>(File.ReadAllText(_logPath)) ?? [];
                var current=_privacy?.Invoke();
                if(entries.Any(e=>e.Privacy is not null && (current is null || e.Privacy.Namespace!=current.Namespace)))return [];
                var retained=entries.Where(e=>IsCurrent(e,current)).ToList();
                if(retained.Count!=entries.Count)
                {
                    QueueAudio(entries.Except(retained));
                    Write(retained);
                    FlushPendingAudio();
                }
                return retained;
            }
            catch
            {
                // Unknown version or unavailable authority must not expose the unfiltered log.
                return [];
            }
        }
    }

    public void Save(IReadOnlyList<ChatEntry> entries)
    {
        lock(_gate)
        {
            var current=_privacy?.Invoke();
            if(entries.Any(e=>e.Privacy is not null && (current is null || e.Privacy.Namespace!=current.Namespace)))
                throw new InvalidOperationException("Chat log namespace does not match the active writer.");
            List<ChatEntry> stored=[];
            if(File.Exists(_logPath))
            {
                stored=JsonSerializer.Deserialize<List<ChatEntry>>(File.ReadAllText(_logPath))??[];
                if(stored.Any(e=>e.Privacy is not null && (current is null || e.Privacy.Namespace!=current.Namespace)))
                    throw new InvalidOperationException("Existing chat log belongs to a different privacy authority.");
            }
            var retained=entries.Where(e=>IsCurrent(e,current)).ToArray();
            QueueAudio(stored.Concat(entries).Where(e=>!IsCurrent(e,current)));
            Write(retained);
            FlushPendingAudio();
        }
    }

    internal static bool IsCurrent(ChatEntry entry,ConversationPrivacyStamp? stamp)=>stamp is null?
        entry.Privacy is null:entry.Privacy==stamp || entry.Privacy is null && stamp.Epoch==0;

    private void Write(IReadOnlyList<ChatEntry> entries)
    {
        var dir = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temporary=_logPath+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary,_logPath,overwrite:true);
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }

    public void DiscardGeneratedAudio(string path,ConversationPrivacyStamp? privacy)
    {
        if(privacy is null || privacy.Namespace!=_privacy?.Invoke()?.Namespace)return;
        lock(_gate)
        {
            QueueAudio([new("","","",path) {Privacy=privacy,OwnsGeneratedAudio=true}]);
            FlushPendingAudio();
        }
    }

    private void QueueAudio(IEnumerable<ChatEntry> entries)
    {
        var pending=ReadPending();
        pending.AddRange(entries.Where(e=>e.OwnsGeneratedAudio && e.Audio is not null && e.Privacy is not null)
            .Select(e=>new PendingAudio(e.Audio!,e.Privacy!)));
        if(pending.Count>0)WritePending(pending.Distinct().ToList());
    }
    private List<PendingAudio> ReadPending()=>File.Exists(_logPath+".audio-purge.json")?
        JsonSerializer.Deserialize<List<PendingAudio>>(File.ReadAllText(_logPath+".audio-purge.json"))??[]:[];
    private void WritePending(List<PendingAudio> pending)
    {
        var path=_logPath+".audio-purge.json";Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
        try {File.WriteAllText(temporary,JsonSerializer.Serialize(pending));File.Move(temporary,path,true);}
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
    private void FlushPendingAudio()
    {
        var pending=ReadPending();if(pending.Count==0)return;
        var current=_privacy?.Invoke();
        var retained=pending.Where(item=>current is null || item.Privacy.Namespace!=current.Namespace || item.Privacy.Epoch>=current.Epoch || !DeleteOwnedAudio(item.Path)).ToList();
        WritePending(retained);
    }

    private bool DeleteOwnedAudio(string audio)
    {
        if(_generatedAudioRoot is null)return false;
        try
        {
            var path=Path.GetFullPath(audio);if(!IsOwnedAudioPath(path))return true;
            File.Delete(path);return true;
        }
        catch(IOException) {return false;}
        catch(UnauthorizedAccessException) {return false;}
    }

    private bool IsOwnedAudioPath(string path)
        => OwnedAudioGuard.IsOwned(_generatedAudioRoot, path);

    public void Clear()
    {
        lock(_gate)
        {
            if(File.Exists(_logPath))
            {
                var current=_privacy?.Invoke();
                var entries=JsonSerializer.Deserialize<List<ChatEntry>>(File.ReadAllText(_logPath))??[];
                if(entries.Any(e=>e.Privacy is not null && (current is null || e.Privacy.Namespace!=current.Namespace)))
                    throw new InvalidOperationException("Cannot clear a different conversation namespace.");
                File.Delete(_logPath);
            }
        }
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
            .Where(e => e.OwnsGeneratedAudio && e.Audio is not null && IsOwnedAudioPath(e.Audio) && IsCurrent(e,_privacy?.Invoke()))
            .OrderBy(e => e.Time)
            .ToList();

        var total = SumAudioBytes(ordered);
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
