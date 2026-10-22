using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Simulation.Routes;

/// <summary>
/// 路線的 JSON 編解碼。格式與原本 Android 版的 org.json 版本相同，
/// 改用 System.Text.Json 實作，欄位順序與名稱不變。
/// </summary>
public static class RouteJsonCodec
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Encode(SimulationRoute route)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            WriteObject(writer, route);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static SimulationRoute? Decode(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return DecodeObject(document.RootElement);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string EncodeList(IReadOnlyList<SimulationRoute> routes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (var route in routes)
            {
                WriteObject(writer, route);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static IReadOnlyList<SimulationRoute> DecodeList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<SimulationRoute>();
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<SimulationRoute>();
            }

            var routes = new List<SimulationRoute>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                try
                {
                    routes.Add(DecodeObject(item));
                }
                catch (Exception)
                {
                    // 單筆壞掉就跳過，和 Kotlin 版一樣。
                }
            }

            return routes;
        }
        catch (Exception)
        {
            return Array.Empty<SimulationRoute>();
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, SimulationRoute route)
    {
        writer.WriteStartObject();
        writer.WriteString("id", route.Id);
        writer.WriteString("name", route.Name);
        writer.WriteNumber("speedKph", route.SpeedKph);
        writer.WriteBoolean("loop", route.Loop);
        writer.WriteNumber("createdAt", route.CreatedAt);
        writer.WriteBoolean("useGpxTiming", route.UseGpxTiming);
        writer.WriteBoolean("naturalSpeed", route.NaturalSpeed);

        writer.WritePropertyName("outages");
        writer.WriteStartArray();
        foreach (var outage in route.Outages)
        {
            writer.WriteStartObject();
            writer.WriteNumber("startSeconds", outage.StartSeconds);
            writer.WriteNumber("durationSeconds", outage.DurationSeconds);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("waypoints");
        writer.WriteStartArray();
        foreach (var waypoint in route.Waypoints)
        {
            writer.WriteStartObject();
            writer.WriteString("id", waypoint.Id);
            writer.WriteString("name", waypoint.Name);
            writer.WriteNumber("latitude", waypoint.Coordinates.Latitude);
            writer.WriteNumber("longitude", waypoint.Coordinates.Longitude);
            writer.WriteNumber("stopDurationSeconds", waypoint.StopDurationSeconds);
            if (waypoint.TimeOffsetSeconds is { } offset)
            {
                writer.WriteNumber("timeOffsetSeconds", offset);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static List<RouteOutage> DecodeOutages(JsonElement root)
    {
        var outages = new List<RouteOutage>();
        if (!root.TryGetProperty("outages", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return outages;
        }

        foreach (var item in array.EnumerateArray())
        {
            try
            {
                outages.Add(new RouteOutage(
                    item.GetProperty("startSeconds").GetInt64(),
                    item.GetProperty("durationSeconds").GetInt64()));
            }
            catch (Exception)
            {
                // 不合法的中斷設定直接忽略，和 Kotlin 版一樣。
            }
        }

        return outages;
    }

    private static SimulationRoute DecodeObject(JsonElement root)
    {
        var waypointsJson = root.GetProperty("waypoints");
        var waypoints = new List<RouteWaypoint>();
        foreach (var waypoint in waypointsJson.EnumerateArray())
        {
            var timeOffsetSeconds = waypoint.TryGetProperty("timeOffsetSeconds", out var timeOffset)
                ? timeOffset.GetInt64()
                : (long?)null;
            var stopDurationSeconds = waypoint.TryGetProperty("stopDurationSeconds", out var stopDuration)
                ? stopDuration.GetInt64()
                : 0L;
            waypoints.Add(new RouteWaypoint(
                name: waypoint.GetProperty("name").GetString() ?? string.Empty,
                coordinates: new Coordinates(
                    waypoint.GetProperty("latitude").GetDouble(),
                    waypoint.GetProperty("longitude").GetDouble()),
                stopDurationSeconds: stopDurationSeconds,
                timeOffsetSeconds: timeOffsetSeconds,
                id: waypoint.GetProperty("id").GetString()));
        }

        var useGpxTiming = root.TryGetProperty("useGpxTiming", out var timing) && timing.GetBoolean();
        var createdAt = root.TryGetProperty("createdAt", out var created)
            ? created.GetInt64()
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new SimulationRoute(
            name: root.GetProperty("name").GetString() ?? string.Empty,
            waypoints: waypoints,
            speedKph: root.GetProperty("speedKph").GetDouble(),
            loop: root.TryGetProperty("loop", out var loop) && loop.GetBoolean(),
            useGpxTiming: useGpxTiming && SimulationRoute.HasTiming(waypoints),
            outages: DecodeOutages(root),
            naturalSpeed: root.TryGetProperty("naturalSpeed", out var naturalSpeed) && naturalSpeed.GetBoolean(),
            createdAt: createdAt,
            id: root.GetProperty("id").GetString());
    }
}
