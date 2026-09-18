using M365Collector.Contracts;

namespace M365Collector.Core;

public sealed class RuntimePaths
{
    public string Root { get; }
    public RuntimePaths(string root) => Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    public string Config => Path.Combine(Root, "Config", "runtime.json");
    public string Database => Path.Combine(Root, "Database", "collector.db");
    public string Health => Path.Combine(Root, "Service", "health.json");
    public string CustomerRoot(Guid tenantId) => tenantId == Guid.Empty
        ? throw new ArgumentException("A non-empty tenant ID is required.") : Path.Combine(Root, "Customers", tenantId.ToString("D"));
    public static readonly string[] Directories = ["Config", "Logs", "Database", "Cache", "Service", "Reports", "Exports", "Temp", "Customers"];
    public void Create()
    {
        Directory.CreateDirectory(Root);
        foreach (var name in Directories) Directory.CreateDirectory(Path.Combine(Root, name));
    }
    public void CreateCustomer(Guid tenantId)
    {
        foreach (var name in new[] { "Data", "Audit", "Reports", "Exports", "Cache" })
            Directory.CreateDirectory(Path.Combine(CustomerRoot(tenantId), name));
    }
    public static bool Contains(string parent, string child) =>
        string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(child), StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string Validate(string root, params string[] binaryRoots)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\") || root.Contains('"'))
            throw new ArgumentException("Choose an absolute local disk path, not a network share.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Skip(1)
            .Any(part => part.EndsWith('.') || part.EndsWith(' ') || part.Contains('~') || part.Contains(':')))
            throw new ArgumentException("Use the full canonical folder name without short-name aliases, trailing dots/spaces or alternate streams.");
        if (full.Equals(Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a dedicated folder, not a drive root.");
        foreach (var binary in binaryRoots.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var normalized = Path.GetFullPath(binary);
            if (Contains(normalized, full) || Contains(full, normalized))
                throw new ArgumentException("DataRoot and application/build directories must be separate, non-overlapping folders.");
        }
        foreach (var system in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(s => s.Length > 0))
            if (Contains(system, full)) throw new ArgumentException("DataRoot must be outside Windows and Program Files.");
        for (var dir = new DirectoryInfo(full); dir != null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("DataRoot must not use junctions or symbolic links.");
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a local fixed NTFS drive to support SQLite and Windows access controls.");
        return full;
    }
    public static long WriteTest(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".write-test-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(path, "M365Collector storage check"); }
        finally { if (File.Exists(path)) File.Delete(path); }
        return new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
    }
    public void RejectReparsePoints()
    {
        Reject(Root);
        static void Reject(string path)
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Runtime storage contains a junction or symbolic link.");
                if (entry is DirectoryInfo) Reject(entry.FullName);
            }
        }
    }
}
