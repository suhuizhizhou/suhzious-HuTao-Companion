using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HuTao.Foundation.Diagnostics;
using HuTao.Dialogue.Storage;

namespace HuTao.Dialogue.Sessions;

/// <summary>一个会话在磁盘上的全部路径。集中在一处，避免各处手拼路径导致「写这里读那里」。</summary>
public sealed record SessionPaths(
    string Root,
    string Directory,
    string IndexPath,
    string SessionPath,
    string TimelinePath,
    string ViewDirectory)
{
    public string ViewPath(string characterId) => Path.Combine(ViewDirectory, characterId + ".jsonl");
}

/// <summary>
/// 会话存储：index.json（目录表）+ timeline.jsonl（权威时间线）+ view/（各角色视角缓存）。
///
/// 为什么不复用 <c>ChatLogStore</c>：它有三条与本场景冲突的性质——
/// ① <c>ChatEntry</c> 没有发言人字段，群聊根本表达不了；
/// ② <c>Save</c> 每次整体覆写（长对话每轮 O(n)），而会话日志天然是追加；
/// ③ <c>Load</c> 吞掉一切异常返回空——对「账号级日志」是对的（不能证明有权读取就不暴露），
///    但用在「我的聊天记录」上会让用户以为消息丢了。
///
/// 因此这里只做一件事：**追加**。时间线永不就地修改，索引整体原子替换（它很小）。
/// </summary>
public sealed class SessionStore
{
    /// <summary>文件级格式版本。目前只有 1；不认识的版本按「空目录」处理并由调用方决定是否迁移。</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>按会话串行的追加门。追加必须串行，否则两个回合会算出同一个 Id。</summary>
    private readonly Dictionary<string, SemaphoreSlim> _appendGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gateLock = new();
    private readonly LocalDiagnosticLog _log;

    public SessionStore(string repositoryRoot, LocalDiagnosticLog? log = null)
    {
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        _log = log ?? LocalDiagnosticLog.Default;
    }

    public string RepositoryRoot { get; }

    public string ConversationsRoot => Path.Combine(RepositoryRoot, "data", "conversations");

    public string IndexPath => Path.Combine(ConversationsRoot, "index.json");

    public string TrashRoot => Path.Combine(ConversationsRoot, ".trash");

    public SessionPaths PathsFor(string sessionId)
    {
        var directory = Path.Combine(ConversationsRoot, sessionId);
        return new SessionPaths(
            ConversationsRoot,
            directory,
            IndexPath,
            Path.Combine(directory, "session.json"),
            Path.Combine(directory, "timeline.jsonl"),
            Path.Combine(directory, "view"));
    }

    private SemaphoreSlim GateFor(string sessionId)
    {
        lock (_gateLock)
        {
            if (!_appendGates.TryGetValue(sessionId, out var gate))
                _appendGates[sessionId] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    // ── 目录表 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 读 index.json。不存在、损坏、版本不认识 → 返回空目录（不抛）。
    /// 与 <c>ChatLogStore.Load</c> 的取舍一致：读不出可证明的内容时返回空，而不是暴露半份。
    /// 由调用方（<c>ChatShellViewModel</c>）决定是否跑迁移。
    /// </summary>
    public async Task<SessionCatalog> LoadCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(IndexPath))
                return new SessionCatalog([]);

            var json = await File.ReadAllTextAsync(IndexPath, ct).ConfigureAwait(false);
            var file = JsonSerializer.Deserialize<SessionIndexFile>(json);
            if (file is null || file.Version != SchemaVersion || file.Sessions is null)
                return new SessionCatalog([]);

            return new SessionCatalog(file.Sessions
                .Where(session => !string.IsNullOrWhiteSpace(session.Id))
                .Select(SessionFactory.Normalize));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Write("session.index_load", ex);
            return new SessionCatalog([]);
        }
    }

    /// <summary>整体写 index.json，走 tmp + Move(overwrite:true)，与 <c>ChatLogStore.Write</c> 同一手法。</summary>
    public async Task SaveCatalogAsync(SessionCatalog catalog, CancellationToken ct = default)
    {
        var file = new SessionIndexFile(SchemaVersion, catalog.Sessions.ToArray());
        var json = JsonSerializer.Serialize(file, IndentedJson);
        await WriteAtomicAsync(IndexPath, json, ct).ConfigureAwait(false);
    }

    // ── 时间线 ───────────────────────────────────────────────────────────

    /// <summary>读全量时间线。单行解析失败 → 跳过该行并记诊断，绝不整份拒绝。</summary>
    public async Task<IReadOnlyList<ConversationTurn>> LoadTimelineAsync(
        string sessionId, CancellationToken ct = default)
        => await ReadJsonLinesAsync(PathsFor(sessionId).TimelinePath, "session.timeline_parse", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// 追加一条并返回落盘后的它。<see cref="ConversationTurn.Id"/> 由本方法按尾行 max+1 生成，
    /// **调用方传进来的 Id 会被忽略**——Id 的唯一性只能由追加器保证。
    /// </summary>
    public async Task<ConversationTurn> AppendAsync(
        string sessionId, ConversationTurn turn, CancellationToken ct = default)
    {
        var gate = GateFor(sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = PathsFor(sessionId).TimelinePath;
            var existing = await ReadJsonLinesAsync(path, "session.timeline_parse", ct).ConfigureAwait(false);
            var stored = turn with { Id = NextTurnId(existing) };
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            await AppendJsonLineAsync(path, stored, ct).ConfigureAwait(false);
            return stored;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>会话内单调递增的 id。取所有 `t-` 前缀的最大值 +1，而不是用行数——行数在跳过坏行后会偏小。</summary>
    internal static string NextTurnId(IReadOnlyList<ConversationTurn> existing)
    {
        var max = 0;
        foreach (var turn in existing)
        {
            if (turn.Id is null || !turn.Id.StartsWith("t-", StringComparison.Ordinal))
                continue;
            if (int.TryParse(turn.Id.AsSpan(2), out var value) && value > max)
                max = value;
        }
        return "t-" + (max + 1).ToString("D4");
    }

    // ── 视角（投影缓存）─────────────────────────────────────────────────

    public async Task<IReadOnlyList<ConversationTurn>> LoadViewAsync(
        string sessionId, string characterId, CancellationToken ct = default)
        => await ReadJsonLinesAsync(PathsFor(sessionId).ViewPath(characterId), "session.view_parse", ct)
            .ConfigureAwait(false);

    /// <summary>
    /// 用投影结果整体覆写视角缓存。**视角是缓存，允许覆写**——这一点与时间线相反。
    /// 只用于「成员变更后重建」与「激活时首次生成」，不要每轮调用。
    /// </summary>
    public async Task SaveViewAsync(
        string sessionId, string characterId,
        IReadOnlyList<ConversationTurn> turns, CancellationToken ct = default)
    {
        var path = PathsFor(sessionId).ViewPath(characterId);
        var builder = new StringBuilder();
        foreach (var turn in turns)
            builder.Append(JsonSerializer.Serialize(turn, CompactJson)).Append('\n');
        await WriteAtomicAsync(path, builder.ToString(), ct).ConfigureAwait(false);
    }

    /// <summary>增量追加一行到视角。比整体重建便宜，是常规回合的写入路径。</summary>
    public async Task AppendViewAsync(
        string sessionId, string characterId,
        ConversationTurn turn, CancellationToken ct = default)
    {
        var path = PathsFor(sessionId).ViewPath(characterId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await AppendJsonLineAsync(path, turn, ct).ConfigureAwait(false);
    }

    // ── 会话目录 ─────────────────────────────────────────────────────────

    /// <summary>新建会话目录（写 session.json + 空 timeline.jsonl）。已存在则幂等返回。</summary>
    public async Task<SessionDescriptor> CreateSessionAsync(
        SessionDescriptor descriptor, CancellationToken ct = default)
    {
        var paths = PathsFor(descriptor.Id);
        Directory.CreateDirectory(paths.Directory);
        Directory.CreateDirectory(paths.ViewDirectory);

        var normalized = SessionFactory.Normalize(descriptor);
        await WriteAtomicAsync(paths.SessionPath,
            JsonSerializer.Serialize(normalized, IndentedJson), ct).ConfigureAwait(false);
        if (!File.Exists(paths.TimelinePath))
            await WriteAtomicAsync(paths.TimelinePath, "", ct).ConfigureAwait(false);
        return normalized;
    }

    /// <summary>同步 session.json（重命名/成员变更/置顶后的持久化）。</summary>
    public async Task SaveSessionAsync(SessionDescriptor descriptor, CancellationToken ct = default)
    {
        var normalized = SessionFactory.Normalize(descriptor);
        var paths = PathsFor(normalized.Id);
        Directory.CreateDirectory(paths.ViewDirectory);
        await WriteAtomicAsync(paths.SessionPath,
            JsonSerializer.Serialize(normalized, IndentedJson), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除会话：整目录移到 <c>.trash/{id}-{utcTicks}/</c>。
    /// **不真删**——用户点错一次的代价远大于占几 MB 磁盘。
    /// </summary>
    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var paths = PathsFor(sessionId);
        if (!Directory.Exists(paths.Directory))
            return Task.CompletedTask;

        Directory.CreateDirectory(TrashRoot);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var destination = Path.Combine(TrashRoot, $"{sessionId}-{stamp}");
        // 目标已存在时加后缀，绝不覆盖回收站里的旧垃圾。
        var unique = destination;
        for (var i = 1; Directory.Exists(unique); i++)
            unique = $"{destination}-{i}";
        Directory.Move(paths.Directory, unique);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 清空会话的消息：时间线清空 + 每个在场成员的视角缓存清空。
    /// **保留会话与记忆**——用户点"清空聊天记录"的意思是"这段对话不要了"，
    /// 不是"把这个人也删了"。所以目录项、session.json、长期记忆一律不动。
    /// </summary>
    public async Task ClearHistoryAsync(string sessionId, CancellationToken ct = default)
    {
        var paths = PathsFor(sessionId);
        await WriteAtomicAsync(paths.TimelinePath, "", ct).ConfigureAwait(false);
        if (Directory.Exists(paths.ViewDirectory))
        {
            foreach (var view in Directory.GetFiles(paths.ViewDirectory, "*.jsonl"))
            {
                ct.ThrowIfCancellationRequested();
                await WriteAtomicAsync(view, "", ct).ConfigureAwait(false);
            }
        }
    }

    // ── 音频配额（T-12）─────────────────────────────────────────────────

    /// <summary>默认语音保存区上限：512 MiB，与桌宠使用的值一致（<c>MainWindow.xaml.cs</c>）。</summary>
    public const long DefaultAudioSizeLimitBytes = 512L * 1024 * 1024;

    /// <summary>引擎产物目录。<c>OwnedAudioGuard</c> 的删除白名单依赖它，不要改。</summary>
    public string GeneratedAudioRoot => Path.Combine(RepositoryRoot, "data", "voice", "generated");

    public static long SumAudioBytes(IEnumerable<ConversationTurn> turns)
    {
        long total = 0;
        foreach (var turn in turns)
        {
            if (turn.AudioPath is null || !File.Exists(turn.AudioPath))
                continue;
            try { total += new FileInfo(turn.AudioPath).Length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    /// <summary>
    /// 超配额时该删哪些音频（最旧的优先）。
    /// 与 <c>ChatLogStore.ComputePrunableAudio</c> 同策略，但判定复用同一个
    /// <see cref="OwnedAudioGuard"/>，避免"两处删除权限，只修了一处"。
    /// </summary>
    public IReadOnlyList<string> ComputePrunableAudio(
        IReadOnlyList<ConversationTurn> timeline,
        long sizeLimitBytes = DefaultAudioSizeLimitBytes)
    {
        var owned = timeline
            .Where(turn => turn.AudioPath is not null &&
                           OwnedAudioGuard.IsOwned(GeneratedAudioRoot, turn.AudioPath))
            .OrderBy(turn => turn.At)
            .ToList();

        var total = SumAudioBytes(owned);
        var prunable = new List<string>();
        foreach (var turn in owned)
        {
            if (total <= sizeLimitBytes)
                break;
            var path = turn.AudioPath!;
            try
            {
                total -= new FileInfo(path).Length;
                prunable.Add(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return prunable;
    }

    /// <summary>
    /// 删除超配额的生成音频（文件层）。时间线本身保持不变——
    /// 音频是"可再生的重物"，消息文本才是记录。
    /// </summary>
    public async Task<IReadOnlyList<string>> PruneAudioAsync(
        string sessionId, long sizeLimitBytes = DefaultAudioSizeLimitBytes,
        CancellationToken ct = default)
    {
        var timeline = await LoadTimelineAsync(sessionId, ct).ConfigureAwait(false);
        var prunable = ComputePrunableAudio(timeline, sizeLimitBytes);
        var deleted = new List<string>();
        foreach (var path in prunable)
        {
            // 再判一次：ComputePrunableAudio 与删除之间文件系统可能已变。
            if (!OwnedAudioGuard.IsOwned(GeneratedAudioRoot, path))
                continue;
            try
            {
                File.Delete(path);
                deleted.Add(path);
            }
            catch (IOException ex) { _log.Write("session.audio_prune", ex); }
            catch (UnauthorizedAccessException ex) { _log.Write("session.audio_prune", ex); }
        }
        return deleted;
    }

    // ── 底层写入 ─────────────────────────────────────────────────────────

    /// <summary>tmp + Move(overwrite:true)，失败时清理 tmp。整个项目的写入约定。</summary>
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task AppendJsonLineAsync(string path, ConversationTurn turn, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(turn, CompactJson) + "\n";
        await using var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 逐行解析 JSONL。坏行跳过并记诊断——一份 2000 行的聊天记录里有一行被截断，
    /// 不该让用户「整段历史消失」。
    /// </summary>
    private async Task<IReadOnlyList<ConversationTurn>> ReadJsonLinesAsync(
        string path, string diagnosticKey, CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];

        var result = new List<ConversationTurn>();
        try
        {
            foreach (var line in await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var turn = JsonSerializer.Deserialize<ConversationTurn>(line);
                    if (turn is not null)
                        result.Add(turn);
                }
                catch (JsonException ex)
                {
                    _log.Write(diagnosticKey, ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Write(diagnosticKey, ex);
        }
        return result;
    }

    /// <summary>index.json 的落盘形态：带版本的对象包一个数组。</summary>
    private sealed record SessionIndexFile(int Version, SessionDescriptor[]? Sessions);
}
