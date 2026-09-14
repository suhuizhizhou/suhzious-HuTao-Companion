namespace HuTao.Foundation.Abstractions;

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

/// <summary>
/// 大模型**暂时不可用**（网络断了、对端重置连接、超时）。
///
/// 单独一个类型是为了让调用方能区分「这句台词没生成出来」和「代码有 bug」：
/// 前者应当被降级处理（跳过这一轮 / 用兜底台词），后者不该被吞掉。
/// 传输层异常在长会话里是**正常会发生的事**，不该直接冒泡成崩溃。
/// </summary>
public sealed class LlmUnavailableException : Exception
{
    public LlmUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
