using System.Text.Json;
using Microsoft.Data.Sqlite;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Entra;
using M365Collector.Modules.TenantIdentity;
using M365Collector.Security;
using M365Collector.Storage;
using Xunit;

namespace M365Collector.Tests;

public sealed class TestRuntime : IDisposable
{
    public RuntimePaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "M365Collector.Tests", Guid.NewGuid().ToString("N")));
    public CollectorDatabase Db { get; }
    public RuntimeConfig Config { get; }
    public TestRuntime()
    {
        Paths.Create(); Db = new(Paths); Db.Migrate();
        Config = new(1, Paths.Root, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "M365Collector", "0.1.0"), "0.1.0", false, "S-1-5-21-test");
    }
    public Customer Customer(Guid? id = null) => new(id ?? Guid.NewGuid(), "Example Ltd", "", Guid.NewGuid(), new(new string('A',40)), CustomerState.Draft);
    public void Dispose()
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "M365Collector.Tests")) + Path.DirectorySeparatorChar;
        if (!Paths.Root.StartsWith(allowed,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
        Directory.Delete(Paths.Root,true);
    }
}

public sealed class FoundationTests
{
    [Fact] public void NewSetupRequiresNoRegisteredRoot() => Assert.Equal(SetupState.New,SetupDetection.Detect(null));
    [Fact] public void MissingRegisteredRuntimeFailsClosed() => Assert.Equal(SetupState.Broken,SetupDetection.Detect(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString())));
    [Fact] public void SetupCompletionRequiresDatabase()
    {
        using var runtime=new TestRuntime(); var store=new ConfigurationStore(runtime.Paths); store.Save(runtime.Config);
        Assert.Equal(SetupState.Incomplete,SetupDetection.Detect(runtime.Paths.Root));
        store.Save(runtime.Config with { SetupComplete=true }); Assert.Equal(SetupState.Complete,SetupDetection.Detect(runtime.Paths.Root));
        File.Delete(runtime.Paths.Database); Assert.Equal(SetupState.Broken,SetupDetection.Detect(runtime.Paths.Root));
    }
    [Fact] public void ConfigRoundTripsWithoutSecrets()
    {
        using var runtime=new TestRuntime(); var store=new ConfigurationStore(runtime.Paths); store.Save(runtime.Config);
        Assert.Equal(runtime.Config,store.Load()); Assert.Contains("SchemaVersion",File.ReadAllText(runtime.Paths.Config));
    }
    [Fact] public void FutureConfigSchemaRefused()
    { using var runtime=new TestRuntime(); JsonFiles.WriteAtomic(runtime.Paths.Config,runtime.Config with { SchemaVersion=99 }); Assert.Throws<InvalidDataException>(()=>new ConfigurationStore(runtime.Paths).Load()); }
    [Fact] public void ConfigCannotRedirectDataRoot()
    { using var runtime=new TestRuntime(); Assert.Throws<InvalidDataException>(()=>new ConfigurationStore(runtime.Paths).Save(runtime.Config with { DataRoot=Path.Combine(runtime.Paths.Root,"other") })); }
    [Theory] [InlineData("relative")] [InlineData("C:\\")] [InlineData("\\\\server\\share")] [InlineData("")]
    public void InvalidStoragePathsRejected(string path) => Assert.Throws<ArgumentException>(()=>RuntimePaths.Validate(path));
    [Theory] [InlineData("C:\\PROGRA~1\\Data")] [InlineData("C:\\Program Files.\\Data")] [InlineData("C:\\Data \\Tenant")]
    public void AmbiguousStorageAliasesRejected(string path) => Assert.Throws<ArgumentException>(()=>RuntimePaths.Validate(path));
    [Fact] public void RepositoryReadsDoNotRecreateMissingDatabase()
    { using var runtime=new TestRuntime();File.Delete(runtime.Paths.Database);Assert.Throws<SqliteException>(()=>runtime.Db.Customers());Assert.False(File.Exists(runtime.Paths.Database)); }
    [Fact] public void BinaryAndDataTreesCannotOverlap()
    {
        using var runtime=new TestRuntime();
        Assert.Throws<ArgumentException>(()=>RuntimePaths.Validate(runtime.Paths.Root,runtime.Paths.Root));
        Assert.Throws<ArgumentException>(()=>RuntimePaths.Validate(Path.Combine(runtime.Paths.Root,"Data"),runtime.Paths.Root));
        Assert.Throws<ArgumentException>(()=>RuntimePaths.Validate(runtime.Paths.Root,Path.Combine(runtime.Paths.Root,"app")));
    }
    [Fact] public void SimilarFolderPrefixIsNotContainment()
    { Assert.False(RuntimePaths.Contains(@"C:\Apps\Collector",@"C:\Apps\CollectorData")); }
    [Fact] public void SelectedPathCanBeWrittenAndTestFileRemoved()
    { using var runtime=new TestRuntime(); Assert.True(RuntimePaths.WriteTest(runtime.Paths.Root)>0); Assert.Empty(Directory.GetFiles(runtime.Paths.Root,".write-test-*")); }
    [Fact] public void DirectoryCreationIsIdempotentAndTenantSeparated()
    {
        using var runtime=new TestRuntime(); runtime.Paths.Create(); var one=Guid.NewGuid(); var two=Guid.NewGuid(); runtime.Paths.CreateCustomer(one); runtime.Paths.CreateCustomer(two);
        Assert.All(RuntimePaths.Directories,name=>Assert.True(Directory.Exists(Path.Combine(runtime.Paths.Root,name))));
        Assert.NotEqual(runtime.Paths.CustomerRoot(one),runtime.Paths.CustomerRoot(two));
        Assert.True(Directory.Exists(Path.Combine(runtime.Paths.CustomerRoot(one),"Audit"))); Assert.Throws<ArgumentException>(()=>runtime.Paths.CustomerRoot(Guid.Empty));
    }
    [Fact] public void MigrationIsVersionedAndIdempotent()
    {
        using var runtime=new TestRuntime(); runtime.Db.Migrate(); Assert.Equal(1,runtime.Db.SchemaVersion());
        using var db=new SqliteConnection("Data Source="+runtime.Paths.Database+";Pooling=False"); db.Open(); using var command=db.CreateCommand(); command.CommandText="SELECT COUNT(*) FROM schema_migrations"; Assert.Equal(1L,command.ExecuteScalar());
    }
    [Fact] public void FutureDatabaseSchemaCannotBeDowngraded()
    {
        using var runtime=new TestRuntime(); using(var db=new SqliteConnection("Data Source="+runtime.Paths.Database+";Pooling=False")) { db.Open(); using var command=db.CreateCommand(); command.CommandText="PRAGMA user_version=99";command.ExecuteNonQuery(); }
        Assert.Throws<InvalidDataException>(runtime.Db.Migrate); Assert.Equal(99,runtime.Db.SchemaVersion());
    }
    [Fact] public void MigrationPreservesCustomersAndModuleSelections()
    {
        using var runtime=new TestRuntime(); var c=runtime.Customer();runtime.Db.SaveDraft(c);runtime.Db.SetModule(c.TenantId,"tenant-identity",false); runtime.Db.Migrate();
        Assert.Equal(c,runtime.Db.Find(c.TenantId)); Assert.False(runtime.Db.ModuleEnabled(c.TenantId,"tenant-identity"));
    }
    [Fact] public void SavingCannotForgeActivation()
    { using var runtime=new TestRuntime();var c=runtime.Customer() with { State=CustomerState.Active,ValidatedAt=DateTimeOffset.UtcNow };runtime.Db.SaveDraft(c);Assert.Equal(CustomerState.Draft,runtime.Db.Find(c.TenantId)!.State);Assert.Null(runtime.Db.Find(c.TenantId)!.ValidatedAt); }
    [Fact] public void TenantsCannotReuseCertificate()
    { using var runtime=new TestRuntime();runtime.Db.SaveDraft(runtime.Customer());Assert.Throws<InvalidOperationException>(()=>runtime.Db.SaveDraft(runtime.Customer())); }
    [Fact] public void DraftTenantsCannotReuseApplicationEvenBeforeCertificateSelection()
    { using var runtime=new TestRuntime();var c=runtime.Customer();runtime.Db.SaveDraft(c);Assert.Throws<InvalidOperationException>(()=>runtime.Db.SaveDraft(runtime.Customer() with {ClientId=c.ClientId,Certificate=null})); }
    [Fact] public void AllProductAssembliesExposeTheExactReleaseVersion()
    {
        foreach(var assembly in new[]{typeof(ProductInfo).Assembly,typeof(RuntimePaths).Assembly,typeof(CollectorDatabase).Assembly,typeof(CertificateStore).Assembly,typeof(TenantIdentityReader).Assembly,typeof(TenantIdentityModule).Assembly,typeof(M365Collector.GUI.MainForm).Assembly,typeof(M365Collector.Service.Program).Assembly})
        {
            Assert.Equal(new Version(0,1,0,0),assembly.GetName().Version);
            var metadata=System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);Assert.Equal("0.1.0.0",metadata.FileVersion);Assert.Equal("0.1.0",metadata.ProductVersion);
        }
    }
    [Fact] public void TenantRecordsAndModuleSelectionsAreIsolated()
    {
        using var runtime=new TestRuntime();var a=runtime.Customer();var b=runtime.Customer() with {Certificate=new(new string('B',40))};runtime.Db.SaveDraft(a);runtime.Db.SaveDraft(b);runtime.Db.SetModule(a.TenantId,"tenant-identity",false);
        Assert.Equal(2,runtime.Db.Customers().Count); Assert.True(runtime.Db.ModuleEnabled(b.TenantId,"tenant-identity"));
    }
    [Fact] public void UnimplementedModuleCannotBeEnabled()
    { using var runtime=new TestRuntime();Assert.Throws<NotSupportedException>(()=>runtime.Db.SetModule(Guid.NewGuid(),"teams",true)); }
    [Fact] public void QueuedValidationLocksConfigurationAndDeduplicates()
    {
        using var runtime=new TestRuntime();var c=runtime.Customer();runtime.Db.SaveDraft(c);var id=runtime.Db.Enqueue(c.TenantId,"Validate");Assert.Equal(id,runtime.Db.Enqueue(c.TenantId,"Validate"));Assert.Throws<InvalidOperationException>(()=>runtime.Db.SaveDraft(c with {ClientId=Guid.NewGuid()}));
    }
    [Fact] public void SuccessfulServiceValidationActivatesOnlyMatchingTenant()
    {
        using var runtime=new TestRuntime();var c=runtime.Customer();runtime.Db.SaveDraft(c);var id=runtime.Db.Enqueue(c.TenantId,"Validate");var job=runtime.Db.Claim()!;
        runtime.Db.Complete(job,new(true,new(c.TenantId,"Verified",[new("tenant.onmicrosoft.com",true,true)],DateTimeOffset.UtcNow),"Success"));
        Assert.Equal(CustomerState.Active,runtime.Db.Find(c.TenantId)!.State);Assert.Equal("tenant.onmicrosoft.com",runtime.Db.Find(c.TenantId)!.DefaultDomain);Assert.Equal("Succeeded",runtime.Db.GetJob(id)!.Status);
    }
    [Fact] public void MismatchedIdentityCannotActivateCustomer()
    {
        using var runtime=new TestRuntime();var c=runtime.Customer();runtime.Db.SaveDraft(c);var id=runtime.Db.Enqueue(c.TenantId,"Validate");var job=runtime.Db.Claim()!;
        runtime.Db.Complete(job,new(true,new(Guid.NewGuid(),"Wrong",[],DateTimeOffset.UtcNow),"Success"));
        Assert.Equal(CustomerState.Draft,runtime.Db.Find(c.TenantId)!.State);Assert.Equal("TenantMismatch",runtime.Db.GetJob(id)!.ResultCode);
    }
    [Fact] public void FailedAuthenticationNeverActivatesCustomer()
    { using var runtime=new TestRuntime();var c=runtime.Customer();runtime.Db.SaveDraft(c);runtime.Db.Enqueue(c.TenantId,"Validate");runtime.Db.Complete(runtime.Db.Claim()!,CollectionResult.Failed("PermissionDenied"));Assert.Equal(CustomerState.Draft,runtime.Db.Find(c.TenantId)!.State); }
    [Fact] public void InterruptedServiceJobIsExplicitFailure()
    { using var runtime=new TestRuntime();var c=runtime.Customer();runtime.Db.SaveDraft(c);var id=runtime.Db.Enqueue(c.TenantId,"Validate");runtime.Db.Claim();runtime.Db.RecoverInterruptedJobs();Assert.Equal("ServiceInterrupted",runtime.Db.GetJob(id)!.ResultCode); }
    [Fact] public void SqliteBackupIncludesCommittedWalData()
    {
        using var runtime=new TestRuntime();var c=runtime.Customer();runtime.Db.SaveDraft(c);var backup=Path.Combine(runtime.Paths.Root,"Service","backup.db");runtime.Db.Backup(backup);
        using var db=new SqliteConnection("Data Source="+backup+";Pooling=False");db.Open();using var command=db.CreateCommand();command.CommandText="SELECT tenant_id FROM customers";Assert.Equal(c.TenantId.ToString("D"),command.ExecuteScalar());
    }
    [Theory] [InlineData("password", "My", "LocalMachine")] [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","My","CurrentUser")] [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","Root","LocalMachine")]
    public void UnsafeCertificateReferencesRejected(string thumbprint,string store,string location) => Assert.Throws<ArgumentException>(()=>CollectorDatabase.ValidateCertificateReference(new(thumbprint,store,location)));
    [Fact] public void SafeCertificateReferenceAccepted() => CollectorDatabase.ValidateCertificateReference(new(new string('A',40)));
    [Fact] public void PermissionManifestHasOnlyIdentityPermissionAndNoPowerShell()
    {
        var permission=Assert.Single(ModuleCatalog.Permissions(["tenant-identity"]));Assert.Equal("Organization.Read.All",permission.Name);Assert.Equal(Guid.Parse("498476ce-e0fe-48b0-b801-37ba7e2685c6"),permission.Id);
        Assert.Empty(TenantIdentityModule.Definition.PowerShellModules);Assert.Empty(ModuleCatalog.Permissions([]));Assert.Single(ModuleCatalog.All,m=>m.Implemented);
    }
    [Fact] public async Task ModuleDeclaresNativeDependency()
    { var module=new TenantIdentityModule(new FakeReader());var dependency=Assert.Single(await module.ValidateDependenciesAsync(default));Assert.True(dependency.Present); }
    [Fact] public async Task ModuleRejectsCrossTenantResult()
    { var customer=new Customer(Guid.NewGuid(),"Example","",Guid.NewGuid(),null,CustomerState.Draft);var result=await new TenantIdentityModule(new FakeReader()).CollectAsync(customer,default);Assert.False(result.Success);Assert.Equal("TenantMismatch",result.Code); }
    [Fact] public void GraphIdentityParserReadsDefaultAndInitialDomains()
    {
        var id=Guid.NewGuid();using var json=JsonDocument.Parse(JsonSerializer.Serialize(new { value=new[]{new { id,displayName="Example",verifiedDomains=new[]{new{name="example.test",isDefault=true,isInitial=false},new{name="example.onmicrosoft.com",isDefault=false,isInitial=true}}}} }));
        var identity=TenantIdentityReader.Parse(json.RootElement,id);Assert.Equal("example.test",identity.DefaultDomain);Assert.Equal("example.onmicrosoft.com",identity.InitialDomain);Assert.Throws<InvalidDataException>(()=>TenantIdentityReader.Parse(json.RootElement,Guid.NewGuid()));
    }
    [Theory] [InlineData(401,"AuthenticationRejected")] [InlineData(403,"PermissionDenied")] [InlineData(429,"GraphThrottled")] [InlineData(503,"GraphUnavailable")]
    public void AuthenticationErrorsAreSafeCodes(int status,string expected) => Assert.Equal(expected,AuthenticationErrors.Code(new GraphAccessException(status)));
    [Fact] public void HealthRejectsStaleAndFutureHeartbeats()
    {
        var now=DateTimeOffset.UtcNow;var health=new ServiceHealth(1,"0.1.0","instance",now,now,"Running",null);Assert.True(health.IsFresh(now));Assert.False((health with {HeartbeatAt=now.AddMinutes(-1)}).IsFresh(now));Assert.False((health with {HeartbeatAt=now.AddMinutes(1)}).IsFresh(now));
    }
    [Fact] public void AuthorizationSeparatesReadCollectAndAdministration()
    { var policy=new RoleAuthorization();Assert.True(policy.Allows(ApplicationRole.Administrator,ApplicationAction.Onboard));Assert.False(policy.Allows(ApplicationRole.Operator,ApplicationAction.Onboard));Assert.True(policy.Allows(ApplicationRole.Operator,ApplicationAction.Collect));Assert.False(policy.Allows(ApplicationRole.ReadOnly,ApplicationAction.Collect)); }
    [Fact] public void LogRejectsRawSecretOrExceptionText()
    {
        using var runtime=new TestRuntime();var log=new StructuredLog(runtime.Paths,"test");Assert.Throws<ArgumentException>(()=>log.Write("Auth","Failure",code:"Bearer a-sensitive-token"));log.Write("Auth","Failure",code:"PermissionDenied");Assert.DoesNotContain("sensitive",File.ReadAllText(Directory.GetFiles(Path.Combine(runtime.Paths.Root,"Logs"))[0]));
    }
    [Fact] public void AtomicConfigurationWriteLeavesNoTemporaryFiles()
    { using var runtime=new TestRuntime();var store=new ConfigurationStore(runtime.Paths);store.Save(runtime.Config);store.Save(runtime.Config with { SetupComplete=true });Assert.Empty(Directory.GetFiles(Path.Combine(runtime.Paths.Root,"Config"),"*.tmp")); }
    private sealed class FakeReader : ITenantIdentityReader
    { public Task<TenantIdentity> ReadAsync(Customer customer,CancellationToken ct)=>Task.FromResult(new TenantIdentity(Guid.NewGuid(),"Wrong tenant",[],DateTimeOffset.UtcNow)); }
}
