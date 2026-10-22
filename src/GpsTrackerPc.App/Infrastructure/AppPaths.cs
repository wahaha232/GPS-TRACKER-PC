using System.IO;

namespace GpsTrackerPc.App.Infrastructure;

/// <summary>PC 版固定使用的本機路徑。</summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GpsTrackerPc");

    public static string DataDirectory => Path.Combine(Root, "data");

    public static string GitHubTokenFile => Path.Combine(Root, "github-token.bin");

    public static string SyncCacheFile => Path.Combine(Root, "sync-cache.json");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string MapSettingsFile => Path.Combine(Root, "map-settings.json");

    public static string FavoritesFile => Path.Combine(Root, "favorites.json");

    public static string RoutesDirectory => Path.Combine(Root, "routes");
}
