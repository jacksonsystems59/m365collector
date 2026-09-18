using System.Text.Json;
using Microsoft.Data.Sqlite;
using M365Collector.Contracts;
using M365Collector.Core;

namespace M365Collector.Storage;

public sealed class CollectorDatabase(RuntimePaths paths)
{
    private SqliteConnection Open(bool create = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.Database,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, DefaultTimeout = 15, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;";
        command.ExecuteNonQuery();
        return connection;
    }
    public int SchemaVersion()
    { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version"; return Convert.ToInt32(command.ExecuteScalar()); }
    public void Migrate()
    {
        using var db = Open(create: true);
        using (var wal = db.CreateCommand()) { wal.CommandText = "PRAGMA journal_mode=WAL;"; wal.ExecuteNonQuery(); }
        using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(command.ExecuteScalar());
        if (current > ProductInfo.DatabaseSchema) throw new InvalidDataException("Database is newer than this application. Downgrade refused.");
        if (current == 0)
        {
            command.CommandText = """
                CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL, application_version TEXT NOT NULL);
                CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE customers(tenant_id TEXT PRIMARY KEY, payload TEXT NOT NULL, state TEXT NOT NULL, updated_utc TEXT NOT NULL);
                CREATE TABLE enabled_modules(tenant_id TEXT NOT NULL REFERENCES customers(tenant_id), module_id TEXT NOT NULL,
                    enabled INTEGER NOT NULL CHECK(enabled IN (0,1)), configuration_schema INTEGER NOT NULL DEFAULT 1,
                    configuration_json TEXT NOT NULL DEFAULT '{}', PRIMARY KEY(tenant_id,module_id));
                CREATE TABLE collection_jobs(id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id TEXT NOT NULL REFERENCES customers(tenant_id),
                    module_id TEXT NOT NULL, kind TEXT NOT NULL, status TEXT NOT NULL, created_utc TEXT NOT NULL,
                    completed_utc TEXT, result_code TEXT, identity_json TEXT);
                CREATE INDEX ix_jobs_status ON collection_jobs(status,id);
                CREATE TABLE tenant_identity(tenant_id TEXT PRIMARY KEY REFERENCES customers(tenant_id), payload TEXT NOT NULL, collected_utc TEXT NOT NULL);
                CREATE TABLE service_status(id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
                CREATE TABLE application_roles(id TEXT PRIMARY KEY, description TEXT NOT NULL);
                CREATE TABLE application_users(sid TEXT PRIMARY KEY, role_id TEXT NOT NULL REFERENCES application_roles(id));
                INSERT INTO application_roles VALUES('Administrator','Windows administrators in v0.1.0'),('Operator','Reserved'),('ReadOnly','Reserved');
                INSERT INTO schema_migrations VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ','now'), '0.1.0');
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public void InitializeAdministrator(string sid)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO application_users VALUES($sid,'Administrator');";
        command.Parameters.AddWithValue("$sid", sid); command.ExecuteNonQuery();
    }
    public IReadOnlyList<Customer> Customers()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM customers ORDER BY tenant_id";
        using var reader = command.ExecuteReader(); var result = new List<Customer>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<Customer>(reader.GetString(0))!);
        return result;
    }
    public Customer? Find(Guid tenantId) => Customers().SingleOrDefault(c => c.TenantId == tenantId);
    public void SaveDraft(Customer customer)
    {
        if (customer.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(customer.DisplayName)) throw new ArgumentException("Customer name and tenant ID are required.");
        if (customer.Certificate is not null) ValidateCertificateReference(customer.Certificate);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT COUNT(*) FROM collection_jobs WHERE tenant_id=$id AND status IN ('Queued','Running')";
        command.Parameters.AddWithValue("$id", customer.TenantId.ToString("D"));
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidOperationException("Wait for pending collection or connection validation before editing this customer.");
        if (customer.Certificate != null || customer.ClientId != Guid.Empty)
        {
            command.CommandText = "SELECT payload FROM customers WHERE tenant_id<>$id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var existing = JsonSerializer.Deserialize<Customer>(reader.GetString(0))!;
                if ((customer.Certificate != null && existing.Certificate?.Thumbprint.Equals(customer.Certificate.Thumbprint, StringComparison.OrdinalIgnoreCase) == true)
                    || (customer.ClientId != Guid.Empty && existing.ClientId == customer.ClientId))
                    throw new InvalidOperationException("Each tenant requires its own application and certificate.");
            }
        }
        command.CommandText = "INSERT INTO customers VALUES($id,$payload,'Draft',$time) ON CONFLICT(tenant_id) DO UPDATE SET payload=$payload,state='Draft',updated_utc=$time;";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(customer with { State = CustomerState.Draft, ValidatedAt = null }));
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
        command.CommandText = "INSERT OR IGNORE INTO enabled_modules(tenant_id,module_id,enabled) VALUES($id,'tenant-identity',1)"; command.ExecuteNonQuery();
        tx.Commit(); paths.CreateCustomer(customer.TenantId);
    }
    public static void ValidateCertificateReference(CertificateReference reference)
    {
        if (reference.Store != "My" || reference.Location != "LocalMachine" || reference.Thumbprint.Length != 40 || !reference.Thumbprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Use a SHA-1 thumbprint reference to a certificate in LocalMachine/My. Private keys and passwords are never configuration values.");
    }
    public bool ModuleEnabled(Guid tenantId, string moduleId)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT enabled FROM enabled_modules WHERE tenant_id=$id AND module_id=$module";
        command.Parameters.AddWithValue("$id", tenantId.ToString("D")); command.Parameters.AddWithValue("$module", moduleId);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }
    public void SetModule(Guid tenantId, string moduleId, bool enabled)
    {
        if (moduleId != "tenant-identity") throw new NotSupportedException("This module is not implemented in v0.1.0.");
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO enabled_modules(tenant_id,module_id,enabled) VALUES($id,$module,$enabled) ON CONFLICT(tenant_id,module_id) DO UPDATE SET enabled=$enabled";
        command.Parameters.AddWithValue("$id", tenantId.ToString("D")); command.Parameters.AddWithValue("$module", moduleId); command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.ExecuteNonQuery();
    }
    public long Enqueue(Guid tenantId, string kind)
    {
        if (kind is not ("Validate" or "Collect")) throw new ArgumentException("Unknown job kind.");
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", tenantId.ToString("D"));
        command.CommandText = "SELECT id FROM collection_jobs WHERE tenant_id=$id AND status IN ('Queued','Running') LIMIT 1";
        if (command.ExecuteScalar() is long existing) return existing;
        command.CommandText = "INSERT INTO collection_jobs(tenant_id,module_id,kind,status,created_utc) VALUES($id,'tenant-identity',$kind,'Queued',$now); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var id = (long)command.ExecuteScalar()!; tx.Commit(); return id;
    }
    public CollectionJob? GetJob(long id)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT id,tenant_id,module_id,kind,status,result_code,identity_json FROM collection_jobs WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader(); return reader.Read() ? ReadJob(reader) : null;
    }
    public CollectionJob? Claim()
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT id,tenant_id,module_id,kind,status,result_code,identity_json FROM collection_jobs WHERE status='Queued' ORDER BY id LIMIT 1";
        CollectionJob? job; using (var reader = command.ExecuteReader()) job = reader.Read() ? ReadJob(reader) : null;
        if (job is null) return null;
        command.CommandText = "UPDATE collection_jobs SET status='Running' WHERE id=$id"; command.Parameters.AddWithValue("$id", job.Id); command.ExecuteNonQuery(); tx.Commit();
        return job with { Status = "Running" };
    }
    public void RecoverInterruptedJobs()
    { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE collection_jobs SET status='Failed',result_code='ServiceInterrupted' WHERE status='Running'"; command.ExecuteNonQuery(); }
    public void Complete(CollectionJob job, CollectionResult result)
    {
        if (result.Success && (result.Identity is null || result.Identity.TenantId != job.TenantId))
            result = CollectionResult.Failed("TenantMismatch");
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE collection_jobs SET status=$status,completed_utc=$now,result_code=$code,identity_json=$json WHERE id=$job AND status='Running'";
        command.Parameters.AddWithValue("$status", result.Success ? "Succeeded" : "Failed"); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$code", result.Code); command.Parameters.AddWithValue("$json", result.Identity == null ? DBNull.Value : JsonSerializer.Serialize(result.Identity)); command.Parameters.AddWithValue("$job", job.Id);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Job is not running.");
        if (result.Success)
        {
            command.Parameters.AddWithValue("$tenant", job.TenantId.ToString("D"));
            command.CommandText = "INSERT INTO tenant_identity VALUES($tenant,$json,$now) ON CONFLICT(tenant_id) DO UPDATE SET payload=$json,collected_utc=$now"; command.ExecuteNonQuery();
            if (job.Kind == "Validate")
            {
                command.CommandText = "SELECT payload FROM customers WHERE tenant_id=$tenant";
                var customer = JsonSerializer.Deserialize<Customer>((string)command.ExecuteScalar()!)!;
                customer = customer with { DefaultDomain = result.Identity!.DefaultDomain, State = CustomerState.Active, ValidatedAt = DateTimeOffset.UtcNow };
                command.CommandText = "UPDATE customers SET state='Active',payload=$customer,updated_utc=$now WHERE tenant_id=$tenant";
                command.Parameters.AddWithValue("$customer", JsonSerializer.Serialize(customer)); command.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }
    public DateTimeOffset? LastCollection(Guid tenantId)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT MAX(created_utc) FROM collection_jobs WHERE tenant_id=$id"; command.Parameters.AddWithValue("$id", tenantId.ToString("D"));
        return command.ExecuteScalar() is string value ? DateTimeOffset.Parse(value) : null;
    }
    public void SaveHealth(ServiceHealth health)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "INSERT INTO service_status VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET payload=$json";
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(health)); command.ExecuteNonQuery();
    }
    public void Backup(string destination)
    { using var source = Open(); using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString()); target.Open(); source.BackupDatabase(target); }
    private static CollectionJob ReadJob(SqliteDataReader reader) => new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<TenantIdentity>(reader.GetString(6)));
}
