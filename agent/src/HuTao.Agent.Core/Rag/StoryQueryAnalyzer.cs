using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using HuTao.Agent.Core.Abstractions;

namespace HuTao.Agent.Core.Rag;

/// <summary>低延迟确定性路由。原问题不丢弃，改写仅用于召回，不能作为事实证据。</summary>
public static class StoryQueryAnalyzer
{
    private static readonly Regex Quotes = new("[“\"「『](?<q>[^”\"」』]{3,})[”\"」』]", RegexOptions.Compiled);
    private static readonly Regex Follow = new("^(那后来|然后呢|后来呢|那她|那他|那你|所以|回到刚才|刚才说的|前面说的|上一个|接着|为什么呢|为什么啊|那时候|那时|继续说|再详细|上一句|下一句|她为什么|他为什么|那它|那还|再说说)", RegexOptions.Compiled);
    // 只剥去话语包装；人物、数量、否定词保留在原问题里。
    private static readonly Regex Filler = new(
        "帮我找一下|帮我找找|我好像记得|我记得有一句话|我记得有一句|我记得|有没有一句|有一句|是不是|是什么时候|是什么|是谁说的|谁说的|叫什么名字|叫什么|什么身份|哪些东西|哪儿|在哪里|哪里|具体|确切|为什么|怎么|有没有|好像|大概|原文|原话|台词|那句话|那一句|那句|这句|一句话|什么时候|什么|多少|多久|几岁|第几代|来着|本堂主|胡桃|请问|你的|你们|你|妳|的|了|吗|呢|呀|吧|啊|么|有何", RegexOptions.Compiled);
    private static readonly (string Alias, string Name)[] Aliases = [
        ("胡堂主", "胡桃"), ("桃桃", "胡桃"), ("hutao", "胡桃"), ("妳","你"),
        ("钟老爷子", "钟离"), ("达达利亚", "公子"), ("小僵尸", "七七"), ("白老板", "白术") ];
    // 少量领域同义词；不添加人物关系、时间、结局等断言。
    private static readonly string[][] Concepts = [
        ["爷爷", "祖父", "胡老", "老胡头"], ["宠物", "石狮", "大咪", "二咪", "狮子", "养的"],
        ["帽子", "旧帽", "拆补", "头上", "花饰"], ["诗", "赛诗", "评诗", "切磋", "评委"],
        ["告别", "送别", "葬礼", "丧葬"], ["复活", "复生", "生死"],
        ["埋", "火化", "入土"], ["长生", "不死", "永生"], ["晒月亮", "晒太阳"],
        ["账单", "付钱", "摩拉"], ["童年", "小时候", "倒立", "逃学"],
        ["梅花", "风干", "晾晒"], ["干粮", "行囊", "背包"], ["神之眼", "高天", "馈赠"],
        ["信任","信赖"], ["介绍","客卿"], ["出版","发行"], ["贫富","穷人","贵贱","财富"],
        ["客户","客人","需求"], ["第几代","几代","代堂主"],
        ["带走","掳走"], ["阻止","截停"], ["原谅","既往不咎"], ["改","拆补"] ];

    public static StoryQueryPlan Plan(string input, IReadOnlyList<string>? knownEntities = null,
        IReadOnlyList<ChatMessage>? history = null)
    {
        var raw = input.Trim();
        var text = NormalizeWords(raw);
        foreach (var (alias, name) in Aliases.OrderByDescending(a => a.Alias.Length))
            text = text.Replace(alias, name, StringComparison.OrdinalIgnoreCase);
        var follow = Follow.IsMatch(text);
        StoryQueryPlan? previous = null;
        // 只回溯相邻最多4个用户轮次；遇到非剧情话题立即截断，不消费模型自己编的历史。
        if (follow && history is not null)
        {
            foreach (var m in history.Where(m => m.Role == "user").TakeLast(4).Reverse())
            {
                if (Follow.IsMatch(NormalizeWords(m.Content))) continue;
                var candidate = Plan(m.Content, knownEntities);
                if (candidate.Route == StoryRoute.Retrieve) previous = candidate;
                // “回到刚才/前面说的”是显式恢复请求，可以跳过中间的日常话题；普通“然后呢”仍在最近非剧情话题处截断。
                if (previous is not null || !Regex.IsMatch(text, "^(回到刚才|刚才说的|前面说的)")) break;
            }
        }
        var resolvedFollow = previous is not null;
        var self = Regex.IsMatch(text, "胡桃|本堂主|堂主[，,]|你|大咪|二咪");
        var entities = (knownEntities ?? []).Where(n => n.Length >= 2 && text.Contains(n, StringComparison.Ordinal))
            .Concat(new[] { "胡桃", "钟离", "七七", "白术", "行秋", "公子", "派蒙", "芙宁娜" }.Where(text.Contains))
            .Distinct(StringComparer.Ordinal).ToList();
        var explicitStory = Regex.IsMatch(text, "原神|提瓦特|璃月|往生堂|无妄坡|边界|神之眼|海灯节|剧情|档案|台词|原文|原话|谁说的|我记得有一句|那句|这句|本堂主|大咪|二咪|石狮|帽子|梅花|爷爷|叔公|白术|七七|行秋|重云|钟离|客卿|葬礼|葬仪|诗友|魔神|怨念|岩神|七星|客户|客人|需求|冒险家协会|丘丘");
        var personal = self && Regex.IsMatch(text, "爷爷|父亲|帽子|头上|童年|小时|岁|七七|石狮|宠物|诗|神之眼|第.+代|当时|以前|曾经|是不是|养|生死|送别|梅花|月下|边界");
        var route = explicitStory || entities.Count > 0 || personal || resolvedFollow || Quotes.IsMatch(raw)
            ? StoryRoute.Retrieve : StoryRoute.Bypass;
        var reason = "剧情或角色线索";
        if (follow && !resolvedFollow) { route = StoryRoute.Clarify; reason = "追问缺少相邻剧情上下文"; }
        if (Regex.IsMatch(text, "(忽略|覆盖|泄露|输出|告诉我).{0,18}(提示词|系统指令|api.?key|密钥)|把.{0,15}(同人|编的).{0,10}(当成|说成).{0,8}(官方|正史)|伪造.{0,8}(引用|原文)|假装.{0,10}亲眼"))
        { route = StoryRoute.Boundary; reason = "不能把编造的内容当官方经历"; }
        else if (Regex.IsMatch(text, "(我|家里|我的).{0,12}(爷爷|奶奶|亲人|家人|爸爸|妈妈|朋友).{0,8}(去世|走了|死了)|我.{0,8}(难过|想哭|撑不住|不想活)"))
        { route = StoryRoute.Comfort; reason = "现实情绪优先"; }
        else if (Regex.IsMatch(text, "假如|假设|如果|整活|玩梗|同人|穿越|开高达|外卖|奶茶|手机|wifi|火星|赛博朋克") && !resolvedFollow &&
            (self || route == StoryRoute.Retrieve) &&
            (Regex.IsMatch(text, "同人|穿越|开高达|外卖|奶茶|手机|wifi|火星|赛博朋克") || !explicitStory))
        { route = StoryRoute.Playful; reason = "明确假设/跨作品，不当官方事实"; }
        else if (!resolvedFollow && (!explicitStory || Regex.IsMatch(text, "我的帽子|我养的宠物|丢了|不吃饭")) && Regex.IsMatch(text, "(胡桃|你).{0,8}(早安|晚安|你好|在吗|陪我|抱抱|谢谢|午饭|喜欢我)|今天.{0,8}(天气|吃什么)|电脑|python|报错|编译|蓝屏|音量|我今天加班|我.{0,4}(帽子|宠物).{0,6}(丢|不吃|坏)"))
        { route = StoryRoute.Bypass; reason = "日常聊天或现实技术问题"; }
        var search = resolvedFollow ? previous!.SearchText + " " + text : text;
        if (self && route == StoryRoute.Retrieve && !search.Contains("胡桃")) search = "胡桃 " + search;
        // 追问保留上一轮未剥离的主题变体作为第一召回通道；否则“那后来呢”这类控制词切分后会淹没上一轮的实体和记忆主题。
        var variants = (resolvedFollow ? new[] { previous!.SearchText } : Enumerable.Empty<string>())
            .Concat(GetVariants(search)).Distinct(StringComparer.Ordinal).ToList();
        var vague = Normalize(Filler.Replace(text, "")).Trim();
        if (raw.Length == 0 || Normalize(text).Length == 0 ||
            route == StoryRoute.Retrieve && !resolvedFollow && (vague.Length < 2 || Regex.IsMatch(vague, "^(话|一|谁|人|是|那|个|还记得)+$")))
        { route = StoryRoute.Clarify; reason = "没有可定位的剧情内容"; }
        if (Regex.IsMatch(text, "^(前一个|后一个|那个|这个)"))
        { route = StoryRoute.Clarify; reason = "指代对象不唯一，需要澄清"; }
        var quote = Regex.IsMatch(raw, "原文|原话|台词|谁说的|那句|这句|一句|逐字") || Quotes.IsMatch(raw);
        return new StoryQueryPlan(raw, search, variants, entities, route, quote, self || previous?.IsSelf == true, resolvedFollow, reason);
    }
    public static IReadOnlyList<string> GetVariants(string query)
    {
        var quoted = Quotes.Matches(query).Select(m => Normalize(m.Groups["q"].Value)).ToList();
        // 复杂联动问题常把多个子问题写在一句话里。保留整句用于整体语义，同时将中文标点切出的子句作为独立召回通道，避免某个长子句稀释另一个事实的 BM25 得分。
        var clauses = Regex.Split(query, "[，,；;。！？!?：:]" )
            .Select(part => Normalize(Filler.Replace(part, "")))
            // 纯“那后来呢/然后呢”是指代控制词，不应在全语料中当作普通关键词命中随机台词；真正的主题来自已解析的相邻历史。
            .Where(part => part.Length >= 2 && !Regex.IsMatch(part, "^(那后来|然后呢|后来呢|那时候|那时)$"));
        quoted.AddRange(clauses);
        // 原句保留用于精确定位，内容词版本用于口语问句 BM25；短内容词不会误触发原句快速答复。
        var clean = Normalize(Filler.Replace(query, ""));
        if (clean.Length >= 2) quoted.Add(clean);
        quoted.Add(Normalize(query));
        return quoted.Where(v => v.Length >= 2).Distinct(StringComparer.Ordinal).ToArray();
    }
    public static IEnumerable<string> Expand(StoryQueryPlan plan)
    {
        yield return plan.SearchText;
        foreach (var group in Concepts)
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
        return normalized.Replace('麽', '么'); // Windows NLS 将「麼」映射成异体「麽」。
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
