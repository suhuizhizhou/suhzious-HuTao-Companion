using System.Text;
using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Persona;

namespace HuTao.Agent.Core.Core;

/// <summary>
/// 胡桃 Agent 的核心：一次「说话」走 Reason → Observe → Think → Act 四段。
/// 提供两种粒度：
///   - GenerateTextAsync：只做 Reason→Observe→Think，快速拿文字（桌宠"气泡秒出"用）
///   - RespondAsync / ProactiveAsync：完整四段，含 TTS 合成
/// </summary>
public sealed class ReactAgent
{
    private readonly PersonaProfile _persona;
    private readonly ILLMProvider _llm;
    private readonly ITtsEngine? _tts;
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly string? _refAudio;
    private readonly string? _refText;
    private readonly List<ChatMessage> _history = [];

    public ReactAgent(
        PersonaProfile persona,
        ILLMProvider llm,
        ITtsEngine? tts,
        IEnumerable<IAgentTool> tools,
        string? refAudio = null,
        string? refText = null)
    {
        _persona = persona;
        _llm = llm;
        _tts = tts;
        _tools = tools.ToDictionary(t => t.Name);
        _refAudio = refAudio;
        _refText = refText;
    }

    /// <summary>完整回应（含语音合成），用户主动发消息时调用。</summary>
    public Task<AgentTurnResult> RespondAsync(string userInput, CancellationToken ct = default)
        => RunFullTurnAsync(userInput, isProactive: false, ct);

    /// <summary>完整主动搭话（含语音合成），调度器触发时调用。</summary>
    public Task<AgentTurnResult> ProactiveAsync(CancellationToken ct = default)
        => RunFullTurnAsync(userInput: null, isProactive: true, ct);

    /// <summary>只生成文字（Reason→Observe→Think，不含 TTS），用于"气泡秒出、语音后台补"。</summary>
    public Task<TextResult> GenerateTextAsync(string? userInput, bool isProactive, CancellationToken ct = default)
        => GenerateTextCoreAsync(userInput, isProactive, ct);

    /// <summary>把一段文字合成语音（Act 环节的 TTS 部分，单独抽出以便后台异步）。</summary>
    public async Task<TtsResult?> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        if (_tts is null)
            return null;
        return await _tts.SynthesizeAsync(
            new TtsRequest(text, _refAudio, _refText), ct).ConfigureAwait(false);
    }

    /// <summary>生成多段短句（供"像普通聊天一样分条发送"）。</summary>
    public async Task<IReadOnlyList<string>> GenerateSegmentsAsync(
        string? userInput, bool isProactive, CancellationToken ct = default)
    {
        var text = await GenerateTextCoreAsync(userInput, isProactive, ct).ConfigureAwait(false);
        return SplitSegments(text.Reply);
    }

    /// <summary>当前对话历史（供持久化/恢复记忆用）。</summary>
    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>重开时恢复历史对话（让 LLM 记得之前聊过什么）。</summary>
    public void RestoreHistory(IEnumerable<ChatMessage> history)
    {
        _history.Clear();
        _history.AddRange(history);
    }

    private static IReadOnlyList<string> SplitSegments(string reply)
    {
        return reply
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
    }

    private async Task<AgentTurnResult> RunFullTurnAsync(string? userInput, bool isProactive, CancellationToken ct)
    {
        var text = await GenerateTextCoreAsync(userInput, isProactive, ct).ConfigureAwait(false);

        TtsResult? audio = null;
        var action = "已生成台词";
        if (_tts is not null)
        {
            try
            {
                audio = await SynthesizeAsync(text.Reply, ct).ConfigureAwait(false);
                action = $"已合成语音 {audio!.AudioPath}";
            }
            catch (Exception ex)
            {
                action = $"语音合成失败（仅保留文字）: {ex.Message}";
            }
        }

        return new AgentTurnResult(text.Reason, text.Observation, text.Reply, audio, action);
    }

    private async Task<TextResult> GenerateTextCoreAsync(string? userInput, bool isProactive, CancellationToken ct)
    {
        // ── 1. Reason ──
        var reason = isProactive
            ? "主动搭话：心跳到点，且用户不忙、冷却已过"
            : $"回应用户输入：{userInput}";

        // ── 2. Observe：调用工具感知环境 ──
        var obsBuilder = new StringBuilder();
        foreach (var (name, tool) in _tools.OrderBy(t => t.Key))
        {
            var result = await tool.ExecuteAsync(ct: ct).ConfigureAwait(false);
            obsBuilder.AppendLine($"- {name}: {result}");
        }
        var observation = obsBuilder.ToString().TrimEnd();

        // ── 3. Think：LLM 生成胡桃视角的台词 ──
        if (userInput is not null)
            _history.Add(new ChatMessage("user", userInput));

        var lorePart = string.IsNullOrEmpty(_persona.Lore)
            ? ""
            : "\n\n【你的生平（官方设定，涉及身世/往生堂/生死观时务必以此为准，不得杜撰）】\n" + _persona.Lore;

        var systemWithObs = _persona.SystemPrompt + lorePart + "\n\n" +
            "【回复格式】请把回复拆成 1~3 段短句，每段单独一行、不超过 30 字，像聊天气泡一样简短自然，不要输出一大段长文字。\n\n" +
            $"【当前环境感知】\n{observation}\n\n" +
            "（以上是你能感知到的、电脑前那位用户的实时状态，聊天时可自然提及，但别像念数据一样生硬。）";

        var reply = await _llm.CompleteAsync(systemWithObs, _history, ct).ConfigureAwait(false);
        _history.Add(new ChatMessage("assistant", reply));

        return new TextResult(reason, observation, reply);
    }
}

/// <summary>只含文字的一次 ReAct 结果（Reason/Observe/Reply）。</summary>
public sealed record TextResult(string Reason, string Observation, string Reply);

/// <summary>一次完整 ReAct 循环的产物，四段都可观测。</summary>
public sealed record AgentTurnResult(
    string Reason,
    string Observation,
    string Reply,
    TtsResult? Audio,
    string Action);
