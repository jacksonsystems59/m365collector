using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using M365Collector.Contracts;

namespace M365Collector.Security;

public sealed class RoleAuthorization : IAuthorizationPolicy
{
    public bool Allows(ApplicationRole role, ApplicationAction action) => role == ApplicationRole.Administrator
        || (role == ApplicationRole.Operator && action is ApplicationAction.View or ApplicationAction.Collect)
        || (role == ApplicationRole.ReadOnly && action == ApplicationAction.View);
}
public static class WindowsSecurity
{
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static string CurrentSid => WindowsIdentity.GetCurrent().User!.Value;
    public static void RequireAdministrator() { if (!IsAdministrator) throw new UnauthorizedAccessException("Sign in using an authorised Windows administrator and run elevated."); }
    public static SecurityIdentifier ServiceSid => (SecurityIdentifier)new NTAccount(@"NT SERVICE\M365CollectorService").Translate(typeof(SecurityIdentifier));
    public static void ProtectBinaryDirectory(string path)
    {
        RequireAdministrator();
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    public static void ProtectDirectory(string path, bool allowService)
    {
        RequireAdministrator();
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        if (allowService) acl.AddAccessRule(new FileSystemAccessRule(ServiceSid, FileSystemRights.Modify, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
}
public sealed class CertificateStore
{
    public CertificateReference Create(Guid tenantId)
    {
        WindowsSecurity.RequireAdministrator();
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant ID is required.");
        var parameters = new CngKeyCreationParameters { Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyCreationOptions = CngKeyCreationOptions.MachineKey, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));
        using var key = CngKey.Create(CngAlgorithm.Rsa, "M365Collector-" + tenantId + "-" + Guid.NewGuid().ToString("N"), parameters);
        try
        {
            using var rsa = new RSACng(key);
            var request = new CertificateRequest($"CN=M365Collector-{tenantId:D}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
            certificate.FriendlyName = $"M365Collector {tenantId:D}";
            ProtectKey(key);
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadWrite); store.Add(certificate);
            return new CertificateReference(certificate.Thumbprint);
        }
        catch { key.Delete(); throw; }
    }
    public X509Certificate2 Open(CertificateReference reference)
    {
        if (reference.Location != "LocalMachine" || reference.Store != "My" || reference.Thumbprint.Length != 40 || !reference.Thumbprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid machine certificate reference.");
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, reference.Thumbprint, false);
        if (matches.Count != 1) throw new InvalidOperationException("Certificate not found uniquely in LocalMachine/My.");
        var certificate = matches[0];
        if (!certificate.HasPrivateKey || DateTime.Now < certificate.NotBefore || DateTime.Now >= certificate.NotAfter)
        { certificate.Dispose(); throw new InvalidOperationException("Certificate has no private key or is outside its validity period."); }
        return certificate;
    }
    public void PrepareForService(CertificateReference reference)
    {
        WindowsSecurity.RequireAdministrator();
        using var certificate = Open(reference); using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is not RSACng cng || !cng.Key.IsMachineKey) throw new NotSupportedException("v0.1.0 requires an RSA CNG machine key. Create a certificate here or import one using Microsoft Software Key Storage Provider.");
        if (cng.Key.ExportPolicy != CngExportPolicies.None) throw new InvalidOperationException("Use a non-exportable certificate private key.");
        ProtectKey(cng.Key);
    }
    public byte[] PublicCertificate(CertificateReference reference) { using var certificate = Open(reference); return certificate.Export(X509ContentType.Cert); }
    private static void ProtectKey(CngKey key)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto", "Keys", key.UniqueName!);
        var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(WindowsSecurity.ServiceSid, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
}
