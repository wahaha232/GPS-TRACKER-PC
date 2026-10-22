using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Updates;

public sealed record UpdateCheckState(bool AutoCheckEnabled, long? LastCheckMs);

/// <summary>記錄自動檢查更新開關與上次檢查時間；啟動時一天最多檢查一次。</summary>
public sealed class UpdateCheckStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public UpdateCheckStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public UpdateCheckState Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new UpdateCheckState(AutoCheckEnabled: true, LastCheckMs: null);
            }

            var stored = JsonSerializer.Deserialize<StoredUpdateCheckState>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return new UpdateCheckState(
                stored?.AutoCheckEnabled ?? true,
                stored?.LastCheckMs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UpdateCheckState(AutoCheckEnabled: true, LastCheckMs: null);
        }
    }

    public bool ShouldAutoCheck(DateTimeOffset now)
    {
        var state = Load();
        if (!state.AutoCheckEnabled)
        {
            return false;
        }

        if (state.LastCheckMs is not { } lastCheckMs)
        {
            return true;
        }

        var elapsed = now.ToUnixTimeMilliseconds() - lastCheckMs;
        return elapsed >= TimeSpan.FromDays(1).TotalMilliseconds;
    }

    public void MarkChecked(DateTimeOffset now) =>
        Save(new UpdateCheckState(Load().AutoCheckEnabled, now.ToUnixTimeMilliseconds()));

    public void SetAutoCheck(bool enabled)
    {
        var state = Load();
        Save(new UpdateCheckState(enabled, state.LastCheckMs));
    }

    public void Save(UpdateCheckState state)
    {
        var json = JsonSerializer.Serialize(
            new StoredUpdateCheckState
            {
                AutoCheckEnabled = state.AutoCheckEnabled,
                LastCheckMs = state.LastCheckMs,
            },
            SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    private sealed class StoredUpdateCheckState
    {
        public bool? AutoCheckEnabled { get; set; }

        public long? LastCheckMs { get; set; }
    }
}
