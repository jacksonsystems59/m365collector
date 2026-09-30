using System.Net;
using System.Text.Json;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Entra;
using M365Collector.Modules.SignInActivity;
using M365Collector.Modules.UnifiedAudit;
using M365Collector.Storage;
using Xunit;
namespace M365Collector.Tests;

public class CollectionTests
{
    static Customer Customer(CollectorStore store) { var id=Guid.NewGuid();var c=new Customer(id,"Example",Guid.NewGuid(),"test"){Identity=new(id,"Example",[],DateTimeOffset.UtcNow)};store.SaveCustomer(c);return c; }
    static StoredEvent Event(Guid tenant,string id="event")=>new(tenant,"sign-ins",id,DateTimeOffset.UtcNow,"person@example.test","Example Person","Entra","SignIn","Resource","","192.0.2.1","GB","England","London","Failed","Example","Browser","{\"id\":\"event\"}");
    static AuditFilter Filter(Guid id)=>new(id,DateTimeOffset.UtcNow.AddYears(-1),DateTimeOffset.UtcNow.AddDays(1));
    [Fact] public void V1UpgradePreservesAccountsCustomersAndRollsBackOnFailure()
    {
        using var temp=new Scratch();var db=new CollectorStore(temp.At("old.db"));
        db.Write(File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../tests/M365Collector.Tests/Fixtures/schema-v1.sql"))));
        var id=Guid.NewGuid();var c=new Customer(id,"Existing",Guid.NewGuid(),"EXISTING-CERT"){Identity=new(id,"Existing",[],DateTimeOffset.UtcNow)};
        db.Write("INSERT INTO Customers VALUES($t,'Existing',$p);INSERT INTO ModuleConfiguration VALUES($t,'tenant-identity',1)",("$t",id.ToString()),("$p",JsonSerializer.Serialize(c)));
        db.Write("INSERT INTO Users(Name,Hash,Role) VALUES('admin','existing-password-hash',0)");db.Setting("BootstrapClientId","existing-bootstrap");
        Assert.Throws<IOException>(()=>db.Migrate((_,_)=>throw new IOException("simulated failure")));
        Assert.Equal(1,db.Read("SELECT MAX(Version) FROM SchemaVersion",r=>r.GetInt32(0)).Single());
        db.Backup(temp.At("backup.db"));db.Migrate();db.Verify();
        Assert.Equal(c.ClientId,Assert.Single(db.GetCustomers()).ClientId);Assert.Equal(c.Thumbprint,Assert.Single(db.GetCustomers()).Thumbprint);Assert.Equal("existing-password-hash",db.Read("SELECT Hash FROM Users",r=>r.GetString(0)).Single());Assert.Equal("existing-bootstrap",db.Setting("BootstrapClientId"));
        Assert.Equal(4,db.Modules(id).Count);Assert.Equal(3,db.Modules(id).Count(m=>!m.Enabled));
        var backup=new CollectorStore(temp.At("backup.db"));backup.VerifyIntegrity();Assert.Equal(1,backup.Read("SELECT MAX(Version) FROM SchemaVersion",r=>r.GetInt32(0)).Single());
    }
    [Fact] public void DedupAndQueriesAreTenantScoped()
    {
        using var temp=new Scratch();var db=temp.Database();var a=Customer(db);var b=Customer(db);
        Assert.Equal(1,db.Persist(a.TenantId,"sign-ins",[Event(a.TenantId)]));Assert.Equal(0,db.Persist(a.TenantId,"sign-ins",[Event(a.TenantId)]));Assert.Equal(1,db.Persist(b.TenantId,"sign-ins",[Event(b.TenantId)]));
        Assert.Single(db.Search(Filter(a.TenantId)));Assert.Equal(0,db.Count(Filter(a.TenantId) with{User="' OR 1=1 --"}));Assert.Equal(1,db.Count(Filter(a.TenantId) with{User="Example Person",Location="london",Result="Failed"}));
        Assert.Throws<ArgumentException>(()=>db.Count(Filter(Guid.Empty)));
        Assert.Throws<InvalidDataException>(()=>db.Persist(a.TenantId,"sign-ins",[Event(b.TenantId)]));
        Assert.Single(db.Groups(Filter(a.TenantId),"IP / location"));
    }
    [Fact] public void BlobReceiptIsAtomicWithRecords()
    {
        using var temp=new Scratch();var db=temp.Database();var c=Customer(db);var row=Event(c.TenantId) with{ModuleId="unified-audit"};
        Assert.ThrowsAny<JsonException>(()=>db.Persist(c.TenantId,"unified-audit",[row,row with{EventId="bad",RawJson="invalid"}],"Audit.General","blob"));
        Assert.False(db.ContentProcessed(c.TenantId,"Audit.General","blob"));Assert.Equal(0,db.Count(Filter(c.TenantId)));
        db.Persist(c.TenantId,"unified-audit",[row],"Audit.General","blob");Assert.True(db.ContentProcessed(c.TenantId,"Audit.General","blob"));
    }
    [Fact] public void RetentionRequiresExplicitOptIn()
    {
        using var temp=new Scratch();var db=temp.Database();var c=Customer(db);db.Persist(c.TenantId,"sign-ins",[Event(c.TenantId) with{Timestamp=DateTimeOffset.UtcNow.AddDays(-100)}]);
        Assert.Equal(0,db.Retain(c.TenantId,"sign-ins",DateTimeOffset.UtcNow));db.Configure(c.TenantId,"sign-ins",true,15,90,true);Assert.Equal(1,db.Retain(c.TenantId,"sign-ins",DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(()=>db.Configure(c.TenantId,"sign-ins",true,7,90,false));
    }
    [Fact] public void RunRecoveryAndSchedulingPreserveHistory()
    {
        using var temp=new Scratch();var db=temp.Database();var c=Customer(db);db.Configure(c.TenantId,"sign-ins",true,15,90,false);
        var state=db.Modules(c.TenantId).Single(s=>s.ModuleId=="sign-ins");Assert.True(M365Collector.Service.CollectionScheduler.IsDue(state,DateTimeOffset.UtcNow));
        db.StartRun(c.TenantId,"sign-ins",DateTimeOffset.UtcNow);Assert.False(M365Collector.Service.CollectionScheduler.IsDue(db.Modules(c.TenantId).Single(s=>s.ModuleId=="sign-ins"),DateTimeOffset.UtcNow));
        db.RecoverInterruptedRuns();Assert.Equal(CollectorHealth.Attention,db.Modules(c.TenantId).Single(s=>s.ModuleId=="sign-ins").Health);Assert.NotNull(Assert.Single(db.Runs(c.TenantId)).Ended);
    }
    [Theory][InlineData(0,"Successful")][InlineData(50076,"Interrupted")][InlineData(50126,"Failed")]
    public void SignInClassifiesWithoutLosingRawFields(int code,string expected)
    {
        using var json=JsonDocument.Parse($$$"""{"id":"a","createdDateTime":"2026-09-01T10:00:00Z","status":{"errorCode":{{{code}}}},"deviceDetail":{"browser":"Example"}}""");
        var row=EventParsing.SignIn(Guid.NewGuid(),json.RootElement);Assert.Equal(expected,row.Result);Assert.Contains("deviceDetail",row.RawJson);
    }
    [Fact] public async Task CsvUsesAppliedFiltersAndNeutralizesFormulas()
    {
        using var temp=new Scratch();var db=temp.Database();var c=Customer(db);db.Persist(c.TenantId,"sign-ins",[Event(c.TenantId) with{User="=formula"},Event(c.TenantId,"other") with{Result="Successful"}]);
        var target=temp.At("events.csv");Assert.Equal(1,await CsvExport.ExportAsync(db,Filter(c.TenantId) with{Result="Failed"},c.Name,target,CancellationToken.None));var csv=File.ReadAllText(target);Assert.Contains(c.TenantId.ToString(),csv);Assert.Contains("'=formula",csv);Assert.DoesNotContain("Successful",csv);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>CsvExport.ExportAsync(db,Filter(c.TenantId),c.Name,temp.At("cancel.csv"),new CancellationToken(true)));Assert.False(File.Exists(temp.At("cancel.csv")));Assert.Empty(Directory.GetFiles(temp.Root,"*.partial"));
    }
    [Fact] public async Task ThrottlingHonorsRetryAfterAnd401Refreshes()
    {
        using var temp=new Scratch();var c=Customer(temp.Database());var tokens=new Tokens();var pauses=new List<TimeSpan>();var n=0;
        using var http=new HttpClient(new Handler(_=>{n++;var r=new HttpResponseMessage(n==1?HttpStatusCode.TooManyRequests:n==2?HttpStatusCode.Unauthorized:HttpStatusCode.OK){Content=new StringContent("{\"value\":[]}")};if(n==1)r.Headers.RetryAfter=new(TimeSpan.FromSeconds(9));return r;}));
        var api=new CollectorApi(http,tokens,(p,_)=>{pauses.Add(p);return Task.CompletedTask;});await api.SendAsync(c,CollectorCatalog.Get("sign-ins"),"https://graph.microsoft.com/v1.0/auditLogs/signIns",null,CancellationToken.None);
        Assert.Equal(3,n);Assert.Equal(TimeSpan.FromSeconds(9),Assert.Single(pauses));Assert.True(tokens.Forced);
    }
    [Fact] public async Task FailedGraphPageDoesNotAdvanceCheckpointAndRetryDeduplicates()
    {
        using var temp=new Scratch();var db=temp.Database();var c=Customer(db);var fail=true;
        using var http=new HttpClient(new Handler(r=>r.RequestUri!.Query.Contains("page=2")?new(fail?HttpStatusCode.Forbidden:HttpStatusCode.OK){Content=new StringContent("{\"value\":[]}")}:new(HttpStatusCode.OK){Content=new StringContent("{\"value\":[{\"id\":\"a\",\"createdDateTime\":\"2026-09-01T00:00:00Z\",\"status\":{\"errorCode\":0}}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/auditLogs/signIns?page=2\"}")}));
        var collector=new SignInActivityModule(new CollectorApi(http,new Tokens()));await Assert.ThrowsAsync<CollectorAttentionException>(()=>collector.CollectAsync(c,db,CancellationToken.None));Assert.Null(db.Checkpoint(c.TenantId,"sign-ins","through"));Assert.Single(db.Search(Filter(c.TenantId)));
        fail=false;Assert.Equal(0,(await collector.CollectAsync(c,db,CancellationToken.None)).Added);Assert.NotNull(db.Checkpoint(c.TenantId,"sign-ins","through"));
    }
    [Fact] public async Task EmptyUnifiedFeedReportsAttentionAndStartsAllFourSubscriptions()
    {
        using var temp=new Scratch();var db=temp.Database();var c=Customer(db);var enabled=new HashSet<string>();
        using var http=new HttpClient(new Handler(r=>{var u=r.RequestUri!;if(u.AbsolutePath.EndsWith("/start")){enabled.Add(u.Query.Split('=')[1]);return Ok("{}");}if(u.AbsolutePath.EndsWith("/list"))return Ok(JsonSerializer.Serialize(enabled.Select(t=>new{contentType=t,status="enabled"})));return Ok("[]");}));
        var outcome=await new UnifiedAuditModule(new CollectorApi(http,new Tokens())).CollectAsync(c,db,CancellationToken.None);Assert.Equal(4,enabled.Count);Assert.DoesNotContain("DLP.All",enabled);Assert.Contains("No activity-feed",outcome.Attention);Assert.Equal(0,outcome.Added);
    }
    [Theory][InlineData("https://attacker.example/v1.0/auditLogs/signIns")][InlineData("http://graph.microsoft.com/v1.0/auditLogs/signIns")][InlineData("https://graph.microsoft.com/v1.0/users")]
    public void RejectsUnsafeContinuation(string url)=>Assert.Throws<InvalidDataException>(()=>CollectorApi.ValidateUrl(new(Guid.NewGuid(),"Example",Guid.NewGuid(),"test"),"https://graph.microsoft.com",url));
    [Fact] public async Task SchedulerPreventsOverlapAndCancelsCleanly()
    {
        using var temp=new Scratch();var db=temp.Database();var c=Customer(db);db.Configure(c.TenantId,"sign-ins",true,15,90,false);var collector=new BlockingCollector();
        var scheduler=new M365Collector.Service.CollectionScheduler(db,new RuntimePaths(temp.At("data")),new M365Collector.Modules.TenantIdentity.TenantIdentityModule(new IdentityReader()),[collector]);
        using var cancel=new CancellationTokenSource();var first=scheduler.RunAsync(c,"sign-ins",cancel.Token);await collector.Started.Task;
        Assert.False(await scheduler.RunAsync(c,"sign-ins",CancellationToken.None));cancel.Cancel();Assert.False(await first);Assert.Single(db.Runs(c.TenantId));Assert.Equal(CollectorHealth.Attention,db.Modules(c.TenantId).Single(m=>m.ModuleId=="sign-ins").Health);
    }
    [Fact] public async Task UnifiedDownloadsPagedContentOnceAndPreservesRawFileFields()
    {
        using var temp=new Scratch();var db=temp.Database();var c=Customer(db);var root=$"https://manage.office.com/api/v1.0/{c.TenantId}/activity/feed";var downloads=0;
        using var http=new HttpClient(new Handler(r=>
        {
            var u=r.RequestUri!;
            if(u.AbsolutePath.EndsWith("/list"))return Ok(JsonSerializer.Serialize(UnifiedAuditModule.ContentTypes.Select(t=>new{contentType=t,status="enabled"})));
            if(u.AbsolutePath.EndsWith("/blob")){downloads++;return Ok(JsonSerializer.Serialize(new[]{new{Id="audit",OrganizationId=c.TenantId,CreationTime="2026-09-01T00:00:00Z",Workload="SharePoint",Operation="FileModified",SourceFileName="example.docx",SiteUrl="https://example.test",UserId="example@example.test"}}));}
            if(u.Query.Contains("page=2"))return Ok(JsonSerializer.Serialize(new[]{new{contentId="blob",contentType="Audit.SharePoint",contentUri=root+"/audit/blob"}}));
            var response=Ok("[]");if(u.Query.Contains("Audit.SharePoint"))response.Headers.Add("NextPageUri",root+"/subscriptions/content?page=2");return response;
        }));
        var collector=new UnifiedAuditModule(new CollectorApi(http,new Tokens()));Assert.Equal(1,(await collector.CollectAsync(c,db,CancellationToken.None)).Added);Assert.Equal(0,(await collector.CollectAsync(c,db,CancellationToken.None)).Added);Assert.Equal(1,downloads);
        var row=Assert.Single(db.Search(Filter(c.TenantId) with{Workload="SharePoint / OneDrive",Object=".docx"}));Assert.Contains("SourceFileName",row.RawJson);
    }
    sealed class IdentityReader:IAppOnlyIdentityReader { public Task<TenantIdentity> ReadAsync(Customer c,CancellationToken ct)=>Task.FromResult(c.Identity!); }
    sealed class BlockingCollector:IAuditCollectorModule
    {
        public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);public CollectorDefinition Definition=>CollectorCatalog.Get("sign-ins");
        public async Task<CollectionOutcome> CollectAsync(Customer c,IAuditStore store,CancellationToken ct){Started.SetResult();await Task.Delay(Timeout.Infinite,ct);return new(0);}
    }
    static HttpResponseMessage Ok(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json)};
    sealed class Tokens:ICollectorTokenProvider { public bool Forced;public Task<string> AcquireAsync(Customer c,string resource,bool forceRefresh,CancellationToken ct){Forced|=forceRefresh;return Task.FromResult("opaque");} }
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(send(r)); }
}
