using HuTao.Dialogue.Core;
using HuTao.Persona;

namespace HuTao.Dialogue.ChatRoom;

/// <summary>聊天室里的一位角色。Agent 就是桌宠那套 IAgentConversation，无需改造。</summary>
public sealed record ChatRoomParticipant(
    string Id,
    string Name,
    IAgentConversation Agent,
    PersonaProfile Persona)
{
    /// <summary>用于让其他角色认人：优先自报名，退化到角色名。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Persona.Name) ? Name : Persona.Name;
}

/// <summary>聊天室里的一句话，可能是角色说的，也可能是用户插的话。</summary>
/// <param name="AudioPath">
/// 该句的语音文件（角色用自己声线合成的，或命中的游戏原声）；用户插话与纯文字模式为 null。
/// 放在轮次上而不是让 UI 自己去 Agent 里翻，是因为聊天室是**事后回看**的：
/// 用户可能想单独重播某一句，而那时 Agent 的最后一轮结果早就被覆盖了。
/// </param>
public sealed record ChatRoomTurn(
    string SpeakerId,
    string SpeakerName,
    string Text,
    bool IsUser,
    DateTimeOffset At,
    string? AudioPath = null);

public sealed record ChatRoomOptions
{
    /// <summary>自动对谈的发言上限，防止两个角色互相客套到天荒地老。</summary>
    public int MaxTurns { get; init; } = 10;
    /// <summary>少于这个轮数不允许收场，避免刚开场就冷场。</summary>
    public int MinTurns { get; init; } = 4;
    /// <summary>喂给下一个发言者的历史轮数（太多会淹没当前话题）。</summary>
    public int ContextWindow { get; init; } = 8;
    /// <summary>单轮发言的字数上限，聊天室不该出现长篇大论。</summary>
    public int MaxTurnCharacters { get; init; } = 120;
}

/// <summary>导演：决定下一句由谁说、以及是否该收场。</summary>
public sealed record DirectorDecision(string NextSpeakerId, string Reason, bool ShouldEnd)
{
    public static readonly DirectorDecision End = new("", "导演判定可以收场", true);
}

public interface IChatRoomDirector
{
    Task<DirectorDecision> DecideAsync(
        IReadOnlyList<ChatRoomParticipant> participants,
        IReadOnlyList<ChatRoomTurn> transcript,
        string? userInput,
        CancellationToken ct = default);
}
