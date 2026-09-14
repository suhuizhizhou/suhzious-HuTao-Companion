using HuTao.Foundation.Abstractions;
using HuTao.Persona;

namespace HuTao.Dialogue.Llm;

/// <summary>
/// 占位 LLM：不依赖真实大模型，用模板 + 当前人设口头禅生成一句搭话。
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

        if (_persona.Name == "芙宁娜")
        {
            var furinaReply = userMsg switch
            {
                _ when userMsg.Contains("是谁") || userMsg.Contains("你好") || userMsg.Contains("自我介绍")
                    => "欢迎来到本水神的剧场！我是芙宁娜，今天的主角自然也是我。",
                _ when userMsg.Contains("难过") || userMsg.Contains("累")
                    => "先坐到观众席休息一下吧。主演偶尔也会允许重要的观众喘口气。",
                _ => Pick(_persona.Catchphrases.SignatureQuotes) + Pick(_persona.Catchphrases.Particles),
            };
            return Task.FromResult(furinaReply);
        }

        if (_persona.Name == "可莉")
        {
            var kleeReply = userMsg switch
            {
                _ when userMsg.Contains("是谁") || userMsg.Contains("你好") || userMsg.Contains("自我介绍")
                    => "你好呀！我是可莉，西风骑士团的火花骑士！嘟嘟可也来和你打招呼啦！",
                _ when userMsg.Contains("难过") || userMsg.Contains("累")
                    => "别难过啦……可莉陪你坐一会儿。等你心情好一点，我们再去找好玩的地方！",
                _ when userMsg.Contains("炸") || userMsg.Contains("炸鱼")
                    => "炸鱼要找琴团长看不到的地方！嘿嘿，可莉已经想好路线啦！",
                _ => Pick(_persona.Catchphrases.SignatureQuotes) + Pick(_persona.Catchphrases.Particles),
            };
            return Task.FromResult(kleeReply);
        }

        // 简单规则：识别几个关键词，其余走通用搭话
        //
        // ⚠️ 这个默认分支必须**与具体角色无关**。Mock 只是占位实现，
        // 写死「我是胡桃，往生堂的当代堂主…」会让任何新角色的自我介绍都是胡桃。
        // 想要某个角色的专属口吻有两种做法，都不用把名字写进这里：
        //   1. 给人设包补 catchphrases.json（首选，纯数据）；
        //   2. 像上面芙宁娜/可莉那样加一条专属分支（仅限 Mock 的临时手段）。
        var reply = userMsg switch
        {
            _ when userMsg.Contains("是谁") || userMsg.Contains("你好") || userMsg.Contains("自我介绍")
                => $"我是{_persona.Name}。" + Pick(_persona.Catchphrases.SignatureQuotes),

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
