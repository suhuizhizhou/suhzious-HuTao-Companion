using System.Text;
using HuTao.Foundation.Abstractions;
using HuTao.Persona;

namespace HuTao.Dialogue.ChatRoom;

/// <summary>
/// 多人聊天室：导演决定谁说话，被点到的角色以「用户输入」的形式收到场景与别人的话。
///
/// 这里有个关键的设计取舍：**不改造 IAgentConversation**。
/// 每个角色仍然是独立的 ReactAgent，各自维护自己的人设、记忆与沉浸闸门；
/// 聊天室只是把「别人的话」包装成这位角色收到的输入：
///
///     （你正和胡桃、安魂曲坐在一起闲聊）
///     胡桃说：「……」
///     安魂曲说：「……」
///
/// 好处有三个：
/// 1. 零 Core 改造——桌宠里怎么跑，聊天室里就怎么跑；
/// 2. 每个角色的 `_history` 自然累积成「我经历的这场对话」，上下文自洽；
/// 3. **沉浸闸门自动生效**——聊天室也必须不出戏，这条不用额外写一行代码。
///
/// 包装用全角括号旁白而不是「导演提示：」之类的元指令，是为了不破坏沉浸：
/// 角色感知到的是「场景」，不是「系统在调度我」。
/// </summary>
public sealed class ChatRoom
{
    private readonly List<ChatRoomParticipant> _participants;
    private readonly IChatRoomDirector _director;
    private readonly ChatRoomOptions _options;
    private readonly Action<string, Exception>? _onSpeakerFailed;
    private readonly List<ChatRoomTurn> _transcript = [];
    private readonly object _gate = new();
    private string? _pendingUserInput;

    public ChatRoom(
        IEnumerable<ChatRoomParticipant> participants,
        IChatRoomDirector director,
        ChatRoomOptions? options = null,
        Action<string, Exception>? onSpeakerFailed = null)
    {
        _participants = participants.ToList();
        if (_participants.Count < 2)
            throw new ArgumentException("聊天室至少需要两位角色。", nameof(participants));
        _director = director;
        _options = options ?? new ChatRoomOptions();
        _onSpeakerFailed = onSpeakerFailed;
    }

    public IReadOnlyList<ChatRoomParticipant> Participants => _participants;
    public IReadOnlyList<ChatRoomTurn> Transcript { get { lock (_gate) return _transcript.ToArray(); } }
    public int TurnCount { get { lock (_gate) return _transcript.Count(t => !t.IsUser); } }

    /// <summary>
    /// 最近一次导演决策（谁被点名、为什么、是否打算收场）。
    /// 给 UI 做可观测性用：作者视角想知道「为什么这句是她说」，但观众视角不该看见，
    /// 所以它只是被读出来，不混进 transcript。
    /// </summary>
    public DirectorDecision? LastDecision { get; private set; }

    /// <summary>用户随时插话；下一轮会优先有人回应。</summary>
    public void Interject(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return;
        lock (_gate)
        {
            _transcript.Add(new ChatRoomTurn("user", "旁观用户", trimmed, true, DateTimeOffset.Now));
            _pendingUserInput = trimmed;
        }
    }

    /// <summary>
    /// 走一轮：导演选人 → 该角色发言 → 记入记录。
    /// 返回 null 表示导演判定收场、或在场角色这一轮全都开不了口。
    /// </summary>
    public async Task<ChatRoomTurn?> StepAsync(CancellationToken ct = default)
    {
        ChatRoomTurn[] snapshot;
        string? pending;
        lock (_gate)
        {
            snapshot = _transcript.ToArray();
            pending = _pendingUserInput;
            _pendingUserInput = null;
        }

        var decision = await _director.DecideAsync(_participants, snapshot, pending, ct).ConfigureAwait(false);

        // 收场判定要尊重最小轮数：刚开场就 END 只会让聊天室显得莫名其妙。
        // 注意这里不能简单地放行给「第一个人」——那会让同一个角色连着说两次。
        // 正确做法是退回轮转，挑一个不是上一位发言者的人。
        if (decision.ShouldEnd)
        {
            LastDecision = decision;
            if (TurnCount >= _options.MinTurns)
                return null;
            decision = new DirectorDecision(NextByRotation(snapshot), "未达最小轮数，忽略收场", false);
        }
        LastDecision = decision;

        // 一位角色开不了口（模型暂时不可用）不该让整间聊天室炸掉：
        // 顺着轮转再试下一位，全都失败才收场。详见 TrySpeakAsync。
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateId = decision.NextSpeakerId;
        while (true)
        {
            var speaker = _participants.FirstOrDefault(p => p.Id == candidateId);
            if (speaker is null || !tried.Add(speaker.Id))
            {
                LastDecision = new DirectorDecision("", "在场角色这一轮都没能开口，先散场", true);
                return null;
            }

            var speak = await TrySpeakAsync(speaker, snapshot, pending, ct).ConfigureAwait(false);
            if (!speak.Succeeded)
            {
                candidateId = NextByRotation(speaker.Id);
                continue;
            }

            var turn = new ChatRoomTurn(
                speaker.Id, speaker.DisplayName, speak.Text!, false, DateTimeOffset.Now, speak.AudioPath);
            lock (_gate) _transcript.Add(turn);
            return turn;
        }
    }

    private readonly record struct SpeakOutcome(string? Text, string? AudioPath, bool Succeeded)
    {
        public static SpeakOutcome Ok(string text, string? audioPath) => new(text, audioPath, true);
        public static SpeakOutcome Skipped() => new(null, null, false);
    }

    /// <summary>
    /// 让一位角色开口。只把「模型暂时不可用」当成可跳过的失败；
    /// 其余异常（代码 bug、取消之外的意外）保持抛出——把 bug 静默成「她不想说话」
    /// 会让真正的问题永远查不出来。
    /// </summary>
    private async Task<SpeakOutcome> TrySpeakAsync(
        ChatRoomParticipant speaker,
        IReadOnlyList<ChatRoomTurn> snapshot,
        string? pending,
        CancellationToken ct)
    {
        try
        {
            var input = BuildSpeakerInput(speaker, snapshot, pending);
            var result = await speaker.Agent.RespondAsync(input, ct).ConfigureAwait(false);
            var text = Clamp(string.Join('\n', result.Reply.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)), _options.MaxTurnCharacters);
            return SpeakOutcome.Ok(text, result.Audio?.AudioPath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LlmUnavailableException ex)
        {
            _onSpeakerFailed?.Invoke(speaker.DisplayName, ex);
            return SpeakOutcome.Skipped();
        }
    }

    /// <summary>自动对谈直到导演收场或达到上限。</summary>
    public async Task<IReadOnlyList<ChatRoomTurn>> RunAsync(
        Action<ChatRoomTurn>? onTurn = null,
        CancellationToken ct = default)
    {
        var produced = new List<ChatRoomTurn>();
        for (var i = 0; i < _options.MaxTurns; i++)
        {
            ct.ThrowIfCancellationRequested();
            var turn = await StepAsync(ct).ConfigureAwait(false);
            if (turn is null)
                break;
            produced.Add(turn);
            onTurn?.Invoke(turn);
        }
        return produced;
    }

    /// <summary>
    /// 给发言者构造输入。用全角括号旁白交代场景与在场者，
    /// 保持「我身处一场对话」而不是「系统在调度我」的体感。
    /// </summary>
    private string BuildSpeakerInput(
        ChatRoomParticipant speaker,
        IReadOnlyList<ChatRoomTurn> transcript,
        string? userInput)
    {
        var others = _participants.Where(p => p.Id != speaker.Id).Select(p => p.DisplayName).ToArray();
        var sb = new StringBuilder();
        sb.Append($"（你正和{string.Join('、', others)}闲聊，气氛轻松。）\n");

        var recent = transcript.TakeLast(_options.ContextWindow).ToArray();
        if (recent.Length == 0)
        {
            sb.Append("（这是你开的口。）\n");
        }
        else
        {
            foreach (var turn in recent)
                sb.Append($"{(turn.IsUser ? "旁观的人" : turn.SpeakerName)}说：「{turn.Text}」\n");
        }

        if (!string.IsNullOrWhiteSpace(userInput))
            sb.Append($"（旁边看着的人刚插了一句：「{userInput}」，你可以回应。）\n");

        sb.Append("（轮到你说话了。接着聊，别复述上面的话，别长篇大论。）");
        return sb.ToString();
    }

    /// <summary>避开上一位发言者，按注册顺序取下一个。</summary>
    private string NextByRotation(IReadOnlyList<ChatRoomTurn> transcript)
        => NextByRotation(transcript.LastOrDefault(t => !t.IsUser)?.SpeakerId);

    /// <summary>取 <paramref name="afterSpeakerId"/> 的下一位；为 null 时取第一位。</summary>
    private string NextByRotation(string? afterSpeakerId)
    {
        if (afterSpeakerId is null)
            return _participants[0].Id;
        for (var i = 0; i < _participants.Count; i++)
            if (_participants[i].Id == afterSpeakerId)
                return _participants[(i + 1) % _participants.Count].Id;
        return _participants[0].Id;
    }

    private static string Clamp(string text, int max)
        => text.Length <= max ? text : text[..max];
}
