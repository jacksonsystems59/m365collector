using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace M365Collector.Security;

public static class Certificates
{
    public static X509Certificate2 Create(Guid identity)
    {
        var parameters = new CngKeyCreationParameters { Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider, KeyCreationOptions = CngKeyCreationOptions.MachineKey, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));
        using var key = CngKey.Create(CngAlgorithm.Rsa, "M365Collector-" + identity + "-" + Guid.NewGuid().ToString("N"), parameters);
        using var rsa = new RSACng(key);
        var request = new CertificateRequest("CN=M365Collector-" + identity, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        WindowsAcl.ProtectKey(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto", "Keys", key.UniqueName!));
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadWrite); store.Add(cert);
        return Find(cert.Thumbprint);
    }
    public static X509Certificate2 Find(string thumbprint)
    {
        if (thumbprint.Length != 40 || !thumbprint.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid certificate thumbprint.");
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        if (found.Count != 1) throw new InvalidOperationException("Certificate not found in LocalMachine/My.");
        var cert = found[0];
        if (!cert.HasPrivateKey || cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow || cert.NotBefore.ToUniversalTime() > DateTime.UtcNow) { cert.Dispose(); throw new InvalidOperationException("Certificate is missing its private key or is outside its validity period."); }
        using var key = cert.GetRSAPrivateKey() ?? throw new InvalidOperationException("An RSA private key is required.");
        _ = key.SignData(RandomNumberGenerator.GetBytes(32), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return cert;
    }
    public static byte[] PublicBytes(X509Certificate2 cert) => cert.Export(X509ContentType.Cert);
    public static void ExportPublic(X509Certificate2 cert, string path) => File.WriteAllBytes(path, PublicBytes(cert));
}
