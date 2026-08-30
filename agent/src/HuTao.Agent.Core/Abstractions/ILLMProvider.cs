namespace HuTao.Agent.Core.Abstractions;

/// <summary>
/// 大语言模型提供方接口。Agent 用它生成胡桃的台词。
/// 与 TTS 同理：现在可以是云端 Qwen / 本地模型 / Mock，后续切换只改实现。
/// </summary>
public interface ILLMProvider
{
    string Name { get; }

    /// <summary>给定系统提示词（人设）与历史对话，生成一句回复。</summary>
    Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default);
}

/// <summary>一条对话消息。Role 取值：system / user / assistant。</summary>
public sealed record ChatMessage(string Role, string Content);
