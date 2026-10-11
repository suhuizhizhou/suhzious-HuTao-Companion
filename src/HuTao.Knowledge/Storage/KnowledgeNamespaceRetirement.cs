using Microsoft.Data.Sqlite;

namespace HuTao.Knowledge.Storage;

/// <summary>Called by the host while holding activation exclusion and an immediate transaction.</summary>
public static class KnowledgeNamespaceRetirement
{
    public static void DemandMatching(SqliteConnection connection,SqliteTransaction transaction,string space,string installation)
    {
        using var command=SqliteKnowledgeDatabase.Command(connection,transaction,
            "SELECT installation_id FROM namespace_retirements WHERE namespace_id=@n",("@n",space));
        var existing=command.ExecuteScalar();
        if(existing is not null && !string.Equals(existing as string,installation,StringComparison.Ordinal))
            throw new InvalidOperationException("Namespace retired by another installation.");
    }
    public static void Retire(SqliteConnection connection,SqliteTransaction transaction,string space,string installation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installation);
        DemandMatching(connection,transaction,space,installation);
        SqliteKnowledgeDatabase.Execute(connection,transaction,
            "INSERT OR IGNORE INTO namespace_retirements(namespace_id,installation_id) VALUES(@n,@i)",("@n",space),("@i",installation));
    }
    public static void Restore(SqliteConnection connection,SqliteTransaction transaction,string space,string installation)
    {
        DemandMatching(connection,transaction,space,installation);
        SqliteKnowledgeDatabase.Execute(connection,transaction,
            "DELETE FROM namespace_retirements WHERE namespace_id=@n AND installation_id=@i",("@n",space),("@i",installation));
    }
}
