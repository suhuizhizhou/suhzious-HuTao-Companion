using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Sessions;

/// <summary>
/// 时间线 → 某角色视角的纯函数投影。
///
/// 存在的理由：群聊里每个成员"经历"的对话是不一样的——A 看不到 B 私下说的话、
/// 中途加入的人看不到加入之前的内容、角色不该把自己的台词当成别人说的。
/// 喂给 <c>RestoreHistory</c> 的必须是这个人自己的视角，否则要么串味、要么出戏。
///
/// 纯函数（无 I/O、不读时钟、可重复调用结果一致），所以它能在零依赖下被完整测出来；
/// UI 与存储都只是它的调用方。
/// </summary>
public static class AudienceProjection
{
    /// <summary>
    /// timeline + 成员在场区间 → 某角色视角。规则顺序固定，不许调换：
    ///
    /// 1. 丢掉 <c>At</c> 落在该角色在场区间之外的条目；
    /// 2. 可见性：<c>Audience</c> 为空（全场可见）→ 保留；
    ///    该角色是发言者 → 保留；<c>Audience</c> 含该角色 → 保留；否则丢弃；
    /// 3. 可见的 <see cref="ConversationTurnKind.Hint"/> 保留为旁白（不包装成台词）；
    /// 4. 该角色自己说的 → assistant，**不加前缀**；
    /// 5. 其余 → user，前缀 <c>「{SpeakerName}」：</c>。
    ///
    /// 第 2 步"发言者自己可见"是关键：视角文件是按「这条谁看得见」分发的，
    /// 而角色**必须**看得见自己刚说的话。曾经漏掉这一条，导致她切走再切回来就忘了
    /// 自己上一句说了什么——只能靠交接时的尾巴合并勉强补回来，而那是不可靠的兜底。
    /// </summary>
    public static IReadOnlyList<ConversationTurn> ForCharacter(
        IReadOnlyList<ConversationTurn> timeline,
        MemberPresence presence,
        string characterId)
    {
        if (timeline is null || timeline.Count == 0)
            return [];

        var result = new List<ConversationTurn>(timeline.Count);
        foreach (var turn in timeline)
        {
            // 规则 1：不在场区间内的一律丢弃。
            if (!presence.Contains(turn.At))
                continue;

            var isOwn = string.Equals(turn.SpeakerId, characterId, StringComparison.OrdinalIgnoreCase);

            // 规则 2：可见性。Audience 为空表示没有限定观众（用户插话、系统消息）。
            var visible = isOwn ||
                          turn.Audience is not { Count: > 0 } ||
                          ContainsAudience(turn.Audience, characterId);
            if (!visible)
                continue;

            if (isOwn)
            {
                // 规则 4：自己说的话，原样保留为 assistant 视角，不加前缀。
                result.Add(turn with { IsUser = false });
                continue;
            }

            // 规则 3：旁白保持旁白——它不该被包装成"某某说"。
            if (turn.Kind == ConversationTurnKind.Hint)
            {
                result.Add(turn with { IsUser = false });
                continue;
            }

            // 规则 5：别人的话统一包装成 user，并带上说话人。
            // 前缀格式与 `ChatRoom.BuildSpeakerInput` 的「X说：「…」」保持同一风格——
            // 两种包装打架会让模型分不清"谁在说话"。
            var speaker = string.IsNullOrWhiteSpace(turn.SpeakerName) ? "某人" : turn.SpeakerName;
            result.Add(turn with
            {
                IsUser = true,
                Text = turn.Kind == ConversationTurnKind.System
                    ? turn.Text
                    : $"「{speaker}」：{turn.Text}",
            });
        }
        return result;
    }

    private static bool ContainsAudience(IReadOnlyList<string> audience, string characterId)
        => audience.Any(id => string.Equals(id, characterId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 视角 → 喂给 <c>RestoreHistory</c> 的历史。
    /// 取**末** <paramref name="window"/> 条（不是前 N 条）：要的是"最近发生了什么"，
    /// 而不是"这场对话怎么开场的"。
    /// </summary>
    public static IReadOnlyList<ChatMessage> ToHistory(
        IReadOnlyList<ConversationTurn> view, int window = 24)
    {
        if (view is null || view.Count == 0 || window <= 0)
            return [];

        return view
            .TakeLast(window)
            .Select(turn => new ChatMessage(turn.IsUser ? "user" : "assistant", turn.Text))
            .ToArray();
    }
}
