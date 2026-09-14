using System.Globalization;
using System.Text.RegularExpressions;

namespace HuTao.Knowledge.Memory;

/// <summary>一次时间解析结果：推断出的失效时刻 + 给模型看的时间口径。</summary>
public sealed record TemporalScope(DateTimeOffset? ValidUntil, string Hint, string Evidence)
{
    public static readonly TemporalScope None = new(null, "", "");
    public bool HasScope => ValidUntil is not null || Hint.Length > 0;
}

/// <summary>
/// 中文相对时间解析：把「下周三」「明天」「这周末」「等我考完试」这类表述
/// 换成一个失效时刻，让旧信息到点自动降级而不是继续被当成现状。
///
/// 纯确定性、零 LLM 成本。这是阶段 2 的核心——纯检索只会让过时信息被更准确地召回，
/// 必须额外知道「这条什么时候不再成立」。
///
/// 设计上刻意保守：只有明确的相对时间词才给失效时间；含糊表述（「以后」「有空」）
/// 只给提示不给期限，避免系统自作主张把仍然有效的记忆判死。
/// </summary>
public static class TemporalExpression
{
    // 顺序有意义：先匹配更长更具体的表述。
    private static readonly (Regex Pattern, Func<DateTimeOffset, DateTimeOffset?> Resolve, string Hint)[] Rules =
    [
        // ── 今天/明天/后天 ──
        (new Regex(@"后天", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => EndOfDay(now.AddDays(2)), "后天"),
        (new Regex(@"明天|明日", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => EndOfDay(now.AddDays(1)), "明天"),
        (new Regex(@"今天|今日", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => EndOfDay(now), "今天"),
        (new Regex(@"昨天|昨日|前天", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => null, "过去的时间"),

        // ── 周 ──
        (new Regex(@"下周|下星期|下礼拜", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => EndOfWeek(now.AddDays(7)), "下周"),
        (new Regex(@"本周|这周|这星期|这个星期", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => EndOfWeek(now), "这周"),
        (new Regex(@"上周|上星期", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => null, "过去的时间"),
        (new Regex(@"周末", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => EndOfWeek(now), "这周内"),

        // ── 月 / 年 ──
        (new Regex(@"下个?月|下月", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => EndOfMonth(now.AddMonths(1)), "下个月"),
        (new Regex(@"这个月|本月", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => EndOfMonth(now), "这个月"),
        (new Regex(@"明年", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => new DateTimeOffset(now.Year + 1, 12, 31, 23, 59, 59, now.Offset), "明年"),

        // ── 天级偏移 ──
        (new Regex(@"(?<n>[一二两三四五六七八九十\d]{1,2})\s*天(?:以)?[后内]", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => null, "相对天数"), // 由 Parse 单独处理，见 ResolveDaysFromNow
        (new Regex(@"(?<n>[一二两三四五六七八九十\d]{1,2})\s*(?:个)?(?:星期|周)(?:以)?[后内]", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => null, "相对周数"),
        (new Regex(@"(?<n>[一二两三四五六七八九十\d]{1,2})\s*(?:个)?月(?:以)?[后内]", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => null, "相对月数"),

        // ── 小时级 / 当日剩余 ──
        (new Regex(@"今晚|今天晚上|今夜", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => EndOfDay(now), "今晚"),
        (new Regex(@"今天(?:下午|上午|中午|晚上)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => EndOfDay(now), "今天"),
        (new Regex(@"一会儿|等会儿|待会儿|马上|立刻|这就|稍后", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => now.AddHours(2), "接下来的短时间内"),
        (new Regex(@"今晚之前|今天之内|今天以内", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => EndOfDay(now), "今天之内"),

        // ── 显式日期 M月D日 ──
        (new Regex(@"(?<m>\d{1,2})\s*月\s*(?<d>\d{1,2})\s*[日号]", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            now => null, "具体日期"),
    ];

    private static readonly Regex DaysFromNow = new(
        @"(?<n>[一二两三四五六七八九十\d]{1,2})\s*天(?:以)?[后内]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WeeksFromNow = new(
        @"(?<n>[一二两三四五六七八九十\d]{1,2})\s*(?:个)?(?:星期|周)(?:以)?[后内]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MonthsFromNow = new(
        @"(?<n>[一二两三四五六七八九十\d]{1,2})\s*(?:个)?月(?:以)?[后内]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>含糊、无期限的表述：只提示「这是过去某时的说法」，不给失效时间。</summary>
    private static readonly Regex Vague = new(
        @"以后|将来|总有一天|有空|改天|再说吧|一直|永远",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>已经完成/结束的信号：让记忆更早降级。</summary>
    private static readonly Regex Completed = new(
        @"已经|考完|做完|搞定|结束了|完成了|过了|回来[了啦]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static TemporalScope Parse(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TemporalScope.None;

        var value = text.Trim();

        // 先处理带数字的相对偏移，它们的失效时间不是「当天结束」而是「N 个单位之后」。
        if (TryResolveOffset(DaysFromNow, value, out var days, out var dayEvidence))
            return new TemporalScope(EndOfDay(now.AddDays(days)), $"{days} 天内", dayEvidence);
        if (TryResolveOffset(WeeksFromNow, value, out var weeks, out var weekEvidence))
            return new TemporalScope(EndOfDay(now.AddDays(weeks * 7)), $"{weeks} 周内", weekEvidence);
        if (TryResolveOffset(MonthsFromNow, value, out var months, out var monthEvidence))
            return new TemporalScope(EndOfMonth(now.AddMonths(months)), $"{months} 个月内", monthEvidence);

        foreach (var (pattern, resolve, hint) in Rules)
        {
            var match = pattern.Match(value);
            if (!match.Success)
                continue;

            // 显式日期单独算，因为它可能跨年。
            if (hint == "具体日期")
            {
                var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
                var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
                var until = ResolveExplicitDate(now, month, day);
                return until is null
                    ? new TemporalScope(null, "具体日期（格式可疑）", match.Value)
                    : new TemporalScope(until, $"{month} 月 {day} 日前后", match.Value);
            }

            return new TemporalScope(resolve(now), hint, match.Value);
        }

        if (Vague.IsMatch(value))
            return new TemporalScope(null, "长期有效的表述", "");

        if (Completed.IsMatch(value))
            return new TemporalScope(null, "已经完成的事", "");

        return TemporalScope.None;
    }

    /// <summary>
    /// 给模型看的时间口径。这是阶段 2 真正起作用的地方：
    /// 模型看不到时间就会把三周前的「下周考试」当成现状，明确写出来它才会自己判断。
    /// </summary>
    public static string DescribeAge(DateTimeOffset observedAt, DateTimeOffset now)
    {
        var age = now - observedAt;
        if (age < TimeSpan.Zero)
            return "刚刚";
        if (age < TimeSpan.FromMinutes(2))
            return "刚刚";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes} 分钟前";
        if (age < TimeSpan.FromHours(24))
            return $"{(int)age.TotalHours} 小时前";
        if (age < TimeSpan.FromDays(30))
            return $"{(int)age.TotalDays} 天前";
        if (age < TimeSpan.FromDays(365))
            return $"{(int)(age.TotalDays / 30)} 个月前";
        return $"{(int)(age.TotalDays / 365)} 年前";
    }

    private static bool TryResolveOffset(Regex pattern, string value, out int amount, out string evidence)
    {
        amount = 0;
        evidence = "";
        var match = pattern.Match(value);
        if (!match.Success)
            return false;
        if (!TryParseNumber(match.Groups["n"].Value, out amount) || amount is < 1 or > 365)
            return false;
        evidence = match.Value;
        return true;
    }

    private static bool TryParseNumber(string raw, out int value)
    {
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        // 支持「三天」「两周」这类中文数字写法。
        value = 0;
        foreach (var ch in raw)
        {
            var digit = ch switch
            {
                '一' => 1, '二' => 2, '两' => 2, '三' => 3, '四' => 4,
                '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9,
                '十' => 10, _ => -1,
            };
            if (digit < 0)
                return false;
            value = value == 0 ? digit : value + digit;
        }
        return value > 0;
    }

    private static DateTimeOffset? ResolveExplicitDate(DateTimeOffset now, int month, int day)
    {
        if (month is < 1 or > 12 || day is < 1 or > 31)
            return null;
        try
        {
            var candidate = new DateTimeOffset(now.Year, month, day, 23, 59, 59, now.Offset);
            // 已经过去超过半年的日期，按明年算。
            return candidate < now.AddDays(-180) ? candidate.AddYears(1) : candidate;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTimeOffset EndOfDay(DateTimeOffset value)
        => new(value.Year, value.Month, value.Day, 23, 59, 59, value.Offset);

    private static DateTimeOffset EndOfWeek(DateTimeOffset value)
    {
        // 以周日为一周结束。
        var offset = ((int)value.DayOfWeek + 7) % 7;
        var daysToSunday = (7 - offset) % 7;
        return EndOfDay(value.AddDays(daysToSunday));
    }

    private static DateTimeOffset EndOfMonth(DateTimeOffset value)
        => EndOfDay(new DateTimeOffset(value.Year, value.Month, 1, 0, 0, 0, value.Offset)
            .AddMonths(1).AddDays(-1));
}
