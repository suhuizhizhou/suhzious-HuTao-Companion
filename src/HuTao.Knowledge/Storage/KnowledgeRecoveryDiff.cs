using Microsoft.Data.Sqlite;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Storage;

public sealed record RecoveryTableDiff(string Table,long BaselineOnly,long CurrentOnly,long ChangedSameKey);
public sealed record KnowledgeRecoveryDiffReport(string LineageId,bool Identical,bool ActivationAuthorized,IReadOnlyList<RecoveryTableDiff> Tables)
{
    public string ComparisonScope=>"ordinary-table-rows; excludes FTS virtual/shadow tables, DDL, physical order and external files";
}

/// <summary>Read-only comparison of two pinned SQLite snapshots. Counts only; no private row contents leave the database.</summary>
public static class KnowledgeRecoveryDiff
{
    public static KnowledgeRecoveryDiffReport Compare(string baselinePath,string currentPath,CancellationToken ct=default)
    {
        if(!File.Exists(baselinePath) || !File.Exists(currentPath))throw new FileNotFoundException("Both recovery comparison databases must exist.");
        using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.GetFullPath(baselinePath),Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=5}.ToString());
        c.Open();
        // ATTACH inherits this connection's READONLY open flags; query_only also forbids SQL writes.
        using(var queryOnly=Command(c,null,"PRAGMA query_only=ON"))queryOnly.ExecuteNonQuery();
        using(var attach=Command(c,null,"ATTACH DATABASE @path AS current_db",("@path",Path.GetFullPath(currentPath))))attach.ExecuteNonQuery();
        using var transaction=c.BeginTransaction(deferred:true);
        string Identity(string schema)
        {
            using var q=Command(c,transaction,$"SELECT lineage_id FROM {schema}.database_identity WHERE singleton=1");
            return q.ExecuteScalar() as string??throw new InvalidDataException("Missing database identity.");
        }
        var lineage=Identity("main");
        if(Identity("current_db")!=lineage)throw new InvalidDataException("Different database lineages.");
        List<string> Tables(string schema)
        {
            using var q=Command(c,transaction,$"PRAGMA {schema}.table_list");using var rows=q.ExecuteReader();var names=new List<string>();
            while(rows.Read())if(rows.GetString(2)=="table" && !rows.GetString(1).StartsWith("sqlite_",StringComparison.Ordinal))names.Add(rows.GetString(1));
            names.Sort(StringComparer.Ordinal);return names;
        }
        var tables=Tables("main");
        if(!tables.SequenceEqual(Tables("current_db")))throw new InvalidDataException("Schema table sets differ; compare matching schema versions.");
        List<(string Name,string Type,long NotNull,long Key)> Columns(string schema,string table)
        {
            using var q=Command(c,transaction,$"PRAGMA {schema}.table_info({Quote(table)})");using var rows=q.ExecuteReader();
            var columns=new List<(string,string,long,long)>();while(rows.Read())columns.Add((rows.GetString(1),rows.GetString(2),rows.GetInt64(3),rows.GetInt64(5)));return columns;
        }
        var differences=new List<RecoveryTableDiff>();
        foreach(var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            var columns=Columns("main",table);
            if(!columns.SequenceEqual(Columns("current_db",table)))throw new InvalidDataException("Schema columns differ.");
            var keys=columns.Where(x=>x.Key>0).Select(x=>x.Name).ToArray();
            if(keys.Length==0)throw new InvalidDataException("Recovery comparison requires explicit table keys.");
            var match=string.Join(" AND ",keys.Select(k=>$"b.{Quote(k)} IS a.{Quote(k)}"));
            var equal=string.Join(" AND ",columns.Select(k=>$"b.{Quote(k.Name)} IS a.{Quote(k.Name)}"));
            long Count(string sql)
            {
                using var command=Command(c,transaction,sql);
                using var registration=ct.Register(command.Cancel);
                return Convert.ToInt64(command.ExecuteScalar());
            }
            var name=Quote(table);
            var baselineOnly=Count($"SELECT COUNT(*) FROM main.{name} a WHERE NOT EXISTS(SELECT 1 FROM current_db.{name} b WHERE {match})");
            var currentOnly=Count($"SELECT COUNT(*) FROM current_db.{name} b WHERE NOT EXISTS(SELECT 1 FROM main.{name} a WHERE {match})");
            var changed=Count($"SELECT COUNT(*) FROM main.{name} a JOIN current_db.{name} b ON {match} WHERE NOT ({equal})");
            differences.Add(new(table,baselineOnly,currentOnly,changed));
        }
        // Even identical snapshots do not authorize a later activation: either source can change after this read.
        return new(lineage,differences.All(d=>d.BaselineOnly==0 && d.CurrentOnly==0 && d.ChangedSameKey==0),false,differences);
    }
    private static string Quote(string identifier)=>"\""+identifier.Replace("\"","\"\"")+"\"";
}
