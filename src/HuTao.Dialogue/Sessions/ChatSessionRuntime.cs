using HuTao.Dialogue.ChatRoom;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Llm;
using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;
using HuTao.Persona;
using HuTao.Dialogue.Runtime;
using HuTao.Dialogue.Tools;
using HuTao.Voice;

// 本文件同时用到了命名空间 HuTao.Dialogue.ChatRoom 与其中的类型 ChatRoom，
// 不取别名的话编译器会把裸的 ChatRoom 解析成命名空间（CS0118）。
using Room = HuTao.Dialogue.ChatRoom.ChatRoom;

namespace HuTao.Dialogue.Sessions;

/// <summary>
/// 一轮的结果。UI 只需要这些就够画完一条消息与一行状态。
///
/// <paramref name="Turns"/> 是**这次发言落下的全部记录**，一段一条。
/// 原版桌宠单聊是"一句一个气泡"，所以一轮里角色可能落下多条；
/// 只把最后一条交给 UI 会让前面的句子整段消失。
/// </summary>
public sealed record SessionTurnOutcome(
    ConversationTurn? Turn,
    string? SpeakerId,
    string? SpeakerName,
    bool Ended,
    string? StatusMessage,
    IReadOnlyList<ConversationTurn>? Turns = null)
{
    /// <summary>本轮落下的全部记录（无则退回单条）。</summary>
    public IReadOnlyList<ConversationTurn> AllTurns
        => Turns ?? (Turn is null ? [] : [Turn]);

    public static SessionTurnOutcome Finished(string? status = null) => new(null, null, null, true, status);
}

/// <summary>
/// 群聊导演与 LLM 的注入点。放在构造上而不是让本类自己去读 <c>DEEPSEEK_API_KEY</c>，
/// 离线测试才能塞桩；生产由宿主按"有 key 用 DeepSeek、没 key 退 Mock"组装
/// （沿用旧聊天室窗口的既有策略）。
/// </summary>
public sealed record SessionRuntimeOptions
{
    public IChatRoomDirector? Director { get; init; }

    /// <summary>喂给 <c>RestoreHistory</c> 的历史条数上限。</summary>
    public int HistoryWindow { get; init; } = 24;

    /// <summary>单聊的主动推进字数上限之外，群聊沿用 <see cref="ChatRoomOptions"/> 的默认值。</summary>
    public ChatRoomOptions? RoomOptions { get; init; }
}

/// <summary>
/// 一个会话的运行时编排：激活、收发、交接、收尾。
///
/// 这一层存在的意义是把"会话"从 WPF 窗口代码里提出来——<c>architecture.md</c> 明确要求
/// 新功能只替换对应边界，而不是继续往窗口里堆逻辑。控制台 Host 将来也能直接复用它。
///
/// 它做三件事，别的一概不做：
/// 1. 向 <see cref="AgentPool"/> 借角色实例，并按 <see cref="AudienceProjection"/> 恢复各自视角；
/// 2. 把每一轮写进 timeline（权威事实）与各成员视角（缓存）；
/// 3. 交接（capture → 落盘 → 释放）。
/// 渲染、播放、输入框都是宿主的事。
/// </summary>
public sealed class ChatSessionRuntime : IAsyncDisposable
{
    private readonly SessionStore _store;
    private readonly AgentPool _pool;
    private readonly SessionRuntimeOptions _options;
    private readonly LocalDiagnosticLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AgentContextLease? _lease;
    private Room? _room;
    private IChatRoomDirector? _director;
    private bool _ended;

    public ChatSessionRuntime(
        SessionStore store,
        AgentPool pool,
        SessionDescriptor descriptor,
        SessionRuntimeOptions? options = null,
        LocalDiagnosticLog? log = null)
    {
        _store = store;
        _pool = pool;
        Descriptor = SessionFactory.Normalize(descriptor);
        _options = options ?? new SessionRuntimeOptions();
        _log = log ?? LocalDiagnosticLog.Default;
    }

    public SessionDescriptor Descriptor { get; private set; }

    public bool IsActive => _lease is not null;

    /// <summary>
    /// 会话由运行时自行更新（最近活跃时间、侧栏预览）时抛出。
    ///
    /// 为什么需要它：运行时把这些写进 <c>session.json</c>，而**侧栏读的是 index.json**。
    /// 没有这条通知，用户看到的预览会永远停在"切换会话那一刻"——
    /// 消息区在动、侧栏不动，而且不会报错（静默的观感缺陷最难查）。
    /// 目录表由宿主统一落盘，所以这里只通知、不自己写 index。
    /// </summary>
    public event Action<SessionDescriptor>? DescriptorChanged;

    /// <summary>
    /// 最近一次导演决策（谁被点名、为什么）。**给宿主做可观测性用**，不混进 transcript——
    /// 那是作者视角的信息，摆在对话流里会破坏「我在看她们聊天」。
    /// 单聊没有导演，恒为 null。
    /// </summary>
    public DirectorDecision? LastDirectorDecision => _room?.LastDecision;

    /// <summary>当前在场的角色 Id。</summary>
    /// <summary>
    /// 单聊时"她本人"的情境台词服务；群聊为 null（没有单一说话人，调用方走中性语气）。
    /// 只有在激活之后才拿得到——它来自角色的 <c>PersonaProfile</c>。
    /// </summary>
    public HuTao.Persona.ScriptedLines? PersonaForScriptedLines
        => Descriptor.Kind == SessionKind.Single && _lease is not null
           && _lease.Runtimes.TryGetValue(Descriptor.MemberIds[0], out var runtime)
            ? runtime.Persona.Scripted
            : null;

    public IReadOnlyList<string> MemberIds => Descriptor.MemberIds;

    /// <summary>
    /// 激活：借实例 → 恢复每个成员的视角历史 → 建群聊房间。
    ///
    /// 幂等：已经激活时直接返回，不重复借、不重复恢复——重复恢复会把
    /// agent 在本会话里累积的、尚未落盘的那几轮对话**冲掉**。
    /// </summary>
    public async Task ActivateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lease is not null)
                return;

            var members = Descriptor.ActivePresences.Select(p => p.CharacterId).ToArray();
            if (members.Length == 0)
                throw new InvalidOperationException($"会话 {Descriptor.Id} 没有在场成员。");

            _lease = await _pool.ActivateAsync(
                Descriptor.Id,
                members,
                handoff: HandoffCharacterAsync,
                ct: ct).ConfigureAwait(false);

            // 按视角恢复每个成员的上下文。
            var timeline = await _store.LoadTimelineAsync(Descriptor.Id, ct).ConfigureAwait(false);
            foreach (var characterId in members)
            {
                ct.ThrowIfCancellationRequested();
                var presence = Descriptor.PresenceOf(characterId)
                    ?? new MemberPresence(characterId, Descriptor.CreatedAt);

                var view = await _store.LoadViewAsync(Descriptor.Id, characterId, ct).ConfigureAwait(false);
                if (view.Count == 0 && timeline.Count > 0)
                {
                    // 视角缓存缺失（首次激活、或成员变更后重建）：从权威时间线现算并落盘。
                    view = AudienceProjection.ForCharacter(timeline, presence, characterId);
                    await _store.SaveViewAsync(Descriptor.Id, characterId, view, ct).ConfigureAwait(false);
                }

                var history = AudienceProjection.ToHistory(view, _options.HistoryWindow);
                _pool.Restore(characterId, history, _pool.CaptureContext(characterId)?.Privacy);
            }

            BuildRoom();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 群聊才需要房间（它负责"谁说话"与场景旁白）。单聊只有一位角色，
    /// 直接 <c>ProactiveAsync</c> 更短也更准——不需要导演来告诉她"轮到你了"。
    /// </summary>
    private void BuildRoom()
    {
        if (Descriptor.Kind == SessionKind.Single || _lease is null)
        {
            _room = null;
            _director = null;
            return;
        }

        var participants = new List<ChatRoomParticipant>();
        foreach (var characterId in Descriptor.MemberIds)
        {
            if (!_lease.Runtimes.TryGetValue(characterId, out var runtime))
                continue;
            participants.Add(new ChatRoomParticipant(
                characterId, runtime.Character.Name, runtime.Agent, runtime.Persona));
        }

        if (participants.Count < SessionFactory.MinGroupMembers)
        {
            _room = null;
            _director = null;
            return;
        }

        _director ??= _options.Director;
        if (_director is null)
        {
            _room = null;
            return;
        }

        var roomOptions = _options.RoomOptions ?? new ChatRoomOptions();
        _room = new Room(participants, _director, roomOptions with
        {
            Audience = Descriptor.MemberIds.ToArray(),
            SessionTitle = Descriptor.Title,
        });
    }

    /// <summary>
    /// 用户发一条，然后让下一位角色回应。
    ///
    /// <paramref name="onUserTurnPersisted"/> 在**用户那句刚落盘、生成还没开始**时回调。
    /// 存在的理由：原版桌宠是"输入框一回车，自己的话立刻出现"，而调用方
    /// （聊天窗）只有等整轮返回才能拿到记录——那样用户要干等到角色说完才看到自己那句。
    /// 把这个时刻暴露出来，宿主就能立刻把乐观气泡换成落盘的真记录。
    /// </summary>
    public async Task<SessionTurnOutcome> SubmitUserAsync(
        string text,
        CancellationToken ct = default,
        Func<ConversationTurn, Task>? onUserTurnPersisted = null)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return new SessionTurnOutcome(null, null, null, false, null);

        await ActivateAsync(ct).ConfigureAwait(false);

        // 先落盘再生成：走到 LLM 那一步时用户这句话已经在磁盘上了，
        // 中途崩掉也不会让用户以为自己的消息没发出去（不变量 I4）。
        var userTurn = await _store.AppendAsync(Descriptor.Id, new ConversationTurn(
            "",
            DateTimeOffset.Now,
            "user",
            "你",
            trimmed,
            IsUser: true,
            AudioPath: null,
            Audience: Descriptor.MemberIds.ToArray(),
            Kind: ConversationTurnKind.Message), ct).ConfigureAwait(false);
        await FanOutAsync(userTurn, Descriptor.MemberIds, ct).ConfigureAwait(false);
        await TouchAsync(trimmed, ct).ConfigureAwait(false);

        if (onUserTurnPersisted is not null)
            await onUserTurnPersisted(userTurn).ConfigureAwait(false);

        return await StepAsync(trimmed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 不说话，只推进一步：群聊由导演点名下一位，单聊走主动生成。
    /// </summary>
    public Task<SessionTurnOutcome> StepAsync(CancellationToken ct = default)
        => StepAsync(null, ct);

    /// <summary>
    /// 推进一步。<paramref name="userInput"/> 非空表示"用户刚说了这句话，请回应它"——
    /// 这才是单聊该走的路；为空才是主动搭话。
    ///
    /// 这个区分是必须的：<c>ProactiveAsync()</c> 根本没有接收用户输入的参数，
    /// 迁移时用它来"回应用户"，结果用户的话既没进模型也没进上下文，
    /// 却仍然被落盘——时间线记录的内容和模型实际看到的不一致。
    /// </summary>
    private async Task<SessionTurnOutcome> StepAsync(string? userInput, CancellationToken ct)
    {
        await ActivateAsync(ct).ConfigureAwait(false);
        if (_lease is null)
            return SessionTurnOutcome.Finished("会话尚未激活。");

        if (Descriptor.Kind == SessionKind.Single)
            return await StepSingleAsync(userInput, ct).ConfigureAwait(false);
        return await StepGroupAsync(userInput, ct).ConfigureAwait(false);
    }

    private async Task<SessionTurnOutcome> StepSingleAsync(string? userInput, CancellationToken ct)
    {
        var characterId = Descriptor.MemberIds[0];
        if (!_lease!.Runtimes.TryGetValue(characterId, out var runtime))
            return SessionTurnOutcome.Finished($"{characterId} 没有可用的运行时。");

        try
        {
            // 有用户输入就必须走 RespondAsync——它会把这句喂给模型，也才会
            // 写入记忆、提炼、事件等副作用。只有空闲推进才用 ProactiveAsync。
            var result = userInput is null
                ? await runtime.Agent.ProactiveAsync(ct).ConfigureAwait(false)
                : await runtime.Agent.RespondAsync(userInput, ct).ConfigureAwait(false);

            // 分段：一段一个气泡。没有分段信息时才退回把整段回复当一个气泡。
            var segments = result.Segments.Count > 0
                ? result.Segments.Select(s => s.Text).ToArray()
                : [(result.Reply ?? "").Trim()];
            var spoken = segments
                .Select(s => (s ?? "").Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            if (spoken.Length == 0)
                return SessionTurnOutcome.Finished("这一轮没有产出内容。");

            // 逐段落盘。原版是一句一个气泡，这里用 UtteranceIndex 保住同一语义；
            // 整轮共用一个时间戳，重建时按 (SpeakerId, 时间, 递增 index) 收拢回一次发言。
            var at = DateTimeOffset.Now;
            var written = new List<ConversationTurn>(spoken.Length);
            for (var i = 0; i < spoken.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var turn = await _store.AppendAsync(Descriptor.Id, new ConversationTurn(
                    "",
                    at,
                    characterId,
                    runtime.Character.Name,
                    spoken[i],
                    IsUser: false,
                    // 音频只在第一段上：整轮的合成音频对应整段文本，挂到每一段会重复播放。
                    AudioPath: i == 0 ? result.Audio?.AudioPath : null,
                    Audience: [characterId],
                    Kind: ConversationTurnKind.Message,
                    UtteranceIndex: i), ct).ConfigureAwait(false);
                // 单聊里"观众"就是她自己：包含发言者，语义与群聊一致。
                await FanOutAsync(turn, [characterId], ct).ConfigureAwait(false);
                written.Add(turn);
            }

            await TouchAsync(string.Join(" ", spoken), ct).ConfigureAwait(false);
            return new SessionTurnOutcome(
                written[^1], characterId, runtime.Character.Name, false, null, written);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (LlmUnavailableException ex)
        {
            // 模型抖一下不该让整个会话散场。把原因说清楚，但**不**伪装成角色台词。
            _log.Write("session.speaker_unavailable", ex);
            var name = runtime.Character.Name;
            var notice = await AppendSystemAsync($"{name} 这一轮没能开口（模型暂时不可用）。", ct)
                .ConfigureAwait(false);
            _ = notice;
            return new SessionTurnOutcome(null, null, null, false,
                $"{name} 这一轮没能开口（模型暂时不可用）。");
        }
    }

    private async Task<SessionTurnOutcome> StepGroupAsync(string? userInput, CancellationToken ct)
    {
        if (_room is null)
            return SessionTurnOutcome.Finished("群聊尚未就绪（成员不足或导演不可用）。");

        try
        {
            // 用户插话要带给导演（"下一轮优先有人回应"）。
            // 这里传的是**文本**而不是从房间转录里再取一次——用户那句已经由
            // SubmitUserAsync 落盘一次了，再 Interject 一次会让时间线出现两条。
            if (!string.IsNullOrWhiteSpace(userInput))
                _room.Interject(userInput);

            var roomTurn = await _room.StepAsync(ct).ConfigureAwait(false);
            if (roomTurn is null)
                return SessionTurnOutcome.Finished($"对话自然收场（共 {_room.TurnCount} 轮）。");

            var audience = Descriptor.MemberIds
                .Where(id => !string.Equals(id, roomTurn.SpeakerId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var turn = await _store.AppendAsync(Descriptor.Id, new ConversationTurn(
                "", roomTurn.At, roomTurn.SpeakerId, roomTurn.SpeakerName, roomTurn.Text,
                IsUser: false, AudioPath: roomTurn.AudioPath,
                Audience: audience, Kind: ConversationTurnKind.Message), ct).ConfigureAwait(false);
            // 分发名单要含发言者自己：她必须看得见自己刚说的话，否则切走再切回来就失忆。
            // 投影会按「发言者 → assistant、不加前缀」处理她那一份。
            await FanOutAsync(turn, [roomTurn.SpeakerId, .. audience], ct).ConfigureAwait(false);
            await TouchAsync(roomTurn.Text, ct).ConfigureAwait(false);
            return new SessionTurnOutcome(turn, roomTurn.SpeakerId, roomTurn.SpeakerName, false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (LlmUnavailableException ex)
        {
            _log.Write("session.speaker_unavailable", ex);
            return new SessionTurnOutcome(null, null, null, false, "模型暂时不可用，已跳过这一轮。");
        }
    }

    /// <summary>系统提示：进时间线（观众为空）并分发给所有在场成员——她们都该知道发生了什么。</summary>
    private async Task<ConversationTurn> AppendSystemAsync(string text, CancellationToken ct)
    {
        var members = Descriptor.MemberIds.ToArray();
        var turn = await _store.AppendAsync(Descriptor.Id, new ConversationTurn(
            "", DateTimeOffset.Now, "system", "", text,
            IsUser: false, AudioPath: null,
            Audience: members, Kind: ConversationTurnKind.System), ct).ConfigureAwait(false);
        await FanOutAsync(turn, members, ct).ConfigureAwait(false);
        return turn;
    }

    /// <summary>把一条记录追加进各成员的视角缓存。</summary>
    private async Task FanOutAsync(ConversationTurn turn, IReadOnlyList<string> characterIds, CancellationToken ct)
    {
        foreach (var characterId in characterIds)
        {
            ct.ThrowIfCancellationRequested();
            var presence = Descriptor.PresenceOf(characterId);
            // 规则：只有当时在场的人才收得到（中途加入者不该追认之前的内容）。
            if (presence is not null && !presence.Contains(turn.At))
                continue;
            await _store.AppendViewAsync(Descriptor.Id, characterId, turn, ct).ConfigureAwait(false);
        }
    }

    private Task TouchAsync(string preview, CancellationToken ct)
    {
        Descriptor = Descriptor with
        {
            LastActiveAt = DateTimeOffset.Now,
            LastPreview = SessionCatalog.Truncate(preview, 60),
        };
        // 先通知宿主（它负责 index.json），再落 session.json——
        // 顺序反过来的话，宿主读到的是旧描述符，侧栏又慢一拍。
        DescriptorChanged?.Invoke(Descriptor);
        return _store.SaveSessionAsync(Descriptor, ct);
    }

    /// <summary>
    /// 交接：把角色此刻的上下文写进它在本会话的视角文件。
    ///
    /// 合并策略是**多重集差分**，不是"见过就跳过"：用与投影相同的规则把已有视角
    /// 折算成「(角色, 文本)」计数，再按顺序把 agent 历史里能配上的那几条抵消掉，
    /// 剩下的才是真正还没落盘的新内容。
    ///
    /// 为什么不能用 HashSet：同一句台词（"嗯。"）在一场对话里出现两次完全正常，
    /// 按集合去重会把第二次静默丢掉。为什么必须用投影后的形态比对：
    /// agent 看到的别人台词带「某某说：」前缀，而视角文件里存的是原始记录，
    /// 两边不折算就等于每条都"没见过"。
    /// </summary>
    private async Task HandoffCharacterAsync(string characterId, CancellationToken ct)
    {
        var context = _pool.CaptureContext(characterId);
        if (context is null || context.History.Count == 0)
            return;

        var existing = await _store.LoadViewAsync(Descriptor.Id, characterId, ct).ConfigureAwait(false);
        var presence = Descriptor.PresenceOf(characterId)
            ?? new MemberPresence(characterId, Descriptor.CreatedAt);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var turn in AudienceProjection.ForCharacter(existing, presence, characterId))
        {
            var key = HistoryKey(turn.IsUser ? "user" : "assistant", turn.Text);
            seen[key] = seen.GetValueOrDefault(key) + 1;
        }

        var pending = new List<ConversationTurn>();
        foreach (var message in context.History)
        {
            var role = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "assistant";
            var key = HistoryKey(role, message.Content);
            var remaining = seen.GetValueOrDefault(key);
            if (remaining > 0)
            {
                // 这条已经在视角里了，抵消一次。
                seen[key] = remaining - 1;
                continue;
            }
            pending.Add(new ConversationTurn(
                "", DateTimeOffset.Now, characterId, "", message.Content,
                IsUser: role == "user", AudioPath: null,
                Audience: [characterId], Kind: ConversationTurnKind.Message));
        }

        foreach (var turn in pending)
            await _store.AppendViewAsync(Descriptor.Id, characterId, turn, ct).ConfigureAwait(false);
    }

    private static string HistoryKey(string role, string text) => $"{role}\u001F{text}";

    /// <summary>
    /// 收尾：交接 → 释放租约。**必须幂等**——窗口关闭路径可能因重试被调用多次，
    /// 第二次再交接会把已经归别人的上下文写进本会话。
    /// </summary>
    public async Task EndSessionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ended)
                return;
            _ended = true;

            if (_lease is null)
                return;

            foreach (var characterId in _lease.Runtimes.Keys.ToArray())
            {
                try { await HandoffCharacterAsync(characterId, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log.Write("session.handoff", ex); }
            }

            _room = null;
            _director = null;
            _lease.Dispose();
            _lease = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await EndSessionAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Write("session.dispose", ex); }
    }

    /// <summary>
    /// 组装导演与 LLM。有 DEEPSEEK_API_KEY 就用真的，没有就退到 Mock
    /// ——与旧聊天室窗口的既有策略一致，这样没配 key 也能把 UI 走通。
    /// </summary>
    public static IChatRoomDirector CreateDefaultDirector(PersonaProfile fallbackPersona)
    {
        var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        ILLMProvider llm = string.IsNullOrWhiteSpace(apiKey)
            ? new MockLlmProvider(fallbackPersona)
            : new DeepSeekLlmProvider(apiKey);
        return new LlmChatRoomDirector(llm);
    }

    /// <summary>仅用于测试与宿主预判：本会话需要的角色是否都已在池中。</summary>
    public bool AllMembersResident
        => Descriptor.MemberIds.All(id => _pool.Find(id) is not null);
}
