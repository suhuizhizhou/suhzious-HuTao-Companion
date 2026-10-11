using HuTao.Knowledge.Retrieval;
using HuTao.Persona;

namespace HuTao.Dialogue.Runtime;

/// <summary>Shared host/rollback mapping. Every memory scope owns an independent chat log.</summary>
public sealed record KnowledgeHostPaths(string Authority,string Memory,string Archive,string? ImportantItems,string? ChatLog)
{
    public static KnowledgeHostPaths Resolve(string root,CharacterDefinition character,string user,string? scope=null)
    {
        scope=string.IsNullOrWhiteSpace(scope)?"desktop":scope;
        _=KnowledgeAccessContext.ForSession(user,character.Id,scope,[]);
        root=Path.GetFullPath(root);
        var suffix=scope=="desktop"?"":"_"+scope;
        var memory=Path.Combine(root,"data",$"memory_{character.Id}{suffix}.json");
        string? important=null;
        if(!string.IsNullOrWhiteSpace(character.ImportantMemoryFile))
        {
            important=Path.GetFullPath(Path.Combine(root,character.ImportantMemoryFile));
            if(scope!="desktop")important=Path.Combine(Path.GetDirectoryName(important)!,Path.GetFileNameWithoutExtension(important)+suffix+Path.GetExtension(important));
        }
        var paths=new KnowledgeHostPaths(Path.Combine(root,"data","knowledge",$"authority-{user}-{character.Id}-{scope}.json"),
            memory,Path.ChangeExtension(memory,null)+".archive.jsonl",important,
            Path.Combine(root,"data",$"chat_log_{character.Id}{suffix}.json"));
        var targets=new[]{paths.Authority,paths.Memory,paths.Archive,paths.ImportantItems,paths.ChatLog,
            paths.Authority+".lock",paths.Authority+".legacy-live",paths.Authority+".sql-live",
            paths.Authority+".rollback-pending.json",paths.Authority+".legacy-authority.json",paths.Authority+".retired-sql.json"}
            .Where(path=>path is not null).Select(path=>Path.GetFullPath(path!)).ToArray();
        var comparison=OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
        if(targets.Distinct(comparison).Count()!=targets.Length)
            throw new InvalidOperationException("Host memory, archive, important items, chat log, and authority paths must be distinct.");
        return paths;
    }
}
