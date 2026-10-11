using System.Text.Json;
using HuTao.Knowledge.Import;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using HuTao.Persona;

namespace HuTao.Dialogue.Runtime;

/// <summary>Explicit local administration, never exposed as a conversation tool.</summary>
public static class KnowledgeRollbackCommand
{
    public static async Task<int> RunAsync(string[] args,TextWriter output,Func<string,CharacterDefinition?>? resolveCharacter=null)
    {
        try
        {
            if(args.Length==0 || args[0]=="help")
            {
                await output.WriteLineAsync("knowledge-rollback create|prepare|install|complete|restore --root HOST_ROOT --db DATABASE --user USER --persona CHARACTER_ID [--scope desktop] [--package ZIP] [--prepared DIRECTORY]");
                return 0;
            }
            var action=args[0];
            if(action is not ("create" or "prepare" or "install" or "complete" or "restore"))throw new ArgumentException("Unknown action.");
            var allowed=new HashSet<string>(["--root","--db","--user","--persona","--scope"],StringComparer.Ordinal);
            if(action is "create" or "prepare")allowed.Add("--package");
            if(action!="create")allowed.Add("--prepared");
            var options=new Dictionary<string,string>(StringComparer.Ordinal);
            for(var i=1;i<args.Length;i+=2)
                if(i+1>=args.Length || !allowed.Contains(args[i]) || !options.TryAdd(args[i],args[i+1]))throw new ArgumentException("Invalid or duplicate option.");
            string Required(string name)=>options.TryGetValue(name,out var value)&&!string.IsNullOrWhiteSpace(value)?value:throw new ArgumentException("Missing option.");
            var root=Path.GetFullPath(Required("--root"));var file=Path.GetFullPath(Required("--db"));
            if(!Directory.Exists(root) || !File.Exists(file))throw new FileNotFoundException("Existing host and database required.");
            var character=(resolveCharacter??(id=>CharacterCatalog.Find(id)))(Required("--persona"))??throw new ArgumentException("Unknown host character.");
            var scope=options.GetValueOrDefault("--scope","desktop");
            var user=Required("--user");var access=KnowledgeAccessContext.ForSession(user,character.Id,scope,[]);
            var paths=KnowledgeHostPaths.Resolve(root,character,user,scope);
            // Validate the full command before opening/migrating the existing database.
            var package=action is "create" or "prepare"?Path.GetFullPath(Required("--package")):null;
            var prepared=action!="create"?Path.GetFullPath(Required("--prepared")):null;
            var db=new SqliteKnowledgeDatabase(file);
            object result;
            if(action=="create")result=MemoryRollbackPackage.Create(db,access,package!);
            else if(action=="prepare")result=KnowledgeRollbackCoordinator.Prepare(paths.Authority,db,access,package!,prepared!,paths.ChatLog);
            else
            {
                if(action=="install")KnowledgeRollbackInstallation.InstallFiles(paths,db,access,prepared!);
                else if(action=="complete")KnowledgeRollbackInstallation.Complete(paths,db,access,prepared!);
                else KnowledgeRollbackInstallation.RestoreFiles(paths,db,access,prepared!);
                result=new {Action=action,Namespace=access.MemoryNamespace,Prepared=prepared,
                    Pending=File.Exists(paths.Authority+".rollback-pending.json"),LegacyAuthority=File.Exists(paths.Authority+".legacy-authority.json")};
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            // Errors may originate in private documents or paths; do not print exception bodies.
            await output.WriteLineAsync("Knowledge rollback failed: "+ex.GetType().Name);
            return 1;
        }
    }
}
