using System.Security.Cryptography;
using System.Text.Json;

internal static class KnowledgeCampaign
{
    private sealed record Batch(string Id,string Group,int Repeat,int Start,int Count,string[] ScenarioIds);
    private sealed record Manifest(string Id,string FixtureHash,string GraphUri,int MaxCalls,long MaxReservedTokens,Dictionary<string,string> Files,Batch[] Batches);
    private sealed record Receipt(string BatchId,int Calls,long ReservedTokens,string ReportHash,int ExitCode);
    private static string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    public static async Task<int> RunAsync(string root,string action,string directory,string? fixtures,string? batchId,string? graphUri,JsonSerializerOptions json)
    {
        directory=Path.GetFullPath(directory);
        if(action=="preflight")
        {
            var cases=KnowledgeAblationSmoke.LoadFixtures(fixtures??throw new ArgumentException("--fixtures required"),json);
            var families=cases.Select(c=>c.Family).Where(f=>!string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.Ordinal).Count();
            var graph=cases.Count(c=>c.Partition=="graph-candidate");var control=cases.Count(c=>c.Partition=="non-graph-control");
            var missing=new List<string>();if(cases.Length<30)missing.Add("scenario-families>=30");if(families<30)missing.Add("distinct-families>=30");
            if(graph<15)missing.Add("graph-candidate>=15");if(control<15)missing.Add("non-graph-control>=15");
            if(cases.Any(c=>c.Partition=="development"||string.IsNullOrWhiteSpace(c.Family)))missing.Add("no-development-or-missing-family");
            Console.WriteLine(JsonSerializer.Serialize(new{fixture=Path.GetFullPath(fixtures!),scenarios=cases.Length,families,graph_candidate=graph,non_graph_control=control,
                structurally_ready=missing.Count==0,quality_ready=false,missing,note="Structural readiness does not provide human review or held-out quality evidence."},json));
            return missing.Count==0?0:2;
        }
        if(action=="prepare")
        {
            if(string.IsNullOrWhiteSpace(graphUri))throw new ArgumentException("--graph-uri required when preparing four groups.");
            var cases=KnowledgeAblationSmoke.LoadFixtures(fixtures??throw new ArgumentException("--fixtures required"),json);
            if(cases.Length<30)
                throw new InvalidDataException("Formal campaign requires at least 30 held-out scenario families; use ablation smoke for smaller fixtures.");
            if(cases.Any(c=>c.Partition=="development" || string.IsNullOrWhiteSpace(c.Family)))
                throw new InvalidDataException("Formal campaign requires non-development scenarios with explicit family IDs.");
            if(cases.Select(c=>c.Family!).Distinct(StringComparer.Ordinal).Count()<30)
                throw new InvalidDataException("Formal campaign requires at least 30 distinct held-out families.");
            if(cases.Count(c=>c.Partition=="graph-candidate")<15 || cases.Count(c=>c.Partition=="non-graph-control")<15)
                throw new InvalidDataException("Formal campaign requires at least 15 graph-candidate and 15 non-graph-control scenarios.");
            if(Directory.Exists(directory))throw new IOException("Campaign directory must be new.");
            var files=Directory.GetFiles(AppContext.BaseDirectory,"*.dll").Concat(Directory.GetFiles(Path.Combine(root,"data/persona/hutao"),"*",SearchOption.AllDirectories))
                .Order(StringComparer.Ordinal).ToDictionary(Path.GetFullPath,Hash,StringComparer.Ordinal);
            var batches=new List<Batch>();
            for(var repeat=1;repeat<=2;repeat++)
            for(var start=0;start<cases.Length;start+=3)
            foreach(var group in new[]{"A","B","C","D"})
                batches.Add(new($"r{repeat}-{group}-{start:D3}",group,repeat,start,Math.Min(3,cases.Length-start),cases.Skip(start).Take(3).Select(c=>c.Id).ToArray()));
            Directory.CreateDirectory(directory);
            File.Copy(fixtures!,Path.Combine(directory,"fixtures.json"));
            var manifest=new Manifest(Guid.NewGuid().ToString("N"),Hash(Path.Combine(directory,"fixtures.json")),graphUri,2500,40000000,files,batches.ToArray());
            await File.WriteAllTextAsync(Path.Combine(directory,"manifest.json"),JsonSerializer.Serialize(manifest,json));
            Console.WriteLine($"Prepared {batches.Count} batches; {cases.Length} scenarios x 4 groups x 2 runs; no model calls.");return 0;
        }
        var manifestPath=Path.Combine(directory,"manifest.json");
        var plan=JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath),json)??throw new InvalidDataException("Missing campaign manifest.");
        if(plan.Batches.Select(b=>b.Id).Distinct().Count()!=plan.Batches.Length)throw new InvalidDataException("Duplicate batch identity.");
        var frozen=Path.Combine(directory,"fixtures.json");
        if(Hash(frozen)!=plan.FixtureHash)throw new InvalidDataException("Frozen fixture changed.");
            var frozenCases=KnowledgeAblationSmoke.LoadFixtures(frozen,json);
        if(frozenCases.Length<30)throw new InvalidDataException("Campaign manifest is not a formal held-out set (minimum 30 families).");
        if(frozenCases.Any(c=>c.Partition=="development" || string.IsNullOrWhiteSpace(c.Family)) ||
            frozenCases.Select(c=>c.Family!).Distinct(StringComparer.Ordinal).Count()<30 ||
            frozenCases.Count(c=>c.Partition=="graph-candidate")<15 || frozenCases.Count(c=>c.Partition=="non-graph-control")<15)
            throw new InvalidDataException("Campaign manifest is not a balanced held-out set.");
        var expectedIds=(from repeat in new[]{1,2} from start in Enumerable.Range(0,(frozenCases.Length+2)/3).Select(i=>i*3)
            from variant in new[]{"A","B","C","D"} select $"r{repeat}-{variant}-{start:D3}").ToArray();
        if(!expectedIds.SequenceEqual(plan.Batches.Select(b=>b.Id)))throw new InvalidDataException("Campaign grid is incomplete or reordered.");
        foreach(var b in plan.Batches)
            if(b.Group is not ("A" or "B" or "C" or "D") || b.Repeat is <1 or >2 || b.Count!=Math.Min(3,frozenCases.Length-b.Start) || b.Start<0 ||
                !b.ScenarioIds.SequenceEqual(frozenCases.Skip(b.Start).Take(b.Count).Select(c=>c.Id)) || b.Id!=$"r{b.Repeat}-{b.Group}-{b.Start:D3}")
                throw new InvalidDataException("Invalid campaign batch selection.");
        using var gate=new FileStream(Path.Combine(directory,"campaign.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        int chargedCalls=0;long chargedTokens=0;var completed=new List<string>();var incomplete=new List<string>();
        foreach(var b in plan.Batches)
        {
            var receiptPath=Path.Combine(directory,b.Id+".receipt.json");
            var startedPath=Path.Combine(directory,b.Id+".started");
            if(File.Exists(startedPath) && File.ReadAllText(startedPath)!=Hash(manifestPath))throw new InvalidDataException("Manifest changed after batch start.");
            if(File.Exists(receiptPath))
            {
                if(!File.Exists(startedPath))throw new InvalidDataException("Receipt has no reservation.");
                var receipt=JsonSerializer.Deserialize<Receipt>(await File.ReadAllTextAsync(receiptPath),json)!;
                var reportPath=Path.Combine(directory,b.Id,"report.json");
                if(receipt.BatchId!=b.Id || receipt.Calls is <0 or >60 || receipt.ReservedTokens is <0 or >1000000 || Hash(reportPath)!=receipt.ReportHash)
                    throw new InvalidDataException("Receipt or report mismatch.");
                using var report=JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
                if(report.RootElement.GetProperty("calls").GetInt32()!=receipt.Calls || report.RootElement.GetProperty("reserved_tokens").GetInt64()!=receipt.ReservedTokens)
                    throw new InvalidDataException("Usage receipt differs from report.");
                var recorded=report.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
                if(report.RootElement.GetProperty("group").GetString()!=b.Group || !recorded.Select(s=>s.GetProperty("id").GetString()).SequenceEqual(b.ScenarioIds) ||
                    recorded.Where((s,i)=>!s.GetProperty("turns").EnumerateArray().Select(t=>t.GetProperty("input").GetString()).SequenceEqual(frozenCases[b.Start+i].Inputs)).Any())
                    throw new InvalidDataException("Incomplete or mismatched batch report.");
                chargedCalls+=receipt.Calls;chargedTokens+=receipt.ReservedTokens;completed.Add(b.Id);
            }
            else if(File.Exists(Path.Combine(directory,b.Id+".started")))
            {chargedCalls+=60;chargedTokens+=1000000;incomplete.Add(b.Id);}
        }
        if(action=="audit")
        {
            var humanReview=KnowledgeHumanReview.ReadVerified(directory,json);
            var qualityReasons=new List<string>();
            foreach(var completedBatch in plan.Batches.Where(b=>completed.Contains(b.Id)))
            {
                var reportPath=Path.Combine(directory,completedBatch.Id,"report.json");
                using var report=JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
                var reportRoot=report.RootElement;
                if(reportRoot.TryGetProperty("not_heldout",out var heldout) && heldout.ValueKind==JsonValueKind.True)qualityReasons.Add(completedBatch.Id+":not-heldout");
                if(humanReview is null && reportRoot.TryGetProperty("human_scores_pending",out var pending) && pending.ValueKind==JsonValueKind.True)qualityReasons.Add(completedBatch.Id+":human-scores-pending");
                if(!reportRoot.TryGetProperty("scenarios",out var scenarios) || scenarios.EnumerateArray().Any(s=>!s.TryGetProperty("review_criteria",out var criteria) || criteria.ValueKind!=JsonValueKind.Array || criteria.GetArrayLength()==0))
                    qualityReasons.Add(completedBatch.Id+":review-criteria-missing");
            }
            if(humanReview is not null && !humanReview.ConversationQualityPassed)qualityReasons.Add("human-review:conversation-quality-gate-failed");
            var qualityPassed=completed.Count==plan.Batches.Length && qualityReasons.Count==0 && humanReview is not null;
            Console.WriteLine(JsonSerializer.Serialize(new{plan.Id,completed,uncertain=incomplete,missing=plan.Batches.Select(b=>b.Id).Except(completed).Except(incomplete),
                charged_calls=chargedCalls,charged_reserved_tokens=chargedTokens,coverage_complete=completed.Count==plan.Batches.Length,
                quality_passed=qualityPassed,quality_reasons=qualityReasons,human_scores=humanReview is null?"pending":"recorded",
                graph_default_gate=humanReview?.GraphDefaultPassed,graph_candidate_gain=humanReview?.GraphCandidateGain,
                non_graph_control_delta=humanReview?.NonGraphControlDelta,graph_hallucination_delta=humanReview?.GraphHallucinationDelta,
                note="Completed includes failed-answer turns; coverage is not quality."},json));return completed.Count==plan.Batches.Length?0:2;
        }
        if(action=="export")
        {
            Console.WriteLine(await KnowledgeReviewExport.RunAsync(directory,completed.ToHashSet(StringComparer.Ordinal),json));return 0;
        }
        if(action!="run")throw new ArgumentException("Use prepare, run, audit, or export.");
        foreach(var file in plan.Files)if(!File.Exists(file.Key)||Hash(file.Key)!=file.Value)throw new InvalidDataException("Frozen build/persona changed: "+Path.GetFileName(file.Key));
        var batch=plan.Batches.Single(b=>b.Id==batchId);
        if(chargedCalls+60>Math.Min(plan.MaxCalls,2500) || chargedTokens+1000000>Math.Min(plan.MaxReservedTokens,40000000))throw new InvalidOperationException("Campaign budget exhausted.");
        using(var started=new FileStream(Path.Combine(directory,batch.Id+".started"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {await started.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Hash(manifestPath)));started.Flush(true);}
        var destination=Path.Combine(directory,batch.Id);
        if(graphUri is not null && graphUri!=plan.GraphUri)throw new InvalidDataException("Graph URI differs from frozen campaign.");
        var code=await KnowledgeAblationSmoke.RunAsync(root,destination,batch.Group,batch.Count,json,plan.GraphUri,batch.Start,frozen);
        var reportFile=Path.Combine(destination,"report.json");
        using var finalReport=JsonDocument.Parse(await File.ReadAllTextAsync(reportFile));
        var finalReceipt=new Receipt(batch.Id,finalReport.RootElement.GetProperty("calls").GetInt32(),finalReport.RootElement.GetProperty("reserved_tokens").GetInt64(),Hash(reportFile),code);
        await File.WriteAllTextAsync(Path.Combine(directory,batch.Id+".receipt.json"),JsonSerializer.Serialize(finalReceipt,json));
        return code;
    }
}
