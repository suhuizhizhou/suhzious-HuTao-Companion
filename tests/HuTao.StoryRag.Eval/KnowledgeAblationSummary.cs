using System.Text.Json;

/// <summary>Read-only aggregation of recorded executions; no scoring or model calls.</summary>
internal static class KnowledgeAblationSummary
{
    public static async Task<int> RunAsync(string reportPath,string output,JsonSerializerOptions json)
    {
        if(File.Exists(output))throw new IOException("Use a new summary output file.");
        using var report=JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        var root=report.RootElement;var rows=new List<object>();
        int total=0,errors=0,requestedTurns=0,validatedTurns=0,fallbackTurns=0,paths=0,twoHop=0;
        var durations=new List<long>();
        foreach(var scenario in root.GetProperty("scenarios").EnumerateArray())
        foreach(var turn in scenario.GetProperty("turns").EnumerateArray())
        {
            total++;
            if(turn.TryGetProperty("elapsed_ms",out var elapsed))durations.Add(elapsed.GetInt64());
            if(turn.TryGetProperty("error",out _)){errors++;continue;}
            bool requested=false,validated=false,fallback=false;int turnPaths=0,turnTwoHop=0;
            var validatedTasks=new HashSet<string>(StringComparer.Ordinal);
            if(turn.TryGetProperty("recall",out var recall) && recall.ValueKind==JsonValueKind.Object &&
                recall.TryGetProperty("knowledge",out var knowledge) && knowledge.ValueKind==JsonValueKind.Object)
            {
                string? task=null;
                foreach(var item in knowledge.GetProperty("trace").EnumerateArray())
                {
                    var trace=item.GetString()??"";
                    if(trace.StartsWith("task=",StringComparison.Ordinal))task=trace.Split(';')[0][5..];
                    requested|=trace.Contains("requested=GraphExpand",StringComparison.Ordinal);
                    fallback|=trace.Contains("requested=GraphExpand;fallback=SqlFacts",StringComparison.Ordinal) ||
                        trace.Contains("requested=GraphExpand;actual=SqlLexical",StringComparison.Ordinal);
                    if(trace.StartsWith("backend=neo4j+sqlite;strategy=GraphExpand;",StringComparison.Ordinal))
                    {validated=true;if(task is not null)validatedTasks.Add(task);}
                }
                if(knowledge.TryGetProperty("tasks",out var tasks) && tasks.ValueKind==JsonValueKind.Array)
                foreach(var result in tasks.EnumerateArray())
                {
                    if(!validatedTasks.Contains(result.GetProperty("task_id").GetString()!))continue;
                    foreach(var path in result.GetProperty("paths").EnumerateArray())
                    {
                        var edgeCount=path.GetProperty("facts").GetArrayLength();
                        if(edgeCount==0)continue;
                        turnPaths++;if(edgeCount>=2)turnTwoHop++;
                    }
                }
            }
            if(requested)requestedTurns++;if(validated)validatedTurns++;if(fallback)fallbackTurns++;
            paths+=turnPaths;twoHop+=turnTwoHop;
            rows.Add(new{scenario=scenario.GetProperty("id").GetString(),input=turn.GetProperty("input").GetString(),
                graph_requested=requested,graph_sql_validated_backend=validated,graph_sql_fallback=fallback,
                graph_paths=turnPaths,graph_two_hop_paths=turnTwoHop});
        }
        durations.Sort();
        long? Percentile(double p)=>durations.Count==0?null:durations[(int)Math.Ceiling(p*durations.Count)-1];
        long prompt=0,completion=0;int missingUsage=0;
        foreach(var usage in root.GetProperty("usage").EnumerateArray())
        {
            if(usage.TryGetProperty("prompt_tokens",out var p) && p.ValueKind==JsonValueKind.Number &&
                usage.TryGetProperty("completion_tokens",out var c) && c.ValueKind==JsonValueKind.Number)
            {prompt+=p.GetInt64();completion+=c.GetInt64();}else missingUsage++;
        }
        using var stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        await JsonSerializer.SerializeAsync(stream,new{source_report=Path.GetFullPath(reportPath),group=root.GetProperty("group").GetString(),
            total_turns=total,runtime_errors=errors,graph_requested_turns=requestedTurns,graph_sql_validated_backend_turns=validatedTurns,
            graph_sql_fallback_turns=fallbackTurns,graph_path_count=paths,graph_two_hop_path_count=twoHop,
            text_turn_p50_ms=Percentile(.5),text_turn_p95_ms=Percentile(.95),prompt_tokens=prompt,completion_tokens=completion,missing_usage=missingUsage,
            quality_score="not assessed",coverage="path counts are not gold evidence coverage",latency_scope="whole text turn including errors; excludes TTS",
            graph_scope="requested is planner intent; validated backend trace is not proof of nonempty accepted paths; paths counted only in graph-validated tasks",
            rows},json);
        return 0;
    }
}
