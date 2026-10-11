namespace HuTao.Dialogue.Sessions;

using System.Text.Json.Serialization;

/// <summary>
/// 会话类型。单聊与群聊在 QQNT 形态里是**平级**的（侧栏里就是相邻两行），
/// 所以它们是同一枚举的两个值，而不是两个不同的模型——差异只在成员数与发言调度。
/// </summary>
public enum SessionKind
{
    /// <summary>1 位角色。发言者唯一，不需要导演。</summary>
    Single,

    /// <summary>2..8 位角色。谁说话由 <c>IChatRoomDirector</c> 决定。</summary>
    Group,
}

/// <summary>
/// 一条记录的呈现类别。它决定 UI 怎么画，也决定 <c>AudienceProjection</c> 怎么投影：
/// 系统消息与旁白**不是角色台词**，不能被包装成「某某说」。
/// </summary>
public enum ConversationTurnKind
{
    /// <summary>正常消息：用户或某位角色说的。</summary>
    Message,

    /// <summary>系统提示（例如「安魂曲这一轮没能开口，已换下一位」）。居中灰字，不属于任何角色。</summary>
    System,

    /// <summary>场景旁白（例如「你们围坐在一起，气氛轻松」）。居中斜体。</summary>
    Hint,
}

/// <summary>
/// 成员在场区间。存在的理由只有一个：判断一条历史消息该不该出现在某个人的视角里。
/// 没有它，中途加入的成员会在「视角缓存被删掉后重建」时凭空拿到加入前的历史，
/// 被移出的成员也再也算不出边界。
/// </summary>
public sealed record MemberPresence(
    string CharacterId,
    DateTimeOffset JoinedAt,
    DateTimeOffset? LeftAt = null)
{
    /// <summary>半开区间 [JoinedAt, LeftAt)：离开的那一刻起就不再算「在场」。</summary>
    public bool Contains(DateTimeOffset at) => at >= JoinedAt && (LeftAt is null || at < LeftAt);
}

/// <summary>
/// 时间线/视角里的一条记录。
///
/// 它与 <c>ChatRoomTurn</c> 的关键差别有两个，都是为了「重开可回」和「多人群聊」：
/// 1. <see cref="Id"/> —— 会话内单调递增，追加语义下唯一的定位手段；
/// 2. <see cref="Audience"/> —— 这条**被谁看见了**（不含发言者自己）。
///    群聊里 B 中途加入，就不该在切回时看到 A 与 C 之前的私聊。
///
/// 版本兼容由 `SessionStore.SchemaVersion` 在**文件级**声明，不做成实例属性：
/// 只读属性会被序列化进 JSON、却没有对应的构造参数，反序列化时直接抛。
/// 所有字段都是构造参数，往返才无损。
/// </summary>
public sealed record ConversationTurn(
    string Id,
    DateTimeOffset At,
    string SpeakerId,
    string SpeakerName,
    string Text,
    bool IsUser,
    string? AudioPath,
    IReadOnlyList<string> Audience,
    ConversationTurnKind Kind,
    /// <summary>
    /// 这是该次发言里的第几个气泡（从 0 开始）。
    ///
    /// 为什么一段一行而不是一行放整段：原版桌宠单聊是"一句一个气泡、逐段配音、
    /// 等本段播完再下一段"。要保住这个能力，分段必须是时间线上**可还原**的事实，
    /// 否则重建视图时只能得到一个大气泡（正是迁移后丢掉的观感）。
    ///
    /// 旧数据没有这个字段 → 反序列化得默认值 0 → 等价于"单段发言"，照常渲染。
    /// </summary>
    int UtteranceIndex = 0);

/// <summary>
/// 一个会话的展示主题（**整套配色**，不只是背景）。
///
/// 为什么是"整套"而不是只换背景：QQ 的"仅该成员聊天背景"之所以好看，
/// 是因为背景、气泡、文字、强调色是**一起**变的；只贴一张图而气泡还是默认白，
/// 看起来像贴纸而不是主题。
///
/// 存**值**（而不是只存一个预设 id）：换主题不必回查注册表，
/// 而且旧版本存的配色不会因为新版本改了预设而"变样"。
///
/// 颜色一律是 <c>#AARRGGBB</c> 字符串——Core 不许引用 WPF，只能在宿主层还原成画刷。
/// </summary>
public sealed record ChatTheme
{
    /// <summary>预设 id（如 <c>preset:hutao</c>）。给 UI 标"当前是哪个预设"，也便于排障。</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>消息区背景起点色。有背景图时被图盖住。</summary>
    public string BackgroundColor { get; init; } = "#FFF5F5F5";

    /// <summary>消息区背景终点色。非空时画**线性渐变**——柔光感是"好看"的主要来源。</summary>
    public string? BackgroundColor2 { get; init; }

    /// <summary>背景图，相对仓库根的正斜杠路径（如 <c>data/chat-themes/xxx.png</c>）。</summary>
    public string? BackgroundImage { get; init; }

    public double BackgroundImageOpacity { get; init; } = 1.0;

    /// <summary>图片填充：<c>UniformToFill</c>（铺满裁剪，默认）/ <c>Uniform</c>（完整可见，留边）。</summary>
    public string BackgroundImageStretch { get; init; } = "UniformToFill";

    public string OutgoingBubble { get; init; } = "#FFE08A3C";
    public string OutgoingText { get; init; } = "#FFFFFFFF";

    /// <summary>角色气泡底色与文字色。<c>null</c> 表示"用该角色自己的主题"（单聊常见）。</summary>
    public string? IncomingBubble { get; init; }
    public string? IncomingText { get; init; }

    public string WindowBackground { get; init; } = "#FFFFFFFF";
    public string SidebarBackground { get; init; } = "#FFF2F2F2";

    /// <summary>整体强调色（标题栏、选中态、发送按钮）。<c>null</c> 表示"用该会话角色的 accent"。</summary>
    public string? Accent { get; init; }

    /// <summary>标题与正文文字色。暗色主题必须改这两个，否则白底白字。</summary>
    public string TitleText { get; init; } = "#FF1A1A1A";
    public string BodyText { get; init; } = "#FF1A1A1A";

    /// <summary>复制一份并换掉 id/名字——"基于某预设做自定义"时用。</summary>
    public ChatTheme WithIdentity(string id, string name) => this with { Id = id, Name = name };

    [JsonIgnore]
    public bool HasImage => !string.IsNullOrWhiteSpace(BackgroundImage);
}

/// <summary>
/// 会话的自描述元数据。index.json 里的一项，也是每个会话目录下 session.json 的全部内容。
///
/// <see cref="Presences"/> 不是 <see cref="MemberIds"/> 的冗余副本：
/// 视角投影（<c>AudienceProjection</c>）靠在场区间判断「这条消息某人当时在不在」。
/// 只存当前成员列表的话，重建视角缓存时中途加入者会被错误地补上加入前的历史。
/// <see cref="MemberIds"/> 是从 <see cref="Presences"/> 派生的便捷投影
/// （= LeftAt 为 null 的那些人），由 <c>SessionFactory.Normalize</c> 统一维护，禁止两处手动同步。
/// </summary>
public sealed record SessionDescriptor(
    string Id,
    SessionKind Kind,
    string Title,
    string Emoji,
    string AccentArgb,
    IReadOnlyList<string> MemberIds,
    IReadOnlyList<MemberPresence> Presences,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt,
    string LastPreview,
    bool Pinned,
    bool Muted,
    bool AllowProactive,
    int UnreadCount,
    /// <summary>
    /// 本会话的展示主题（**整套配色**：背景 + 气泡 + 强调色 + 文字）。
    /// 对应 QQ 的"仅该成员聊天背景"——只作用于这一个会话。
    /// <c>null</c> 表示"跟随默认"，此时单聊用该角色的主题、群聊用中性主题。
    /// </summary>
    ChatTheme? Theme = null)
{
    /// <summary>
    /// 当前在场的成员（<see cref="Presences"/> 中未离开的那些）。
    ///
    /// **必须 [JsonIgnore]**：它是派生属性，序列化出去却没有对应的构造参数，
    /// 反序列化会直接抛（「无法将 JSON 对象映射到构造参数」）——整个 index.json
    /// 因此变成一份读不回来的文件。这正是"写的时候看着对、读的时候炸"的翻版，
    /// 本项目已经在 <c>ConversationTurn.Schema</c> 上踩过一次。
    /// 自检方式：任何"只读计算属性"都不许出现在会被反序列化的类型上（除加此特性外）。
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<MemberPresence> ActivePresences
        => Presences.Where(presence => presence.LeftAt is null).ToArray();

    public MemberPresence? PresenceOf(string characterId)
        => Presences.FirstOrDefault(presence =>
            string.Equals(presence.CharacterId, characterId, StringComparison.OrdinalIgnoreCase));
}
