using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using HuTao.Foundation.Abstractions;
using HuTao.Persona;

namespace HuTao.Knowledge.Rag;

/// <summary>
/// 低延迟确定性路由。原问题不丢弃，改写仅用于召回，不能作为事实证据。
///
/// **角色无关**：所有角色/作品专属词汇都来自 <see cref="StoryLexicon"/>，
/// 本文件只保留中文问句的**句式**（第.+代、当时/以前/曾经、然后呢/那后来）与
/// 与作品无关的安全/情绪分支。加一个新角色不需要改这里。
/// </summary>
public static class StoryQueryAnalyzer
{
    private static readonly Regex Quotes = new("[“\"「『](?<q>[^”\"」』]{3,})[”\"」』]", RegexOptions.Compiled);
    private static readonly Regex Follow = new("^(那后来|然后呢|后来呢|那她|那他|那你|所以|回到刚才|刚才说的|前面说的|上一个|接着|为什么呢|为什么啊|那时候|那时|继续说|再详细|上一句|下一句|她为什么|他为什么|那它|那还|再说说)", RegexOptions.Compiled);

    /// <summary>
    /// 通用填充词：只剥去话语包装，人物、数量、否定词保留在原问题里。
    /// **角色专属的填充词（本堂主 / 胡桃）不在这里**，由 <see cref="StoryLexicon.StopWordPattern"/> 追加。
    /// </summary>
    private static readonly Regex Filler = new(
        "帮我找一下|帮我找找|我好像记得|我记得有一句话|我记得有一句|我记得|有没有一句|有一句|是不是|是什么时候|是什么|是谁说的|谁说的|叫什么名字|叫什么|什么身份|哪些东西|哪儿|在哪里|哪里|具体|确切|为什么|怎么|有没有|好像|大概|原文|原话|台词|那句话|那一句|那句|这句|一句话|什么时候|什么|多少|多久|几岁|第几代|来着|请问|你的|你们|你|妳|的|了|吗|呢|呀|吧|啊|么|有何", RegexOptions.Compiled);

    /// <summary>
    /// 与角色无关的剧情标记：这些词在任何作品里都表示「他在问档案/原文」。
    /// </summary>
    private const string GenericStoryMarkers = "剧情|档案|台词|原文|原话|谁说的|我记得有一句|那句|这句";

    /// <summary>与角色无关的身世问句句式。</summary>
    private const string GenericPersonalPattern = "第.+代|当时|以前|曾经|是不是|养";

    public static StoryQueryPlan Plan(string input, StoryLexicon? lexicon = null,
        IReadOnlyList<ChatMessage>? history = null)
    {
        lexicon ??= StoryLexicon.Empty;
        var raw = input.Trim();
        var text = lexicon.ApplyAliases(NormalizeWords(raw));
        var follow = Follow.IsMatch(text);
        StoryQueryPlan? previous = null;
        // 只回溯相邻最多4个用户轮次；遇到非剧情话题立即截断，不消费模型自己编的历史。
        if (follow && history is not null)
        {
            foreach (var m in history.Where(m => m.Role == "user").TakeLast(4).Reverse())
            {
                if (Follow.IsMatch(NormalizeWords(m.Content))) continue;
                var candidate = Plan(m.Content, lexicon);
                if (candidate.Route == StoryRoute.Retrieve) previous = candidate;
                // “回到刚才/前面说的”是显式恢复请求，可以跳过中间的日常话题；普通“然后呢”仍在最近非剧情话题处截断。
                if (previous is not null || !Regex.IsMatch(text, "^(回到刚才|刚才说的|前面说的)")) break;
            }
        }
        var resolvedFollow = previous is not null;
        var self = lexicon.SelfPattern.IsMatch(text);
        var entities = lexicon.Entities
            .Where(n => n.Length >= 2 && text.Contains(n, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToList();
        var explicitStory = lexicon.StoryPattern.IsMatch(text) ||
                            Regex.IsMatch(text, GenericStoryMarkers);
        var personal = self && Regex.IsMatch(text,
            StoryLexicon.Group(lexicon.PersonalTopics) + "|" + GenericPersonalPattern);
        // 用户显式声明「不是在问剧情/游戏」时，剧情词不能再当检索线索。
        // 踩过的坑（很讽刺）：原句里出现「别讲游戏剧情」，反而因为含「剧情」被判成剧情提问，
        // 于是「帮我解释 Python 的 IndexError，别讲游戏剧情」走了 Retrieve，
        // 检索到石狮子那句再高置信作答——用户已经把边界说清楚了，路由器听反了。
        //
        // 但声明有两种含义，必须分开：
        //   · 「我根本不在问剧情」 → 该域词不再构成线索（Python / 算数 / 折字符串）。
        //   · 「你可以讲剧情，但别那样包装」 → 声明只管表达方式，问题仍是剧情
        //     （「先说你和行秋诗风有什么区别…别包装成游戏名句」）。
        // 判据：把声明段摘掉之后**还有没有剧情线索**。另外自身名字（胡桃）不算线索——
        // 几乎每句话都在叫她的名字，用它当线索会让所有声明都失效。
        // 留出验证（2026-09-13，12 句不在 benchmark 里的新句子）暴露第一版只过 2/5：
        // 真实用户表达「不在问这个」的方式远不止「别讲 X」，还有「跟 X 无关」「和 X 没关系」
        // 「不用讲他们是谁」。所以补两条与句式无关的构造，而不是往动词表里堆词：
        //   · 「跟/和/与 X 无关」——把域词显式排除；
        //   · 「不用/不必/别 + 讲/说/提/介绍 + 他们/这些/人物」——声明不需要介绍对象。
        var disclaimer = new Regex(
            "(别|不要|不用|不需要|不必|不是|别讲|不要讲|不是问|不是聊|别提|别介绍).{0,10}(剧情|故事|游戏|人设|家谱|资料|原作|原神|设定|人物|背景)" +
            "|(跟|和|与).{0,8}(剧情|故事|游戏|原作|原神|设定|他们|这些).{0,6}(无关|没关系|没关|不相干)" +
            "|(不用|不需要|不必|别|不要).{0,6}(讲|说|提|介绍|解释).{0,8}(他们|这些|是谁|人物|背景)");
        var stripped = disclaimer.Replace(text, "");
        // 实体也可能是**待处理的数据**，而不是剧情主语：
        // 「把七七 钟离 行秋 按拼音排序，不用讲他们是谁」里那三个名字只是要排序的字符串。
        // 出现数据处理动词时，实体不再算作剧情线索——否则「声明不在问剧情」会被
        // 这些被当数据用的名字抵消掉。
        var dataTask = Regex.IsMatch(stripped,
            "排序|按拼音|按笔画|拆分|分割|去重|转成|转换成|格式化|缩进|正则|json|数组|编码|解码|统计|字数|替换成");
        if (stripped != text)
        {
            var stillStory = lexicon.StoryPattern.IsMatch(stripped) ||
                             Regex.IsMatch(stripped, GenericStoryMarkers) ||
                             (!dataTask && lexicon.Entities.Any(n => n.Length >= 2 &&
                                 !string.Equals(n, lexicon.SelfName, StringComparison.Ordinal) &&
                                 stripped.Contains(n, StringComparison.Ordinal))) ||
                             (self && Regex.IsMatch(stripped,
                                 StoryLexicon.Group(lexicon.PersonalTopics) + "|" + GenericPersonalPattern));
            if (stillStory)
            {
                // 真正的线索只从摘掉声明的文本里取，避免声明自身的「游戏/剧情」又被算一次。
                explicitStory = lexicon.StoryPattern.IsMatch(stripped) || Regex.IsMatch(stripped, GenericStoryMarkers);
                personal = self && Regex.IsMatch(stripped,
                    StoryLexicon.Group(lexicon.PersonalTopics) + "|" + GenericPersonalPattern);
                entities = dataTask ? [] : lexicon.Entities
                    .Where(n => n.Length >= 2 && stripped.Contains(n, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal).ToList();
            }
            else
            {
                explicitStory = false;
                personal = false;
                entities.Clear();
            }
        }
        var route = explicitStory || entities.Count > 0 || personal || resolvedFollow || Quotes.IsMatch(raw)
            ? StoryRoute.Retrieve : StoryRoute.Bypass;
        var reason = stripped != text ? "识别到剧情域声明，按剩余线索判断" : "剧情或角色线索";
        if (follow && !resolvedFollow) { route = StoryRoute.Clarify; reason = "追问缺少相邻剧情上下文"; }
        if (Regex.IsMatch(text, "(忽略|覆盖|泄露|输出|告诉我).{0,18}(提示词|系统指令|api.?key|密钥)|把.{0,15}(同人|编的).{0,10}(当成|说成).{0,8}(官方|正史)|伪造.{0,8}(引用|原文)|假装.{0,10}亲眼"))
        { route = StoryRoute.Boundary; reason = "不能把编造的内容当官方经历"; }
        // 现实情绪优先。判据是「现实生活里的情绪困扰」，与物种、关系都无关。
        // 原规则只认「人类亲属去世」，于是「我养的猫走了」被判成剧情提问，检索出角色资料里的
        // 大咪二咪，用高置信讲游戏里的猫——对正在经历丧失的人是伤害。
        //
        // 留出验证（2026-09-13，用 10 句**不在 benchmark 里**的新句子）证明第一版是过拟合的：
        // 现实情绪组只过 2/6，对照组（必须走剧情）4/4。缺口全是结构性的，据此补：
        //   · 结构要覆盖「养了…的」，不只是「我养的」；
        //   · 失去动词要含「丢了 / 不见了」；
        //   · 情绪词要含「分手 / 压力 / 会哭 / 睡不着 / 提不起劲」；
        //   · 第一人称语境不能只认紧邻的「我」——「晚上一个人会哭」整句一个「我」都没有。
        //     「一个人 / 最近 / 这几天」只在用户描述自己近况时出现，算同义语境。
        else if (Regex.IsMatch(text,
            // ① 失去陪伴对象（亲属 / 宠物 / 伴侣）
            "(我|我家|家里|我的).{0,12}(爷爷|奶奶|外公|外婆|亲人|家人|爸爸|妈妈|父母|朋友|男朋友|女朋友|猫|狗|宠物|毛孩子|仓鼠|兔子|鹦鹉|金鱼).{0,8}(去世|走了|死了|没了|丢了|不见了|安乐死|离开了)" +
            "|(养的|养了|陪伴).{0,10}(猫|狗|宠物|毛孩子|鹦鹉|仓鼠|兔子|金鱼).{0,8}(去世|走了|死了|没了|丢了|不见了|安乐死|离开了)" +
            // ② 现实情绪表达：第一人称语境 + 情绪词
            "|(我|自己|一个人|我们|最近|这几天|今天|昨晚).{0,14}(分手|失恋|离婚|难过|难受|害怕|恐慌|脆弱|想哭|哭了|会哭|撑不住|不想活|好累|崩溃|抱怨|睡不着|提不起劲|没缓|压力|陪陪我|陪我缓)" +
            // ③ 刚发生的现实事件 + 情绪词
            "|(刚|刚刚|最近|昨天|前天).{0,10}(分手|失恋|离婚)" +
            "|(后事|葬礼|住院|病危|生日|噩梦|梦见|压力).{0,14}(我|没人|难过|难受|害怕|哭了|陪我)"))
        { route = StoryRoute.Comfort; reason = "现实情绪优先"; }
        else if (Regex.IsMatch(text, "假如|假设|如果|整活|玩梗|同人|穿越|开高达|外卖|奶茶|手机|wifi|火星|赛博朋克") && !resolvedFollow &&
            (self || route == StoryRoute.Retrieve) &&
            (Regex.IsMatch(text, "同人|穿越|开高达|外卖|奶茶|手机|wifi|火星|赛博朋克") || !explicitStory))
        { route = StoryRoute.Playful; reason = "明确假设/跨作品，不当官方事实"; }
        else if (!resolvedFollow && (!explicitStory || lexicon.IntimatePattern.IsMatch(text)) &&
            Regex.IsMatch(text, SelfTalkPattern(lexicon)))
        { route = StoryRoute.Bypass; reason = "日常聊天或现实技术问题"; }
        var search = resolvedFollow ? previous!.SearchText + " " + text : text;
        if (self && route == StoryRoute.Retrieve && !string.IsNullOrWhiteSpace(lexicon.SelfName) &&
            !search.Contains(lexicon.SelfName, StringComparison.Ordinal))
            search = lexicon.SelfName + " " + search;
        // 追问保留上一轮未剥离的主题变体作为第一召回通道；否则“那后来呢”这类控制词切分后会淹没上一轮的实体和记忆主题。
        var variants = (resolvedFollow ? new[] { previous!.SearchText } : Enumerable.Empty<string>())
            .Concat(GetVariants(search, lexicon)).Distinct(StringComparer.Ordinal).ToList();
        var vague = Normalize(StripFillers(text, lexicon)).Trim();
        if (raw.Length == 0 || Normalize(text).Length == 0 ||
            route == StoryRoute.Retrieve && !resolvedFollow && (vague.Length < 2 || Regex.IsMatch(vague, "^(话|一|谁|人|是|那|个|还记得)+$")))
        { route = StoryRoute.Clarify; reason = "没有可定位的剧情内容"; }
        // 指代不明：句子一开头就是「那之后 / 第二个 / 前面那个 / 他们」这类指向，
        // 而相邻剧情上下文又没解析出来（resolvedFollow 为假）时，不能拿这些词去全语料碰运气——
        // 碰出来的必然是同形噪声（「帽子是谁的」匹配到「这靴子是谁的」就是这么发生的），
        // 应当先反问澄清。
        // 原规则只覆盖「^(前一个|后一个|那个|这个)」，于是「第二个是怎么回事」「他们后来为什么这样」
        // 「那之后呢」「前面那个到底是哥哥还是弟弟」全部落到 Bypass，clarification 这一类 5 例全错。
        if (!resolvedFollow && Regex.IsMatch(text,
            "^(那之后|这之后|那后来|然后|接着|继续说|前一个|后一个|上一个|下一个|" +
            "前面(那个|说的|提到的)|后面(那个|说的|提到的)|第[一二三四五六七八九十]+个|他们|她们|它们|" +
            "那个|这个)"))
        { route = StoryRoute.Clarify; reason = "指代对象不唯一，需要澄清"; }
        var quote = Regex.IsMatch(raw, "原文|原话|台词|谁说的|那句|这句|一句|逐字") || Quotes.IsMatch(raw);
        return new StoryQueryPlan(raw, search, variants, entities, route, quote, self || previous?.IsSelf == true, resolvedFollow, reason,
            lexicon.SelfName, lexicon.AcceptanceLine, lexicon.SelfReferences);
    }

    /// <summary>
    /// 「打招呼 / 问现实技术问题」的判定句式。角色名与私人话题来自词表，句式本身与角色无关。
    /// </summary>
    private static string SelfTalkPattern(StoryLexicon lexicon)
    {
        var self = StoryLexicon.Group(
            string.IsNullOrWhiteSpace(lexicon.SelfName) ? ["你"] : [lexicon.SelfName, "你"]);
        var topics = StoryLexicon.Group(lexicon.PersonalTopics);
        return $"{self}.{{0,8}}(早安|晚安|你好|在吗|陪我|抱抱|谢谢|午饭|喜欢我)" +
               "|今天.{0,8}(天气|吃什么)" +
               "|电脑|python|报错|编译|蓝屏|音量|我今天加班" +
               $"|我.{{0,4}}{topics}.{{0,6}}(丢|不吃|坏)";
    }

    /// <summary>剥掉通用填充词与角色专属填充词。</summary>
    private static string StripFillers(string text, StoryLexicon lexicon)
        => lexicon.StopWordPattern.Replace(Filler.Replace(text, ""), "");

    /// <summary>查询变体。同样要求显式传词表，理由见 <see cref="Expand"/>。</summary>
    /// <summary>
    /// 派生变体是否有区分度。**必须按 gram 数判，不能按字符数判。**
    /// 踩过的坑：原来用 `Length >= 2` 当准入条件，而 bigram 口径下两字串只含 1 个 gram，
    /// 于是 <c>StoryLineIndex.Coverage</c> 拿变体自身长度归一后必然得到 1.000——
    /// 「胡桃现在几岁」剥完填充词只剩「现在」，任何含「现在」的台词都被判成满分命中
    /// （实测无关的猫台词拿到 Coverage=1.000 / Score=0.7400，比真正沾边的证据还高）。
    /// 单 gram 变体在数学上无法区分任何东西，所以不配当召回通道。
    /// </summary>
    private static bool IsInformative(string value) => BigramHashes(value).Take(2).Count() == 2;

    public static IReadOnlyList<string> GetVariants(string query, StoryLexicon lexicon)
    {
        var quoted = Quotes.Matches(query).Select(m => Normalize(m.Groups["q"].Value)).ToList();
        // 复杂联动问题常把多个子问题写在一句话里。保留整句用于整体语义，同时将中文标点切出的子句作为独立召回通道，避免某个长子句稀释另一个事实的 BM25 得分。
        var clauses = Regex.Split(query, "[，,；;。！？!?：:]" )
            .Select(part => Normalize(StripFillers(part, lexicon)))
            // 纯「那后来呢/继续说/接着」是指代控制词，不应在全语料中当作普通关键词命中随机台词；
            // 真正的主题来自已解析的相邻历史。
            // 同理，剥完填充词只剩一两个字的残渣（「胡桃现在几岁」→「现在」）也不能当通道。
            // 实测踩过的坑：问「继续说」时控制词自己成了一条召回通道，捞回一堆字面含"你继续说"的
            // 无关台词（覆盖度 1.000、拿 Answer），把本该由上一轮主题决定的证据全挤掉了。
            // 所以这里**只匹配纯控制词**——带主题的（「继续说钟离在层岩巨渊做了什么」）不算。
            .Where(part => IsInformative(part) && !Regex.IsMatch(part,
                "^(那后来|然后呢|后来呢|那时候|那时|" +
                "继续(说|讲|聊)(下去|吧|呀|啊|嘛)?|接着(说|讲|聊)?(下去|吧)?|" +
                "再(详细|具体)(说说|讲讲)?|再(说说|讲讲)|" +
                "上一句|下一句|那之后|这之后|回到刚才|刚才说的|前面说的)$"));
        quoted.AddRange(clauses);
        // 原句保留用于精确定位，内容词版本用于口语问句 BM25；短内容词不会误触发原句快速答复。
        var clean = Normalize(StripFillers(query, lexicon));
        if (IsInformative(clean)) quoted.Add(clean);
        // 原句这一路无条件保留：用户真正打出来的整句永远有资格当召回通道。
        quoted.Add(Normalize(query));
        return quoted.Where(v => v.Length >= 2).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 查询扩展。
    ///
    /// ⚠️ <paramref name="lexicon"/> 是**必填**，故意不给默认值。
    /// 踩过的坑：它原来有 `= null` 默认值，于是我从「写死词表」改成「词表驱动」时，
    /// 同一文件里改掉了一处 `Expand(plan)`、漏了另一处，**编译器一声不吭**。
    /// 症状是 100 场景回归里 2 个变体的事实组覆盖掉了（Context 100% → 97.8%），
    /// 而路由准确率完全正常——最难查的那一类回归。
    /// 必填参数能在编译期抓住它。
    /// </summary>
    public static IEnumerable<string> Expand(StoryQueryPlan plan, StoryLexicon lexicon)
    {
        yield return plan.SearchText;
        // 概念组**必须整组拼成一条变体**，不要拆成逐个同义词。实测记录（2026-09-13）：
        // 曾为了「不被长串稀释」改成逐条 yield，结果全面变差——
        // regression Context 100.0%→98.9%、challenge R@5 71.7%→70.9%、MRR 0.649→0.641、legacy 88.1%→87.4%。
        // 原因是 `Expand` 的输出**不经过 GetVariants 的 IsInformative 准入闸门**：
        // 拆开后「旧帽」这类两字同义词只有 1 个 bigram，Coverage 按自身长度归一 → 必然 1.000，
        // 而 Coverage 取变体最大值 → 撞上一个短同义词就满分。等于把已修掉的覆盖率虚高从另一条路放回来。
        // 拼串在这里是**故意的**：它把若干短同义词聚成一条信息量足够的变体。
        // 若将来要拆开，必须同时让每个同义词过 IsInformative。
        foreach (var group in lexicon.ConceptGroups)
            if (group.Any(plan.SearchText.Contains)) yield return string.Join(' ', group);
    }

    public static string NormalizeWords(string value)
    {
        string normalized;
        try { normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(); }
        catch (ArgumentException)
        {
            // 非法 UTF-16 不应让粘贴输入崩溃；枚举 Rune 将孤立代理项替换为 U+FFFD。
            normalized = string.Concat(value.EnumerateRunes().Select(r => r.ToString())).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        }
        if (OperatingSystem.IsWindows() && normalized.Length > 0)
        {
            var buffer = new char[normalized.Length * 2 + 1];
            var length = LCMapStringEx("zh-CN", 0x02000000, normalized, normalized.Length, buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (length > 0) normalized = new string(buffer, 0, length);
        }
        // 这两个是**与角色无关**的中文异体字归一，所以留在引擎里而不是词表里：
        // Windows NLS 把「麼」映射成「麽」；繁体「妳」与「你」在检索上等价。
        return normalized.Replace('麽', '么').Replace('妳', '你');
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        [Out] char[] destination, int destinationLength, IntPtr version, IntPtr reserved, IntPtr sortHandle);
    public static string Normalize(string value) => new(NormalizeWords(value).Where(char.IsLetterOrDigit).ToArray());
    public static IEnumerable<uint> BigramHashes(string value)
    {
        var normalized = Normalize(value);
        for (var i = 0; i + 1 < normalized.Length; i++) yield return ((uint)normalized[i] << 16) | normalized[i + 1];
    }
}
