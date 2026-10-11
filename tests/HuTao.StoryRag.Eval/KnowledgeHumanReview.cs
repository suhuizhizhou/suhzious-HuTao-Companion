using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class KnowledgeHumanReview
{
    internal sealed record Result(string ScoresSha256,string KeySha256,string DialoguesSha256,int Samples,int Reviewers,
        bool ReviewComplete,bool ConversationQualityPassed,bool GraphDefaultPassed,double GraphCandidateGain,double NonGraphControlDelta,
        double GraphHallucinationDelta,object[] Cohorts);

    private sealed record Row(string Sample,string Reviewer,int Current,int Useful,int Natural,int Restraint,string Hallucination);
    private sealed class Totals
    {
        public int Count,Current,Useful,Natural,Restraint,Hallucinations,KnownHallucination;
    }

    public static async Task<int> RunAsync(string campaign,string review,JsonSerializerOptions json,TextWriter? output=null)
    {
        campaign=Path.GetFullPath(campaign);review=Path.GetFullPath(review);
        var campaignPrefix=campaign.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!review.StartsWith(campaignPrefix,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new InvalidOperationException("Use the original review directory inside the campaign so the scored inputs remain auditable.");
        using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(campaign,"manifest.json")));
        var batches=manifest.RootElement.GetProperty("batches").EnumerateArray().Select(b=>b.GetProperty("id").GetString()!).ToArray();
        if(batches.Any(id=>!File.Exists(Path.Combine(campaign,id+".receipt.json"))))throw new InvalidOperationException("Campaign coverage is incomplete.");
        var scoresPath=Path.Combine(review,"reviewer","scores.csv");var keyPath=Path.Combine(review,"coordinator","key.json");
        var dialoguesPath=Path.Combine(review,"reviewer","dialogues.json");
        using var keysDocument=JsonDocument.Parse(await File.ReadAllTextAsync(keyPath));
        var keys=keysDocument.RootElement.EnumerateArray().ToDictionary(x=>x.GetProperty("sample_id").GetString()!,x=>(
            Group:x.GetProperty("group").GetString()!,Partition:x.GetProperty("partition").GetString()!),StringComparer.Ordinal);
        using var dialoguesDocument=JsonDocument.Parse(await File.ReadAllTextAsync(dialoguesPath));
        var dialogueIds=dialoguesDocument.RootElement.EnumerateArray().Select(x=>x.GetProperty("sample_id").GetString()!).ToHashSet(StringComparer.Ordinal);
        if(!dialogueIds.SetEquals(keys.Keys))throw new InvalidDataException("Reviewer dialogues and coordinator key differ.");
        var records=ParseCsv(await File.ReadAllTextAsync(scoresPath));
        if(records.Count>0 && records[0].Length>0)records[0][0]=records[0][0].TrimStart('\uFEFF');
        var expectedHeader=new[]{"sample_id","reviewer_id","current_response","useful_specificity","character_naturalness","uncertainty_restraint","hallucination","notes"};
        if(records.Count==0 || !records[0].SequenceEqual(expectedHeader,StringComparer.Ordinal))throw new InvalidDataException("Unexpected score CSV header.");
        var rows=new List<Row>();var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var fields in records.Skip(1))
        {
            if(fields.Length!=8 || !keys.ContainsKey(fields[0]) || !seen.Add(fields[0]) || string.IsNullOrWhiteSpace(fields[1]) || fields[1].Length>100)
                throw new InvalidDataException("Invalid, duplicate, or unknown score row.");
            int Score(int index)=>int.TryParse(fields[index],out var value)&&value is>=1 and<=5?value:throw new InvalidDataException("Scores must be integers from 1 to 5.");
            var hallucination=fields[6].Trim().ToLowerInvariant();
            if(hallucination is not ("0" or "1" or "unknown"))throw new InvalidDataException("Hallucination must be 0, 1, or unknown.");
            rows.Add(new(fields[0],fields[1].Trim(),Score(2),Score(3),Score(4),Score(5),hallucination));
        }
        if(!seen.SetEquals(keys.Keys))throw new InvalidDataException("Every exported sample must have exactly one human score row.");
        var totals=new Dictionary<string,Totals>(StringComparer.Ordinal);
        foreach(var row in rows)
        {
            var key=keys[row.Sample];var cohort=key.Group+"/"+key.Partition;
            if(!totals.TryGetValue(cohort,out var total))totals[cohort]=total=new();
            total.Count++;total.Current+=row.Current;total.Useful+=row.Useful;total.Natural+=row.Natural;total.Restraint+=row.Restraint;
            if(row.Hallucination!="unknown"){total.KnownHallucination++;if(row.Hallucination=="1")total.Hallucinations++;}
        }
        double Average(string group,string partition,Func<Totals,int> select)
        {var t=totals[group+"/"+partition];return (double)select(t)/t.Count;}
        double Hallucination(string group,string partition)
        {var t=totals[group+"/"+partition];return t.KnownHallucination==0?1d:(double)t.Hallucinations/t.KnownHallucination;}
        const string graph="graph-candidate",control="non-graph-control";
        var graphGain=Average("D",graph,t=>t.Useful)-Average("C",graph,t=>t.Useful);
        var controlDelta=Average("D",control,t=>t.Useful)-Average("C",control,t=>t.Useful);
        var hallucinationDelta=Hallucination("D",graph)-Hallucination("C",graph);
        var cohorts=totals.OrderBy(t=>t.Key,StringComparer.Ordinal).Select(pair=>new {
            cohort=pair.Key,samples=pair.Value.Count,current=(double)pair.Value.Current/pair.Value.Count,useful=(double)pair.Value.Useful/pair.Value.Count,
            natural=(double)pair.Value.Natural/pair.Value.Count,restraint=(double)pair.Value.Restraint/pair.Value.Count,
            hallucination_known=pair.Value.KnownHallucination,hallucination_rate=pair.Value.KnownHallucination==0?(double?)null:(double)pair.Value.Hallucinations/pair.Value.KnownHallucination}).Cast<object>().ToArray();
        var conversationPassed=totals.Values.All(t=>t.Count>0 && (double)t.Current/t.Count>=3 && (double)t.Useful/t.Count>=3 &&
            (double)t.Natural/t.Count>=3 && (double)t.Restraint/t.Count>=3 && t.KnownHallucination>=Math.Ceiling(t.Count*.8) && (double)t.Hallucinations/t.KnownHallucination<=.1);
        // A 0.4 increase on a 1-5 scale is ten percentage points of its four-point range.
        // The control allowance is 0.08 points, two percentage points of that range.
        var graphPassed=graphGain>=.4 && controlDelta>=-.08 && hallucinationDelta<=.02;
        var result=new Result(Hash(scoresPath),Hash(keyPath),Hash(dialoguesPath),rows.Count,rows.Select(r=>r.Reviewer).Distinct(StringComparer.Ordinal).Count(),
            true,conversationPassed,graphPassed,graphGain,controlDelta,hallucinationDelta,cohorts);
        var resultPath=Path.Combine(campaign,"human-review.json");
        if(File.Exists(resultPath))throw new IOException("Human review result already exists; preserve the reviewed version.");
        await File.WriteAllTextAsync(resultPath,JsonSerializer.Serialize(result,json));
        await (output??Console.Out).WriteLineAsync(JsonSerializer.Serialize(result,json));return 0;
    }

    public static Result? ReadVerified(string campaign,JsonSerializerOptions json)
    {
        var resultPath=Path.Combine(campaign,"human-review.json");if(!File.Exists(resultPath))return null;
        var result=JsonSerializer.Deserialize<Result>(File.ReadAllText(resultPath),json)??throw new InvalidDataException("Human review result missing.");
        var reviews=Directory.EnumerateDirectories(campaign,"review-*").Where(path=>
            File.Exists(Path.Combine(path,"reviewer","scores.csv")) && Hash(Path.Combine(path,"reviewer","scores.csv"))==result.ScoresSha256 &&
            File.Exists(Path.Combine(path,"coordinator","key.json")) && Hash(Path.Combine(path,"coordinator","key.json"))==result.KeySha256 &&
            File.Exists(Path.Combine(path,"reviewer","dialogues.json")) && Hash(Path.Combine(path,"reviewer","dialogues.json"))==result.DialoguesSha256).ToArray();
        if(reviews.Length!=1 || !result.ReviewComplete)throw new InvalidDataException("Human review inputs are missing or changed.");
        return result;
    }

    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static List<string[]> ParseCsv(string text)
    {
        var rows=new List<string[]>();var row=new List<string>();var value=new StringBuilder();var quoted=false;
        for(var i=0;i<text.Length;i++)
        {
            var c=text[i];
            if(quoted){if(c=='"' && i+1<text.Length && text[i+1]=='"'){value.Append('"');i++;}else if(c=='"')quoted=false;else value.Append(c);continue;}
            if(c=='"'){if(value.Length!=0)throw new InvalidDataException("Invalid CSV quote.");quoted=true;}
            else if(c==','){row.Add(value.ToString());value.Clear();}
            else if(c is '\r' or '\n')
            {if(c=='\r' && i+1<text.Length && text[i+1]=='\n')i++;row.Add(value.ToString());value.Clear();if(row.Any(x=>x.Length>0))rows.Add(row.ToArray());row.Clear();}
            else value.Append(c);
        }
        if(quoted)throw new InvalidDataException("Unclosed CSV quote.");
        if(value.Length>0 || row.Count>0){row.Add(value.ToString());rows.Add(row.ToArray());}
        return rows;
    }
}
