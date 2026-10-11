using Microsoft.Data.Sqlite;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Graph;

internal static class GraphSnapshotReader
{
    internal static IEnumerable<GraphAssertionSnapshot> Read(SqliteConnection c, SqliteTransaction t, string space, long? changesAfter = null)
    {
        using var command = Command(c, t, """
            SELECT a.assertion_id,a.revision,a.subject_id,s.type,a.predicate_id,a.object_id,o.type,
                a.polarity,CASE WHEN a.validation_method='forgotten' THEN 'forgotten' ELSE a.status END,a.valid_from,a.valid_to,a.recorded_at
            FROM assertions a JOIN entities s ON s.namespace_id=a.namespace_id AND s.entity_id=a.subject_id
            LEFT JOIN entities o ON o.namespace_id=a.namespace_id AND o.entity_id=a.object_id
            WHERE a.namespace_id=@n AND NOT EXISTS
                (SELECT 1 FROM assertions newer WHERE newer.namespace_id=a.namespace_id
                 AND newer.assertion_id=a.assertion_id AND newer.revision>a.revision)
            AND (@after IS NULL OR a.assertion_id IN
                (SELECT aggregate_id FROM graph_outbox WHERE namespace_id=@n AND commit_seq>@after))
            ORDER BY a.assertion_id
            """, ("@n",space), ("@after",changesAfter));
        using var rows=command.ExecuteReader();
        while(rows.Read())
            yield return new(space,rows.GetString(0),rows.GetInt64(1),rows.GetString(2),rows.GetString(3),
                rows.GetString(4),rows.IsDBNull(5)?null:rows.GetString(5),rows.IsDBNull(6)?null:rows.GetString(6),
                rows.GetInt32(7),rows.GetString(8),rows.IsDBNull(9)?null:rows.GetInt64(9),
                rows.IsDBNull(10)?null:rows.GetInt64(10),rows.GetInt64(11));
    }
}
