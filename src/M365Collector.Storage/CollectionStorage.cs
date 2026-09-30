using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using M365Collector.Contracts;

namespace M365Collector.Storage;

public sealed partial class CollectorStore
{
    private const string Migration2 = """
        ALTER TABLE ModuleConfiguration ADD COLUMN ScheduleMinutes INTEGER NOT NULL DEFAULT 15;
        ALTER TABLE ModuleConfiguration ADD COLUMN RetentionDays INTEGER NOT NULL DEFAULT 90;
        ALTER TABLE ModuleConfiguration ADD COLUMN RetentionEnabled INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE ModuleConfiguration ADD COLUMN Requested INTEGER NOT NULL DEFAULT 0;
        UPDATE ModuleConfiguration SET ScheduleMinutes=60 WHERE ModuleId='tenant-identity';
        CREATE TABLE ModuleHealth(TenantId TEXT NOT NULL REFERENCES Customers(TenantId),ModuleId TEXT NOT NULL,State TEXT NOT NULL DEFAULT 'NotConfigured',LastAttempt TEXT,LastSuccess TEXT,LastFailure TEXT,Error TEXT,LastAdded INTEGER NOT NULL DEFAULT 0,PermissionStatus TEXT NOT NULL DEFAULT 'Not checked',DependencyStatus TEXT NOT NULL DEFAULT 'Native .NET; no PowerShell modules',PRIMARY KEY(TenantId,ModuleId));
        CREATE TABLE CollectorRuns(Id TEXT PRIMARY KEY,TenantId TEXT NOT NULL REFERENCES Customers(TenantId),ModuleId TEXT NOT NULL,Started TEXT NOT NULL,Ended TEXT,State TEXT NOT NULL,Added INTEGER NOT NULL DEFAULT 0,Error TEXT);
        CREATE INDEX IX_Runs_TenantTime ON CollectorRuns(TenantId,Started DESC);
        CREATE TABLE CollectorCheckpoints(TenantId TEXT NOT NULL REFERENCES Customers(TenantId),ModuleId TEXT NOT NULL,Key TEXT NOT NULL,Value TEXT NOT NULL,PRIMARY KEY(TenantId,ModuleId,Key));
        CREATE TABLE AuditEvents(TenantId TEXT NOT NULL REFERENCES Customers(TenantId),ModuleId TEXT NOT NULL,EventId TEXT NOT NULL,Timestamp TEXT NOT NULL,User TEXT NOT NULL,UserDisplayName TEXT NOT NULL,Workload TEXT NOT NULL,Action TEXT NOT NULL,Object TEXT NOT NULL,Site TEXT NOT NULL,Ip TEXT NOT NULL,Country TEXT NOT NULL,State TEXT NOT NULL,City TEXT NOT NULL,Result TEXT NOT NULL,Application TEXT NOT NULL,Category TEXT NOT NULL,RawJson TEXT NOT NULL,PRIMARY KEY(TenantId,ModuleId,EventId));
        CREATE INDEX IX_Events_Time ON AuditEvents(TenantId,Timestamp DESC);
        CREATE INDEX IX_Events_ModuleTime ON AuditEvents(TenantId,ModuleId,Timestamp DESC);
        CREATE INDEX IX_Events_User ON AuditEvents(TenantId,User,Timestamp);
        CREATE INDEX IX_Events_Ip ON AuditEvents(TenantId,Ip,Timestamp);
        CREATE INDEX IX_Events_Action ON AuditEvents(TenantId,Action,Timestamp);
        CREATE INDEX IX_Events_Workload ON AuditEvents(TenantId,Workload,Timestamp);
        CREATE INDEX IX_Events_Object ON AuditEvents(TenantId,Object,Timestamp);
        CREATE TABLE ProcessedAuditContent(TenantId TEXT NOT NULL REFERENCES Customers(TenantId),ContentType TEXT NOT NULL,ContentId TEXT NOT NULL,Processed TEXT NOT NULL,PRIMARY KEY(TenantId,ContentType,ContentId));
        INSERT INTO ModuleHealth(TenantId,ModuleId,State,LastAttempt,LastSuccess,Error)
        SELECT TenantId,ModuleId,CASE WHEN Status='Succeeded' THEN 'Healthy' ELSE 'Attention' END,LastRun,CASE WHEN Status='Succeeded' THEN LastRun ELSE NULL END,CASE WHEN Status='Succeeded' THEN NULL ELSE Status END FROM CollectionState;
        INSERT OR IGNORE INTO ModuleConfiguration(TenantId,ModuleId,Enabled,ScheduleMinutes)
        SELECT TenantId,'sign-ins',0,15 FROM Customers UNION ALL SELECT TenantId,'directory-changes',0,15 FROM Customers UNION ALL SELECT TenantId,'unified-audit',0,15 FROM Customers;
        INSERT OR IGNORE INTO ModuleHealth(TenantId,ModuleId) SELECT TenantId,ModuleId FROM ModuleConfiguration;
        UPDATE SchemaVersion SET Version=2;
        """;
    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    private static DateTimeOffset? Date(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : DateTimeOffset.Parse(r.GetString(index), CultureInfo.InvariantCulture);
    public void EnsureModules(Guid tenant)
    {
        CollectorCatalog.ValidateTenant(tenant);
        foreach (var module in CollectorCatalog.All)
        {
            Write("INSERT OR IGNORE INTO ModuleConfiguration(TenantId,ModuleId,Enabled,ScheduleMinutes) VALUES($t,$m,0,$s)", ("$t",tenant.ToString()),("$m",module.Id),("$s",module.DefaultMinutes));
            Write("INSERT OR IGNORE INTO ModuleHealth(TenantId,ModuleId) VALUES($t,$m)",("$t",tenant.ToString()),("$m",module.Id));
        }
    }
    public IReadOnlyList<ModuleState> Modules(Guid tenant)
    {
        CollectorCatalog.ValidateTenant(tenant);
        return Read("""
            SELECT c.ModuleId,c.Enabled,c.ScheduleMinutes,c.RetentionDays,c.RetentionEnabled,c.Requested,
            COALESCE(h.State,'NotConfigured'),h.LastAttempt,h.LastSuccess,h.LastFailure,h.Error,
            (SELECT COUNT(*) FROM AuditEvents e WHERE e.TenantId=c.TenantId AND e.ModuleId=c.ModuleId),COALESCE(h.LastAdded,0),COALESCE(h.PermissionStatus,'Not checked'),COALESCE(h.DependencyStatus,'Native .NET; no PowerShell modules')
            FROM ModuleConfiguration c LEFT JOIN ModuleHealth h ON h.TenantId=c.TenantId AND h.ModuleId=c.ModuleId WHERE c.TenantId=$t
            """,r=>new ModuleState(tenant,r.GetString(0),r.GetBoolean(1),r.GetInt32(2),r.GetInt32(3),r.GetBoolean(4),r.GetBoolean(5),!r.GetBoolean(1)?CollectorHealth.Disabled:Enum.Parse<CollectorHealth>(r.GetString(6)),Date(r,7),Date(r,8),Date(r,9),r.IsDBNull(10)?null:r.GetString(10),r.GetInt64(11),r.GetInt64(12),r.GetString(13),r.GetString(14)),("$t",tenant.ToString()));
    }
    public void Configure(Guid tenant,string module,bool enabled,int minutes,int retentionDays,bool purge)
    {
        CollectorCatalog.ValidateTenant(tenant);CollectorCatalog.Get(module);
        if(!CollectorCatalog.Intervals.Contains(minutes)||retentionDays is <1 or >3650)throw new ArgumentException("Choose a supported interval and retention of 1–3650 days.");
        Write("UPDATE ModuleConfiguration SET Enabled=$e,ScheduleMinutes=$s,RetentionDays=$r,RetentionEnabled=$p,Requested=CASE WHEN $e=1 AND Enabled=0 THEN 1 ELSE Requested END WHERE TenantId=$t AND ModuleId=$m",("$e",enabled),("$s",minutes),("$r",retentionDays),("$p",purge),("$t",tenant.ToString()),("$m",module));
    }
    public void RequestRun(Guid tenant,string module)
    {
        CollectorCatalog.ValidateTenant(tenant);CollectorCatalog.Get(module);
        Write("UPDATE ModuleConfiguration SET Requested=1 WHERE TenantId=$t AND ModuleId=$m AND Enabled=1 AND NOT EXISTS(SELECT 1 FROM ModuleHealth WHERE TenantId=$t AND ModuleId=$m AND State='Collecting')",("$t",tenant.ToString()),("$m",module));
    }
    public string StartRun(Guid tenant,string module,DateTimeOffset now)
    {
        CollectorCatalog.ValidateTenant(tenant);CollectorCatalog.Get(module);var id=Guid.NewGuid().ToString();
        using var db=Open();using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE ModuleHealth SET State='Collecting',LastAttempt=$now,Error=NULL,LastAdded=0 WHERE TenantId=$t AND ModuleId=$m AND State<>'Collecting'";Bind(cmd,[("$now",Utc(now)),("$t",tenant.ToString()),("$m",module)]);
        if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Collector is already running or not configured.");
        cmd.CommandText="UPDATE ModuleConfiguration SET Requested=0 WHERE TenantId=$t AND ModuleId=$m; INSERT INTO CollectorRuns(Id,TenantId,ModuleId,Started,State) VALUES($id,$t,$m,$now,'Collecting')";cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();tx.Commit();return id;
    }
    public void FinishRun(Guid tenant,string module,string run,CollectorHealth health,long added,string? error,string permission)
    {
        var now=Utc(DateTimeOffset.UtcNow);using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="UPDATE ModuleHealth SET State=$state,LastSuccess=CASE WHEN $state='Healthy' THEN $now ELSE LastSuccess END,LastFailure=CASE WHEN $state IN ('Failed','Attention','NotConfigured') THEN $now ELSE LastFailure END,Error=$error,LastAdded=$added,PermissionStatus=$permission WHERE TenantId=$t AND ModuleId=$m; UPDATE CollectorRuns SET Ended=$now,State=$state,Added=$added,Error=$error WHERE Id=$id AND TenantId=$t AND ModuleId=$m";
        Bind(cmd,[("$state",health.ToString()),("$now",now),("$error",error),("$added",added),("$permission",permission),("$t",tenant.ToString()),("$m",module),("$id",run)]);cmd.ExecuteNonQuery();tx.Commit();
    }
    public void RecoverInterruptedRuns()
    {
        Write("UPDATE ModuleHealth SET State='Attention',Error='Service stopped during collection. The next run safely resumes with overlap.' WHERE State='Collecting'; UPDATE CollectorRuns SET State='Attention',Ended=$now,Error='Service stopped during collection.' WHERE State='Collecting'",("$now",Utc(DateTimeOffset.UtcNow)));
    }
    public IReadOnlyList<CollectorRun> Runs(Guid tenant,int limit=25)
    {
        CollectorCatalog.ValidateTenant(tenant);return Read("SELECT Id,ModuleId,Started,Ended,State,Added,Error FROM CollectorRuns WHERE TenantId=$t ORDER BY Started DESC LIMIT $limit",r=>new CollectorRun(r.GetString(0),tenant,r.GetString(1),Date(r,2)!.Value,Date(r,3),r.GetString(4),r.GetInt64(5),r.IsDBNull(6)?null:r.GetString(6)),("$t",tenant.ToString()),("$limit",Math.Clamp(limit,1,1000)));
    }
    public string? Checkpoint(Guid tenant,string module,string key)
    {
        CollectorCatalog.ValidateTenant(tenant);CollectorCatalog.Get(module);return Read("SELECT Value FROM CollectorCheckpoints WHERE TenantId=$t AND ModuleId=$m AND Key=$k",r=>r.GetString(0),("$t",tenant.ToString()),("$m",module),("$k",key)).SingleOrDefault();
    }
    public void Checkpoint(Guid tenant,string module,string key,string value)
    {
        CollectorCatalog.ValidateTenant(tenant);CollectorCatalog.Get(module);Write("INSERT INTO CollectorCheckpoints VALUES($t,$m,$k,$v) ON CONFLICT(TenantId,ModuleId,Key) DO UPDATE SET Value=excluded.Value",("$t",tenant.ToString()),("$m",module),("$k",key),("$v",value));
    }
    public bool ContentProcessed(Guid tenant,string type,string id) => Read("SELECT 1 FROM ProcessedAuditContent WHERE TenantId=$t AND ContentType=$type AND ContentId=$id",r=>r.GetInt32(0),("$t",tenant.ToString()),("$type",type),("$id",id)).Count>0;
    public int Persist(Guid tenant,string module,IReadOnlyList<StoredEvent> events,string? contentType=null,string? contentId=null)
    {
        CollectorCatalog.ValidateTenant(tenant);CollectorCatalog.Get(module);
        if(events.Any(e=>e.TenantId!=tenant||e.ModuleId!=module||string.IsNullOrWhiteSpace(e.EventId)))throw new InvalidDataException("Event tenant, module or provider ID is invalid.");
        using var db=Open();using var tx=db.BeginTransaction();var added=0;
        foreach(var e in events)
        {
            using var parsed=JsonDocument.Parse(e.RawJson);using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT OR IGNORE INTO AuditEvents VALUES($t,$m,$id,$time,$user,$name,$work,$action,$object,$site,$ip,$country,$state,$city,$result,$app,$category,$raw)";
            Bind(cmd,[("$t",tenant.ToString()),("$m",module),("$id",e.EventId),("$time",Utc(e.Timestamp)),("$user",e.User),("$name",e.UserDisplayName),("$work",e.Workload),("$action",e.Action),("$object",e.Object),("$site",e.Site),("$ip",e.Ip),("$country",e.Country),("$state",e.State),("$city",e.City),("$result",e.Result),("$app",e.Application),("$category",e.Category),("$raw",e.RawJson)]);added+=cmd.ExecuteNonQuery();
        }
        if(contentId!=null)
        {
            using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT OR IGNORE INTO ProcessedAuditContent VALUES($t,$type,$id,$now)";Bind(cmd,[("$t",tenant.ToString()),("$type",contentType??throw new ArgumentException("Content type required.")),("$id",contentId),("$now",Utc(DateTimeOffset.UtcNow))]);cmd.ExecuteNonQuery();
        }
        tx.Commit();return added;
    }
    private static (string Sql,(string,object?)[] Values) Filter(AuditFilter filter)
    {
        CollectorCatalog.ValidateTenant(filter.TenantId);if(filter.From>=filter.To)throw new ArgumentException("End date must be after start date.");if(filter.ModuleId.Length>0)CollectorCatalog.Get(filter.ModuleId);
        var sql="TenantId=$t AND Timestamp >= $from AND Timestamp < $to";var values=new List<(string,object?)>{("$t",filter.TenantId.ToString()),("$from",Utc(filter.From)),("$to",Utc(filter.To))};
        foreach(var (column,value) in new[]{("ModuleId",filter.ModuleId),("Result",filter.Result)})if(value.Length>0){sql+=$" AND {column}=${column}";values.Add(("$"+column,value));}
        if(filter.Workload=="SharePoint / OneDrive")sql+=" AND Workload IN ('SharePoint','OneDrive')";
        else if(filter.Workload=="General Microsoft 365")sql+=" AND Workload NOT IN ('SharePoint','OneDrive','Exchange','MicrosoftTeams','Teams')";
        else if(filter.Workload=="Teams")sql+=" AND Workload IN ('MicrosoftTeams','Teams')";
        else if(filter.Workload.Length>0){sql+=" AND Workload=$workload";values.Add(("$workload",filter.Workload));}
        foreach(var (column,value) in new[]{("User",filter.User),("Action",filter.Action),("Object",filter.Object),("Site",filter.Site),("Ip",filter.Ip),("Application",filter.Application),("Category",filter.Category)})if(value.Length>0){if(value.Length>512)throw new ArgumentException("Filter is too long.");sql+=$" AND instr(lower({(column=="User"?"User||' '||UserDisplayName":column)}),lower(${column}))>0";values.Add(("$"+column,value));}
        if(filter.Location.Length>0){sql+=" AND instr(lower(Country||' '||State||' '||City),lower($location))>0";values.Add(("$location",filter.Location));}
        return(sql,values.ToArray());
    }
    public long Count(AuditFilter filter){var f=Filter(filter);return Read("SELECT COUNT(*) FROM AuditEvents WHERE "+f.Sql,r=>r.GetInt64(0),f.Values).Single();}
    public IReadOnlyList<StoredEvent> Search(AuditFilter filter,int offset=0,int limit=250)
    {
        if(offset<0||limit is <1 or >1000)throw new ArgumentException("Invalid page size.");var f=Filter(filter);
        return Read("SELECT * FROM AuditEvents WHERE "+f.Sql+" ORDER BY Timestamp DESC,ModuleId,EventId LIMIT $limit OFFSET $offset",r=>new StoredEvent(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),Date(r,3)!.Value,r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10),r.GetString(11),r.GetString(12),r.GetString(13),r.GetString(14),r.GetString(15),r.GetString(16),r.GetString(17)),[..f.Values,("$limit",limit),("$offset",offset)]);
    }
    public int Retain(Guid tenant,string module,DateTimeOffset now)
    {
        var state=Modules(tenant).Single(s=>s.ModuleId==module);if(!state.RetentionEnabled)return 0;
        // Bounded batches avoid long write locks. Feed receipts outlive the provider's seven-day window.
        var removed=Write("DELETE FROM AuditEvents WHERE rowid IN (SELECT rowid FROM AuditEvents WHERE TenantId=$t AND ModuleId=$m AND Timestamp<$cutoff LIMIT 5000)",("$t",tenant.ToString()),("$m",module),("$cutoff",Utc(now.AddDays(-state.RetentionDays))));
        if(module=="unified-audit")Write("DELETE FROM ProcessedAuditContent WHERE TenantId=$t AND Processed<$cutoff",("$t",tenant.ToString()),("$cutoff",Utc(now.AddDays(-Math.Max(14,state.RetentionDays)))));
        return removed;
    }
    public IEnumerable<StoredEvent> StreamEvents(AuditFilter filter)
    {
        var f=Filter(filter);using var db=Open();using var tx=db.BeginTransaction(deferred:true);using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT * FROM AuditEvents WHERE "+f.Sql+" ORDER BY Timestamp DESC,ModuleId,EventId";Bind(cmd,f.Values);using var r=cmd.ExecuteReader();
        while(r.Read())yield return new StoredEvent(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),Date(r,3)!.Value,r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10),r.GetString(11),r.GetString(12),r.GetString(13),r.GetString(14),r.GetString(15),r.GetString(16),r.GetString(17));
    }
    public IReadOnlyList<AuditGroup> Groups(AuditFilter filter,string group)
    {
        var expression=group switch{"User"=>"User","IP / location"=>"Ip||' | '||Country||' | '||State||' | '||City","Country"=>"Country","State"=>"State","City"=>"City",_=>throw new ArgumentException("Unknown grouping.")};
        var f=Filter(filter);return Read($"SELECT {expression},COUNT(*),MAX(Timestamp) FROM AuditEvents WHERE {f.Sql} GROUP BY {expression} ORDER BY COUNT(*) DESC LIMIT 500",r=>new AuditGroup(r.GetString(0),r.GetInt64(1),Date(r,2)!.Value),f.Values);
    }
}
public sealed record AuditGroup(string Group,long Records,DateTimeOffset LatestUtc);
