using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace HuTao.Knowledge.Storage;

/// <summary>Short serialized write transactions; no remote work inside a transaction.</summary>
public sealed class SqliteKnowledgeDatabase
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    public string FilePath { get; }
    private readonly object _writeGate;
    public SqliteKnowledgeDatabase(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _writeGate = Gates.GetOrAdd(FilePath, _ => new());
        lock (_writeGate)
        {
            using var connection = Open();
            Execute(connection, null, "PRAGMA journal_mode=WAL;");
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, checksum TEXT NOT NULL, applied_at INTEGER NOT NULL);");
            var migrations = new[] { Migration("001_initial.sql"), Migration("002_corpus_manifests.sql"), Migration("003_extraction_jobs.sql"), Migration("004_extraction_publications.sql"), Migration("005_outbox_leases.sql"), Migration("006_graph_rebuilds.sql"), Migration("007_turn_ingestion.sql"), Migration("008_recovery_gate.sql"), Migration("009_database_identity.sql"), Migration("010_recovery_adoptions.sql"), Migration("011_namespace_retirement.sql") };
            var applied = new HashSet<int>();
            using var read = Command(connection, transaction, "SELECT version,checksum FROM schema_migrations ORDER BY version");
            using (var rows = read.ExecuteReader())
                while (rows.Read())
                {
                    var version=rows.GetInt32(0);
                    if (version<1 || version>migrations.Length || rows.GetString(1) != Hash(migrations[version-1]))
                        throw new InvalidDataException("Unsupported schema version or migration checksum.");
                    applied.Add(version);
                }
            if(applied.Count>0 && Enumerable.Range(1,applied.Max()).Any(v=>!applied.Contains(v)))
                throw new InvalidDataException("Non-contiguous migration history.");
            for(var version=1;version<=migrations.Length;version++)
            {
                if(applied.Contains(version))continue;
                var sql=migrations[version-1];
                Execute(connection, transaction, sql);
                Execute(connection, transaction, "INSERT INTO schema_migrations VALUES(@version,@hash,@time)", ("@version",version),("@hash", Hash(sql)), ("@time", Now()));
            }
            transaction.Commit();
        }
    }
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = FilePath, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true, DefaultTimeout = 2, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA busy_timeout=2000;");
        Execute(connection, null, "PRAGMA secure_delete=ON;");
        return connection;
    }
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            ct.ThrowIfCancellationRequested();
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var value = action(connection, transaction);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return value;
        }
    }
    /// <summary>
    /// Runs synchronous retrieval on its own read-only connection. SQLite's VM progress hook
    /// interrupts running queries; Command.Cancel is a no-op in Microsoft.Data.Sqlite.
    /// A native read cursor acquires and holds the snapshot before managed commands run,
    /// so lock acquisition uses our deadline instead of the provider's whole-second retries.
    /// </summary>
    public T Read<T>(Func<SqliteConnection,T> action,CancellationToken ct=default,TimeSpan? duration=null)
    {
        var limit=duration??TimeSpan.FromMilliseconds(300);
        ct.ThrowIfCancellationRequested();
        if(limit<=TimeSpan.Zero)throw new TimeoutException("SQLite read budget exhausted.");
        var clock=Stopwatch.StartNew();
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder
        {DataSource=FilePath,Mode=SqliteOpenMode.ReadOnly,ForeignKeys=true,DefaultTimeout=1,Pooling=false}.ToString());
        void Check()
        {
            ct.ThrowIfCancellationRequested();
            if(clock.Elapsed>=limit)throw new TimeoutException("SQLite read budget exhausted.");
        }
        var progressInstalled=false;
        try
        {
            connection.Open();
            Check();
            SQLitePCL.raw.sqlite3_busy_timeout(connection.Handle,0);
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle,1000,_=>ct.IsCancellationRequested || clock.Elapsed>=limit?1:0,null);
            progressInstalled=true;
            using var snapshot=AcquireReadSnapshot(connection,Check);
            var result=action(connection);
            Check();
            return result;
        }
        catch(SqliteException)
        {
            Check();
            throw;
        }
        finally
        {
            // No timer, worker, or cross-connection interrupt outlives this read.
            if(progressInstalled)SQLitePCL.raw.sqlite3_progress_handler(connection.Handle,0,null,null);
        }
    }
    private static SQLitePCL.sqlite3_stmt AcquireReadSnapshot(SqliteConnection connection,Action check)
    {
        SQLitePCL.sqlite3_stmt? statement=null;
        try
        {
            while(true)
            {
                check();
                var result=SQLitePCL.raw.sqlite3_prepare_v2(connection.Handle,"SELECT count(*) FROM sqlite_schema",out statement);
                if(result==SQLitePCL.raw.SQLITE_OK)break;
                statement?.Dispose();statement=null;
                if(result is not (SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED))
                    throw new SqliteException("Cannot prepare read snapshot.",result);
                Thread.Sleep(5);
            }
            while(true)
            {
                check();
                var result=SQLitePCL.raw.sqlite3_step(statement);
                if(result==SQLitePCL.raw.SQLITE_ROW)
                {
                    check();
                    // Keep the cursor on its row: finalizing it would release the implicit
                    // read transaction and allow an exclusive writer to race the real query.
                    var acquired=statement;statement=null;return acquired;
                }
                if(result is not (SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED))
                    throw new SqliteException("Cannot acquire read snapshot.",result);
                SQLitePCL.raw.sqlite3_reset(statement);
                Thread.Sleep(5);
            }
        }
        finally{statement?.Dispose();}
    }
    public void EnsureNamespace(string space)
    {
        if (!space.StartsWith("corpus:", StringComparison.Ordinal) && !space.StartsWith("memory:", StringComparison.Ordinal))
            throw new ArgumentException("Invalid namespace.");
        Write((c, t) => {
            Execute(c, t, "INSERT OR IGNORE INTO namespaces(namespace_id,domain) VALUES(@n,@d)", ("@n",space),("@d",space.Split(':')[0]));
            Execute(c, t, "INSERT OR IGNORE INTO namespace_state(namespace_id) VALUES(@n)", ("@n",space));
            return 0;
        });
    }
    public void Backup(string destination)
    {
        var path = Path.GetFullPath(destination);
        if (File.Exists(path) || string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Backup destination must be new.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var source = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        target.Open(); source.BackupDatabase(target);
    }
    internal static SqliteCommand Command(SqliteConnection c, SqliteTransaction? t, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    internal static int Execute(SqliteConnection c, SqliteTransaction? t, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(c,t,sql,parameters); return command.ExecuteNonQuery(); }
    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Migration(string name)
    {
        using var stream = typeof(SqliteKnowledgeDatabase).Assembly.GetManifestResourceStream("HuTao.Knowledge.Storage.Migrations."+name)
            ?? throw new InvalidOperationException("Embedded migration missing.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
