using M365Collector.Contracts;
using M365Collector.Security;
using Xunit;
namespace M365Collector.Tests;
public class RecoveryTests
{
    [Fact] public void RecoveryRequiresWindowsElevationForListingAndReset()
    {
        using var temp = new Scratch(); var db = temp.Database(); new LocalAccounts(db).CreateFirst("admin", "old local password");
        var recovery = new AccountRecovery(db, () => false, () => "test-sid");
        Assert.Throws<UnauthorizedAccessException>(() => recovery.Accounts());
        Assert.Throws<UnauthorizedAccessException>(() => recovery.Reset("admin", "new local password", "new local password"));
        Assert.NotNull(new LocalAccounts(db).Login("admin", "old local password"));
    }
    [Fact] public void RecoveryClearsLockoutPreservesRolesAndRecordsAuditWithoutPasswords()
    {
        using var temp = new Scratch(); var db = temp.Database(); var accounts = new LocalAccounts(db); accounts.CreateFirst("admin", "old local password");
        accounts.Add(new("admin", LocalRole.Administrator), "reader", "reader old password", LocalRole.ReadOnly);
        for (var i = 0; i < 5; i++) accounts.Login("reader", "incorrect");
        db.Setting("preserved", "customer configuration");
        var recovery = new AccountRecovery(db, () => true, () => "test-windows-sid");
        Assert.Equal(2, recovery.Accounts().Count); recovery.Reset("reader", "reader new password", "reader new password");
        Assert.Equal(LocalRole.ReadOnly, accounts.Login("reader", "reader new password")!.Role);
        Assert.Null(accounts.Login("reader", "reader old password")); Assert.NotNull(accounts.Login("admin", "old local password"));
        Assert.Equal("customer configuration", db.Setting("preserved"));
        var audit = Assert.Single(db.Read("SELECT Value FROM Settings WHERE Key LIKE 'SecurityRecovery:%'", r => r.GetString(0)));
        Assert.Contains("test-windows-sid", audit); Assert.Contains("reader", audit); Assert.DoesNotContain("reader new password", audit);
        Assert.DoesNotContain("reader new password", db.Read("SELECT Hash FROM Users WHERE Name='reader'", r => r.GetString(0)).Single());
    }
    [Theory][InlineData("short", "short")][InlineData("valid long password", "different password")]
    public void InvalidRecoveryDoesNotChangeHashOrWriteAudit(string password, string confirmation)
    {
        using var temp = new Scratch(); var db = temp.Database(); var accounts = new LocalAccounts(db); accounts.CreateFirst("admin", "old local password");
        var before = db.Read("SELECT Hash FROM Users", r => r.GetString(0)).Single();
        Assert.Throws<ArgumentException>(() => new AccountRecovery(db, () => true, () => "test").Reset("admin", password, confirmation));
        Assert.Equal(before, db.Read("SELECT Hash FROM Users", r => r.GetString(0)).Single());
        Assert.Empty(db.Read("SELECT Value FROM Settings WHERE Key LIKE 'SecurityRecovery:%'", r => r.GetString(0)));
    }
    [Fact] public void UnknownAccountCannotBeCreatedThroughRecovery()
    {
        using var temp = new Scratch(); var db = temp.Database();
        Assert.Throws<InvalidOperationException>(() => new AccountRecovery(db, () => true, () => "test").Reset("unknown", "long reset password", "long reset password"));
        Assert.False(new LocalAccounts(db).HasUsers);
    }
}
