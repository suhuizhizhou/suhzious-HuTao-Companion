using Microsoft.Data.Sqlite;

internal static class KnowledgeLockProcessProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        if(args.Length is not (3 or 4))return 64;
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource=Path.GetFullPath(args[1]),Mode=SqliteOpenMode.ReadWrite,Pooling=false,DefaultTimeout=1}.ToString());
        connection.Open();
        try
        {
            using var transaction=connection.BeginTransaction(deferred:false);
            using var command=connection.CreateCommand();command.Transaction=transaction;
            if(args[0]=="retired-write")
            {
                if(args.Length!=4)return 64;
                // Require the dedicated synthetic fixture marker before touching scoped state.
                command.CommandText="SELECT count(*) FROM rollback_lock_probe WHERE singleton=1";
                if(Convert.ToInt32(command.ExecuteScalar())!=1)return 65;
                command.CommandText="UPDATE namespace_state SET commit_seq=commit_seq+1 WHERE namespace_id=@n";
                command.Parameters.AddWithValue("@n",args[3]);
                if(command.ExecuteNonQuery()!=1)return 65;
                transaction.Commit();return 0;
            }
            // Dedicated synthetic fixture table: this probe cannot update production memory.
            command.CommandText="UPDATE rollback_lock_probe SET value=value+1 WHERE singleton=1";
            command.ExecuteNonQuery();
            if(args[0]=="hold")
            {
                File.WriteAllText(args[2]+".ready","ready");
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while(!File.Exists(args[2]+".release"))await Task.Delay(20,timeout.Token);
            }
            else if(args[0]!="write")return 64;
            transaction.Commit();return 0;
        }
        catch(SqliteException ex) when(ex.SqliteErrorCode is 5 or 6){return 5;}
        catch(SqliteException ex) when(ex.SqliteErrorCode==19 && ex.Message.Contains("Namespace SQL authority retired")){return 19;}
    }
}
