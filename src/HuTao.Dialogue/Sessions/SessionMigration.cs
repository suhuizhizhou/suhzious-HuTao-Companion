using HuTao.Dialogue.Storage;
using HuTao.Foundation.Diagnostics;
using HuTao.Persona;
using HuTao.Dialogue.Runtime;

namespace HuTao.Dialogue.Sessions;

/// <summary>迁移结果。宿主把它打印出来——"迁了几条"必须是可见的，不能静默。</summary>
public sealed record SessionMigrationReport(
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 一次性迁移：把桌宠时代的 <c>data/chat_log_{角色}.json</c> 搬进会话目录模型。
///
/// 三条原则，缺一条都会变成事故：
/// 1. **幂等** —— 任何已存在的目标一律跳过，所以它在每次启动跑都没问题；
/// 2. **只复制、不移动、不删除** —— 原文件全部保留，用户可以随时回退到旧版本；
/// 3. **条数对不上要报警** —— <c>ChatLogStore.Load</c> 会吞掉异常返回空列表
///    （那是刻意的隐私设计），如果直接照单全收，一次读取失败就会让用户以为
///    "聊天记录全没了"。所以源文件存在却读出 0 条时必须写 Warning。
/// </summary>
public static class SessionMigration
{
    public const string DefaultGroupTitle = "提瓦特茶话会";

    public static async Task<SessionMigrationReport> RunAsync(
        SessionStore store,
        string repositoryRoot,
        IReadOnlyList<CharacterDefinition> characters,
        Func<string, string>? accentProvider = null,
        LocalDiagnosticLog? log = null,
        CancellationToken ct = default)
    {
        var diagnostics = log ?? LocalDiagnosticLog.Default;
        var created = new List<string>();
        var skipped = new List<string>();
        var warnings = new List<string>();
        var catalog = await store.LoadCatalogAsync(ct).ConfigureAwait(false);

        foreach (var character in characters)
        {
            ct.ThrowIfCancellationRequested();
            var sessionId = SessionFactory.SingleSessionId(character.Id);
            var target = store.PathsFor(sessionId);

            if (catalog.Find(sessionId) is not null || File.Exists(target.TimelinePath))
            {
                skipped.Add(sessionId);
                continue;
            }

            var legacyLog = Path.Combine(repositoryRoot, "data", $"chat_log_{character.Id}.json");
            if (!File.Exists(legacyLog))
            {
                skipped.Add(sessionId);
                continue;
            }

            List<ChatEntry> entries;
            try
            {
                entries = LoadLegacyEntries(legacyLog);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                // ChatLogStore.Load 会把解析异常吞成空列表，那是它作为隐私边界的正确行为；
                // 但在迁移里"静默迁 0 条"等于让用户以为聊天记录没了。所以这里自己先解析一遍，
                // 失败就当作硬错误报告，并且**不动原文件**。
                diagnostics.Write("session.migration.parse", ex);
                warnings.Add($"{sessionId}: {Path.GetFileName(legacyLog)} 解析失败，已跳过（原文件未改动）。");
                skipped.Add(sessionId);
                continue;
            }

            if (entries.Count == 0)
            {
                warnings.Add($"{sessionId}: 源文件 {Path.GetFileName(legacyLog)} 存在但读出 0 条，已跳过（原文件未改动）。");
                skipped.Add(sessionId);
                continue;
            }

            var at = DateTimeOffset.Now;
            var descriptor = SessionFactory.Normalize(new SessionDescriptor(
                sessionId,
                SessionKind.Single,
                character.Name,
                character.Emoji,
                accentProvider?.Invoke(character.Id) ?? SessionFactory.DefaultAccent(character.Id),
                [character.Id],
                [new MemberPresence(character.Id, at)],
                at,
                at,
                "",
                Pinned: false, Muted: false, AllowProactive: false, UnreadCount: 0));
            await store.CreateSessionAsync(descriptor, ct).ConfigureAwait(false);

            var previousPresence = descriptor.Presences;
            var migrated = 0;
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var isUser = string.Equals(entry.Role, "user", StringComparison.OrdinalIgnoreCase);
                var timestamp = DateTimeOffset.TryParse(entry.Time, out var parsed) ? parsed : at;
                var turn = new ConversationTurn(
                    "",
                    timestamp,
                    isUser ? "user" : character.Id,
                    isUser ? "你" : character.Name,
                    entry.Text ?? "",
                    IsUser: isUser,
                    AudioPath: entry.Audio,
                    Audience: [character.Id],
                    Kind: ConversationTurnKind.Message);
                var stored = await store.AppendAsync(sessionId, turn, ct).ConfigureAwait(false);
                // 视角缓存也一并建好：迁移的价值就在于"打开就能用"，
                // 不建的话首次激活要现场重算一遍整份时间线。
                await store.AppendViewAsync(sessionId, character.Id, stored, ct).ConfigureAwait(false);
                migrated++;
            }

            // 用真实的首条时间作为在场起点，而不是"迁移那一刻"——
            // 否则迁移会把全部历史都算成"加入之前"，投影出来是空的。
            var first = (await store.LoadTimelineAsync(sessionId, ct).ConfigureAwait(false)).FirstOrDefault();
            if (first is not null)
            {
                var adjusted = descriptor with
                {
                    Presences = [new MemberPresence(character.Id, first.At)],
                    CreatedAt = first.At,
                    LastActiveAt = DateTimeOffset.Now,
                    LastPreview = SessionCatalog.Truncate(entries[^1].Text ?? "", 60),
                };
                await store.SaveSessionAsync(adjusted, ct).ConfigureAwait(false);
                await store.SaveViewAsync(sessionId, character.Id,
                    await store.LoadViewAsync(sessionId, character.Id, ct).ConfigureAwait(false), ct)
                    .ConfigureAwait(false);
                catalog = catalog.Upsert(adjusted);
            }
            else
            {
                catalog = catalog.Upsert(descriptor);
            }

            created.Add($"{sessionId} ({migrated} 条)");
            await CopyMemoryFileAsync(repositoryRoot, character, sessionId, warnings, diagnostics, ct)
                .ConfigureAwait(false);
            _ = previousPresence;
        }

        // 默认群聊：只建壳，**不搬** data/memory_{id}_chatroom.json 的内容。
        // 那个 scope 属于旧的聊天室窗口，与新群聊的 scope 不同；
        // 硬搬会把"上一场对话的记忆"灌进一个还没说过话的新群。
        var cast = CharacterCatalog.DefaultCastIds
            .Select(id => CharacterCatalog.Find(id))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .ToArray();
        if (cast.Length >= SessionFactory.MinGroupMembers)
        {
            // 先按**固定 id** 查：默认群聊必须始终叫 group_default，
            // 否则"幂等迁移"每次都会新建一个群（踩过：产生了
            // group_20261010T124609-a106 这种时间戳 id，而查询用的是 group_default）。
            var existing = catalog.Find(SessionFactory.DefaultGroupSessionId) is not null
                || catalog.Sessions.Any(session =>
                    session.Kind == SessionKind.Group &&
                    session.ActivePresences.Select(p => p.CharacterId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(cast.Select(c => c.Id)));
            if (existing)
            {
                skipped.Add(SessionFactory.DefaultGroupSessionId);
            }
            else
            {
                try
                {
                    var group = await SessionFactory.EnsureGroupAsync(
                        store, catalog, cast.Select(c => c.Id).ToArray(), DefaultGroupTitle,
                        accentProvider, ct: ct, sessionId: SessionFactory.DefaultGroupSessionId)
                        .ConfigureAwait(false);
                    created.Add(group.Id);
                    catalog = catalog.Upsert(group);
                }
                catch (ArgumentException ex)
                {
                    warnings.Add($"默认群聊未创建：{ex.Message}");
                }
            }
        }

        await store.SaveCatalogAsync(catalog, ct).ConfigureAwait(false);
        return new SessionMigrationReport(created, skipped, warnings);
    }

    /// <summary>
    /// 读旧格式聊天记录（顶层 JSON 数组）。空文件视为合法的空记录；有内容但解析不出
    /// 数组则抛 <see cref="System.Text.Json.JsonException"/>，由调用方转成 Warning。
    /// </summary>
    internal static List<ChatEntry> LoadLegacyEntries(string path)
    {
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return System.Text.Json.JsonSerializer.Deserialize<List<ChatEntry>>(text) ?? [];
    }

    /// <summary>
    /// 把桌宠的长期记忆复制成新会话作用域的那一份。
    ///
    /// 目标路径**故意不手拼**，而是问 <see cref="Runtime.KnowledgeHostPaths.Resolve"/>：
    /// 它是全项目唯一的作用域→路径映射，自己再写一遍就等于埋一个"改名了但只有一处知道"的雷。
    /// 当前映射是 <c>memory_{角色}_{会话}.json</c>（suffix = "_" + scope），但以上面那句为准。
    ///
    /// 顺带把该角色的重要记忆文件（`记住…` 彩蛋）也复制到作用域版本，
    /// 否则用户在会话里会发现"我明明让她记过"。
    /// </summary>
    private static async Task CopyMemoryFileAsync(
        string repositoryRoot,
        CharacterDefinition character,
        string sessionId,
        List<string> warnings,
        LocalDiagnosticLog log,
        CancellationToken ct)
    {
        KnowledgeHostPaths legacy;
        KnowledgeHostPaths scoped;        try
        {
            legacy = KnowledgeHostPaths.Resolve(repositoryRoot, character, "local", "desktop");
            scoped = KnowledgeHostPaths.Resolve(repositoryRoot, character, "local", sessionId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            log.Write("session.migration.paths", ex);
            warnings.Add($"{sessionId}: 无法解析记忆路径，记忆未迁移（原文件未改动）。");
            return;
        }

        var pairs = new List<(string Source, string Destination)>
        {
            (legacy.Memory, scoped.Memory),
            (legacy.Archive, scoped.Archive),
        };
        if (legacy.ImportantItems is not null && scoped.ImportantItems is not null)
            pairs.Add((legacy.ImportantItems, scoped.ImportantItems));

        foreach (var (source, destination) in pairs)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(source) || File.Exists(destination))
                continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Write("session.migration.memory", ex);
                warnings.Add($"{sessionId}: 记忆文件复制失败（{Path.GetFileName(source)}），原文件未改动。");
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
