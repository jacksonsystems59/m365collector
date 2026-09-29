using M365Collector.Contracts;
using M365Collector.Storage;
namespace M365Collector.Security;
public sealed class LocalAccounts(CollectorStore store)
{
    public bool HasUsers => store.Read("SELECT COUNT(*) FROM Users", r => r.GetInt32(0)).Single() > 0;
    public void CreateFirst(string name, string password)
    {
        ValidateName(name); var hash = PasswordHash.Create(password);
        if (store.Write("INSERT INTO Users(Name,Hash,Role) SELECT $name,$hash,0 WHERE NOT EXISTS(SELECT 1 FROM Users)", ("$name", name.Trim()), ("$hash", hash)) != 1) throw new InvalidOperationException("An administrator already exists. Sign in to manage accounts.");
    }
    public void Add(LocalUser actor, string name, string password, LocalRole role)
    {
        Authorization.Require(actor, Capability.ManageUsers); ValidateName(name);
        if (!Enum.IsDefined(role)) throw new ArgumentException("Invalid role.");
        store.Write("INSERT INTO Users(Name,Hash,Role) VALUES($name,$hash,$role)", ("$name", name.Trim()), ("$hash", PasswordHash.Create(password)), ("$role", (int)role));
    }
    public LocalUser? Login(string name, string password)
    {
        var rows = store.Read("SELECT Name,Hash,Role,Failures,LockedUntil FROM Users WHERE Name=$name", r => (Name: r.GetString(0), Hash: r.GetString(1), Role: (LocalRole)r.GetInt32(2), Failures: r.GetInt32(3), Until: r.IsDBNull(4) ? null : r.GetString(4)), ("$name", name.Trim()));
        if (rows.Count == 0) { _ = PasswordHash.Verify(password, DummyHash); return null; }
        var user = rows[0]; if (user.Until != null && DateTimeOffset.Parse(user.Until) > DateTimeOffset.UtcNow) return null;
        if (!PasswordHash.Verify(password, user.Hash))
        {
            store.Write("UPDATE Users SET Failures=Failures+1,LockedUntil=CASE WHEN Failures+1>=5 THEN $until ELSE NULL END WHERE Name=$name", ("$until", DateTimeOffset.UtcNow.AddMinutes(15).ToString("O")), ("$name", name.Trim())); return null;
        }
        store.Write("UPDATE Users SET Failures=0,LockedUntil=NULL WHERE Name=$name", ("$name", name.Trim())); return new(user.Name, user.Role);
    }
    private static readonly string DummyHash = PasswordHash.Create("unused-random-" + Guid.NewGuid());
    private static void ValidateName(string name) { if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)) throw new ArgumentException("Enter a local username of 1–100 characters."); }
}
