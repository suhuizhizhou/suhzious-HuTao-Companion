using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HuTao.Foundation.Diagnostics;
using HuTao.Knowledge.Rag;

namespace HuTao.Knowledge.Memory;

/// <summary>
/// 长期对话记忆的持久化与演进。职责只有「记什么 / 什么时候失效 / 什么时候被取代」，
/// 检索打分在 <see cref="MemoryRetriever"/>，提炼在 <see cref="MemoryConsolidator"/>。
///
/// 三条来自公开研究的硬约束（见 docs/memory.md）：
/// 1. **失效而非删除**——过时或被取代的记录留在磁盘上，只是不参与常规检索；
/// 2. **过度剪枝有害**——超限走归档文件，绝不静默丢弃稀有但关键的信息；
/// 3. **不追求「永不遗忘」**——活跃集有上限，否则检索质量会随噪声单调下降。
/// </summary>
public sealed class ConversationMemoryStore
{
    /// <summary>用户明确要求记住、或明显是个人事实的信号词，用于零成本重要度打分。</summary>
    private static readonly Regex ExplicitMarkers = new(
        @"记住|别忘|提醒我|重要|答应|约好|约定|承诺",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PersonalFacts = new(
        @"我叫|我的名字|我是|我住|我在做|我正在|我喜欢|我讨厌|我习惯|我生日|我的?[^，。！？]{0,6}(?:喜欢|讨厌|习惯|计划|打算)|考上|入职|搬家|毕业",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Noise = new(
        @"^[。，！？…~\s]*$|^(?:嗯+|哦+|啊+|哈+|呃+|唉+|喂+)[。，！？…~\s]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _path;
    private readonly string _archivePath;
    private readonly MemoryOptions _options;
    private readonly LocalDiagnosticLog _diagnostics;
    private readonly object _gate = new();

    private readonly List<MemoryRecord> _records = [];
    private readonly Dictionary<string, MemoryRecord> _byFingerprint = new(StringComparer.Ordinal);
    private int _importedChatEntries;
    private DateTimeOffset? _lastConsolidatedAt;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private bool _dirty;

    public ConversationMemoryStore(
        string path,
        MemoryOptions? options = null,
        LocalDiagnosticLog? diagnostics = null)
    {
        _path = Path.GetFullPath(path);
        _archivePath = Path.ChangeExtension(_path, null) + ".archive.jsonl";
        _options = options ?? MemoryOptions.FromEnvironment();
        _options.Validate();
        _diagnostics = diagnostics ?? LocalDiagnosticLog.Default;
        Load();
    }

    public MemoryOptions Options => _options;
    public string FilePath => _path;
    public DateTimeOffset? LastConsolidatedAt { get { lock (_gate) return _lastConsolidatedAt; } }

    /// <summary>直接回答「她知道什么」。调用方不应修改返回的集合。</summary>
    public IReadOnlyList<MemoryRecord> Snapshot()
    {
        lock (_gate) return _records.ToArray();
    }

    public int Count { get { lock (_gate) return _records.Count; } }

    // ── 写入 ──────────────────────────────────────────────────────────────

    /// <summary>记录一轮真实对话。零 LLM 成本，是阶段 1 的检索主力。</summary>
    public MemoryRecord? ObserveTurn(string speaker, string text, DateTimeOffset at)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length < 2 || Noise.IsMatch(trimmed))
            return null;

        lock (_gate)
        {
            var fingerprint = Fingerprint(speaker, trimmed);
            // 同一句话在极短时间内重复（重复投递、导入与实时写入重叠）只留一条。
            if (_byFingerprint.TryGetValue(fingerprint, out var existing) &&
                Math.Abs((existing.ObservedAt - at).TotalSeconds) < 120)
            {
                if (existing.Kind != MemoryKind.Turn)
                    return existing; // 已提炼过的事实不要降级回原始轮次
                return null;
            }

            var scope = TemporalExpression.Parse(trimmed, at);
            var record = new MemoryRecord
            {
                Id = "m" + Guid.NewGuid().ToString("N")[..16],
                Kind = MemoryKind.Turn,
                Text = trimmed,
                Speaker = speaker,
                ObservedAt = at,
                RecordedAt = at,
                ValidUntil = scope.ValidUntil,
                Importance = EstimateImportance(trimmed, speaker),
                Fingerprint = fingerprint,
            };
            _records.Add(record);
            _byFingerprint[fingerprint] = record;
            TouchSave(at);
            return record;
        }
    }

    /// <summary>
    /// 从既有聊天记录补齐历史。只导入游标之后的部分，所以反复启动不会重复。
    /// 返回本次实际导入的条数。
    /// </summary>
    public int ImportChatLog(IEnumerable<(string Time, string Role, string Text)> entries, DateTimeOffset now)
    {
        var list = entries.ToList();
        lock (_gate)
        {
            if (list.Count <= _importedChatEntries)
                return 0;

            var imported = 0;
            foreach (var (time, role, text) in list.Skip(_importedChatEntries))
            {
                // 旧聊天记录的时间是本地时间字符串；解析失败就退回当前时间。
                var observedAt = DateTimeOffset.TryParse(time, out var parsed) ? parsed : now;
                var trimmed = (text ?? "").Trim();
                if (trimmed.Length < 2 || Noise.IsMatch(trimmed))
                    continue;

                var fingerprint = Fingerprint(role, trimmed);
                if (_byFingerprint.ContainsKey(fingerprint))
                    continue;

                var scope = TemporalExpression.Parse(trimmed, observedAt);
                var record = new MemoryRecord
                {
                    Id = "m" + Guid.NewGuid().ToString("N")[..16],
                    Kind = MemoryKind.Turn,
                    Text = trimmed,
                    Speaker = role,
                    ObservedAt = observedAt,
                    RecordedAt = now,
                    ValidUntil = scope.ValidUntil,
                    Importance = EstimateImportance(trimmed, role),
                    Fingerprint = fingerprint,
                };
                _records.Add(record);
                _byFingerprint[fingerprint] = record;
                imported++;
            }

            _importedChatEntries = list.Count;
            _dirty = true;
            Save();
            return imported;
        }
    }

    /// <summary>写入一条提炼后的记忆。相同指纹只更新不重复追加。</summary>
    public MemoryRecord AddConsolidated(
        string text,
        MemoryKind kind,
        double importance,
        IReadOnlyList<string> sources,
        DateTimeOffset observedAt,
        DateTimeOffset now,
        DateTimeOffset? validUntil,
        string? supersedesId)
    {
        lock (_gate)
        {
            var trimmed = (text ?? "").Trim();
            var fingerprint = Fingerprint(kind.ToString(), trimmed);
            MemoryRecord record;
            if (_byFingerprint.TryGetValue(fingerprint, out var existing) && existing.Kind == kind)
            {
                existing.ObservedAt = observedAt;
                existing.ValidUntil = validUntil;
                existing.Importance = Math.Max(existing.Importance, importance);
                existing.ConsolidatedAt = now;
                foreach (var source in sources)
                    if (!existing.Sources.Contains(source))
                        existing.Sources.Add(source);
                record = existing;
            }
            else
            {
                record = new MemoryRecord
                {
                    Id = "m" + Guid.NewGuid().ToString("N")[..16],
                    Kind = kind,
                    Text = trimmed,
                    Speaker = "consolidation",
                    ObservedAt = observedAt,
                    RecordedAt = now,
                    ValidUntil = validUntil,
                    Importance = Math.Clamp(importance, 0.05, 1.0),
                    Fingerprint = fingerprint,
                    Sources = [.. sources],
                    ConsolidatedAt = now,
                };
                _records.Add(record);
                _byFingerprint[fingerprint] = record;
            }

            if (!string.IsNullOrWhiteSpace(supersedesId) && supersedesId != record.Id)
                SupersedeLocked(supersedesId, record.Id, now);

            _dirty = true;
            TouchSave(now);
            return record;
        }
    }

    /// <summary>把旧记录标成被取代。不删除——「当时是什么情况」仍然可查。</summary>
    public bool Supersede(string oldId, string newId, DateTimeOffset now)
    {
        lock (_gate) return SupersedeLocked(oldId, newId, now);
    }

    private bool SupersedeLocked(string oldId, string newId, DateTimeOffset now)
    {
        var old = _records.FirstOrDefault(r => r.Id == oldId);
        if (old is null || old.Id == newId)
            return false;
        old.SupersededById = newId;
        old.ValidUntil ??= now;
        _dirty = true;
        return true;
    }

    /// <summary>标注某轮已被整合处理，避免反复提炼。</summary>
    public void MarkConsolidated(IEnumerable<string> ids, DateTimeOffset now)
    {
        lock (_gate)
        {
            var set = ids.ToHashSet(StringComparer.Ordinal);
            foreach (var record in _records.Where(r => set.Contains(r.Id)))
                record.ConsolidatedAt = now;
            _lastConsolidatedAt = now;
            _dirty = true;
            TouchSave(now);
        }
    }

    /// <summary>记录被检索命中，用于频率型打分（常被谈起的记忆更该留在前台）。</summary>
    public void TouchAccess(IEnumerable<string> ids, DateTimeOffset now)
    {
        lock (_gate)
        {
            var set = ids.ToHashSet(StringComparer.Ordinal);
            foreach (var record in _records.Where(r => set.Contains(r.Id)))
            {
                record.AccessCount++;
                record.LastAccessedAt = now;
            }
            _dirty = true;
        }
    }

    /// <summary>尚未提炼的轮次，按时间正序。</summary>
    public IReadOnlyList<MemoryRecord> UnconsolidatedTurns(int max)
    {
        lock (_gate)
            return _records
                .Where(r => r.Kind == MemoryKind.Turn && r.ConsolidatedAt is null)
                .OrderBy(r => r.ObservedAt)
                .Take(max)
                .ToArray();
    }

    public int UnconsolidatedTurnCount
    {
        get { lock (_gate) return _records.Count(r => r.Kind == MemoryKind.Turn && r.ConsolidatedAt is null); }
    }

    /// <summary>按 id 取记录，供取代关系解析。</summary>
    public MemoryRecord? FindById(string id)
    {
        lock (_gate) return _records.FirstOrDefault(r => r.Id == id);
    }

    /// <summary>重置整合游标，让下一轮维护重新提炼全部历史。</summary>
    public void ResetConsolidation(DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var record in _records.Where(r => r.Kind == MemoryKind.Turn))
                record.ConsolidatedAt = null;
            _lastConsolidatedAt = null;
            _dirty = true;
            TouchSave(now);
        }
    }

    // ── 持久化与归档 ──────────────────────────────────────────────────────

    /// <summary>把内存状态写盘。维护循环与退出路径应显式调用，避免丢最近几秒。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty)
                return;
            Save();
        }
    }

    /// <summary>写盘节流：一轮对话一次全量重写代价过高，改动先留在内存。</summary>
    private void TouchSave(DateTimeOffset now)
    {
        if (now - _lastSave < TimeSpan.FromSeconds(3))
            return;
        Save();
    }

    private void Save()
    {
        try
        {
            ArchiveOverflow();
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var state = new MemoryFileState
            {
                ImportedChatEntries = _importedChatEntries,
                LastConsolidatedAt = _lastConsolidatedAt,
                Records = _records,
            };
            // 先写临时文件再替换，避免进程中断留下半个 JSON。
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, MemoryFileState.Json), Encoding.UTF8);
            File.Move(temp, _path, overwrite: true);
            _dirty = false;
            _lastSave = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            // 记忆写盘失败不能影响对话。
            _diagnostics.Write("memory.save", ex);
        }
    }

    /// <summary>
    /// 活跃集超限时把最旧、最不重要、已被取代的记录移入归档文件。
    /// 归档 = 追加到 jsonl 后从活跃集移除，随时可以人工回捞，不会静默丢失。
    /// </summary>
    private void ArchiveOverflow()
    {
        if (_records.Count <= _options.MaxActiveRecords)
            return;

        var victims = _records
            .Where(r => r.Kind == MemoryKind.Turn)
            .OrderByDescending(r => r.Superseded)
            .ThenByDescending(r => r.ValidUntil is not null && r.ValidUntil < DateTimeOffset.Now)
            .ThenBy(r => r.Importance)
            .ThenBy(r => r.ObservedAt)
            .Take(_records.Count - _options.MaxActiveRecords)
            .ToArray();
        if (victims.Length == 0)
            return;

        try
        {
            var directory = Path.GetDirectoryName(_archivePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            using var writer = new StreamWriter(_archivePath, append: true, Encoding.UTF8);
            foreach (var victim in victims)
                writer.WriteLine(JsonSerializer.Serialize(victim, MemoryFileState.Json));
        }
        catch (Exception ex)
        {
            _diagnostics.Write("memory.archive", ex);
            return; // 归档失败就不要丢内存里的记录
        }

        var victimIds = victims.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        _records.RemoveAll(r => victimIds.Contains(r.Id));
        foreach (var victim in victims)
            _byFingerprint.Remove(victim.Fingerprint);
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;
        try
        {
            var state = JsonSerializer.Deserialize<MemoryFileState>(
                File.ReadAllText(_path, Encoding.UTF8), MemoryFileState.Json);
            if (state is null)
                return;
            _importedChatEntries = state.ImportedChatEntries;
            _lastConsolidatedAt = state.LastConsolidatedAt;
            _records.AddRange(state.Records);
            foreach (var record in _records)
                _byFingerprint[record.Fingerprint] = record;
        }
        catch (Exception ex)
        {
            // 文件损坏时从空记忆继续，不阻塞启动。
            _diagnostics.Write("memory.load", ex);
            _records.Clear();
            _byFingerprint.Clear();
        }
    }

    // ── 辅助 ──────────────────────────────────────────────────────────────

    /// <summary>与剧情索引共用同一套分词，保证两种检索的「词」是同一个概念。</summary>
    public static string Fingerprint(string speaker, string text)
        => speaker + "\u001f" + StoryQueryAnalyzer.Normalize(text);

    /// <summary>
    /// 零成本重要度：用户说的、带明确记忆标记的、像个人事实的，都更值得长期保留。
    /// 这里只做粗排，精排交给整合阶段的 LLM 判断。
    /// </summary>
    public static double EstimateImportance(string text, string speaker)
    {
        var score = 0.30;
        if (string.Equals(speaker, "user", StringComparison.OrdinalIgnoreCase))
            score += 0.10;
        if (ExplicitMarkers.IsMatch(text))
            score += 0.35;
        if (PersonalFacts.IsMatch(text))
            score += 0.18;
        if (text.Length is >= 8 and <= 60)
            score += 0.08;
        if (text.Length > 200)
            score -= 0.10;
        if (text.EndsWith('？') || text.EndsWith('?'))
            score -= 0.12;
        return Math.Clamp(score, 0.05, 1.0);
    }
}
