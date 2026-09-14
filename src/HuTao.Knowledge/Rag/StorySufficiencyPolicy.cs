using System.Text.RegularExpressions;

namespace HuTao.Knowledge.Rag;

/// <summary>
/// 已核验角色资料的认识边界，不是通用“事实判定器”。
/// 规则只降低确定性，不生成新事实。新增剧情明确了答案时，应连同规则与回归标注一起更新。
/// </summary>
internal static class StorySufficiencyPolicy
{
    public static (StoryStatus Status, string? Caution) Apply(StoryQueryPlan plan,
        IReadOnlyList<StoryEvidence> evidence, StoryStatus status)
    {
        var q = StoryQueryAnalyzer.NormalizeWords(plan.Original);
        var lines = evidence.SelectMany(e => e.Context).ToArray();
        var year = Regex.Match(q, @"(?<!\d)\d{4}年");
        if (year.Success && !lines.Any(l => l.Text.Contains(year.Value, StringComparison.Ordinal)))
            return (StoryStatus.NotFound, "当前证据没有用户指定年份的剧情，不从同名人物推断未来结局");
        if (status is StoryStatus.NotFound or StoryStatus.Clarify) return (status, null);
        // 这里原本有一条「(现在|目前|当前).{0,6}(几岁|年龄|多大) → Tentative」的专项规则，
        // 连解释文案都硬写了「十三岁」。已删除，理由是它属于**评分器的替身**而不是认识边界：
        // 它挡住的是「短问句覆盖率虚高 → 越过 AnswerScore」这个普遍缺陷，
        // 而那个缺陷现在由通用的证据下限处理
        // （StoryRagOptions.MinAnswerCoverage：被引证据必须承载问题里足够份额的信息量）。
        // 「胡桃现在确切几岁？」在通用机制下 Coverage 只有 0.149，本来就到不了 Answer，
        // 不需要为年龄单独开一条。删掉后 unknown_current_age_caution 仍然通过，
        // 说明它确实是靠通用机制而不是靠这条正则。
        if (plan.IsSelf && Regex.IsMatch(q, "(父亲|爸爸|老爹).{0,12}(名字|名叫|叫什么|七十六|第.+代)|七十六.{0,8}(父亲|爸爸)"))
            return (StoryStatus.Tentative, "不能由堂主代数推导父亲身份或姓名；必须找到明确的原文关系才能确认");
        if (q.Contains("边界") && Regex.IsMatch(q, "一共|总共|总计|合计"))
            return (StoryStatus.Tentative, "原文有路上两日、等一整日及日复一日，并未给出边界逗留总天数，禁止相加猜测");
        if (q.Contains("神之眼") && Regex.IsMatch(q, "几点|几分|几时|确切.*(出现|时间)"))
            return (StoryStatus.Tentative, "整理行囊时发现神之眼，不等于知道它具体出现的时刻");
        if (Regex.IsMatch(q, "(老妇|妇人).{0,10}(叫什么|姓名|名字|身份)"))
            return (StoryStatus.Tentative, "角色资料没有给边界老妇人的名字，不凭外貌或称呼推断身份");
        if (q.Contains("神之眼") && Regex.IsMatch(q, "哪.{0,6}神|神明确|谁赐|谁给"))
            return (StoryStatus.Tentative, "关于神明动机原文用或许，未明确具体是哪位神明");
        return (status, null);
    }
}
