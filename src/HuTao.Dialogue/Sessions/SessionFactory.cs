using System.Security.Cryptography;
using HuTao.Persona;

namespace HuTao.Dialogue.Sessions;

/// <summary>
/// 建会话、生成 Id、去重与校验。所有"什么样的会话才算合法"的规则集中在这里，
/// 这样 UI（T-20 的对话框）与迁移（T-13）不会各写一份校验、然后慢慢漂移。
/// </summary>
public static class SessionFactory
{
    /// <summary>群聊成员下限。少于两位时 <c>ChatRoom</c> 会直接拒绝构造。</summary>
    public const int MinGroupMembers = 2;

    /// <summary>
    /// 群聊成员上限。这不是任意的：群聊的场景旁白要把其他成员名字全列出来
    /// （`ChatRoom.BuildSpeakerInput`），人越多这句话越长，模型越容易跑题。
    /// </summary>
    public const int MaxGroupMembers = 8;

    public const int MaxTitleLength = 24;

    /// <summary>单聊的会话 Id。与 characterId 一一对应，所以「每个角色只有一个单聊」是结构性保证，不靠查重。</summary>
    public static string SingleSessionId(string characterId) => "single_" + characterId;

    /// <summary>
    /// 用户自建群聊的 Id。
    /// 时间戳让人一眼看出"什么时候建的"，4 位随机 hex 消除同一秒连点两次的碰撞。
    /// 用 <see cref="RandomNumberGenerator"/> 而不是 <c>Random</c>：后者在多线程下可能返回同一个序列。
    /// </summary>
    public static string NewGroupSessionId(DateTimeOffset now)
        => $"group_{now:yyyyMMddTHHmmss}-{RandomNumberGenerator.GetHexString(4).ToLowerInvariant()}";

    /// <summary>默认群聊的固定 Id。与用户自建群聊区分开，便于迁移与排障。</summary>
    public const string DefaultGroupSessionId = "group_default";

    /// <summary>
    /// 单聊的展示名 = 角色名。群聊没给标题时用「A、B的群聊」这种兜底，
    /// 但**不要**在对话框里预填——用户应该自己起名（见设计书 §6.5 的 N3）。
    /// </summary>
    public static string GroupTitleFor(IReadOnlyList<CharacterDefinition> members)
        => members.Count == 0 ? "新的群聊" : $"{members[0].Name}、{members[1 % members.Count].Name}的群聊";

    /// <summary>
    /// 归一化：把从 <see cref="SessionDescriptor.Presences"/> 派生的字段重新算一遍。
    /// 任何构造/反序列化出来的描述符都必须过这一道，否则 MemberIds 会与 Presences 漂移
    /// ——那正是设计书里警告过的"两处手动同步"。
    /// </summary>
    public static SessionDescriptor Normalize(SessionDescriptor descriptor)
    {
        var target = descriptor.Kind == SessionKind.Single ? 1 : Math.Max(0, descriptor.MemberIds.Count);
        var presences = (descriptor.Presences ?? [])
            .Where(presence => !string.IsNullOrWhiteSpace(presence.CharacterId))
            .ToList();

        // Presences 缺失/不全时按 MemberIds 补齐：老数据（迁移产物）与手写的
        // session.json 都可能没有它，此时"从会话创建起就在场"是最合理的假设。
        // 逐个补而不是整体替换：能保留已有的 JoinedAt，不把中途加入者洗成元老。
        foreach (var memberId in descriptor.MemberIds ?? [])
        {
            if (string.IsNullOrWhiteSpace(memberId))
                continue;
            if (presences.Any(presence =>
                string.Equals(presence.CharacterId, memberId, StringComparison.OrdinalIgnoreCase)))
                continue;
            presences.Add(new MemberPresence(memberId, descriptor.CreatedAt));
        }

        // 用户在 UI 里移除成员时，LeftAt 会被写上；MemberIds 必须只反映"还在场"的人。
        var memberIds = presences
            .Where(presence => presence.LeftAt is null)
            .Select(presence => presence.CharacterId)
            .ToList();

        // Single 的成员恒为 1：即便调用方塞了多个或零个，也按 Kind 校正，
        // 免得单聊里出现"多个人说话"这种不可能的状态。
        if (target == 1 && memberIds.Count != 1 && presences.Count > 0)
        {
            var first = presences[0] with { LeftAt = null };
            presences = [first];
            memberIds = [first.CharacterId];
        }

        return descriptor with
        {
            Title = (descriptor.Title ?? "").Trim(),
            Emoji = string.IsNullOrWhiteSpace(descriptor.Emoji) ? "💬" : descriptor.Emoji,
            AccentArgb = string.IsNullOrWhiteSpace(descriptor.AccentArgb) ? "#FF8C60BE" : descriptor.AccentArgb,
            Presences = presences,
            MemberIds = memberIds,
            LastPreview = descriptor.LastPreview ?? "",
            UnreadCount = Math.Max(0, descriptor.UnreadCount),
        };
    }

    /// <summary>
    /// 校验。失败一律 <see cref="ArgumentException"/> —— 这是**编程错误**（UI 传了非法成员），
    /// 不是运行期可恢复的用户输入问题；后者由对话框自己先禁用确认按钮。
    /// </summary>
    public static void Validate(SessionKind kind, string title, IReadOnlyList<string> memberIds)
    {
        var distinct = memberIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (kind == SessionKind.Single)
        {
            if (distinct.Count != 1)
                throw new ArgumentException($"单聊必须恰好 1 位角色，收到 {distinct.Count} 位。", nameof(memberIds));
        }
        else
        {
            if (distinct.Count < MinGroupMembers || distinct.Count > MaxGroupMembers)
                throw new ArgumentException(
                    $"群聊成员须在 {MinGroupMembers}..{MaxGroupMembers} 之间，收到 {distinct.Count} 位。",
                    nameof(memberIds));
        }

        foreach (var memberId in distinct)
        {
            if (CharacterCatalog.Find(memberId) is null)
                throw new ArgumentException($"未注册角色: {memberId}", nameof(memberIds));
        }

        if (kind == SessionKind.Group)
        {
            var trimmed = (title ?? "").Trim();
            if (trimmed.Length is < 1 or > MaxTitleLength)
                throw new ArgumentException($"群名须为 1..{MaxTitleLength} 字。", nameof(title));
        }
    }

    /// <summary>
    /// 建单聊（幂等）。已存在则返回既有的，**不重置**任何状态——
    /// 用户重复点「和胡桃聊天」不该把未读清零或把置顶取消。
    /// </summary>
    public static async Task<SessionDescriptor> EnsureSingleAsync(
        SessionStore store,
        SessionCatalog catalog,
        string characterId,
        Func<string, string>? accentProvider = null,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        Validate(SessionKind.Single, "", [characterId]);
        var id = SingleSessionId(characterId);
        if (catalog.Find(id) is { } existing)
            return existing;

        var definition = CharacterCatalog.Get(characterId);
        var at = now ?? DateTimeOffset.Now;
        var descriptor = Normalize(new SessionDescriptor(
            id,
            SessionKind.Single,
            definition.Name,
            definition.Emoji,
            accentProvider?.Invoke(characterId) ?? DefaultAccent(characterId),
            [characterId],
            [new MemberPresence(characterId, at)],
            at,
            at,
            "",
            Pinned: false,
            Muted: false,
            AllowProactive: false,
            UnreadCount: 0));

        await store.CreateSessionAsync(descriptor, ct).ConfigureAwait(false);
        return descriptor;
    }

    /// <summary>
    /// 建群聊。**先查重**：成员集合完全相同的群已存在时直接返回那个，
    /// 不新建——否则用户点两次"发起群聊"就会得到两个一模一样的群，侧栏里还分不清。
    /// 比对的是"当前在场成员"而不是 MemberIds，因为两者可能漂移。
    /// </summary>
    public static async Task<SessionDescriptor> EnsureGroupAsync(
        SessionStore store,
        SessionCatalog catalog,
        IReadOnlyList<string> memberIds,
        string title,
        Func<string, string>? accentProvider = null,
        DateTimeOffset? now = null,
        CancellationToken ct = default,
        string? sessionId = null)
    {
        Validate(SessionKind.Group, title, memberIds);
        var distinct = memberIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 先按**明确指定**的 id 查：迁移要的"默认群聊"有固定 id（group_default），
        // 不能每次都生成新的时间戳 id——那样"幂等迁移"就会每次多建一个群。
        if (!string.IsNullOrWhiteSpace(sessionId) && catalog.Find(sessionId) is { } byId)
            return byId;

        var wanted = new HashSet<string>(distinct, StringComparer.OrdinalIgnoreCase);
        var duplicate = catalog.Sessions.FirstOrDefault(session =>
            session.Kind == SessionKind.Group &&
            session.ActivePresences.Select(p => p.CharacterId).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(wanted));
        if (duplicate is not null)
            return duplicate;

        var at = now ?? DateTimeOffset.Now;
        var descriptor = Normalize(new SessionDescriptor(
            string.IsNullOrWhiteSpace(sessionId) ? NewGroupSessionId(at) : sessionId,
            SessionKind.Group,
            title.Trim(),
            "💬",
            accentProvider?.Invoke(distinct[0]) ?? DefaultAccent(distinct[0]),
            distinct,
            distinct.Select(id => new MemberPresence(id, at)).ToArray(),
            at,
            at,
            "",
            Pinned: false,
            Muted: false,
            AllowProactive: false,
            UnreadCount: 0));

        await store.CreateSessionAsync(descriptor, ct).ConfigureAwait(false);
        return descriptor;
    }

    /// <summary>
    /// Core 拿不到 WPF 的 <c>CharacterThemeCatalog</c>（那会反向依赖宿主层，
    /// 被 `dependency_direction_acyclic` 判红）。这里给一个与四位角色主题同色系的
    /// 确定性兜底：宿主可以用 <c>accentProvider</c> 覆盖它。
    /// </summary>
    public static string DefaultAccent(string characterId) => characterId.ToLowerInvariant() switch
    {
        "hutao" => "#FFE08A3C",
        "furina" => "#FF4B82BE",
        "klee" => "#FFDA4D3E",
        "lacrimosa" => "#FF8C60BE",
        _ => "#FF8C60BE",
    };
}
