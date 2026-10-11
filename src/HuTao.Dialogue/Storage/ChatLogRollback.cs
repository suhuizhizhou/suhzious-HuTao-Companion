using System.Security.Cryptography;
using System.Text.Json;
using HuTao.Dialogue.Core;

namespace HuTao.Dialogue.Storage;

public sealed record ChatLogRollbackReport(string SourceSha256,string OutputSha256,
    ConversationPrivacyStamp Authority,int Retained,int Excluded);

/// <summary>Explicit offline conversion. Caller must obtain the stamp from the stopped authoritative runtime/database.</summary>
public static class ChatLogRollback
{
    public static ChatLogRollbackReport Prepare(string source,string destination,ConversationPrivacyStamp current,string? expectedSourceSha256=null)
    {
        if(string.IsNullOrWhiteSpace(current.Namespace) || current.Epoch<0)throw new ArgumentException("Invalid privacy authority.");
        var bytes=File.ReadAllBytes(source);
        var sourceHash=Convert.ToHexString(SHA256.HashData(bytes));
        if(expectedSourceSha256 is not null && !string.Equals(sourceHash,expectedSourceSha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Chat log changed since review.");
        var json=bytes.AsSpan();if(json.StartsWith(new byte[]{0xef,0xbb,0xbf}))json=json[3..];
        var entries=JsonSerializer.Deserialize<List<ChatEntry>>(json)??throw new InvalidDataException("Missing chat entries.");
        if(entries.Any(e=>e is null || e.Privacy is { } stamp && (stamp.Namespace!=current.Namespace || stamp.Epoch>current.Epoch || stamp.Epoch<0)))
            throw new InvalidDataException("Chat log belongs to a different or newer privacy authority.");
        var retained=entries.Where(e=>ChatLogStore.IsCurrent(e,current)).Select(e=>e with {Privacy=null}).ToArray();
        var output=JsonSerializer.SerializeToUtf8Bytes(retained,new JsonSerializerOptions{WriteIndented=true});
        var path=Path.GetFullPath(destination);
        var temporary=Path.Combine(Path.GetDirectoryName(path)!,".chat-rollback-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {stream.Write(output);stream.Flush(true);}
            File.Move(temporary,path,overwrite:false);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
        return new(sourceHash,Convert.ToHexString(SHA256.HashData(output)),current,retained.Length,entries.Count-retained.Length);
    }
}
