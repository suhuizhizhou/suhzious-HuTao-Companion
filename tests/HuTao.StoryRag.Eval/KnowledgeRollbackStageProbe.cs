using HuTao.Dialogue.Runtime;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;

internal static class KnowledgeRollbackStageProbe
{
    public static int Run(string[] args)
    {
        if(args.Length!=13 || args[0] is not ("install" or "complete"))return 64;
        var checkpoint=args[1];var signal=Path.GetFullPath(args[2]);
        var paths=new KnowledgeHostPaths(Path.GetFullPath(args[3]),Path.GetFullPath(args[4]),Path.GetFullPath(args[5]),
            args[6]=="-"?null:Path.GetFullPath(args[6]),Path.GetFullPath(args[7]));
        var database=new SqliteKnowledgeDatabase(Path.GetFullPath(args[8]));
        var access=KnowledgeAccessContext.ForSession(args[9],args[10],args[11],[]);
        var prepared=Path.GetFullPath(args[12]);
        void Pause(string phase)
        {
            if(phase!=checkpoint)return;
            using(var ready=new FileStream(signal,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
            {using var writer=new StreamWriter(ready);writer.Write(phase);writer.Flush();ready.Flush(true);}
            Thread.Sleep(Timeout.Infinite);
        }
        if(args[0]=="install")KnowledgeRollbackInstallation.InstallFiles(paths,database,access,prepared,Pause);
        else KnowledgeRollbackInstallation.Complete(paths,database,access,prepared,Pause);
        return 0;
    }
}
