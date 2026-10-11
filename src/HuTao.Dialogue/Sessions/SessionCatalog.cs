namespace HuTao.Dialogue.Sessions;

/// <summary>
/// index.json 的内存态。**纯逻辑**：排序、未读、置顶、免打扰，以及增删改。
/// 这个类里不许出现 <c>System.IO</c>——读写是 <see cref="SessionStore"/> 的职责，
/// 这样"侧栏顺序为什么是这样"可以在零文件系统的情况下被完整测出来。
///
/// 所有变更方法返回**新实例**而不是就地改：UI 会长期持有旧的
/// <c>SessionDescriptor</c> 引用，就地改会让绑定视图与数据悄悄不同步。
/// </summary>
public sealed class SessionCatalog
{
    private readonly List<SessionDescriptor> _sessions;

    public SessionCatalog(IEnumerable<SessionDescriptor> sessions)
        => _sessions = sessions.Select(SessionFactory.Normalize).ToList();

    public IReadOnlyList<SessionDescriptor> Sessions => _sessions;

    /// <summary>
    /// 侧栏顺序：置顶优先 → 最近活跃降序 → Id 升序。
    /// **第三个键不能省**：同一毫秒活跃的两个会话若没有确定性的兜底键，
    /// 两次读取的顺序可能不同，侧栏会在用户眼前抖动。测试用例钉死了这条。
    /// </summary>
    public IReadOnlyList<SessionDescriptor> Ordered()
        => _sessions
            .OrderByDescending(session => session.Pinned)
            .ThenByDescending(session => session.LastActiveAt)
            .ThenBy(session => session.Id, StringComparer.Ordinal)
            .ToArray();

    public SessionDescriptor? Find(string sessionId)
        => _sessions.FirstOrDefault(session =>
            string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>存在则整体替换（按 Id），不存在则追加。</summary>
    public SessionCatalog Upsert(SessionDescriptor descriptor)
    {
        var next = new List<SessionDescriptor>(_sessions);
        var index = next.FindIndex(session =>
            string.Equals(session.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            next[index] = descriptor;
        else
            next.Add(descriptor);
        return new SessionCatalog(next);
    }

    public SessionCatalog Remove(string sessionId)
        => new(_sessions.Where(session =>
            !string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase)));

    /// <summary>把未读清零，并把「已读位置」推进到 <paramref name="now"/>。</summary>
    public SessionCatalog MarkRead(string sessionId, DateTimeOffset now)
        => Patch(sessionId, session => session with { UnreadCount = 0 });

    public SessionCatalog BumpUnread(string sessionId, int delta = 1)
        => Patch(sessionId, session => session with { UnreadCount = Math.Max(0, session.UnreadCount + delta) });

    /// <summary>一轮结束：更新最近活跃时间与侧栏预览。</summary>
    public SessionCatalog Touch(string sessionId, DateTimeOffset at, string preview)
        => Patch(sessionId, session => session with
        {
            LastActiveAt = at,
            LastPreview = Truncate(preview, 60),
        });

    public SessionCatalog SetPinned(string sessionId, bool pinned)
        => Patch(sessionId, session => session with { Pinned = pinned });

    public SessionCatalog SetMuted(string sessionId, bool muted)
        => Patch(sessionId, session => session with { Muted = muted });

    public SessionCatalog SetAllowProactive(string sessionId, bool allow)
        => Patch(sessionId, session => session with { AllowProactive = allow });

    public SessionCatalog Rename(string sessionId, string title)
        => Patch(sessionId, session => session with { Title = title.Trim() });

    /// <summary>成员变更：套用新的在场区间，并重算 MemberIds。视角缓存的重建由调用方负责。</summary>
    public SessionCatalog ApplyPresences(string sessionId, IReadOnlyList<MemberPresence> presences)
        => Patch(sessionId, session => session with { Presences = presences });

    private SessionCatalog Patch(string sessionId, Func<SessionDescriptor, SessionDescriptor> change)
    {
        var index = _sessions.FindIndex(session =>
            string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return this;

        var next = new List<SessionDescriptor>(_sessions)
        {
            [index] = SessionFactory.Normalize(change(_sessions[index])),
        };
        return new SessionCatalog(next);
    }

    internal static string Truncate(string text, int max)
    {
        var flat = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max];
    }
}
