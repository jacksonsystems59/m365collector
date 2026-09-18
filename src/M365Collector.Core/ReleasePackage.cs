using System.Security.Cryptography;
using M365Collector.Contracts;

namespace M365Collector.Core;

public sealed record ReleaseFile(string Path, string Sha256);
public sealed record ReleaseManifest(int SchemaVersion, string Product, string Version, string RuntimeIdentifier,
    string GuiExecutable, string ServiceExecutable, int ConfigSchema, int DatabaseSchema, IReadOnlyList<ReleaseFile> Files);
public static class ReleasePackage
{
    public static ReleaseManifest Verify(string packageRoot)
    {
        var manifest=JsonFiles.Read<ReleaseManifest>(System.IO.Path.Combine(packageRoot,"release.json"));
        if(manifest.SchemaVersion!=1||manifest.Product!="M365Collector"||manifest.Version!=ProductInfo.Version||manifest.RuntimeIdentifier!="win-x64"
            ||manifest.ConfigSchema!=ProductInfo.ConfigSchema||manifest.DatabaseSchema!=ProductInfo.DatabaseSchema)
            throw new InvalidDataException("The release manifest does not match this application.");
        var unique=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in manifest.Files)
        {
            if(System.IO.Path.IsPathRooted(file.Path)||file.Path.Split('/', '\\').Any(part=>part is ".." or "." or "")||file.Path.Contains(':')||!unique.Add(file.Path))
                throw new InvalidDataException("Unsafe or duplicate package path.");
            var path=System.IO.Path.GetFullPath(System.IO.Path.Combine(packageRoot,file.Path));
            if(!RuntimePaths.Contains(System.IO.Path.GetFullPath(packageRoot),path))throw new InvalidDataException("Package file escapes its root.");
            for(var current=new FileInfo(path) as FileSystemInfo;current!=null;current=current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent)
            {
                if(current.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new InvalidDataException("Package paths must not contain symbolic links or junctions.");
                if(string.Equals(current.FullName,System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(packageRoot)),StringComparison.OrdinalIgnoreCase))break;
            }
            using var input=File.OpenRead(path);
            if(!Convert.ToHexString(SHA256.HashData(input)).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Package integrity check failed: "+file.Path);
        }
        if(!unique.Contains("M365Collector.exe")||!unique.Contains("service/M365Collector.Service.exe"))throw new InvalidDataException("The package is missing an application executable.");
        return manifest;
    }
}
