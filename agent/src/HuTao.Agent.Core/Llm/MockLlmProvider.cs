using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Persona;

namespace HuTao.Agent.Core.Llm;

/// <summary>
/// 占位 LLM：不依赖真实大模型，用模板 + 人设口头禅生成一句"胡桃风格"的搭话。
/// 用途：在接入真实 LLM（云端 Qwen / 本地模型）之前，先跑通整个 Agent 流程。
/// 切换真实 LLM = 新建一个 ILLMProvider 实现 + 改配置 LlmProvider。
/// </summary>
public sealed class MockLlmProvider : ILLMProvider
{
    private readonly PersonaProfile _persona;
    private readonly Random _random = new();

    public MockLlmProvider(PersonaProfile persona) => _persona = persona;

    public string Name => "mock";

    public Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default)
    {
        var userMsg = history.LastOrDefault(m => m.Role == "user")?.Content ?? "";

        // 简单规则：识别几个关键词，其余走通用搭话
        var reply = userMsg switch
        {
            _ when userMsg.Contains("是谁") || userMsg.Contains("你好") || userMsg.Contains("自我介绍")
                => "哦呀，这位客官问得好！我是胡桃，往生堂的当代堂主，具体掌管的……嗯，就是些生离死别的小事啦。",

            _ when userMsg.Contains("生意") || userMsg.Contains("优惠") || userMsg.Contains("客户")
                => "说到生意，本堂主可就来精神了！往生堂出品，品质有保证，最近还有新优惠，客官要不要了解一下？",

            _ when userMsg.Contains("死") || userMsg.Contains("鬼")
                => "生离死别，本来就是自然之事，何须忌讳？真遇到什么玄乎的事，本堂主动动手指就解决喽。",

            _ => Pick(_persona.Catchphrases.BusinessLines) + " " + Pick(_persona.Catchphrases.Particles) + "，怎么样？",
        };

        return Task.FromResult(reply);
    }

    private string Pick(List<string> list)
        => list.Count == 0 ? "" : list[_random.Next(list.Count)];
}
