using HuTao.Knowledge.Retrieval;

namespace HuTao.Knowledge.Facts;

public sealed record AssertionSource(EvidenceRef Reference,int StartUtf16,int EndUtf16,string Quote);
public sealed record AssertionWrite(string Id,string SubjectId,string PredicateId,string? ObjectId,string? Literal,
    int Polarity,IReadOnlyDictionary<string,string> Qualifiers,IReadOnlyList<AssertionSource> Sources,
    DateTimeOffset? ValidFrom=null,DateTimeOffset? ValidTo=null,string Status="accepted",string ValidationMethod="manual");
public sealed record AssertionFact(string Id,long Revision,string SubjectId,string PredicateId,string? ObjectId,string? Literal,
    int Polarity,string QualifiersJson,string Status,DateTimeOffset? ValidFrom,DateTimeOffset? ValidTo,
    IReadOnlyList<AssertionSource> Sources);
public sealed record FactQuery(string SubjectId,string? PredicateId=null,IReadOnlyDictionary<string,string>? Qualifiers=null,
    KnowledgeTimeMode TimeMode=KnowledgeTimeMode.Current,DateTimeOffset? AsOf=null,DateTimeOffset? KnownAt=null,int Limit=40,
    IReadOnlyList<string>? AssertionIds=null);
