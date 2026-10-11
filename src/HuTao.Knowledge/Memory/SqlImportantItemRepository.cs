using System.Text.Json;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using Microsoft.Data.Sqlite;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Memory;

public sealed record ImportantItem(Guid Id,string Content,DateTimeOffset CreatedAt,DateTimeOffset? DueAt,DateTimeOffset? LastOfferedAt);

public sealed class SqlImportantItemRepository
{
    private readonly SqliteKnowledgeDatabase _database;
    private readonly string _space;
    private string? _expected;
    public SqlImportantItemRepository(SqliteKnowledgeDatabase database,KnowledgeAccessContext access)
    { _database=database;_space=access.MemoryNamespace;database.EnsureNamespace(_space); }

    public IReadOnlyList<ImportantItem> Load()
    {
        using var c=_database.Open();
        var items=Read(c,null);
        _expected=Signature(items);
        return items;
    }

    public void Save(IReadOnlyList<ImportantItem> items)
    {
        if(_expected is null)throw new InvalidOperationException("Load before save.");
        if(items.Select(i=>i.Id).Distinct().Count()!=items.Count)throw new InvalidDataException("Duplicate important item.");
        var next=_database.Write((c,t)=>{
            var previous=Read(c,t);
            if(Signature(previous)!=_expected)throw new InvalidOperationException("Stale important items writer.");
            foreach(var removed in previous.Where(p=>items.All(i=>i.Id!=p.Id)))
            {
                var id="important:"+removed.Id.ToString("N");
                KnowledgeDeletionService.ForgetInTransaction(c,t,_space,[id]);
            }
            foreach(var item in items)
            {
                var id=item.Id.ToString("N");
                var reference=SqlEvidenceRepository.Put(c,t,_space,new("important:"+id,item.Content,"user","important:"+id,"important",ObservedAt:item.CreatedAt),"memory");
                Execute(c,t,"""
                    INSERT INTO important_items VALUES(@n,@id,@e,@r,@due,@offered,'active')
                    ON CONFLICT(namespace_id,item_id) DO UPDATE SET evidence_revision=excluded.evidence_revision,due_at=excluded.due_at,last_offered_at=excluded.last_offered_at
                    """,("@n",_space),("@id",id),("@e",reference.Id),("@r",reference.Revision),("@due",item.DueAt?.ToUnixTimeMilliseconds()),("@offered",item.LastOfferedAt?.ToUnixTimeMilliseconds()));
            }
            Execute(c,t,"UPDATE namespace_state SET commit_seq=commit_seq+1 WHERE namespace_id=@n",("@n",_space));
            return Signature(Read(c,t));
        });
        _expected=next;
    }

    private List<ImportantItem> Read(SqliteConnection c,SqliteTransaction? t)
    {
        using var cmd=Command(c,t,"""
            SELECT i.item_id,e.text,e.observed_at,i.due_at,i.last_offered_at FROM important_items i
            JOIN evidence_units e ON e.namespace_id=i.namespace_id AND e.evidence_id=i.evidence_id AND e.revision=i.evidence_revision
            WHERE i.namespace_id=@n AND i.status='active' AND e.deleted_at IS NULL ORDER BY i.item_id
            """,("@n",_space));
        using var rows=cmd.ExecuteReader();var result=new List<ImportantItem>();
        while(rows.Read())result.Add(new(Guid.Parse(rows.GetString(0)),rows.GetString(1),DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(2)),
            rows.IsDBNull(3)?null:DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(3)),rows.IsDBNull(4)?null:DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(4))));
        return result;
    }
    private static string Signature(IEnumerable<ImportantItem> items)=>Hash(JsonSerializer.Serialize(items.OrderBy(i=>i.Id)));
}
