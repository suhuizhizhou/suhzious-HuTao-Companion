using System.Text.Json;

namespace HuTao.Dialogue.Runtime;

public enum KnowledgeRuntimeMode {Legacy,Shadow,Sql,SqlGraph}
public sealed record KnowledgeRuntimeOptions(KnowledgeRuntimeMode Mode,string DatabasePath,string UserId="local",
    IReadOnlyDictionary<string,string>? CorpusNamespaces=null,string? GraphUri=null,string? GraphUser=null,string? GraphPassword=null)
{
    public static KnowledgeRuntimeOptions FromEnvironment(string root)
    {
        var mode=(Environment.GetEnvironmentVariable("HU_TAO_KNOWLEDGE_MODE")??"legacy").Trim().ToLowerInvariant() switch
        {"legacy"=>KnowledgeRuntimeMode.Legacy,"shadow"=>KnowledgeRuntimeMode.Shadow,"sql"=>KnowledgeRuntimeMode.Sql,"sql-graph"=>KnowledgeRuntimeMode.SqlGraph,_=>throw new ArgumentException("Unknown knowledge runtime mode.")};
        var map=Environment.GetEnvironmentVariable("HU_TAO_KNOWLEDGE_CORPORA");
        return new(mode,Path.GetFullPath(Environment.GetEnvironmentVariable("HU_TAO_KNOWLEDGE_DB")??Path.Combine(root,"data","knowledge","knowledge.db")),
            Environment.GetEnvironmentVariable("HU_TAO_KNOWLEDGE_USER")??"local",
            string.IsNullOrWhiteSpace(map)?null:JsonSerializer.Deserialize<Dictionary<string,string>>(map),
            Environment.GetEnvironmentVariable("HU_TAO_KNOWLEDGE_GRAPH_URI"),Environment.GetEnvironmentVariable("HU_TAO_KNOWLEDGE_GRAPH_USER"),
            Environment.GetEnvironmentVariable("HU_TAO_KNOWLEDGE_GRAPH_PASSWORD"));
    }
}
