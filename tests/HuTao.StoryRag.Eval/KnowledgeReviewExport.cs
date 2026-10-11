using System.Text.Json;

internal static class KnowledgeReviewExport
{
    private sealed class Totals
    {
        public int Planned,Observed,Errors,Eligible,Covered,Calls,UsageMissing;
        public long Prompt,Completion;
    }
    public static async Task<string> RunAsync(string campaign,IReadOnlySet<string> completed,JsonSerializerOptions json)
    {
        using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(campaign,"manifest.json")));
        var cases=KnowledgeAblationSmoke.LoadFixtures(Path.Combine(campaign,"fixtures.json"),json).ToDictionary(c=>c.Id);
        var totals=new Dictionary<string,Totals>();var packets=new List<(string Id,object Body,object Key)>();
        var missing=new List<string>();
        foreach(var batch in manifest.RootElement.GetProperty("batches").EnumerateArray())
        {
            var batchId=batch.GetProperty("id").GetString()!;var group=batch.GetProperty("group").GetString()!;
            var ids=batch.GetProperty("scenario_ids").EnumerateArray().Select(x=>x.GetString()!).ToArray();
            foreach(var id in ids)
            {
                var key=group+"/"+cases[id].Partition;
                if(!totals.TryGetValue(key,out var total))totals[key]=total=new();
                total.Planned+=cases[id].Inputs.Length;
            }
            if(!completed.Contains(batchId)){missing.Add(batchId);continue;}
            using var report=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(campaign,batchId,"report.json")));
            // Usage is batch-level and cannot honestly be assigned to individual partitions.
            var usageKey=group+"/all-usage";
            if(!totals.TryGetValue(usageKey,out var usageTotals))totals[usageKey]=usageTotals=new();
            usageTotals.Calls+=report.RootElement.GetProperty("calls").GetInt32();
            foreach(var usage in report.RootElement.GetProperty("usage").EnumerateArray())
                if(usage.TryGetProperty("prompt_tokens",out var p) && p.ValueKind==JsonValueKind.Number && usage.TryGetProperty("completion_tokens",out var c) && c.ValueKind==JsonValueKind.Number)
                {usageTotals.Prompt+=p.GetInt64();usageTotals.Completion+=c.GetInt64();}else usageTotals.UsageMissing++;
            foreach(var scenario in report.RootElement.GetProperty("scenarios").EnumerateArray())
            {
                var id=scenario.GetProperty("id").GetString()!;var source=cases[id];var total=totals[group+"/"+source.Partition];
                var turns=new List<object>();var index=0;
                foreach(var turn in scenario.GetProperty("turns").EnumerateArray())
                {
                    total.Observed++;var error=turn.TryGetProperty("error",out _);if(error)total.Errors++;
                    var expected=source.ExpectedEvidence?[index];
                    if(expected is {Length:>0})
                    {
                        total.Eligible++;var found=new HashSet<string>();
                        if(!error && turn.TryGetProperty("recall",out var recall) && recall.ValueKind==JsonValueKind.Object)
                        {
                            if(recall.TryGetProperty("knowledge",out var knowledge) && knowledge.ValueKind==JsonValueKind.Object)
                                foreach(var e in knowledge.GetProperty("evidence").EnumerateArray())found.Add(e.GetProperty("reference").GetProperty("id").GetString()!);
                            else if(recall.TryGetProperty("memories",out var memories))
                                foreach(var m in memories.EnumerateArray())found.Add(m.GetProperty("record").GetProperty("id").GetString()!);
                        }
                        if(expected.All(i=>found.Contains(id+":"+i)))total.Covered++;
                    }
                    turns.Add(new{input=turn.GetProperty("input").GetString(),reply=turn.TryGetProperty("reply",out var reply)?reply.GetString():null,missing_reply=error});index++;
                }
                var sampleId=Guid.NewGuid().ToString("N");
                packets.Add((sampleId,new{sample_id=sampleId,source_originals=source.Originals,turns},
                    new{sample_id=sampleId,batch_id=batchId,group,repeat=batch.GetProperty("repeat").GetInt32(),scenario_id=id,partition=source.Partition}));
            }
        }
        var output=Path.Combine(campaign,"review-"+Guid.NewGuid().ToString("N"));
        var reviewer=Path.Combine(output,"reviewer");var coordinator=Path.Combine(output,"coordinator");
        Directory.CreateDirectory(reviewer);Directory.CreateDirectory(coordinator);
        var shuffled=packets.OrderBy(_=>Guid.NewGuid()).ToArray();
        await File.WriteAllTextAsync(Path.Combine(reviewer,"dialogues.json"),JsonSerializer.Serialize(shuffled.Select(p=>p.Body),json));
        await File.WriteAllTextAsync(Path.Combine(coordinator,"key.json"),JsonSerializer.Serialize(shuffled.Select(p=>p.Key),json));
        await File.WriteAllLinesAsync(Path.Combine(reviewer,"scores.csv"),new[]{"sample_id,reviewer_id,current_response,useful_specificity,character_naturalness,uncertainty_restraint,hallucination,notes"}
            .Concat(shuffled.Select(p=>p.Id+",,,,,,,")));
        await File.WriteAllTextAsync(Path.Combine(reviewer,"rubric.md"),"""
            # 人工对话评分
            只阅读本reviewer目录，不读取coordinator对照表。每条为整段对话，原文是判断个人事实的依据。
            四项分别1–5分：回应当前问题、具体信息有用、角色自然、未知克制。1=明显失败，3=部分满足但有实质问题，5=充分满足。
            hallucination填0（未见无据事实）、1（有无据事实）或unknown（无法判断），不能用空白冒充通过。
            missing_reply表示未获得回复，保留并评价交互失败，不跳过。评分由真人填写reviewer_id与说明，不接受模型代填。
            这不是官方设定的完整参考库；无法判断的角色断言标unknown并解释。不要因语言流畅忽略来源错误。
            此导出可能仅含已完成批次，不能依据材料数量自行宣称四组实验完成。元数据匿名不保证从回复风格无法猜出组别。
            """);
        await File.WriteAllTextAsync(Path.Combine(coordinator,"summary.json"),JsonSerializer.Serialize(new{
            completed_batches=completed.Count,missing_batches=missing,coverage_complete=missing.Count==0,human_scores="pending",quality_passed=false,
            cohorts=totals.Select(t=>new{cohort=t.Key,planned_turns=t.Value.Planned,observed_turns=t.Value.Observed,runtime_errors=t.Value.Errors,
                evidence_eligible_observed_turns=t.Value.Eligible,evidence_fully_covered_turns=t.Value.Covered,
                evidence_coverage=t.Value.Eligible==0?(double?)null:(double)t.Value.Covered/t.Value.Eligible,
                calls=t.Value.Calls,prompt_tokens=t.Value.Prompt,completion_tokens=t.Value.Completion,missing_usage=t.Value.UsageMissing}),
            notes="Coverage is source-ID coverage, not semantic correctness; failed turns stay in eligible denominator; missing batches remain incomplete."},json));
        return output;
    }
}
