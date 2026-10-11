using System.Text.Json;
using System.Text.RegularExpressions;
using HuTao.Knowledge.Retrieval;

namespace HuTao.Knowledge.Rag;

/// <summary>Rejects explicit contradictions. These checks do not claim general semantic entailment.</summary>
public static class EvidenceAnswerValidator
{
    public static string Citation(EvidenceRef reference)=>reference.Namespace.StartsWith("memory:",StringComparison.Ordinal)?"memory:"+reference.Id:reference.Id;
    public static IReadOnlyList<string> Check(IReadOnlyList<StoryAnswerSegment> segments,EvidenceBundle bundle)
    {
        var issues=new List<string>();var available=bundle.Evidence.ToDictionary(e=>Citation(e.Reference));
        foreach(var segment in segments.Where(s=>s.Kind is "thought" or "action"))
            if(Regex.IsMatch(segment.Text,"(?:我|你|用户|本堂主)(?:晚上|早上|现在|以前|一直|也|只|不|最|很|曾经|当时|亲手)*(?:喝|喜欢|讨厌|织|做过|去过|答应过)") ||
                Regex.IsMatch(segment.Text,"导致|原因是"))issues.Add("fact_in_unattributed_segment");
        foreach(var segment in segments.Where(s=>s.Kind=="fact"))
        {
            var cited=segment.EvidenceIds.Where(available.ContainsKey).Select(id=>available[id]).ToArray();
            if(cited.Length==0)continue;
            var text=segment.Text;
            var personal=cited.All(e=>e.Reference.Namespace.StartsWith("memory:",StringComparison.Ordinal));
            if(personal && cited.All(e=>e.Speaker=="user") && Regex.IsMatch(text,"(?:我|本堂主)(?:晚上|早上|现在|以前|一直|也|只|不|最|很|曾经|当时|亲手)*(?:喝|喜欢|讨厌|织|做|去|答应)"))
                issues.Add("user_experience_as_character");
            if(personal && cited.All(e=>e.Speaker=="assistant") && Regex.IsMatch(text,"(?:你|用户)(?:晚上|早上|现在|以前|一直|也|只|不|最|很)*(?:喝|喜欢|讨厌|织|做|去)"))
                issues.Add("assistant_statement_as_user_fact");
            if(!personal && cited.All(e=>!e.Reference.Namespace.StartsWith("memory:",StringComparison.Ordinal)) &&
                Regex.IsMatch(text,"(?:你|用户)(?:当时|亲手|小时候|曾经|以前)(?:做|织|去|见|喝|喜欢)"))issues.Add("character_experience_as_user");
            var facts=(bundle.Tasks??[]).SelectMany(t=>t.Paths).SelectMany(p=>p.Facts).DistinctBy(f=>(f.Id,f.Revision))
                .Where(f=>f.Sources.Any(s=>segment.EvidenceIds.Contains(Citation(s.Reference)))).ToArray();
            if(Regex.IsMatch(text,"因为|导致|原因是|因此.{0,8}(发生|离开|死亡|决定)|所以.{0,8}(才|发生|离开)") && !Regex.IsMatch(text,"不能|不确定|未必|不知道|无法确定") &&
                !cited.Any(e=>Regex.IsMatch(e.Text,"因为|导致|所以|因此|由于|为了")) && !facts.Any(f=>f.PredicateId=="causes"))
                issues.Add("causal_claim_without_source");
            foreach(var fact in facts)
            {
                if(fact.Status=="disputed" && !Regex.IsMatch(text,"矛盾|冲突|不确定|之前|曾经|说过|确认|到底|现在呢"))issues.Add("disputed_fact_as_certain");
                if(fact.Literal is not {Length:>0} literal || !text.Contains(literal,StringComparison.Ordinal))continue;
                var clauses=Regex.Split(text,"[，。！？；]").Where(c=>c.Contains(literal,StringComparison.Ordinal)).ToArray();
                foreach(var clause in clauses)
                {
                    var verb=fact.PredicateId switch {"drinks"=>"喝","prefers"=>"喜欢|爱",_=>null};
                    if(verb is not null)
                    {
                        var escaped=Regex.Escape(literal);
                        var negative=Regex.IsMatch(clause,$"(?:不|没|不能|不会|不再)(?:再|会|能)?(?:{verb})[^，。！？；]{{0,5}}{escaped}");
                        var positive=Regex.IsMatch(clause,$"(?:{verb})[^，。！？；]{{0,5}}{escaped}");
                        if(positive && (fact.Polarity==1 && negative || fact.Polarity==-1 && !negative))issues.Add("assertion_polarity_reversed");
                    }
                    using var qualifiers=JsonDocument.Parse(fact.QualifiersJson);
                    if(qualifiers.RootElement.TryGetProperty("time_of_day",out var time))
                    {
                        if(time.GetString()=="evening" && Regex.IsMatch(clause,"早上|上午|早晨") && !Regex.IsMatch(clause,"晚上|夜里"))issues.Add("assertion_time_condition_changed");
                        if(time.GetString()=="morning" && Regex.IsMatch(clause,"晚上|夜里") && !Regex.IsMatch(clause,"早上|上午|早晨"))issues.Add("assertion_time_condition_changed");
                    }
                }
            }
        }
        return issues.Distinct().ToArray();
    }
}
