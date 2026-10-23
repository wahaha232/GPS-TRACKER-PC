using System.Text;
using GpsTrackerPc.Core.Util;
using GpsTrackerPc.Simulation.Routes;

namespace GpsTrackerPc.Core.Simulator;

/// <summary>
/// 多條路線存成 %LOCALAPPDATA%\GpsTrackerPc\routes\&lt;routeId&gt;.json；
/// 檔案內容直接使用 RouteJsonCodec，和模擬端的路線 JSON 相同。
/// </summary>
public sealed class RouteLibraryStore
{
    private readonly string _directory;

    public RouteLibraryStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
    }

    public string DirectoryPath => _directory;

    public void Save(SimulationRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var path = GetPath(route.Id);
        AtomicFile.WriteAllBytes(path, Encoding.UTF8.GetBytes(RouteJsonCodec.Encode(route)));
    }

    public SimulationRoute? Load(string id)
    {
        var path = GetPath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return RouteJsonCodec.Decode(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<SimulationRoute> LoadAll()
    {
        if (!Directory.Exists(_directory))
        {
            return Array.Empty<SimulationRoute>();
        }

        var routes = new List<SimulationRoute>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var route = RouteJsonCodec.Decode(File.ReadAllText(file));
                if (route != null)
                {
                    routes.Add(route);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // 單一檔案壞掉不影響其他路線。
            }
        }

        return routes
            .OrderByDescending(route => route.CreatedAt)
            .ToList();
    }

    public bool Delete(string id)
    {
        var path = GetPath(id);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private string GetPath(string id)
    {
        if (!IsSafeId(id))
        {
            throw new ArgumentException("路線 ID 不合法", nameof(id));
        }

        System.IO.Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, id + ".json");
    }

    private static bool IsSafeId(string id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.Length <= 200 &&
        id == Path.GetFileName(id) &&
        !id.Contains("..", StringComparison.Ordinal) &&
        id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
