using System.Reflection;
namespace M365Collector.GUI;
internal static class AppIcons
{
    public static Icon ApplicationIcon { get; } = LoadIcon();
    private static Icon LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Icons.application.ico")!;
        using var icon = new Icon(stream); return (Icon)icon.Clone();
    }
    private static readonly Dictionary<string, Image> Images = new();
    public static Image? ForAction(string text)
    {
        var key = text switch
        {
            "Dashboard" => "dashboard", "All Customers" => "customers", "Add Customer" => "add",
            "Collection Modules" => "modules", "Audit Explorer" or "Search" => "search", "Failed Sign-ins" => "warning",
            "Location / IP Activity" or "Location / IP" => "location", "Reports" => "reports", "Administration" => "shield",
            "Settings · Updates" or "Install / upgrade & open" => "update", "Logs" => "logs", "LOCK" => "lock",
            "Forgot local login?" or "Reset local password" or "Reset password" => "key", "Run Now" => "play", "Export filtered CSV" => "export", _ => null
        };
        if (key == null) return null;
        if (!Images.TryGetValue(key, out var image))
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Icons." + key + ".png")!;
            using var original = Image.FromStream(stream); image = new Bitmap(original); Images[key] = image;
        }
        return image;
    }
}
