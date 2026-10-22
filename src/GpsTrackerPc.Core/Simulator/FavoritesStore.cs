using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GpsTrackerPc.Core.Util;
using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Core.Simulator;

public sealed record FavoritePlace(
    string Id,
    string Name,
    Coordinates Coordinates,
    long CreatedAt);

/// <summary>收藏地點存在 %LOCALAPPDATA%\GpsTrackerPc\favorites.json。</summary>
public sealed class FavoritesStore
{
    private const int MaxNameLength = 80;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly Func<DateTimeOffset> _clock;

    public FavoritesStore(string filePath, Func<DateTimeOffset>? clock = null)
    {
        _filePath = Path.GetFullPath(filePath);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string FilePath => _filePath;

    public IReadOnlyList<FavoritePlace> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<FavoritePlace>();
            }

            var document = JsonSerializer.Deserialize<FavoritesDocument>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return (document?.Places ?? new List<FavoritePlace>())
                .Where(place => place is not null && IsValid(place))
                .OrderByDescending(place => place.CreatedAt)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Array.Empty<FavoritePlace>();
        }
    }

    /// <summary>同一個座標視為同一個收藏，只更新名稱（和 Android 版規則相同）。</summary>
    public FavoritePlace Add(string name, Coordinates coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("收藏名稱不能是空白", nameof(name));
        }

        if (trimmed.Length > MaxNameLength)
        {
            trimmed = trimmed[..MaxNameLength];
        }

        var favorites = Load().ToList();
        var existingIndex = favorites.FindIndex(place =>
            place.Coordinates.Latitude == coordinates.Latitude &&
            place.Coordinates.Longitude == coordinates.Longitude);

        FavoritePlace favorite;
        if (existingIndex >= 0)
        {
            favorite = favorites[existingIndex] with { Name = trimmed };
            favorites[existingIndex] = favorite;
        }
        else
        {
            favorite = new FavoritePlace(
                Id: Guid.NewGuid().ToString(),
                Name: trimmed,
                Coordinates: coordinates,
                CreatedAt: _clock().ToUnixTimeMilliseconds());
            favorites.Insert(0, favorite);
        }

        Save(favorites);
        return favorite;
    }

    public bool Remove(string id)
    {
        var favorites = Load().ToList();
        var removed = favorites.RemoveAll(place => place.Id == id) > 0;
        if (removed)
        {
            Save(favorites);
        }

        return removed;
    }

    private void Save(IReadOnlyList<FavoritePlace> favorites)
    {
        var document = new FavoritesDocument { Places = favorites.ToList() };
        var json = JsonSerializer.Serialize(document, SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    private static bool IsValid(FavoritePlace place) =>
        place.Coordinates is not null &&
        !string.IsNullOrWhiteSpace(place.Id) &&
        !string.IsNullOrWhiteSpace(place.Name);

    private sealed class FavoritesDocument
    {
        public List<FavoritePlace> Places { get; set; } = new();
    }
}
