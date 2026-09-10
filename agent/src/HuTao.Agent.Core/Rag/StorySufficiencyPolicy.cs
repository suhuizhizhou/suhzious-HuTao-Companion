using System.Text.RegularExpressions;

namespace HuTao.Agent.Core.Rag;

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
        if (Regex.IsMatch(q, "(现在|目前|当前).{0,6}(几岁|年龄|多大)") && plan.IsSelf)
            return (StoryStatus.Tentative, "十三岁是既往经历的年龄，当前准确年龄未由这批角色资料确定");
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
