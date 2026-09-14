using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.ChatRoom;

/// <summary>
/// LLM 导演：每轮多一次调用，换来确定性的发言调度。
///
/// 为什么不复用演员的 IAgentConversation 来做调度：演员的上下文里塞满了人设与
/// 角色记忆，让它兼任主持会让「谁该说话」的决策被角色立场污染（比如谁都想抢话）。
/// 导演用**独立的系统提示词**、只看对话记录，判断才干净。
///
/// 失败一律降级为「按顺序轮转下一个」，绝不因为导演挂了就卡住聊天室。
/// </summary>
public sealed class LlmChatRoomDirector : IChatRoomDirector
{
    private static readonly Regex JsonBlock = new(@"\{.*\}", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly ILLMProvider _llm;
    private readonly ChatRoomOptions _options;

    public LlmChatRoomDirector(ILLMProvider llm, ChatRoomOptions? options = null)
    {
        _llm = llm;
        _options = options ?? new ChatRoomOptions();
    }

    public async Task<DirectorDecision> DecideAsync(
        IReadOnlyList<ChatRoomParticipant> participants,
        IReadOnlyList<ChatRoomTurn> transcript,
        string? userInput,
        CancellationToken ct = default)
    {
        try
        {
            var history = new List<ChatMessage>
            {
                new("user", BuildPrompt(participants, transcript, userInput)),
            };
            var raw = await _llm.CompleteAsync(SystemPrompt, history, ct).ConfigureAwait(false);
            return Parse(raw, participants) ?? Fallback(participants, transcript);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Fallback(participants, transcript);
        }
    }

    private static string BuildPrompt(
        IReadOnlyList<ChatRoomParticipant> participants,
        IReadOnlyList<ChatRoomTurn> transcript,
        string? userInput)
    {
        var sb = new StringBuilder();
        sb.AppendLine("在场角色：");
        foreach (var p in participants)
            sb.AppendLine($"- id={p.Id} 名字={p.DisplayName}");

        sb.AppendLine();
        sb.AppendLine(transcript.Count == 0 ? "对话还没开始。" : "最近对话：");
        foreach (var turn in transcript.TakeLast(10))
            sb.AppendLine($"{(turn.IsUser ? "[旁观用户] " : "")}{turn.SpeakerName}：{turn.Text}");

        if (!string.IsNullOrWhiteSpace(userInput))
        {
            sb.AppendLine();
            sb.AppendLine($"旁观用户刚刚插话：「{userInput}」——下一位应当回应他。");
        }

        sb.AppendLine();
        sb.AppendLine("请决定下一位由谁发言。只输出 JSON：{\"next\":\"角色id 或 END\",\"reason\":\"一句话理由\"}");
        return sb.ToString();
    }

    private const string SystemPrompt =
        "你是一个多人聊天室的导演。你不参与对话，只负责决定下一位由谁说。\n" +
        "判断原则：\n" +
        "- 谁被点名、被提问，就该谁回答。\n" +
        "- 观点刚被反驳、或话题正好戳中某人关心的事，那个人最该接话。\n" +
        "- 不要连续让同一个人说两次。\n" +
        "- 旁观用户插话时，优先让最相关的人回应。\n" +
        "- 对话已经自然收尾、或双方只是在重复客套时，选 END 收场。\n" +
        "只输出 JSON，不要解释。";

    private static DirectorDecision? Parse(string raw, IReadOnlyList<ChatRoomParticipant> participants)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var match = JsonBlock.Match(raw);
        if (!match.Success)
            return null;

        try
        {
            using var document = JsonDocument.Parse(match.Value);
            var root = document.RootElement;
            var next = root.TryGetProperty("next", out var n) ? n.GetString()?.Trim() : null;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(next))
                return null;

            if (next.Equals("END", StringComparison.OrdinalIgnoreCase))
                return new DirectorDecision("", reason ?? "导演判定可以收场", true);

            var hit = participants.FirstOrDefault(p =>
                p.Id.Equals(next, StringComparison.OrdinalIgnoreCase) ||
                p.DisplayName.Equals(next, StringComparison.OrdinalIgnoreCase));
            return hit is null ? null : new DirectorDecision(hit.Id, reason ?? "", false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>导演不可用时的兜底：避开上一位，按注册顺序轮转。</summary>
    private static DirectorDecision Fallback(
        IReadOnlyList<ChatRoomParticipant> participants,
        IReadOnlyList<ChatRoomTurn> transcript)
    {
        if (participants.Count == 0)
            return DirectorDecision.End;

        var lastSpeaker = transcript.LastOrDefault(t => !t.IsUser)?.SpeakerId;
        var index = 0;
        if (lastSpeaker is not null)
        {
            var found = -1;
            for (var i = 0; i < participants.Count; i++)
                if (participants[i].Id == lastSpeaker) { found = i; break; }
            index = found < 0 ? 0 : (found + 1) % participants.Count;
        }
        return new DirectorDecision(participants[index].Id, "导演不可用，按顺序轮转", false);
    }
}
