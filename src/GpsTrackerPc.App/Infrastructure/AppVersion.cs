namespace GpsTrackerPc.App.Infrastructure;

/// <summary>版本號唯一來源是 GpsTrackerPc.App.csproj 的 &lt;Version&gt;，這裡只讀組件版本。</summary>
internal static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
