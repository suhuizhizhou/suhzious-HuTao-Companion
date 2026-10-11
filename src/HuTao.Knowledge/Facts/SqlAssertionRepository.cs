using System.Text.Json;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using Microsoft.Data.Sqlite;
using HuTao.Knowledge.Graph;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Facts;

/// <summary>Trusted ingestion and parameterized fact reads. No model-supplied SQL or namespace grants.</summary>
public sealed class SqlAssertionRepository(SqliteKnowledgeDatabase database)
{
    public void RegisterEntity(KnowledgeAccessContext access,string space,string id,string type,string name)
    {
        access.Demand(space);ArgumentException.ThrowIfNullOrWhiteSpace(id);ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if(type is not ("person" or "character" or "organization" or "object" or "event" or "animal" or "location"))throw new ArgumentException("Unknown entity type.");
        database.EnsureNamespace(space);
        database.Write((c,t)=>{
            Execute(c,t,"INSERT OR IGNORE INTO entities(namespace_id,entity_id,type,canonical_name) VALUES(@n,@id,@type,@name)",("@n",space),("@id",id),("@type",type),("@name",name));
            using var check=Command(c,t,"SELECT type,canonical_name FROM entities WHERE namespace_id=@n AND entity_id=@id",("@n",space),("@id",id));
            using var row=check.ExecuteReader();row.Read();
            if(row.GetString(0)!=type || row.GetString(1)!=name)throw new InvalidDataException("Entity identity collision.");
            return 0;
        });
    }

    public long Put(KnowledgeAccessContext access,string space,AssertionWrite fact,DateTimeOffset recordedAt,string? supersedes=null)
        => database.Write((c,t)=>PutInTransaction(c,t,access,space,fact,recordedAt,supersedes));

    internal long PutInTransaction(SqliteConnection c,SqliteTransaction t,KnowledgeAccessContext access,string space,AssertionWrite fact,DateTimeOffset recordedAt,string? supersedes=null)
    {
        access.Demand(space);
        if(fact.Polarity is not (-1 or 1) || (fact.ObjectId is null)==(fact.Literal is null) || fact.Sources.Count==0 ||
           fact.Status is not ("accepted" or "candidate" or "disputed") || fact.ValidFrom>=fact.ValidTo)
            throw new InvalidDataException("Invalid assertion.");
        if(!PredicateCatalog.Entries.ContainsKey(fact.PredicateId))
            throw new InvalidDataException("Unknown predicate.");
        ArgumentException.ThrowIfNullOrWhiteSpace(fact.Id);
        var qualifiers=Qualifiers(fact.Qualifiers);
        var at=recordedAt.ToUnixTimeMilliseconds();
            string TypeOf(string id)
            {
                using var cmd=Command(c,t,"SELECT type FROM entities WHERE namespace_id=@n AND entity_id=@id AND status='active'",("@n",space),("@id",id));
                return cmd.ExecuteScalar() as string??throw new InvalidDataException("Missing entity in assertion namespace.");
            }
            var predicate=PredicateCatalog.Demand(fact.PredicateId,TypeOf(fact.SubjectId),fact.ObjectId is null?"literal":TypeOf(fact.ObjectId),fact.Polarity);
            Execute(c,t,"INSERT INTO predicates VALUES(@id,@subjects,@objects,@symmetric,@functional,@negative,NULL) ON CONFLICT(predicate_id) DO UPDATE SET subject_types=excluded.subject_types,object_types=excluded.object_types,symmetric=excluded.symmetric,functional=excluded.functional,allows_negation=excluded.allows_negation",("@id",predicate.Id),
                ("@subjects",string.Join(',',predicate.SubjectTypes.Order(StringComparer.Ordinal))),("@objects",string.Join(',',predicate.ObjectTypes.Order(StringComparer.Ordinal))),
                ("@symmetric",predicate.Symmetric?1:0),("@functional",predicate.Functional?1:0),("@negative",predicate.AllowsNegation?1:0));
            foreach(var source in fact.Sources)
            {
                if(source.Reference.Namespace!=space)throw new UnauthorizedAccessException("Cross-namespace assertion source.");
                if(!SourceValid(c,t,source,at))throw new InvalidDataException("Invalid or retired assertion evidence span.");
            }
            using(var exists=Command(c,t,"SELECT 1 FROM assertions WHERE namespace_id=@n AND assertion_id=@id",("@n",space),("@id",fact.Id)))
                if(exists.ExecuteScalar() is not null)throw new InvalidDataException("Assertion ID already exists; revisions require an explicit state transition.");
            if(supersedes is not null)
            {
                using var previous=Command(c,t,"""
                    SELECT revision,subject_id,predicate_id,qualifiers_json,recorded_at FROM assertions
                    WHERE namespace_id=@n AND assertion_id=@id AND retired_at IS NULL AND status IN ('accepted','disputed')
                    """,("@n",space),("@id",supersedes));
                long previousRevision;
                using(var row=previous.ExecuteReader())
                {
                    if(!row.Read() || row.GetString(1)!=fact.SubjectId || row.GetString(2)!=fact.PredicateId || Dimension(row.GetString(3))!=Dimension(qualifiers) || row.GetInt64(4)>=at)
                        throw new InvalidDataException("Correction must match the same fact dimension and advance knowledge time.");
                    previousRevision=row.GetInt64(0);
                }
                Execute(c,t,"UPDATE assertions SET retired_at=@at WHERE namespace_id=@n AND assertion_id=@id AND revision=@r",("@at",at),("@n",space),("@id",supersedes),("@r",previousRevision));
                Execute(c,t,"""
                    INSERT INTO assertions SELECT namespace_id,assertion_id,revision+1,subject_id,predicate_id,object_id,typed_literal,polarity,qualifiers_json,
                    valid_from,@until,time_basis,'superseded',extraction_confidence,validation_method,@new,@at,NULL
                    FROM assertions WHERE namespace_id=@n AND assertion_id=@id AND revision=@r
                    """,("@until",fact.ValidFrom?.ToUnixTimeMilliseconds()??at),("@new",fact.Id),("@at",at),("@n",space),("@id",supersedes),("@r",previousRevision));
                Execute(c,t,"""
                    INSERT INTO assertion_evidence SELECT namespace_id,assertion_id,assertion_revision+1,evidence_id,evidence_revision,start_utf16,end_utf16,support_kind
                    FROM assertion_evidence WHERE namespace_id=@n AND assertion_id=@id AND assertion_revision=@r
                    """,("@n",space),("@id",supersedes),("@r",previousRevision));
                GraphOutboxRepository.Enqueue(c,t,space,supersedes,previousRevision+1);
            }
            var status=fact.Status;
            if(supersedes is null && status=="accepted")
            {
                var conflicts=new List<(string Id,long Revision)>();
                using(var cmd=Command(c,t,"""
                    SELECT assertion_id,revision FROM assertions WHERE namespace_id=@n AND subject_id=@s AND predicate_id=@p
                    AND json_remove(qualifiers_json,'$.exclusive')=@dimension AND retired_at IS NULL AND status IN ('accepted','disputed')
                    AND (valid_to IS NULL OR @from IS NULL OR valid_to>@from) AND (@to IS NULL OR valid_from IS NULL OR valid_from<@to)
                    AND (((@exclusive=1 OR json_extract(qualifiers_json,'$.exclusive')='true') AND (object_id IS NOT @o OR typed_literal IS NOT @literal OR polarity<>@polarity))
                        OR (object_id IS @o AND typed_literal IS @literal AND polarity<>@polarity))
                    """,("@n",space),("@s",fact.SubjectId),("@p",fact.PredicateId),("@dimension",Dimension(qualifiers)),("@o",fact.ObjectId),("@literal",fact.Literal),("@polarity",fact.Polarity),
                    ("@from",fact.ValidFrom?.ToUnixTimeMilliseconds()),("@to",fact.ValidTo?.ToUnixTimeMilliseconds()),
                    ("@exclusive",predicate.Functional || (fact.Qualifiers.TryGetValue("exclusive",out var exclusive) && exclusive=="true")?1:0)))
                using(var rows=cmd.ExecuteReader())while(rows.Read())conflicts.Add((rows.GetString(0),rows.GetInt64(1)));
                foreach(var conflict in conflicts)
                {
                    status="disputed";
                    Execute(c,t,"UPDATE assertions SET retired_at=@at WHERE namespace_id=@n AND assertion_id=@id AND revision=@r",("@at",at),("@n",space),("@id",conflict.Id),("@r",conflict.Revision));
                    Execute(c,t,"""
                        INSERT INTO assertions SELECT namespace_id,assertion_id,revision+1,subject_id,predicate_id,object_id,typed_literal,polarity,qualifiers_json,
                        valid_from,valid_to,time_basis,'disputed',extraction_confidence,validation_method,superseded_by,@at,NULL
                        FROM assertions WHERE namespace_id=@n AND assertion_id=@id AND revision=@r
                        """,("@at",at),("@n",space),("@id",conflict.Id),("@r",conflict.Revision));
                    Execute(c,t,"""
                        INSERT INTO assertion_evidence SELECT namespace_id,assertion_id,assertion_revision+1,evidence_id,evidence_revision,start_utf16,end_utf16,support_kind
                        FROM assertion_evidence WHERE namespace_id=@n AND assertion_id=@id AND assertion_revision=@r
                        """,("@n",space),("@id",conflict.Id),("@r",conflict.Revision));
                    GraphOutboxRepository.Enqueue(c,t,space,conflict.Id,conflict.Revision+1);
                }
            }
            Execute(c,t,"""
                INSERT INTO assertions(namespace_id,assertion_id,revision,subject_id,predicate_id,object_id,typed_literal,polarity,qualifiers_json,
                    valid_from,valid_to,time_basis,status,extraction_confidence,validation_method,recorded_at)
                VALUES(@n,@id,1,@s,@p,@o,@literal,@polarity,@q,@from,@to,@basis,@status,1,@method,@at)
                """,("@n",space),("@id",fact.Id),("@s",fact.SubjectId),("@p",fact.PredicateId),("@o",fact.ObjectId),("@literal",fact.Literal),
                ("@polarity",fact.Polarity),("@q",qualifiers),("@from",fact.ValidFrom?.ToUnixTimeMilliseconds()),("@to",fact.ValidTo?.ToUnixTimeMilliseconds()),
                ("@basis",fact.ValidFrom is null?"unknown":"explicit"),("@status",status),("@method",fact.ValidationMethod),("@at",at));
            foreach(var source in fact.Sources)
                Execute(c,t,"INSERT INTO assertion_evidence VALUES(@n,@id,1,@e,@r,@start,@end,'supports')",("@n",space),("@id",fact.Id),("@e",source.Reference.Id),("@r",source.Reference.Revision),("@start",source.StartUtf16),("@end",source.EndUtf16));
            GraphOutboxRepository.Enqueue(c,t,space,fact.Id,1);
            return 1L;
    }

    public IReadOnlyList<AssertionFact> Query(KnowledgeAccessContext access,string space,FactQuery query,CancellationToken ct=default,TimeSpan? duration=null)
    {
        access.Demand(space);
        if(!Enum.IsDefined(query.TimeMode))throw new ArgumentException("Unknown time mode.");
        if(query.AssertionIds is { Count: > 100 } || string.IsNullOrWhiteSpace(query.SubjectId) && query.AssertionIds is not { Count: > 0 })
            throw new ArgumentException("Fact query requires a subject or a bounded assertion selection.");
        var now=DateTimeOffset.UtcNow;
        var asOf=(query.AsOf??now).ToUnixTimeMilliseconds();
        var known=(query.KnownAt??now).ToUnixTimeMilliseconds();
        var history=query.TimeMode==KnowledgeTimeMode.Historical;
        if(!history && (query.AsOf is not null || query.KnownAt is not null))throw new ArgumentException("Explicit times require historical mode.");
        return database.Read(c=>
        {
        using var t=c.BeginTransaction(deferred:true);
        using var cmd=Command(c,t,"""
            SELECT assertion_id,revision,subject_id,predicate_id,object_id,typed_literal,polarity,qualifiers_json,status,valid_from,valid_to
            FROM assertions WHERE namespace_id=@n AND (@s='' OR subject_id=@s) AND (@p IS NULL OR predicate_id=@p)
            AND (@ids IS NULL OR assertion_id IN (SELECT value FROM json_each(@ids)))
            AND (@q IS NULL OR NOT EXISTS(SELECT 1 FROM json_each(@q) wanted WHERE NOT EXISTS(
                SELECT 1 FROM json_each(assertions.qualifiers_json) actual WHERE actual.key=wanted.key AND actual.value=wanted.value)))
            AND recorded_at<=@known AND (retired_at IS NULL OR retired_at>@known)
            AND (valid_from IS NULL OR valid_from<=@asof) AND (valid_to IS NULL OR valid_to>@asof)
            AND (status IN ('accepted','disputed') OR (@history=1 AND status='superseded'))
            ORDER BY recorded_at DESC,assertion_id LIMIT @limit
            """,("@n",space),("@s",query.SubjectId),("@p",query.PredicateId),("@q",query.Qualifiers is null?null:Qualifiers(query.Qualifiers)),
            ("@known",known),("@asof",asOf),("@history",history?1:0),("@limit",Math.Clamp(query.Limit,1,100)),
            ("@ids",query.AssertionIds is null?null:JsonSerializer.Serialize(query.AssertionIds)));
        using var rows=cmd.ExecuteReader();var result=new List<AssertionFact>();
        while(rows.Read())
        {
            var id=rows.GetString(0);var revision=rows.GetInt64(1);var sources=new List<AssertionSource>();var invalid=false;
            using var supports=Command(c,t,"""
                SELECT s.evidence_id,s.evidence_revision,s.start_utf16,s.end_utf16,e.text FROM assertion_evidence s JOIN evidence_units e
                ON e.namespace_id=s.namespace_id AND e.evidence_id=s.evidence_id AND e.revision=s.evidence_revision
                WHERE s.namespace_id=@n AND s.assertion_id=@id AND s.assertion_revision=@r AND s.support_kind='supports'
                """,("@n",space),("@id",id),("@r",revision));
            using(var sourceRows=supports.ExecuteReader())
                while(sourceRows.Read())
                {
                    var start=sourceRows.GetInt32(2);var end=sourceRows.GetInt32(3);var text=sourceRows.GetString(4);
                    if(start<0 || end<=start || end>text.Length){invalid=true;break;}
                    var source=new AssertionSource(new(space,sourceRows.GetString(0),sourceRows.GetInt64(1)),start,end,text[start..end]);
                    if(!SourceValid(c,t,source,known)){invalid=true;break;}
                    sources.Add(source);
                }
            if(invalid || sources.Count==0)continue;
            result.Add(new(id,revision,rows.GetString(2),rows.GetString(3),rows.IsDBNull(4)?null:rows.GetString(4),rows.IsDBNull(5)?null:rows.GetString(5),rows.GetInt32(6),rows.GetString(7),rows.GetString(8),Time(rows,9),Time(rows,10),sources));
        }
        t.Commit();return (IReadOnlyList<AssertionFact>)result;
        },ct,duration);
    }

    internal static bool SourceValid(SqliteConnection c,SqliteTransaction t,AssertionSource source,long known)
    {
        using var cmd=Command(c,t,$"""
            SELECT e.text FROM evidence_units e JOIN namespaces n ON n.namespace_id=e.namespace_id
            WHERE e.namespace_id=@n AND e.evidence_id=@id AND e.revision=@r AND e.deleted_at IS NULL AND e.recorded_at<=@known
            AND (n.domain='memory' OR e.corpus_version=n.active_corpus_version)
            AND NOT EXISTS(SELECT 1 FROM evidence_units newer WHERE newer.namespace_id=e.namespace_id AND newer.evidence_id=e.evidence_id
                AND newer.revision>e.revision AND newer.recorded_at<=@known AND (n.domain='memory' OR newer.corpus_version=n.active_corpus_version))
            AND {SqlEvidenceRepository.ProvenancePredicate}
            """,("@n",source.Reference.Namespace),("@id",source.Reference.Id),("@r",source.Reference.Revision),("@known",known));
        var text=cmd.ExecuteScalar() as string;
        return text is not null && source.StartUtf16>=0 && source.EndUtf16>source.StartUtf16 && source.EndUtf16<=text.Length && text[source.StartUtf16..source.EndUtf16]==source.Quote;
    }
    private static string Qualifiers(IReadOnlyDictionary<string,string> values)=>JsonSerializer.Serialize(new SortedDictionary<string,string>(values.ToDictionary(p=>p.Key,p=>p.Value),StringComparer.Ordinal));
    private static string Dimension(string json)=>Qualifiers(JsonSerializer.Deserialize<Dictionary<string,string>>(json)!.Where(p=>p.Key!="exclusive").ToDictionary(p=>p.Key,p=>p.Value));
    private static DateTimeOffset? Time(SqliteDataReader row,int index)=>row.IsDBNull(index)?null:DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(index));
}
