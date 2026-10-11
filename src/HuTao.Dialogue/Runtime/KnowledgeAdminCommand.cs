using System.Text.Json;
using HuTao.Dialogue.Llm;
using HuTao.Foundation.Abstractions;
using HuTao.Knowledge.Graph;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;

namespace HuTao.Dialogue.Runtime;

/// <summary>Explicit local administration; never registered as an agent tool.</summary>
public static class KnowledgeAdminCommand
{
    public static async Task<int> RunAsync(string[] args,TextWriter output,Func<ILLMProvider>? providerFactory=null,CancellationToken ct=default)
    {
        try
        {
            if(args.Length==0 || args[0]=="help")
            {
                await output.WriteLineAsync("knowledge-admin list|extract|review|publish|facts|recover-fact --db PATH --user ID --persona ID --scope ID [--source ID --revision N] [--job ID] [--decision-file PATH] [--subject ID --predicate ID] [--quarantine PATH --assertion ID --assertion-revision N]");
                return 0;
            }
            var action=args[0];
            if(action is not ("list" or "extract" or "review" or "publish" or "facts" or "recover-fact"))throw new ArgumentException("Unknown action.");
            var options=new Dictionary<string,string>(StringComparer.Ordinal);
            var allowed=new HashSet<string>(["--db","--user","--persona","--scope"],StringComparer.Ordinal);
            if(action=="extract"){allowed.Add("--source");allowed.Add("--revision");}
            if(action=="review")allowed.Add("--job");
            if(action=="publish")allowed.Add("--decision-file");
            if(action=="facts"){allowed.Add("--subject");allowed.Add("--predicate");}
            if(action=="recover-fact"){allowed.Add("--quarantine");allowed.Add("--assertion");allowed.Add("--assertion-revision");}
            for(var i=1;i<args.Length;i+=2)
                if(i+1>=args.Length || !allowed.Contains(args[i]) || !options.TryAdd(args[i],args[i+1]))throw new ArgumentException("Invalid or duplicate option.");
            string Required(string key)=>options.TryGetValue(key,out var value)&&!string.IsNullOrWhiteSpace(value)?value:throw new ArgumentException("Missing "+key);
            var path=Path.GetFullPath(Required("--db"));
            if(!File.Exists(path))throw new FileNotFoundException("Initialize or migrate the database first.");
            var access=KnowledgeAccessContext.ForSession(Required("--user"),Required("--persona"),Required("--scope"),[]);
            var db=new SqliteKnowledgeDatabase(path);
            var json=new JsonSerializerOptions {WriteIndented=true,PropertyNameCaseInsensitive=false,UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
            object result;
            if(action=="list")result=new DeferredTurnExtraction(db,new NoModel()).List(access);
            else if(action=="recover-fact")result=KnowledgeRecoveryFactReview.Stage(db,Required("--quarantine"),access,Required("--assertion"),long.Parse(Required("--assertion-revision")));
            else if(action=="facts")result=new HuTao.Knowledge.Facts.SqlAssertionRepository(db).Query(access,access.MemoryNamespace,new(Required("--subject"),Required("--predicate")));
            else if(action=="extract")
            {
                var reference=new EvidenceRef(access.MemoryNamespace,Required("--source"),long.Parse(Required("--revision")));
                var provider=providerFactory?.Invoke()??new DeepSeekLlmProvider(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")??throw new InvalidOperationException("DEEPSEEK_API_KEY required."),temperature:0.2,transportRetries:0);
                var budget=new ExtractionBudget(maxCalls:1,maxSourceCharacters:4000,maxReservedTokens:64000);
                var extracted=await new DeferredTurnExtraction(db,provider).ExtractAsync(access,reference,budget,ct).ConfigureAwait(false);
                result=new {extracted.JobId,extracted.Cached,extracted.Validations,extracted.EntityProposals,budget.Calls,budget.ReservedTokens,Publication="not-published"};
            }
            else if(action=="review")
            {
                var reviewSequence=new SqlEvidenceRepository(db).ReadStamps(access,[access.MemoryNamespace])[access.MemoryNamespace].CommitSequence;
                using var c=db.Open();using var command=c.CreateCommand();
                command.CommandText="SELECT evidence_id,evidence_revision,status,output_json,error_code,prompt_tokens,completion_tokens FROM extraction_jobs WHERE job_id=@job AND namespace_id=@n";
                command.Parameters.AddWithValue("@job",Required("--job"));command.Parameters.AddWithValue("@n",access.MemoryNamespace);
                using var row=command.ExecuteReader();
                if(!row.Read())throw new InvalidOperationException("Job not found in this scope.");
                var reference=new EvidenceRef(access.MemoryNamespace,row.GetString(0),row.GetInt64(1));
                var evidence=new SqlEvidenceRepository(db).Read(access,reference);
                result=new {Reference=reference,ExpectedCommitSequence=reviewSequence,Status=row.GetString(2),SourceCurrent=evidence is not null,Source=evidence?.Text,
                    Extraction=evidence is null||row.IsDBNull(3)?null:JsonSerializer.Deserialize<StoredExtraction>(row.GetString(3)),
                    Error=row.IsDBNull(4)?null:row.GetString(4),PromptTokens=row.IsDBNull(5)?(long?)null:row.GetInt64(5),CompletionTokens=row.IsDBNull(6)?(long?)null:row.GetInt64(6)};
            }
            else
            {
                var decisionPath=Required("--decision-file");
                if(new FileInfo(decisionPath).Length>16384)throw new InvalidDataException("Decision file exceeds limit.");
                var decision=JsonSerializer.Deserialize<Decision>(File.ReadAllText(decisionPath),json)??throw new InvalidDataException("Invalid decision.");
                if(string.IsNullOrWhiteSpace(decision.JobId)||string.IsNullOrWhiteSpace(decision.ReviewerId)||decision.AssertionIds is null||decision.AssertionIds.Length is <1 or >6||decision.AssertionIds.Distinct(StringComparer.Ordinal).Count()!=decision.AssertionIds.Length)
                    throw new InvalidDataException("Explicit job, reviewer and unique assertion selection required.");
                result=new ExtractionPublisher(db).Publish(access,decision.JobId,decision.AssertionIds.ToHashSet(StringComparer.Ordinal),decision.ReviewerId,decision.ApproveCandidates,ct,decision.Supersedes,decision.ExpectedCommitSequence);
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(result,json));return 0;
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {await output.WriteLineAsync("Knowledge administration failed: "+ex.GetType().Name);return 1;}
    }
    public sealed record Decision(string JobId,string ReviewerId,string[] AssertionIds,bool ApproveCandidates=false,
        Dictionary<string,string>? Supersedes=null,long? ExpectedCommitSequence=null);
    private sealed class NoModel:ILLMProvider
    {
        public string Name=>"read-only";
        public Task<string> CompleteAsync(string systemPrompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)=>throw new InvalidOperationException();
    }
}
