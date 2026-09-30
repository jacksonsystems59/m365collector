using Microsoft.Data.Sqlite;
using M365Collector.Contracts;
using M365Collector.Core;
using System.Text.Json;

namespace M365Collector.Storage;

public sealed partial class CollectorStore : ICustomerRepository, IAuditStore
{
    public const int CurrentSchema = 2;
    private readonly string path;
    public CollectorStore(string path) { this.path = path; }
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 15 }.ToString());
        connection.Open(); using var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;"; pragma.ExecuteNonQuery(); return connection;
    }
    public void Migrate(Action<SqliteConnection, SqliteTransaction>? beforeCommit = null)
    {
        using var db = Open(); using var transaction = db.BeginTransaction();
        Execute(db, transaction, "CREATE TABLE IF NOT EXISTS SchemaVersion(Version INTEGER NOT NULL);");
        using var version = db.CreateCommand(); version.Transaction = transaction; version.CommandText = "SELECT COALESCE(MAX(Version),0) FROM SchemaVersion";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current > CurrentSchema) throw new InvalidDataException("Database belongs to a newer version.");
        if (current == 0)
        {
            Execute(db, transaction, """
                CREATE TABLE Settings(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE TABLE Users(Name TEXT PRIMARY KEY COLLATE NOCASE, Hash TEXT NOT NULL, Role INTEGER NOT NULL CHECK(Role BETWEEN 0 AND 2), Failures INTEGER NOT NULL DEFAULT 0, LockedUntil TEXT);
                CREATE TABLE Customers(TenantId TEXT PRIMARY KEY, Name TEXT NOT NULL, Payload TEXT NOT NULL);
                CREATE TABLE ModuleConfiguration(TenantId TEXT NOT NULL REFERENCES Customers(TenantId), ModuleId TEXT NOT NULL, Enabled INTEGER NOT NULL, PRIMARY KEY(TenantId,ModuleId));
                CREATE TABLE CollectionState(TenantId TEXT NOT NULL REFERENCES Customers(TenantId), ModuleId TEXT NOT NULL, LastRun TEXT, Status TEXT NOT NULL, PRIMARY KEY(TenantId,ModuleId));
                CREATE TABLE ConnectionRequests(Id TEXT PRIMARY KEY, Payload TEXT NOT NULL, State TEXT NOT NULL, Error TEXT, Created TEXT NOT NULL);
                CREATE TABLE UpdateHistory(Id INTEGER PRIMARY KEY, Time TEXT NOT NULL, Version TEXT NOT NULL, Result TEXT NOT NULL);
                INSERT INTO SchemaVersion VALUES(1);
                """);
        }
        if (current < 2) Execute(db, transaction, Migration2);
        beforeCommit?.Invoke(db, transaction); transaction.Commit();
        using var journal=db.CreateCommand();journal.CommandText="PRAGMA journal_mode=WAL";journal.ExecuteScalar();
    }
    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql) { using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private static void Bind(SqliteCommand cmd, (string, object?)[] values) { foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value); }
    public int Write(string sql, params (string, object?)[] values) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; Bind(cmd, values); return cmd.ExecuteNonQuery(); }
    public List<T> Read<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] values)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; Bind(cmd, values); using var reader = cmd.ExecuteReader(); var result = new List<T>(); while (reader.Read()) result.Add(map(reader)); return result;
    }
    public void Verify(bool allowNewerSchema = false)
    {
        var health = Read("PRAGMA quick_check", r => r.GetString(0));
        var schema = Read("SELECT MAX(Version) FROM SchemaVersion", r => r.GetInt32(0)).Single();
        if (health.Count != 1 || health[0] != "ok" || (allowNewerSchema ? schema < CurrentSchema : schema != CurrentSchema)) throw new InvalidDataException("Database health verification failed.");
    }
    public void VerifyIntegrity()
    {
        var check=Read("PRAGMA quick_check",r=>r.GetString(0));
        if(check.Count!=1||check[0]!="ok"||Read("SELECT MAX(Version) FROM SchemaVersion",r=>r.GetInt32(0)).Single()<1)throw new InvalidDataException("Database integrity check failed.");
    }
    public string? Setting(string key) => Read("SELECT Value FROM Settings WHERE Key=$key", r => r.GetString(0), ("$key", key)).SingleOrDefault();
    public void Setting(string key, string value) => Write("INSERT INTO Settings VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", ("$key", key), ("$value", value));
    public IReadOnlyList<Customer> GetCustomers() => Read("SELECT Payload FROM Customers ORDER BY Name", r => JsonSerializer.Deserialize<Customer>(r.GetString(0))!);
    public void SaveCustomer(Customer customer)
    {
        if (customer.Identity?.TenantId != customer.TenantId || customer.TenantId == Guid.Empty) throw new InvalidDataException("A verified matching tenant identity is required.");
        Write("INSERT INTO Customers VALUES($id,$name,$payload) ON CONFLICT(TenantId) DO UPDATE SET Name=excluded.Name,Payload=excluded.Payload", ("$id", customer.TenantId.ToString()), ("$name", customer.Name), ("$payload", JsonSerializer.Serialize(customer)));
        Write("INSERT OR IGNORE INTO ModuleConfiguration(TenantId,ModuleId,Enabled,ScheduleMinutes) VALUES($id,'tenant-identity',1,60)", ("$id", customer.TenantId.ToString()));
        EnsureModules(customer.TenantId);
    }
    public Guid RequestConnection(Customer customer)
    {
        var id = Guid.NewGuid(); Write("INSERT INTO ConnectionRequests VALUES($id,$payload,'Pending',NULL,$created)", ("$id", id.ToString()), ("$payload", JsonSerializer.Serialize(customer)), ("$created", DateTimeOffset.UtcNow.ToString("O"))); return id;
    }
    public IReadOnlyList<ConnectionRequest> Requests() => Read("SELECT Id,Payload,State,Error FROM ConnectionRequests", r => new ConnectionRequest(Guid.Parse(r.GetString(0)), JsonSerializer.Deserialize<Customer>(r.GetString(1))!, r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
    public void FinishRequest(Guid id, Customer customer, string? error) => Write("UPDATE ConnectionRequests SET Payload=$payload,State=$state,Error=$error WHERE Id=$id", ("$payload", JsonSerializer.Serialize(customer)), ("$state", error == null ? "Verified" : "Failed"), ("$error", error), ("$id", id.ToString()));
    public void CollectionResult(Guid tenant, string status) => Write("INSERT INTO CollectionState VALUES($id,'tenant-identity',$now,$status) ON CONFLICT(TenantId,ModuleId) DO UPDATE SET LastRun=excluded.LastRun,Status=excluded.Status", ("$id", tenant.ToString()), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$status", status));
    public void UpdateHistory(string version, string result) => Write("INSERT INTO UpdateHistory(Time,Version,Result) VALUES($now,$version,$result)", ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$version", version), ("$result", result));
    public void Backup(string destination)
    {
        using var source = Open(); using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString()); target.Open(); source.BackupDatabase(target);
    }
}
