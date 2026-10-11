using System.Text.Json;
using HuTao.Knowledge.Rag;

namespace HuTao.Dialogue.Core;

public static class ReactTaskPlanValidator
{
    public static ReactSubTask Parse(JsonElement row,int index)
    {
        string[] allowed=["id","query","required_facts","depends_on","strategy"];
        if(row.ValueKind!=JsonValueKind.Object || row.EnumerateObject().Any(p=>!allowed.Contains(p.Name)) ||
            row.EnumerateObject().Select(p=>p.Name).Distinct().Count()!=row.EnumerateObject().Count())
            throw new InvalidDataException("Unknown plan field.");
        RetrievalStrategy? strategy=null;
        if(row.TryGetProperty("strategy",out var arm))
        {
            if(arm.ValueKind!=JsonValueKind.String || !RetrievalStrategyCatalog.TryParse(arm.GetString(),out var parsed))
                throw new InvalidDataException("Unknown named strategy.");
            strategy=parsed;
        }
        return new(row.TryGetProperty("id",out var id)?id.GetString()??"":$"t{index+1}",row.GetProperty("query").GetString()??"",
            row.TryGetProperty("required_facts",out var facts)?facts.EnumerateArray().Select(x=>x.GetString()??"").ToArray():[],
            row.TryGetProperty("depends_on",out var depends)?depends.EnumerateArray().Select(x=>x.GetString()??"").ToArray():[],strategy);
    }

    public static void Validate(IReadOnlyList<ReactSubTask> tasks)
    {
        if(tasks.Count>4 || tasks.Select(t=>t.Id).Distinct(StringComparer.Ordinal).Count()!=tasks.Count ||
            tasks.Any(t=>string.IsNullOrWhiteSpace(t.Id) || t.Id.Length>40 || string.IsNullOrWhiteSpace(t.Query) || t.Query.Length>4000 ||
                t.RequiredFacts.Count>8 || t.RequiredFacts.Any(f=>string.IsNullOrWhiteSpace(f) || f.Length>200) ||
                t.DependsOn.Count>3 || t.Strategy is not null && !Enum.IsDefined(t.Strategy.Value)))
            throw new InvalidDataException("Invalid bounded task plan.");
        var byId=tasks.ToDictionary(t=>t.Id,StringComparer.Ordinal);
        int Depth(string id,HashSet<string> visiting)
        {
            if(!byId.TryGetValue(id,out var task) || !visiting.Add(id))throw new InvalidDataException("Unknown or cyclic dependency.");
            return 1+task.DependsOn.Select(d=>Depth(d,new(visiting,StringComparer.Ordinal))).DefaultIfEmpty(0).Max();
        }
        foreach(var task in tasks)if(Depth(task.Id,[])>2)throw new InvalidDataException("Task dependency depth exceeds two.");
    }
}
