using System.Text.Json;
using HuTao.Foundation.Abstractions;
using HuTao.Knowledge.Rag;

/// <summary>Evaluation-only experiment. Complete textual coverage is not proof of semantic correctness.</summary>
internal sealed class ClaimPartitionReviewer(ILLMProvider inner,IReadOnlyList<StoryAnswerSegment> expected):ILLMProvider
{
    public string Name=>"experimental-claim-partition";
    public async Task<string> CompleteAsync(string systemPrompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)
    {
        const string oldSchema="只返回JSON：{\"segments\":[{\"index\":0,\"support\":\"supported\"}]}。";
        if(!systemPrompt.Contains(oldSchema,StringComparison.Ordinal))throw new InvalidOperationException("Review protocol changed.");
        var prompt=systemPrompt.Replace(oldSchema,
            "只返回JSON：{\"segments\":[{\"index\":0,\"claims\":[{\"text\":\"原文连续片段\",\"support\":\"supported\"}]}]}。"+
            "每段按不同断言或表达拆成1到12个连续片段，text依次拼接必须逐字等于该段Text（含标点），不得遗漏或改写。"+
            "每个片段单独标support，检查隐含前提与跨片段关系；建议中的必须、期限、因果和既定用途同样需要依据。"+
            "允许不预设事实的开放邀请、当前意向和校准未知为subjective。不要输出整段support或解释字段。",StringComparison.Ordinal);
        var raw=await inner.CompleteAsync(prompt,history,ct);
        return Aggregate(raw,expected);
    }

    internal static string Aggregate(string raw,IReadOnlyList<StoryAnswerSegment> expected)
    {
        if(raw.Length>16000)return "{}";
        try
        {
            using var doc=JsonDocument.Parse(raw);
            if(!Fields(doc.RootElement,"segments"))return "{}";
            var rows=doc.RootElement.GetProperty("segments").EnumerateArray().ToArray();
            if(rows.Length!=expected.Count)return "{}";
            var seen=new HashSet<int>();var output=new List<object>();
            foreach(var row in rows)
            {
                if(!Fields(row,"index","claims"))return "{}";
                var index=row.GetProperty("index").GetInt32();
                if(index<0 || index>=expected.Count || !seen.Add(index))return "{}";
                var claims=row.GetProperty("claims").EnumerateArray().ToArray();
                if(claims.Length is <1 or >12)return "{}";
                var text="";var labels=new List<string>();
                foreach(var claim in claims)
                {
                    if(!Fields(claim,"text","support"))return "{}";
                    var part=claim.GetProperty("text").GetString();var label=claim.GetProperty("support").GetString();
                    if(string.IsNullOrEmpty(part) || label is not ("supported" or "subjective" or "unsupported" or "uncertain"))return "{}";
                    text+=part;labels.Add(label);
                }
                if(text!=expected[index].Text)return "{}";
                var support=labels.Contains("unsupported")?"unsupported":labels.Contains("uncertain")?"uncertain":
                    labels.Contains("supported")?"supported":"subjective";
                output.Add(new{index,support});
            }
            return JsonSerializer.Serialize(new{segments=output});
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {return "{}";}
    }
    private static bool Fields(JsonElement value,params string[] names)=>value.ValueKind==JsonValueKind.Object &&
        value.EnumerateObject().Count()==names.Length && names.All(name=>value.EnumerateObject().Count(p=>p.Name==name)==1);
}
