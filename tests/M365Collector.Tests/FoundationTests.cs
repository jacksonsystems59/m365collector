using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Entra;
using M365Collector.Modules.TenantIdentity;
using M365Collector.Security;
using M365Collector.Storage;
using Xunit;
namespace M365Collector.Tests;
public sealed class Scratch : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "M365Collector.Tests", Guid.NewGuid().ToString("N"));
    public Scratch() { Directory.CreateDirectory(Root); }
    public string At(string path) => Path.Combine(Root, path);
    public CollectorStore Database() { var paths = new RuntimePaths(At("data")); paths.Create(); var store = new CollectorStore(paths.Database); store.Migrate(); return store; }
    public void Dispose() { Directory.Delete(Root, true); }
}
public class FoundationTests
{
    [Fact] public void FirstRunRequiresCompletedInstallation()
    {
        using var temp = new Scratch(); var path = temp.At("locator.json"); Assert.True(InstallationState.IsFirstRun(path)); JsonFile.Write(path, new Installation("data", "app", false)); Assert.True(InstallationState.IsFirstRun(path)); JsonFile.Write(path, new Installation("data", "app", true)); Assert.False(InstallationState.IsFirstRun(path));
    }
    [Fact] public void DamagedLocatorFailsClosed() { using var temp = new Scratch(); File.WriteAllText(temp.At("locator"), "bad"); Assert.Throws<JsonException>(() => InstallationState.IsFirstRun(temp.At("locator"))); }
    [Fact] public void DataRootRejectsBinaryOverlapAndInsufficientSpace()
    {
        using var temp = new Scratch(); var paths = new RuntimePaths(temp.At("data")); Assert.Throws<IOException>(() => paths.Validate(temp.At("data/app"))); Assert.Throws<IOException>(() => paths.Validate(temp.Root)); Assert.Throws<IOException>(() => paths.Validate(temp.At("app"), long.MaxValue)); paths.Validate(temp.At("app"), 0);
    }
    [Fact] public void CreatesRuntimeAndIsolatedCustomerFolders()
    {
        using var temp = new Scratch(); var paths = new RuntimePaths(temp.At("data")); paths.Create(); var a = paths.CustomerDirectory(Guid.NewGuid()); var b = paths.CustomerDirectory(Guid.NewGuid()); Assert.NotEqual(a, b); File.WriteAllText(Path.Combine(a, "Data", "identity.json"), "customer A"); Assert.Empty(Directory.GetFiles(Path.Combine(b, "Data"))); Assert.True(Directory.Exists(Path.Combine(paths.Root, "Logs", "Updates"))); Assert.Throws<ArgumentException>(() => paths.CustomerDirectory(Guid.Empty));
    }
    [Theory][InlineData("C:\\Data.\\nested")][InlineData("C:\\Data ")][InlineData("C:\\Data\\NUL")][InlineData("C:\\Data:stream")]
    public void DataRootRejectsWindowsAliasPaths(string path) => Assert.Throws<IOException>(() => new RuntimePaths(path));
    [Fact] public void MigrationsAreVersionedIdempotentAndTransactional()
    {
        using var temp = new Scratch(); var store = temp.Database(); store.Migrate(); store.Verify();
        Assert.Throws<IOException>(() => store.Migrate((db, tx) => { using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO Settings VALUES('uncommitted','bad')"; cmd.ExecuteNonQuery(); throw new IOException("migration failure"); })); Assert.Null(store.Setting("uncommitted")); store.Verify();
    }
    [Fact] public void NewerSchemaCannotBeOpenedForOldMigrations() { using var temp = new Scratch(); var store = temp.Database(); store.Write("UPDATE SchemaVersion SET Version=999"); Assert.Throws<InvalidDataException>(() => store.Migrate()); }
    [Fact] public void CustomersRequireVerifiedMatchingTenant()
    {
        using var temp = new Scratch(); var store = temp.Database(); var id = Guid.NewGuid(); var customer = new Customer(id, "Example", Guid.NewGuid(), "A"); Assert.Throws<InvalidDataException>(() => store.SaveCustomer(customer)); Assert.Throws<InvalidDataException>(() => store.SaveCustomer(customer with { Identity = new(Guid.NewGuid(), "Other", [], DateTimeOffset.UtcNow) }));
        store.SaveCustomer(customer with { Identity = new(id, "Example", [], DateTimeOffset.UtcNow) }); Assert.Single(store.GetCustomers());
    }
    [Fact] public void LocalPasswordsAreSaltedAndIncorrectPasswordsFail()
    {
        const string password = "long local passphrase"; var a = PasswordHash.Create(password); var b = PasswordHash.Create(password); Assert.NotEqual(a, b); Assert.DoesNotContain(password, a); Assert.True(PasswordHash.Verify(password, a)); Assert.False(PasswordHash.Verify("wrong", a)); Assert.False(PasswordHash.Verify(password, "malformed")); Assert.Throws<ArgumentException>(() => PasswordHash.Create("short"));
    }
    [Fact] public void FirstAdministratorIsUniqueAndLockoutIsEnforced()
    {
        using var temp = new Scratch(); var store = temp.Database(); var accounts = new LocalAccounts(store); accounts.CreateFirst("admin", "first local password"); Assert.Throws<InvalidOperationException>(() => accounts.CreateFirst("second", "second local password")); Assert.Equal(LocalRole.Administrator, accounts.Login("admin", "first local password")!.Role);
        for (var i = 0; i < 5; i++) Assert.Null(accounts.Login("admin", "wrong")); Assert.Null(accounts.Login("admin", "first local password")); store.Write("UPDATE Users SET LockedUntil=NULL"); Assert.NotNull(accounts.Login("admin", "first local password"));
    }
    [Theory][InlineData(LocalRole.Administrator,Capability.InstallUpdates,true)][InlineData(LocalRole.Operator,Capability.Collect,true)][InlineData(LocalRole.Operator,Capability.ManageCustomers,false)][InlineData(LocalRole.ReadOnly,Capability.View,true)][InlineData(LocalRole.ReadOnly,Capability.Collect,false)]
    public void RolesSeparateAuthorization(LocalRole role, Capability action, bool allowed) => Assert.Equal(allowed, Authorization.Allows(new("user", role), action));
    [Fact] public void PublicCerNeverContainsPrivateKey()
    {
        using var temp = new Scratch(); using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)); Certificates.ExportPublic(certificate, temp.At("public.cer")); using var read = X509CertificateLoader.LoadCertificateFromFile(temp.At("public.cer")); Assert.False(read.HasPrivateKey); Assert.Equal(certificate.Thumbprint, read.Thumbprint); Assert.Equal(X509ContentType.Cert, X509Certificate2.GetCertContentType(temp.At("public.cer")));
    }
    [Theory][InlineData("")][InlineData("../../key")][InlineData("not-a-thumbprint")] public void InvalidCertificateReferencesAreRejected(string thumbprint) => Assert.Throws<ArgumentException>(() => Certificates.Find(thumbprint));
    [Fact] public void OnlyTenantIdentityHasCollectionPermissions()
    {
        var manifest = new TenantIdentityModule(new FakeReader()).Manifest; Assert.Equal("Organization.Read.All", Assert.Single(manifest.ApplicationPermissions).Name); Assert.Empty(manifest.PowerShellDependencies); Assert.True(manifest.CanCollect); Assert.Equal("Exchange Message Trace", Assert.Single(TenantIdentityModule.FutureModules));
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task DelegatedBootstrapIsAlwaysDisposed(bool fail)
    {
        var session = new FakeSession(fail); if (fail) await Assert.ThrowsAsync<IOException>(() => OnboardingWorkflow.ProvisionAsync(session, "Example", _ => ("thumb", [1]), new Progress<string>(), CancellationToken.None));
        else { var customer = await OnboardingWorkflow.ProvisionAsync(session, "Example", _ => ("thumb", [1]), new Progress<string>(), CancellationToken.None); Assert.Equal(session.Tenant, customer.TenantId); Assert.Null(customer.Identity); }
        Assert.True(session.Disposed);
    }
    [Fact] public void TenantIdentityRejectsDifferentTenant()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { value = new[] { new { id = Guid.NewGuid(), displayName = "test", verifiedDomains = Array.Empty<object>() } } })); Assert.Throws<InvalidDataException>(() => AppOnlyIdentityReader.ParseIdentity(json.RootElement, Guid.NewGuid()));
    }
    [Fact] public void GuidedFallbackUsesProcessScopeAndAlwaysDisconnects()
    {
        Assert.Contains("-ContextScope Process", GuidedSetup.Script); Assert.Contains("finally", GuidedSetup.Script); Assert.Contains("Disconnect-MgGraph", GuidedSetup.Script); Assert.DoesNotContain("Connect-ExchangeOnline", GuidedSetup.Script); Assert.DoesNotContain("-Password", GuidedSetup.Script);
    }
    private sealed class FakeSession(bool fail) : IBootstrapSession
    {
        public Guid Tenant = Guid.NewGuid(); public bool Disposed;
        public Task<Guid> IdentifyTenantAsync(CancellationToken ct) => Task.FromResult(Tenant);
        public Task<Guid> ProvisionAsync(Guid tenant, byte[] cert, IProgress<string> progress, CancellationToken ct) => fail ? Task.FromException<Guid>(new IOException("denied")) : Task.FromResult(Guid.NewGuid());
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class FakeReader : IAppOnlyIdentityReader { public Task<TenantIdentity> ReadAsync(Customer c, CancellationToken ct) => Task.FromResult(new TenantIdentity(c.TenantId,"Example",[],DateTimeOffset.UtcNow)); }
}
