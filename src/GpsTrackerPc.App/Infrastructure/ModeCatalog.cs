namespace GpsTrackerPc.App.Infrastructure;

internal sealed record ModeOption(string Value, string Label);

/// <summary>和網頁版 EDITABLE_MODES 相同的移動方式清單。</summary>
internal static class ModeCatalog
{
    public static IReadOnlyList<ModeOption> Options { get; } = new List<ModeOption>
    {
        new("WALKING", "🚶 步行"),
        new("RUNNING", "🏃 跑步"),
        new("BICYCLE", "🚲 單車"),
        new("MOTORCYCLE", "🏍️ 機車"),
        new("CAR", "🚗 開車"),
        new("PUBLIC_TRANSPORT", "🚌 大眾運輸"),
        new("BUS", "🚌 公車"),
        new("TRAIN", "🚆 火車／捷運"),
        new("AIRPLANE", "✈️ 飛機"),
        new("BOAT", "⛵ 船"),
        new("UNKNOWN", "❓ 未知"),
    };

    public static string DisplayName(string? mode) =>
        Options.FirstOrDefault(option => option.Value == mode)?.Label ?? "❓ 未知";

    /// <summary>依平均速度給一個合理的預設移動方式；使用者仍可自行改選。</summary>
    public static string SuggestForSpeed(double kilometersPerHour) => kilometersPerHour switch
    {
        <= 6 => "WALKING",
        <= 12 => "RUNNING",
        <= 25 => "BICYCLE",
        <= 60 => "MOTORCYCLE",
        <= 160 => "CAR",
        _ => "UNKNOWN",
    };
}
