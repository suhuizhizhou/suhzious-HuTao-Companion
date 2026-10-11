using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Graph;

public sealed record ResolvedEntity(string Namespace,string Id,string Type,string Name);
public sealed record EntityResolution(string Status,IReadOnlyList<ResolvedEntity> Candidates);
public sealed record ConversationParticipants(string SpeakerEntityId,string AddresseeEntityId);

public sealed class EntityResolver(SqliteKnowledgeDatabase database)
{
    public void AddAlias(KnowledgeAccessContext access,string space,string entityId,string alias,AssertionSource source)
    {
        access.Demand(space);
        if(source.Reference.Namespace!=space)throw new UnauthorizedAccessException("Alias source scope mismatch.");
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        var text=new SqlEvidenceRepository(database).Read(access,source.Reference)?.Text;
        if(text is null || source.StartUtf16<0 || source.EndUtf16>text.Length || source.StartUtf16>=source.EndUtf16 ||
           text[source.StartUtf16..source.EndUtf16]!=source.Quote || !source.Quote.Contains(alias,StringComparison.Ordinal))
            throw new InvalidDataException("Alias requires an exact attributable mention.");
        database.Write((c,t)=>Execute(c,t,"INSERT OR IGNORE INTO entity_aliases VALUES(@n,@normalized,@alias,@id,@e,@r)",
            ("@n",space),("@normalized",Normalize(alias)),("@alias",alias),("@id",entityId),("@e",source.Reference.Id),("@r",source.Reference.Revision)));
    }

    public EntityResolution Resolve(KnowledgeAccessContext access,string space,string mention,string? expectedType=null,ConversationParticipants? participants=null,CancellationToken ct=default,TimeSpan? duration=null)
    {
        access.Demand(space);
        var normalized=Normalize(mention);
        var pronoun=normalized is "我" or "你";
        if(pronoun && participants is null)return new("unknown",[]);
        var bound=normalized=="我"?participants?.SpeakerEntityId:normalized=="你"?participants?.AddresseeEntityId:null;
        ct.ThrowIfCancellationRequested();
        try
        {
        return database.Read(c=>
        {
        using var cmd=Command(c,null,"""
            SELECT DISTINCT e.entity_id,e.type,e.canonical_name FROM entities e
            WHERE e.namespace_id=@n AND e.status='active' AND (@type IS NULL OR e.type=@type)
            AND ((@pronoun=1 AND e.entity_id=@bound) OR (@pronoun=0 AND (e.canonical_name=@mention OR EXISTS(
                SELECT 1 FROM entity_aliases a JOIN evidence_units u ON u.namespace_id=a.namespace_id AND u.evidence_id=a.source_evidence_id AND u.revision=a.source_revision
                JOIN namespaces n ON n.namespace_id=u.namespace_id
                WHERE a.namespace_id=e.namespace_id AND a.entity_id=e.entity_id AND a.normalized_alias=@normalized AND u.deleted_at IS NULL
                AND (n.domain='memory' OR u.corpus_version=n.active_corpus_version)
                AND NOT EXISTS(SELECT 1 FROM evidence_units newer WHERE newer.namespace_id=u.namespace_id AND newer.evidence_id=u.evidence_id AND newer.revision>u.revision
                    AND (n.domain='memory' OR newer.corpus_version=n.active_corpus_version))))))
            ORDER BY e.entity_id LIMIT 20
            """,("@n",space),("@type",expectedType),("@pronoun",pronoun?1:0),("@bound",bound),("@mention",mention.Trim()),("@normalized",normalized));
        using var rows=cmd.ExecuteReader();var matches=new List<ResolvedEntity>();
        while(rows.Read())matches.Add(new(space,rows.GetString(0),rows.GetString(1),rows.GetString(2)));
        return new EntityResolution(matches.Count==0?"unknown":matches.Count==1?"resolved":"ambiguous",matches);
        },ct,duration);
        }
        catch(TimeoutException){return new("deadline",[]);}
    }
    private static string Normalize(string value)=>value.Trim().Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
}
