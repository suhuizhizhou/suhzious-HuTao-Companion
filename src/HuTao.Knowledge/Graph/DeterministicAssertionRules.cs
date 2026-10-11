using System.Text.RegularExpressions;
using HuTao.Knowledge.Retrieval;

namespace HuTao.Knowledge.Graph;

/// <summary>Narrow, full-clause rules. Unknown wording remains for reviewed extraction.</summary>
public static class DeterministicAssertionRules
{
    public const string Version="explicit-drinks-v3";
    public static bool IsExplicitCorrection(string text)=>text.StartsWith("改一下，我现在",StringComparison.Ordinal);
    // A free-form suffix also matches questions and counterfactuals (e.g. 喝茶吗/喝茶的话).
    // Only unambiguous complete beverage names qualify for unattended publication.
    private static readonly Regex Beverage=new("^(?:我)?(?<time>早上|上午|晚上|夜里)(?<restriction>只|不)?喝(?<value>水|温水|热水|凉水|白开水|矿泉水|茶|热茶|绿茶|红茶|乌龙茶|花茶|咖啡|黑咖啡|牛奶|热牛奶|豆浆|果汁|奶茶|可乐|汽水|啤酒|红酒)$",RegexOptions.CultureInvariant);
    public static IReadOnlyList<ExtractedAssertion> Extract(KnowledgeEvidence evidence,string? speakerEntityId)
    {
        if(evidence.Speaker!="user" || speakerEntityId is null || evidence.Text.Length>200)return [];
        var text=IsExplicitCorrection(evidence.Text)?"我"+evidence.Text["改一下，我现在".Length..]:evidence.Text;
        var clauses=text.TrimEnd('。').Split(['，','；'],StringSplitOptions.None);
        if(clauses.Length is <1 or >4)return [];
        var result=new List<ExtractedAssertion>();
        foreach(var clause in clauses)
        {
            var match=Beverage.Match(clause);
            if(!match.Success)return [];
            var qualifiers=new Dictionary<string,string>{{"time_of_day",match.Groups["time"].Value is "早上" or "上午"?"morning":"evening"}};
            if(match.Groups["restriction"].Value=="只")qualifiers["exclusive"]="true";
            result.Add(new(speakerEntityId,"drinks",null,match.Groups["value"].Value,match.Groups["restriction"].Value=="不"?-1:1,
                qualifiers,0,evidence.Text.Length,evidence.Text));
        }
        return result;
    }
    public static bool Matches(ExtractedAssertion left,ExtractedAssertion right)=>left.SubjectId==right.SubjectId && left.PredicateId==right.PredicateId &&
        left.ObjectId==right.ObjectId && left.Literal==right.Literal && left.Polarity==right.Polarity && left.StartUtf16==right.StartUtf16 && left.EndUtf16==right.EndUtf16 &&
        left.Quote==right.Quote && left.Qualifiers.Count==right.Qualifiers.Count && left.Qualifiers.All(p=>right.Qualifiers.TryGetValue(p.Key,out var value) && value==p.Value);
}
