using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using M365Collector.Contracts;

namespace M365Collector.Security;

public static class PasswordHash
{
    public const int Iterations = 600_000;
    public static string Create(string password)
    {
        if (password.Length < 14 || password.Length > 1024) throw new ArgumentException("Use a local password of 14–1024 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    public static bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (password.Length > 1024 || parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations) || iterations < Iterations || iterations > 2_000_000) return false;
            return CryptographicOperations.FixedTimeEquals(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, 32), Convert.FromBase64String(parts[3]));
        }
        catch (FormatException) { return false; }
    }
}
public enum Capability { View, Collect, ManageCustomers, ManageUsers, InstallUpdates }
public static class Authorization
{
    public static bool Allows(LocalUser user, Capability capability) => user.Role == LocalRole.Administrator || capability == Capability.View || (user.Role == LocalRole.Operator && capability == Capability.Collect);
    public static void Require(LocalUser user, Capability capability) { if (!Allows(user, capability)) throw new UnauthorizedAccessException("Your application role does not permit this action."); }
}
public static class WindowsAcl
{
    public static SecurityIdentifier ServiceSid
    {
        get
        {
            // Windows service SIDs use SHA-1 of the upper-case UTF-16 service name.
            // This is an identifier calculation, not a password or integrity primitive.
            var hash = SHA1.HashData(System.Text.Encoding.Unicode.GetBytes(Product.ServiceName.ToUpperInvariant()));
            return new SecurityIdentifier("S-1-5-80-" + string.Join("-", Enumerable.Range(0, 5).Select(i => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(i * 4, 4)))));
        }
    }
    public static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static void ProtectDirectory(string path, bool serviceWrites)
    {
        Directory.CreateDirectory(path);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(ServiceSid, serviceWrites ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    public static void ProtectKey(string path)
    {
        var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.Read, AccessControlType.Allow)); new FileInfo(path).SetAccessControl(acl);
    }
}
