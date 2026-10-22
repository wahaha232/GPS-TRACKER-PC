using System.Globalization;
using System.Text;
using System.Xml;
using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Simulation.Routes;

/// <summary>匯入結果：除了路線，也回報原本有幾個點、簡化後留下幾個。</summary>
public sealed record GpxImportResult(SimulationRoute Route, long OriginalPointCount)
{
    public int KeptPointCount => Route.Waypoints.Count;

    public bool WasSimplified => OriginalPointCount > KeptPointCount;
}

public sealed class ImportCancelledException : Exception
{
    public ImportCancelledException() : base("Import cancelled")
    {
    }
}

/// <summary>
/// 容量固定的均勻抽樣：點一個一個進來、事先不知道總數，記憶體卻永遠不超過 2 × <paramref name="max"/> 個。
/// 做法是每隔 stride 個點保留一個；保留的點滿 2 × max 時，丟掉其中每隔一個的點並把 stride 加倍。
/// 第一個點一定在，最後一個點在 <see cref="Result"/> 時補上。
/// </summary>
public sealed class DecimatingReservoir<T>
{
    private readonly int _max;
    private readonly List<T> _kept = new();
    private readonly List<long> _keptIndex = new();
    private long _stride = 1L;
    private T? _last;

    public DecimatingReservoir(int max)
    {
        if (max < 2)
        {
            throw new ArgumentException("max 必須至少為 2", nameof(max));
        }

        _max = max;
    }

    /// <summary>到目前為止收到的點數（包含被丟掉的）。</summary>
    public long Count { get; private set; }

    public void Add(T item)
    {
        var index = Count++;
        _last = item;
        if (index % _stride != 0L)
        {
            return;
        }

        _kept.Add(item);
        _keptIndex.Add(index);
        if (_kept.Count >= 2 * _max)
        {
            Compact();
        }
    }

    private void Compact()
    {
        var write = 0;
        for (var read = 0; read < _kept.Count; read += 2)
        {
            _kept[write] = _kept[read];
            _keptIndex[write] = _keptIndex[read];
            write++;
        }

        while (_kept.Count > write)
        {
            _kept.RemoveAt(_kept.Count - 1);
            _keptIndex.RemoveAt(_keptIndex.Count - 1);
        }

        _stride *= 2;
    }

    /// <summary>最多 <see cref="_max"/> 個點、依原本順序，包含第一個與最後一個。</summary>
    public IReadOnlyList<T> Result()
    {
        if (Count == 0L)
        {
            return Array.Empty<T>();
        }

        var all = new List<T>(_kept);
        if (_keptIndex[^1] != Count - 1)
        {
            all.Add(_last!);
        }

        return LocationDocumentCodec.ThinIndices(all.Count, _max).Select(index => all[index]).ToList();
    }
}

/// <summary>
/// 串流方式匯入 GPX：邊讀邊簡化，不會把整個檔案載入記憶體，
/// 最後只留下最多 <see cref="MaxPoints"/> 個點（均勻抽稀，保留首尾與每個保留點的原始時間）。
/// 路線選取規則：有 trk 用第一條 trk（其中所有 trkseg 合併），沒有就用第一條 rte，再沒有就用全部 wpt。
/// </summary>
public static class GpxStreamImporter
{
    public const int MaxPoints = 3000;

    private const int PrologLimitBytes = 64 * 1024;
    private const long CancelCheckInterval = 1024L;
    private const int MaxTextChars = 4096;
    private const double DefaultSpeedKph = 30.0;

    public static GpxImportResult Import(
        Stream stream,
        string fallbackName,
        int maxPoints = MaxPoints,
        Func<bool>? isCancelled = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var (head, read) = ReadHead(stream, PrologLimitBytes);
        RejectDangerousProlog(head, read);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        using var reader = XmlReader.Create(new PrefixStream(head, read, stream), settings);
        var handler = new Handler(maxPoints, isCancelled ?? (() => false));
        handler.Read(reader);
        return handler.Build(fallbackName);
    }

    /// <summary>檔案開頭看起來是不是 GPX（用來決定走串流匯入還是其他格式）。</summary>
    public static bool LooksLikeGpx(byte[] head, int length) =>
        Encoding.Latin1.GetString(head, 0, length).ToLowerInvariant().Contains("<gpx", StringComparison.Ordinal);

    /// <summary>讀取開頭固定長度的位元組，之後再用 <see cref="PrefixStream"/> 接回原本的串流。</summary>
    internal static (byte[] Head, int Length) ReadHead(Stream stream, int limit)
    {
        var head = new byte[limit];
        var read = 0;
        while (read < head.Length)
        {
            var count = stream.Read(head, read, head.Length - read);
            if (count <= 0)
            {
                break;
            }

            read += count;
        }

        return (head, read);
    }

    /// <summary>DOCTYPE／ENTITY 只可能出現在根元素之前：先檢查開頭那一段，發現就拒絕。</summary>
    private static void RejectDangerousProlog(byte[] head, int read)
    {
        var text = Encoding.Latin1.GetString(head, 0, read).ToLowerInvariant();
        var rootAt = text.IndexOf("<gpx", StringComparison.Ordinal);
        if (rootAt < 0)
        {
            throw new ArgumentException("Not a GPX document");
        }

        var prolog = text[..rootAt];
        if (prolog.Contains("<!doctype", StringComparison.Ordinal))
        {
            throw new ArgumentException("DOCTYPE is not allowed");
        }

        if (prolog.Contains("<!entity", StringComparison.Ordinal))
        {
            throw new ArgumentException("Entities are not allowed");
        }
    }

    private sealed record RawPoint(
        long Index,
        double Latitude,
        double Longitude,
        string Name,
        long StopSeconds,
        long? OffsetSeconds);

    private sealed class Collector
    {
        public Collector(int maxPoints)
        {
            Points = new DecimatingReservoir<RawPoint>(maxPoints);
        }

        public DecimatingReservoir<RawPoint> Points { get; }

        public string Name { get; set; } = string.Empty;

        public double? SpeedKph { get; set; }

        public bool? Loop { get; set; }

        public bool? UseGpxTiming { get; set; }

        public bool? NaturalSpeed { get; set; }

        public List<RouteOutage> Outages { get; } = new();

        public long? FirstEpoch { get; set; }

        public long RunningMax { get; set; }

        public bool MissingTime { get; set; }
    }

    private enum CaptureKind
    {
        None,
        Name,
        Time,
        Stop,
        ContainerName,
        Speed,
        Loop,
        UseTiming,
        Natural,
    }

    private sealed class Handler
    {
        private static readonly HashSet<string> PointElements = new(StringComparer.Ordinal)
        {
            "trkpt",
            "rtept",
            "wpt",
        };

        private readonly int _maxPoints;
        private readonly Func<bool> _isCancelled;
        private readonly List<string> _stack = new();
        private readonly Collector _waypointCollector;
        private readonly StringBuilder _text = new();

        private Collector? _track;
        private Collector? _routeElement;
        private bool _inFirstTrack;
        private bool _inFirstRoute;
        private Collector? _point;
        private double _pointLat;
        private double _pointLon;
        private string _pointName = string.Empty;
        private string _pointTime = string.Empty;
        private string _pointStop = string.Empty;
        private CaptureKind _capture;
        private long _pointsSeen;

        public Handler(int maxPoints, Func<bool> isCancelled)
        {
            _maxPoints = maxPoints;
            _isCancelled = isCancelled;
            _waypointCollector = new Collector(maxPoints);
        }

        public void Read(XmlReader reader)
        {
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var name = reader.LocalName.ToLowerInvariant();
                        var parent = _stack.Count > 0 ? _stack[^1] : null;
                        _stack.Add(name);
                        StartElement(name, parent, reader);
                        // XmlReader 對 <trkpt/> 這類空元素不會另外回報 EndElement，要自己補上。
                        if (reader.IsEmptyElement)
                        {
                            EndElement(name);
                            _stack.RemoveAt(_stack.Count - 1);
                        }

                        break;
                    }

                    case XmlNodeType.EndElement:
                    {
                        var name = reader.LocalName.ToLowerInvariant();
                        EndElement(name);
                        if (_stack.Count > 0)
                        {
                            _stack.RemoveAt(_stack.Count - 1);
                        }

                        break;
                    }

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        Characters(reader.Value);
                        break;
                }
            }
        }

        private void StartElement(string name, string? parent, XmlReader reader)
        {
            switch (name)
            {
                case "trk":
                    if (_track == null)
                    {
                        _track = new Collector(_maxPoints);
                        _inFirstTrack = true;
                    }

                    break;
                case "rte":
                    if (_routeElement == null)
                    {
                        _routeElement = new Collector(_maxPoints);
                        _inFirstRoute = true;
                    }

                    break;
                case "trkpt":
                    if (_inFirstTrack)
                    {
                        StartPoint(_track!, reader);
                    }

                    break;
                case "rtept":
                    if (_inFirstRoute)
                    {
                        StartPoint(_routeElement!, reader);
                    }

                    break;
                case "wpt":
                    StartPoint(_waypointCollector, reader);
                    break;
            }

            if (_point != null)
            {
                // 點本身的子元素：name、time 要是直接子元素；stopSeconds 是擴充，任何深度都可以
                _capture = name switch
                {
                    "name" when parent != null && PointElements.Contains(parent) => CaptureKind.Name,
                    "time" when parent != null && PointElements.Contains(parent) => CaptureKind.Time,
                    "stopseconds" => CaptureKind.Stop,
                    _ => CaptureKind.None,
                };
            }
            else
            {
                var container = _inFirstTrack ? _track : _inFirstRoute ? _routeElement : null;
                _capture = container == null
                    ? CaptureKind.None
                    : name switch
                    {
                        "name" when parent is "trk" or "rte" => CaptureKind.ContainerName,
                        "speedkph" => CaptureKind.Speed,
                        "loop" => CaptureKind.Loop,
                        "usegpxtiming" => CaptureKind.UseTiming,
                        "naturalspeed" => CaptureKind.Natural,
                        _ => CaptureKind.None,
                    };

                if (name == "outage" && container != null)
                {
                    var startText = reader.GetAttribute("start")?.Trim();
                    var durationText = reader.GetAttribute("duration")?.Trim();
                    if (long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) &&
                        long.TryParse(durationText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration))
                    {
                        try
                        {
                            container.Outages.Add(new RouteOutage(start, duration));
                        }
                        catch (ArgumentException)
                        {
                            // 不合法的中斷設定直接忽略，和 Kotlin 版的 runCatching 一樣。
                        }
                    }
                }
            }

            if (_capture != CaptureKind.None)
            {
                _text.Clear();
            }
        }

        private void StartPoint(Collector collector, XmlReader reader)
        {
            _pointsSeen++;
            if (_pointsSeen % CancelCheckInterval == 0L && _isCancelled())
            {
                throw new ImportCancelledException();
            }

            _point = collector;
            var latitudeText = reader.GetAttribute("lat") ?? throw new ArgumentException("Missing lat");
            var longitudeText = reader.GetAttribute("lon") ?? throw new ArgumentException("Missing lon");
            _pointLat = double.Parse(latitudeText.Trim(), CultureInfo.InvariantCulture);
            _pointLon = double.Parse(longitudeText.Trim(), CultureInfo.InvariantCulture);
            _pointName = string.Empty;
            _pointTime = string.Empty;
            _pointStop = string.Empty;
        }

        private void Characters(string value)
        {
            if (_capture != CaptureKind.None && _text.Length < MaxTextChars)
            {
                _text.Append(value);
            }
        }

        private void EndElement(string name)
        {
            var value = _text.ToString().Trim();
            switch (_capture)
            {
                case CaptureKind.Name:
                    _pointName = value;
                    break;
                case CaptureKind.Time:
                    _pointTime = value;
                    break;
                case CaptureKind.Stop:
                    _pointStop = value;
                    break;
                case CaptureKind.ContainerName:
                    SetContainer(container =>
                    {
                        if (container.Name.Length == 0)
                        {
                            container.Name = value;
                        }
                    });
                    break;
                case CaptureKind.Speed:
                    SetContainer(container =>
                    {
                        if (container.SpeedKph == null)
                        {
                            container.SpeedKph = double.TryParse(
                                value,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out var parsed)
                                ? parsed
                                : null;
                        }
                    });
                    break;
                case CaptureKind.Loop:
                    SetContainer(container => container.Loop ??= ParseStrictBoolean(value));
                    break;
                case CaptureKind.UseTiming:
                    SetContainer(container => container.UseGpxTiming ??= ParseStrictBoolean(value));
                    break;
                case CaptureKind.Natural:
                    SetContainer(container => container.NaturalSpeed ??= ParseStrictBoolean(value));
                    break;
            }

            _capture = CaptureKind.None;

            switch (name)
            {
                case "trkpt":
                case "rtept":
                case "wpt":
                    FinishPoint(name);
                    break;
                case "trk":
                    _inFirstTrack = false;
                    break;
                case "rte":
                    _inFirstRoute = false;
                    break;
            }
        }

        private void SetContainer(Action<Collector> action)
        {
            var container = _inFirstTrack ? _track : _routeElement;
            if (container != null)
            {
                action(container);
            }
        }

        private void FinishPoint(string name)
        {
            var collector = _point;
            if (collector == null)
            {
                return;
            }

            var expected = ReferenceEquals(collector, _track)
                ? "trkpt"
                : ReferenceEquals(collector, _routeElement)
                    ? "rtept"
                    : "wpt";
            if (name != expected)
            {
                return;
            }

            _point = null;
            var epoch = ParseTimeSeconds(_pointTime);
            long? offset = null;
            if (epoch == null)
            {
                collector.MissingTime = true;
            }
            else
            {
                var first = collector.FirstEpoch;
                if (first == null)
                {
                    first = epoch.Value;
                    collector.FirstEpoch = first;
                }

                collector.RunningMax = Math.Max(collector.RunningMax, Math.Max(epoch.Value - first.Value, 0));
                offset = collector.RunningMax;
            }

            collector.Points.Add(
                new RawPoint(
                    Index: collector.Points.Count,
                    Latitude: _pointLat,
                    Longitude: _pointLon,
                    Name: _pointName,
                    StopSeconds: long.TryParse(_pointStop, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stop)
                        ? stop
                        : 0,
                    OffsetSeconds: offset));
        }

        public GpxImportResult Build(string fallbackName)
        {
            var collector = _track ?? _routeElement ?? _waypointCollector;
            var kept = collector.Points.Result();
            var hasTimes = !collector.MissingTime && collector.Points.Count >= 2;
            var waypoints = kept.Select(raw => new RouteWaypoint(
                    name: string.IsNullOrWhiteSpace(raw.Name) ? $"Point {raw.Index + 1}" : raw.Name,
                    coordinates: new Coordinates(raw.Latitude, raw.Longitude),
                    stopDurationSeconds: raw.StopSeconds,
                    timeOffsetSeconds: hasTimes ? raw.OffsetSeconds : null))
                .ToList();
            var route = new SimulationRoute(
                name: string.IsNullOrWhiteSpace(collector.Name) ? fallbackName : collector.Name,
                waypoints: waypoints,
                speedKph: Math.Clamp(
                    collector.SpeedKph ?? DefaultSpeedKph,
                    SimulationRoute.MinSpeedKph,
                    SimulationRoute.MaxSpeedKph),
                loop: collector.Loop ?? false,
                // 每個點都有時間戳記時預設依時間播放；本 App 匯出的檔案會記錄當初的設定。
                useGpxTiming: (collector.UseGpxTiming ?? true) && SimulationRoute.HasTiming(waypoints),
                outages: collector.Outages.ToList(),
                naturalSpeed: collector.NaturalSpeed ?? false);
            return new GpxImportResult(route, collector.Points.Count);
        }

        private static long? ParseTimeSeconds(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed.ToUnixTimeSeconds()
                : null;
        }

        private static bool? ParseStrictBoolean(string value) => value switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
    }

    /// <summary>先回放開頭讀過的位元組，再繼續讀原本的串流；不會把整份檔案載入記憶體。</summary>
    private sealed class PrefixStream : Stream
    {
        private readonly byte[] _head;
        private readonly int _headLength;
        private readonly Stream _inner;
        private int _headPosition;

        public PrefixStream(byte[] head, int headLength, Stream inner)
        {
            _head = head;
            _headLength = headLength;
            _inner = inner;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            if (_headPosition < _headLength)
            {
                var copied = Math.Min(count, _headLength - _headPosition);
                Array.Copy(_head, _headPosition, buffer, offset, copied);
                _headPosition += copied;
                return copied;
            }

            return _inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            if (_headPosition < _headLength)
            {
                var copied = Math.Min(buffer.Length, _headLength - _headPosition);
                _head.AsSpan(_headPosition, copied).CopyTo(buffer);
                _headPosition += copied;
                return copied;
            }

            return _inner.Read(buffer);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
