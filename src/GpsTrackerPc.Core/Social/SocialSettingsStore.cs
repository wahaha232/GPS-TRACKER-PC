using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Social;

/// <summary>好友／群組的本機設定；只存在這台電腦，不會上傳。</summary>
public sealed class SocialSettings
{
    public int Version { get; set; } = 1;

    /// <summary>這台電腦在好友分享中的固定識別碼，不會跟著裝置名稱改變。</summary>
    public string DeviceId { get; set; } = string.Empty;

    public bool ShareLocation { get; set; }

    public string Server { get; set; } = SocialLimits.DefaultServer;

    public List<SocialTarget> Targets { get; set; } = new();
}

/// <summary>
/// 讀寫 <c>%LOCALAPPDATA%\GpsTrackerPc\social.json</c>。
/// 第一次使用會產生固定的裝置識別碼；壞掉的資料會重建，不讓 App 起不來。
/// </summary>
public sealed class SocialSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    public SocialSettingsStore(string filePath, Func<DateTimeOffset>? clock = null)
    {
        _filePath = Path.GetFullPath(filePath);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string FilePath => _filePath;

    public SocialSettings Load()
    {
        lock (_gate)
        {
            SocialSettings settings;
            var needsSave = false;

            if (!File.Exists(_filePath))
            {
                settings = new SocialSettings();
                needsSave = true;
            }
            else
            {
                try
                {
                    settings = JsonSerializer.Deserialize<SocialSettings>(
                        File.ReadAllText(_filePath),
                        SerializerOptions) ?? new SocialSettings();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    settings = new SocialSettings();
                    needsSave = true;
                }
            }

            needsSave |= Normalize(settings);
            if (needsSave)
            {
                SaveCore(settings);
            }

            return settings;
        }
    }

    public void Save(SocialSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            Normalize(settings);
            SaveCore(settings);
        }
    }

    public SocialSettings AddTarget(SocialTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!SocialValidation.IsValidTarget(target))
        {
            throw new ArgumentException("好友／群組資料不完整。", nameof(target));
        }

        var settings = Load();
        if (settings.Targets.Count >= SocialLimits.MaxTargets &&
            !settings.Targets.Any(item => item.Kind == target.Kind && item.Id == target.Id))
        {
            throw new InvalidOperationException($"最多只能建立 {SocialLimits.MaxTargets} 個好友或群組。");
        }

        settings.Targets.RemoveAll(item => item.Kind == target.Kind && item.Id == target.Id);
        settings.Targets.Insert(0, target);
        Save(settings);
        return settings;
    }

    public SocialSettings RemoveTarget(SocialTargetKind kind, string id)
    {
        var settings = Load();
        if (settings.Targets.RemoveAll(item => item.Kind == kind && item.Id == id) > 0)
        {
            Save(settings);
        }

        return settings;
    }

    public SocialSettings SetShareLocation(bool enabled)
    {
        var settings = Load();
        if (settings.ShareLocation != enabled)
        {
            settings.ShareLocation = enabled;
            Save(settings);
        }

        return settings;
    }

    public SocialSettings SetServer(string? server)
    {
        var settings = Load();
        var normalized = SocialValidation.NormalizeServer(server);
        if (!string.Equals(settings.Server, normalized, StringComparison.Ordinal))
        {
            settings.Server = normalized;
            Save(settings);
        }

        return settings;
    }

    /// <summary>建立新的好友或群組分享連線；名稱空白時使用預設名稱。</summary>
    public SocialTarget CreateTarget(SocialTargetKind kind, string? name)
    {
        var fallback = kind == SocialTargetKind.Group ? "新群組" : "新好友";
        var targetName = SocialValidation.NormalizeName(name);
        if (targetName.Length == 0)
        {
            targetName = fallback;
        }

        var settings = Load();
        return SocialCrypto.CreateTarget(kind, targetName, settings.Server, _clock());
    }

    private void SaveCore(SocialSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    /// <summary>修正缺漏／不合法欄位；回傳是否有變更。</summary>
    private static bool Normalize(SocialSettings settings)
    {
        var changed = false;

        if (!SocialValidation.IsSafeId(settings.DeviceId) || settings.DeviceId.Length < 8)
        {
            settings.DeviceId = Guid.NewGuid().ToString("N");
            changed = true;
        }

        var server = SocialValidation.NormalizeServer(settings.Server);
        if (!string.Equals(settings.Server, server, StringComparison.Ordinal))
        {
            settings.Server = server;
            changed = true;
        }

        if (settings.Version != 1)
        {
            settings.Version = 1;
            changed = true;
        }

        if (settings.Targets is null)
        {
            settings.Targets = new List<SocialTarget>();
            changed = true;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var validTargets = new List<SocialTarget>();
        foreach (var target in settings.Targets ?? new List<SocialTarget>())
        {
            if (!SocialValidation.IsValidTarget(target))
            {
                changed = true;
                continue;
            }

            var key = ((int)target.Kind).ToString() + ":" + target.Id;
            if (!seen.Add(key))
            {
                changed = true;
                continue;
            }

            if (validTargets.Count >= SocialLimits.MaxTargets)
            {
                changed = true;
                break;
            }

            validTargets.Add(target);
        }

        if (validTargets.Count != (settings.Targets?.Count ?? 0))
        {
            settings.Targets = validTargets;
            changed = true;
        }

        return changed;
    }
}
