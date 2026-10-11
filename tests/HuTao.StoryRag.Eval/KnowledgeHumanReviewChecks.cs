using System.Text.Json;
using System.Text.Json.Serialization;

internal static class KnowledgeHumanReviewChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var results=new List<CheckResult>();void Check(string name,bool passed)=>results.Add(new("human_review_"+name,passed,"isolated synthetic review; not a human score"));
        var root=Path.Combine(Path.GetTempPath(),"hutao-human-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var json=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,PropertyNameCaseInsensitive=true,WriteIndented=true,
            Converters={new JsonStringEnumConverter()}};
        try
        {
            var review=Path.Combine(root,"review-synthetic");var reviewer=Path.Combine(review,"reviewer");var coordinator=Path.Combine(review,"coordinator");
            Directory.CreateDirectory(reviewer);Directory.CreateDirectory(coordinator);
            await File.WriteAllTextAsync(Path.Combine(root,"manifest.json"),JsonSerializer.Serialize(new{batches=new[]{new{id="synthetic"}}},json));
            await File.WriteAllTextAsync(Path.Combine(root,"synthetic.receipt.json"),"{}");
            var cohorts=(from groupName in new[]{"A","B","C","D"} from partitionName in new[]{"graph-candidate","non-graph-control"}
                select new{sample_id=groupName+"-"+partitionName,Group=groupName,Partition=partitionName}).ToArray();
            await File.WriteAllTextAsync(Path.Combine(coordinator,"key.json"),JsonSerializer.Serialize(cohorts,json));
            await File.WriteAllTextAsync(Path.Combine(reviewer,"dialogues.json"),JsonSerializer.Serialize(cohorts.Select(x=>new{x.sample_id}),json));
            var header="sample_id,reviewer_id,current_response,useful_specificity,character_naturalness,uncertainty_restraint,hallucination,notes";
            await File.WriteAllLinesAsync(Path.Combine(reviewer,"scores.csv"),new[]{"\uFEFF"+header}.Concat(cohorts.Select(x=>$"{x.sample_id},human-fixture,5,5,5,5,0,synthetic")));
            using var output=new StringWriter();
            await KnowledgeHumanReview.RunAsync(root,review,json,output);
            var result=KnowledgeHumanReview.ReadVerified(root,json);
            Check("complete_scores_are_hashed_and_auditable",result is {Samples:8,Reviewers:1,ReviewComplete:true,ConversationQualityPassed:true,GraphDefaultPassed:false});
            File.AppendAllText(Path.Combine(reviewer,"scores.csv"),Environment.NewLine+"tampered");
            var tamperRejected=false;try{KnowledgeHumanReview.ReadVerified(root,json);}catch(InvalidDataException){tamperRejected=true;}
            Check("changed_scores_invalidate_recorded_review",tamperRejected);

            var incompleteRoot=Path.Combine(Path.GetTempPath(),"hutao-human-review-incomplete-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(incompleteRoot);
            try
            {
                var incompleteReview=Path.Combine(incompleteRoot,"review-synthetic");var incompleteReviewer=Path.Combine(incompleteReview,"reviewer");var incompleteCoordinator=Path.Combine(incompleteReview,"coordinator");
                Directory.CreateDirectory(incompleteReviewer);Directory.CreateDirectory(incompleteCoordinator);
                await File.WriteAllTextAsync(Path.Combine(incompleteRoot,"manifest.json"),JsonSerializer.Serialize(new{batches=new[]{new{id="synthetic"}}},json));
                await File.WriteAllTextAsync(Path.Combine(incompleteRoot,"synthetic.receipt.json"),"{}");
                await File.WriteAllTextAsync(Path.Combine(incompleteCoordinator,"key.json"),JsonSerializer.Serialize(cohorts,json));
                await File.WriteAllTextAsync(Path.Combine(incompleteReviewer,"dialogues.json"),JsonSerializer.Serialize(cohorts.Select(x=>new{x.sample_id}),json));
                await File.WriteAllLinesAsync(Path.Combine(incompleteReviewer,"scores.csv"),new[]{header}.Concat(cohorts.Skip(1).Select(x=>$"{x.sample_id},human-fixture,5,5,5,5,0,synthetic")));
                var incompleteRejected=false;try{await KnowledgeHumanReview.RunAsync(incompleteRoot,incompleteReview,json,new StringWriter());}catch(InvalidDataException){incompleteRejected=true;}
                Check("missing_sample_is_rejected_without_result",incompleteRejected && !File.Exists(Path.Combine(incompleteRoot,"human-review.json")));
            }
            finally{if(Directory.Exists(incompleteRoot))Directory.Delete(incompleteRoot,true);}
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
        return results;
    }
}
