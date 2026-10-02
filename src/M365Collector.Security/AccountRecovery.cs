using System.Security.Principal;
using System.Text.Json;
using M365Collector.Contracts;
using M365Collector.Storage;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("M365Collector.Tests")]
namespace M365Collector.Security;

public sealed class AccountRecovery
{
    private readonly CollectorStore store;
    private readonly Func<bool> elevated;
    private readonly Func<string> identity;
    public AccountRecovery(CollectorStore store) : this(store, () => WindowsAcl.Elevated, () => WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("Windows identity unavailable.")) { }
    internal AccountRecovery(CollectorStore store, Func<bool> elevated, Func<string> identity) { this.store = store; this.elevated = elevated; this.identity = identity; }
    private void RequireAdministrator()
    {
        if (!elevated()) throw new UnauthorizedAccessException("Run M365Collector as a Windows administrator to recover a local account.");
    }
    public IReadOnlyList<LocalUser> Accounts()
    {
        RequireAdministrator();
        return store.Read("SELECT Name,Role FROM Users ORDER BY Name", r => new LocalUser(r.GetString(0), (LocalRole)r.GetInt32(1)));
    }
    public void Reset(string name, string password, string confirmation)
    {
        RequireAdministrator();
        if (password != confirmation) throw new ArgumentException("Passwords do not match.");
        var hash = PasswordHash.Create(password);
        var audit = JsonSerializer.Serialize(new { Action = "Local password reset", Account = name, WindowsSid = identity(), Time = DateTimeOffset.UtcNow });
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE Users SET Hash=$hash,Failures=0,LockedUntil=NULL WHERE Name=$name";
        command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$name", name);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Select an existing local account.");
        command.Parameters.Clear(); command.CommandText = "INSERT INTO Settings(Key,Value) VALUES($key,$value)";
        command.Parameters.AddWithValue("$key", "SecurityRecovery:" + Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$value", audit); command.ExecuteNonQuery(); tx.Commit();
    }
}
