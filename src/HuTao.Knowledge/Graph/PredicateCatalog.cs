using System.Collections.Frozen;

namespace HuTao.Knowledge.Graph;

public sealed record PredicateDefinition(string Id,IReadOnlySet<string> SubjectTypes,IReadOnlySet<string> ObjectTypes,
    bool Functional=false,bool Symmetric=false,bool AllowsNegation=true);

public static class PredicateCatalog
{
    public const string Version="predicates-v3";
    private static IReadOnlySet<string> Types(params string[] types)=>types.ToFrozenSet(StringComparer.Ordinal);
    public static readonly IReadOnlyDictionary<string,PredicateDefinition> Entries = new[]
    {
        new PredicateDefinition("prefers",Types("person","character"),Types("object","literal")),
        new PredicateDefinition("drinks",Types("person","character"),Types("object","literal")),
        new PredicateDefinition("reacts_to",Types("person","character"),Types("object","event","literal")),
        new PredicateDefinition("promised",Types("person","character"),Types("event","literal")),
        new PredicateDefinition("member_of",Types("person","character","organization"),Types("organization")),
        new PredicateDefinition("made",Types("person","character","organization"),Types("object")),
        new PredicateDefinition("has_name",Types("person","character","organization","object","animal","location"),Types("literal")),
        new PredicateDefinition("cares_for",Types("person","character","organization"),Types("person","character","object","animal")),
        new PredicateDefinition("treats_as",Types("person","character"),Types("object","animal")),
        new PredicateDefinition("has_part",Types("object","location"),Types("object")),
        new PredicateDefinition("located_at",Types("person","character","organization","object","animal","event"),Types("location")),
        new PredicateDefinition("knows",Types("person","character"),Types("person","character"),Symmetric:true),
        new PredicateDefinition("causes",Types("event"),Types("event"))
    }.ToFrozenDictionary(p=>p.Id,StringComparer.Ordinal);

    public static PredicateDefinition Demand(string id,string subjectType,string objectType,int polarity)
    {
        if(!Entries.TryGetValue(id,out var predicate) || !predicate.SubjectTypes.Contains(subjectType) ||
           !predicate.ObjectTypes.Contains(objectType) || polarity is not (-1 or 1) || (polarity==-1 && !predicate.AllowsNegation))
            throw new InvalidDataException("Predicate or argument types are not in the host catalog.");
        return predicate;
    }
}
