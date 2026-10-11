using System.Text.Json;
using HuTao.Knowledge.Rag;
using HuTao.Knowledge.Storage;
using HuTao.Persona;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Import;

public sealed record KnowledgeImportReport(string Namespace, string Version, int Count, bool AlreadyImported, IReadOnlyList<string> Warnings);
public sealed class StorySqlImporter(SqliteKnowledgeDatabase database)
{
    public static KnowledgeImportReport VerifyExistingCorpus(string databasePath,string space,string dialogueRoot,StoryLexicon lexicon,CancellationToken ct=default)
    {
        var corpus=StoryCorpus.Load(dialogueRoot,lexicon);
        VerifyExisting(databasePath,space,corpus.Lines,corpus.Stats.Version,ct);
        return new(space,corpus.Stats.Version,corpus.Lines.Count,true,corpus.Stats.Warnings);
    }
    public static void VerifyExisting(string databasePath,string space,IReadOnlyList<StoryDialogueLine> expected,string expectedVersion,CancellationToken ct=default)
    {
        using var connection=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {DataSource=Path.GetFullPath(databasePath),Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        connection.Open();
        using var transaction=connection.BeginTransaction(deferred:true);
        using var version=Command(connection,transaction,"SELECT active_corpus_version FROM namespaces WHERE namespace_id=@n",("@n",space));
        if(version.ExecuteScalar() is not string active || active!=expectedVersion)
            throw new InvalidDataException("Existing corpus version mismatch.");
        using var query=Command(connection,transaction,"SELECT metadata_json FROM evidence_units WHERE namespace_id=@n AND corpus_version=@v AND deleted_at IS NULL ORDER BY rowid",("@n",space),("@v",active));
        using var rows=query.ExecuteReader();
        var index=0;
        while(rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            if(index>=expected.Count || rows.GetString(0)!=JsonSerializer.Serialize(expected[index]))
                throw new InvalidDataException($"Existing corpus metadata mismatch at position {index}.");
            index++;
        }
        if(index!=expected.Count)throw new InvalidDataException("Existing corpus count mismatch.");
    }
    public KnowledgeImportReport VerifyRoundTrip(string space,string dialogueRoot,StoryLexicon lexicon,CancellationToken ct=default)
    {
        var original=StoryCorpus.Load(dialogueRoot,lexicon);
        var report=Import(space,dialogueRoot,lexicon,ct:ct);
        var restored=ReadActiveLines(space);
        if(original.Lines.Count!=restored.Count)throw new InvalidDataException("SQL roundtrip count mismatch.");
        for(var i=0;i<restored.Count;i++)
        {
            ct.ThrowIfCancellationRequested();
            if(JsonSerializer.Serialize(original.Lines[i])!=JsonSerializer.Serialize(restored[i]))
                throw new InvalidDataException($"SQL roundtrip metadata mismatch at position {i}.");
        }
        return report;
    }
    public KnowledgeImportReport Import(string space,string dialogueRoot,StoryLexicon? lexicon=null,bool dryRun=false,CancellationToken ct=default)
    {
        var corpus=StoryCorpus.Load(dialogueRoot,lexicon);
        if(corpus.Stats.Warnings.Any(w=>w.StartsWith("Conflicting duplicate",StringComparison.Ordinal)))
            throw new InvalidDataException("Corpus has conflicting IDs; resolve before SQL activation.");
        var result=ImportLines(space,corpus.Lines,corpus.Stats.Version,dryRun,ct,corpus.Stats.Warnings);
        if(!dryRun)database.Write((c,t)=>Execute(c,t,"INSERT OR REPLACE INTO corpus_manifests VALUES(@n,@v,@stats)",("@n",space),("@v",corpus.Stats.Version),("@stats",JsonSerializer.Serialize(corpus.Stats))),ct);
        return result;
    }
    public KnowledgeImportReport ImportLines(string space,IReadOnlyList<StoryDialogueLine> lines,string version,bool dryRun=false,CancellationToken ct=default,IReadOnlyList<string>? warnings=null)
    {
        if(!space.StartsWith("corpus:",StringComparison.Ordinal))throw new ArgumentException("Expected corpus namespace.");
        if(lines.Any(l=>string.IsNullOrWhiteSpace(l.Text)) || lines.Select(l=>l.EvidenceId).Distinct().Count()!=lines.Count)
            throw new InvalidDataException("Empty or duplicate corpus evidence.");
        if(dryRun)return new(space,version,lines.Count,false,warnings??[]);
        database.EnsureNamespace(space);
        string? expectedActive;
        using(var c=database.Open())
        using(var cmd=Command(c,null,"SELECT active_corpus_version FROM namespaces WHERE namespace_id=@n",("@n",space)))
            expectedActive=cmd.ExecuteScalar() as string;
        using(var c=database.Open())
        using(var cmd=Command(c,null,"SELECT counts FROM import_runs WHERE namespace_id=@n AND manifest_hash=@v AND validation_status='committed'",("@n",space),("@v",version)))
            if(cmd.ExecuteScalar() is long count)
            {
                // A version label is not proof that its inputs are unchanged.
                using var stored=Command(c,null,"SELECT metadata_json FROM evidence_units WHERE namespace_id=@n AND corpus_version=@v ORDER BY rowid",("@n",space),("@v",version));
                using var rows=stored.ExecuteReader();
                var index=0;
                while(rows.Read())
                {
                    if(index>=lines.Count || rows.GetString(0)!=JsonSerializer.Serialize(lines[index++]))
                        throw new InvalidDataException("Committed corpus version has different contents or order.");
                }
                if(index!=lines.Count || count!=lines.Count)
                    throw new InvalidDataException("Committed corpus version has different evidence count.");
                return new(space,version,(int)count,true,warnings??[]);
            }
        foreach(var batch in lines.Chunk(500))
            database.Write((c,t)=>{
                foreach(var line in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    SqlEvidenceRepository.Put(c,t,space,new(line.EvidenceId,line.Text,line.Speaker,line.SourceFile,line.EvidenceKind,
                        line.ChapterId,line.Sequence,line.Variant,MetadataJson:JsonSerializer.Serialize(line)),version);
                }
                return 0;
            },ct);
        database.Write((c,t)=>{
            using(var current=Command(c,t,"SELECT active_corpus_version FROM namespaces WHERE namespace_id=@n",("@n",space)))
                if((current.ExecuteScalar() as string)!=expectedActive)
                    throw new InvalidOperationException("Another import activated a corpus; retry against current state.");
            Execute(c,t,"INSERT OR REPLACE INTO import_runs VALUES(@run,@n,@v,'story-v1',@count,'committed',@now)",("@run",Hash(space+version)),("@n",space),("@v",version),("@count",lines.Count),("@now",Now()));
            Execute(c,t,"UPDATE namespaces SET active_corpus_version=@v WHERE namespace_id=@n",("@v",version),("@n",space));
            Execute(c,t,"UPDATE namespace_state SET commit_seq=commit_seq+1 WHERE namespace_id=@n",("@n",space));
            return 0;
        },ct);
        return new(space,version,lines.Count,false,warnings??[]);
    }
    internal StoryCorpus LoadActive(string space)
    {
        using var c=database.Open();
        using var version=Command(c,null,"SELECT active_corpus_version FROM namespaces WHERE namespace_id=@n",("@n",space));
        var active=version.ExecuteScalar() as string ?? throw new InvalidOperationException("No active SQL corpus.");
        var lines=new List<StoryDialogueLine>();
        using var cmd=Command(c,null,"SELECT metadata_json FROM evidence_units WHERE namespace_id=@n AND corpus_version=@v AND deleted_at IS NULL ORDER BY rowid",("@n",space),("@v",active));
        using var rows=cmd.ExecuteReader();
        while(rows.Read())lines.Add(JsonSerializer.Deserialize<StoryDialogueLine>(rows.GetString(0))??throw new InvalidDataException("Invalid line metadata."));
        using var statsQuery=Command(c,null,"SELECT stats_json FROM corpus_manifests WHERE namespace_id=@n AND corpus_version=@v",("@n",space),("@v",active));
        var stats=statsQuery.ExecuteScalar() as string;
        var corpus=new StoryCorpus{Stats=stats is null?new(active,lines.Count,0,lines.Select(l=>l.SourceFile).Distinct().Count(),[],0):JsonSerializer.Deserialize<StoryCorpusStats>(stats)!};
        corpus.Lines.AddRange(lines);return corpus;
    }
    public IReadOnlyList<StoryDialogueLine> ReadActiveLines(string space)=>LoadActive(space).Lines;
}
