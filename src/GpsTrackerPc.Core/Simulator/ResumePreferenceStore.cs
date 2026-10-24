using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Simulator;

public enum RestartPromptChoice
{
    AlwaysAsk,
    Continue,
    SaveAndEnd,
    Discard,
}

/// <summary>記住使用者對「重啟後如何處理暫停模擬」的選擇，可在設定重設。</summary>
public sealed class ResumePreferenceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;

    public ResumePreferenceStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public RestartPromptChoice Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return RestartPromptChoice.AlwaysAsk;
            }

            var document = JsonSerializer.Deserialize<PreferenceDocument>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return document?.Choice is { } choice && Enum.IsDefined(choice)
                ? choice
                : RestartPromptChoice.AlwaysAsk;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return RestartPromptChoice.AlwaysAsk;
        }
    }

    public void Save(RestartPromptChoice choice)
    {
        var json = JsonSerializer.Serialize(
            new PreferenceDocument { Choice = choice },
            SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    public void Reset() => Save(RestartPromptChoice.AlwaysAsk);

    private sealed class PreferenceDocument
    {
        public RestartPromptChoice Choice { get; set; }
    }
}
