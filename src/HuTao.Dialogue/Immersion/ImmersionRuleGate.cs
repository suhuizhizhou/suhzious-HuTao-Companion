using System.Text;
using System.Text.RegularExpressions;
using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.Core;

namespace HuTao.Dialogue.Immersion;

/// <summary>
/// 确定性沉浸规则层：零延迟、零成本，专抓机械性出戏。
/// 这里只做「模式明确、几乎不会误判」的判定；语气像不像角色、和上文矛不矛盾交给 LLM 评审层。
/// </summary>
public sealed class ImmersionRuleGate
{
    // ── 自称 AI / 模型 / 助手 ──
    private static readonly Regex AiSelf = new(
        @"作为(一个)?(AI|人工智能|大?语言模型|智能助手|虚拟助手|助手|程序|机器人|聊天机器人)" +
        @"|我是(一个)?(AI|人工智能|大?语言模型|智能助手|虚拟助手|模型|助手|程序|机器人|聊天机器人)" +
        @"|我(只|不过|就)是(一个)?(AI|程序|模型|助手)" +
        @"|(AI|人工智能|语言模型)(没有|不具备|无法)(真正的)?(感情|情感|意识|身体|情绪)" +
        @"|as an ai\b|i am an ai\b|i'm an ai\b|language model|i don't have (real )?(feelings|emotions)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ── 客服式客套 ──
    private static readonly Regex Boilerplate = new(
        @"有什么(可以|能)(帮|为)" +
        @"|我(能|可以|来)(帮|为)(你|您)" +
        @"|很高兴(为您|为你)服务|乐意为您|竭诚为您" +
        @"|希望(这|能|可以)(对|帮)(你|您)(有(所)?帮助|)" +
        @"|还有(其他|别的)(问题|需要|可以帮)" +
        @"|请问(还)?(有)?(什么|需要)" +
        @"|请您?(稍等|放心|注意查收)|已为您|为您(解答|提供|整理)" +
        @"|如果(你|您)(还)?有(任何|其他)(问题|疑问)" +
        @"|how can i help|is there anything else|i'm here to help|feel free to ask",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ── 系统内部概念泄露（不含"模型"这类会与前台活动描述撞车的宽泛词）──
    private static readonly Regex SystemLeak = new(
        @"系统(提示词|消息|指令|设定)|提示词|system prompt|\bprompt\b" +
        @"|上下文(窗口|长度)|token(数|限制)?|\bapi\b|接口(调用|参数)" +
        @"|函数调用|工具调用|tool_call|function call|\bjson\b|schema" +
        @"|向量(检索|库|数据库)|语义检索|\brag\b|bm25|倒排索引" +
        @"|语料(库|文件)|数据库|索引文件|配置文件|环境变量|参数配置" +
        @"|第四面墙|\bnpc\b|玩家(视角)?|策划(案|组)?|人设(文件|卡)?|设定集|文案|台词库|脚本文件",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ── 服务式拒绝 ──
    private static readonly Regex Refusal = new(
        @"抱歉[，,、]?\s*(我|本堂主|人家)?(无法|不能|没办法|不能帮)(协助|提供|回答|满足|完成|处理)" +
        @"|我(无法|不能|没办法)(回答|提供|满足|完成|协助)(这个|该|你的)?(请求|问题|要求)" +
        @"|(这个问题|该请求)超出(了)?我" +
        @"|i cannot\b|i can't (assist|help|answer)|i'm unable to|i am unable to",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ── 格式泄露 ──
    private static readonly Regex CodeFence = new(@"```", RegexOptions.Compiled);
    private static readonly Regex MarkdownHeading = new(@"^\s{0,3}#{1,6}\s+\S", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex MarkdownListLine = new(@"^\s{0,3}([-*+]|\d{1,2}[.)])\s+\S", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex MarkdownEmphasis = new(@"\*\*[^*\n]{1,40}\*\*|__[^_\n]{1,40}__|~~[^~\n]{1,40}~~", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`[^`\n]{1,60}`", RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new(@"</?[a-zA-Z][a-zA-Z0-9_]{0,20}(\s[^<>\n]{0,40})?/?>", RegexOptions.Compiled);
    private static readonly Regex Jsonish = new(@"^\s*[{\[]\s*""[^""\n]{1,40}""\s*:", RegexOptions.Compiled | RegexOptions.Multiline);

    // ── 半角星号动作 / 整行半角括号旁白 ──
    private static readonly Regex StarAction = new(@"(?<!\*)\*(?!\*)[^*\n]{1,40}\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex HalfWidthAside = new(@"^\s*\(([^()\n]{2,40})\)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex Cjk = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
    private static readonly Regex AsciiWord = new(@"[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex NormalizeNoise = new(
        @"[\s，。！？、；：…—~～,.!?;:'‘’“”""「」『』（）()*\-]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ImmersionVerdict Inspect(ImmersionRequest request)
    {
        var violations = new List<ImmersionViolation>();
        var lines = request.VisibleText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToList();

        var visible = request.VisibleText;
        Scan(AiSelf, visible, ImmersionViolationKind.AiSelfReference, "自称 AI / 模型 / 助手", violations);
        Scan(Boilerplate, visible, ImmersionViolationKind.AssistantBoilerplate, "客服式客套话", violations);
        Scan(SystemLeak, visible, ImmersionViolationKind.SystemLeak, "泄露系统内部概念", violations);
        Scan(Refusal, visible, ImmersionViolationKind.RefusalTone, "服务式拒绝口吻", violations);

        // 元游戏词单独走一遍：允许角色以"翻档案/资料"的设定口吻说话，所以不放进 SystemLeak 的通杀里。
        var meta = Regex.Match(visible,
            @"(这|那)?(款|个)?游戏(里|中|的)?(设定|剧情|文本|数据)|现实(中|里)的?我|玩家(你|您|自己)?" +
            @"|(官方|米哈游|原神)(设定|文案|策划|资料)",
            RegexOptions.CultureInvariant);
        if (meta.Success)
            violations.Add(new ImmersionViolation(
                ImmersionViolationKind.MetaReference, meta.Value, "打破第四面墙：把虚构世界说成游戏内容"));

        if (CodeFence.IsMatch(visible))
            violations.Add(new ImmersionViolation(ImmersionViolationKind.FormatLeak, "```", "出现代码块围栏"));
        if (MarkdownHeading.IsMatch(visible))
            violations.Add(new ImmersionViolation(ImmersionViolationKind.FormatLeak, "#", "出现 markdown 标题"));
        if (MarkdownListLine.Matches(visible).Count >= 2)
            violations.Add(new ImmersionViolation(ImmersionViolationKind.FormatLeak, "- / 1.", "出现 markdown 列表"));
        if (MarkdownEmphasis.IsMatch(visible))
            violations.Add(new ImmersionViolation(ImmersionViolationKind.FormatLeak, "**", "出现 markdown 强调"));
        if (InlineCode.IsMatch(visible))
            violations.Add(new ImmersionViolation(ImmersionViolationKind.FormatLeak, "`code`", "出现行内代码"));
        if (HtmlTag.IsMatch(visible))
            violations.Add(new ImmersionViolation(ImmersionViolationKind.FormatLeak, "<tag>", "出现 HTML 标签"));
        if (Jsonish.IsMatch(visible))
            violations.Add(new ImmersionViolation(ImmersionViolationKind.FormatLeak, "\"key\":", "出现 JSON 片段"));

        var star = StarAction.Match(visible);
        if (star.Success)
            violations.Add(new ImmersionViolation(ImmersionViolationKind.StageDirection, star.Value, "用半角星号写动作"));
        var aside = HalfWidthAside.Match(visible);
        if (aside.Success)
            violations.Add(new ImmersionViolation(ImmersionViolationKind.StageDirection, aside.Value, "旁白应使用全角括号"));

        // 角色设定为中文：整段几乎不含中文且确有英文词，才判定语言漂移。
        if (lines.Count > 0 && !Cjk.IsMatch(visible) && AsciiWord.Matches(visible).Count >= 3)
            violations.Add(new ImmersionViolation(
                ImmersionViolationKind.LanguageDrift, visible.Length > 30 ? visible[..30] + "…" : visible,
                "整段没有中文，角色应说中文"));

        DetectRepetition(request, violations);

        // 空回复不算出戏，但同样不能放行——交给上层用安全兜底。
        if (visible.Trim().Length == 0)
            violations.Add(new ImmersionViolation(ImmersionViolationKind.Coherence, "(空)", "回复为空"));

        return new ImmersionVerdict(
            violations.Count == 0,
            violations,
            1.0,
            "rules");
    }

    private static void DetectRepetition(ImmersionRequest request, List<ImmersionViolation> violations)
    {
        var current = Normalize(request.VisibleText);
        if (current.Length < 6)
            return;

        var recent = request.History
            .Where(message => message.Role == "assistant")
            .TakeLast(6)
            .Select(message => Normalize(message.Content))
            .Where(text => text.Length >= 6)
            .ToList();

        foreach (var previous in recent)
        {
            if (previous == current)
            {
                violations.Add(new ImmersionViolation(
                    ImmersionViolationKind.Repetition, request.VisibleText[..Math.Min(20, request.VisibleText.Length)],
                    "与之前说过的话完全重复"));
                return;
            }

            if (Jaccard(Bigrams(previous), Bigrams(current)) >= 0.85)
            {
                violations.Add(new ImmersionViolation(
                    ImmersionViolationKind.Repetition, request.VisibleText[..Math.Min(20, request.VisibleText.Length)],
                    "与之前说过的话高度雷同"));
                return;
            }
        }
    }

    /// <summary>
    /// 能确定性修好的问题就地修好，省掉一次 LLM 往返。
    /// 只处理格式类违规；但凡涉及语义（自称 AI、客服腔）一律返回 null 交给模型重写。
    /// </summary>
    public string? TryLocalRepair(string rawReply, ImmersionVerdict verdict)
    {
        if (verdict.Violations.Count == 0)
            return rawReply;

        var repairable = verdict.Violations.All(v => v.Kind is
            ImmersionViolationKind.FormatLeak or ImmersionViolationKind.StageDirection);
        if (!repairable)
            return null;

        var text = rawReply;
        text = CodeFence.Replace(text, "");
        text = Regex.Replace(text, @"^\s{0,3}#{1,6}\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s{0,3}([-*+]|\d{1,2}[.)])\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\*\*([^*\n]{1,40})\*\*", "$1");
        text = Regex.Replace(text, @"__([^_\n]{1,40})__", "$1");
        text = Regex.Replace(text, @"~~([^~\n]{1,40})~~", "$1");
        text = Regex.Replace(text, @"`([^`\n]{1,60})`", "$1");
        text = Regex.Replace(text, @"</?[a-zA-Z][a-zA-Z0-9_]{0,20}(\s[^<>\n]{0,40})?/?>", "");
        // 半角星号动作改成约定的全角括号旁白。
        text = Regex.Replace(text, @"(?<!\*)\*(?!\*)([^*\n]{1,40})\*(?!\*)", "（$1）");
        text = Regex.Replace(text, @"^\s*\(([^()\n]{2,40})\)\s*$", "（$1）", RegexOptions.Multiline);

        var cleaned = string.Join('\n',
            text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static void Scan(
        Regex pattern,
        string text,
        ImmersionViolationKind kind,
        string detail,
        List<ImmersionViolation> violations)
    {
        var match = pattern.Match(text);
        if (match.Success)
            violations.Add(new ImmersionViolation(kind, match.Value, detail));
    }

    private static string Normalize(string text) => NormalizeNoise.Replace(text ?? "", "").ToLowerInvariant();

    private static HashSet<string> Bigrams(string value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < value.Length; i++)
            result.Add(value.Substring(i, 2));
        return result;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        var intersection = a.Count(b.Contains);
        return (double)intersection / (a.Count + b.Count - intersection);
    }
}
