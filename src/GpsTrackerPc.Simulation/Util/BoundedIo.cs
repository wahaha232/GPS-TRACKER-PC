using System.Text;

namespace GpsTrackerPc.Simulation.Util;

/// <summary>讀取外部來源時一律設上限，避免超大檔案把記憶體吃光。</summary>
public static class BoundedIo
{
    /// <summary>路線檔（GPX／KML／本 App 的 JSON）上限。</summary>
    public const int RouteDocumentMaxBytes = 10 * 1024 * 1024;

    /// <summary>GPX 走串流解析，不會整個載入記憶體，所以上限可以放寬很多。</summary>
    public const long GpxStreamMaxBytes = 300L * 1024 * 1024;

    /// <summary>包裝 <paramref name="stream"/>：累計讀取超過 <paramref name="maximumBytes"/> 就丟 <see cref="IOException"/>。</summary>
    public static Stream Limit(Stream stream, long maximumBytes) => new LimitedStream(stream, maximumBytes);

    /// <summary>以 UTF-8 讀完 <paramref name="stream"/>；超過 <paramref name="maximumBytes"/> 就丟 <see cref="IOException"/>。</summary>
    public static string ReadText(Stream stream, int maximumBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = stream.Read(buffer, 0, buffer.Length);
            if (count <= 0)
            {
                break;
            }

            if (output.Length + count > maximumBytes)
            {
                throw new IOException($"Content is larger than {maximumBytes} bytes");
            }

            output.Write(buffer, 0, count);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maximumBytes;
        private long _total;

        public LimitedStream(Stream inner, long maximumBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _maximumBytes = maximumBytes;
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

        private int Account(int count)
        {
            if (count > 0)
            {
                _total += count;
                if (_total > _maximumBytes)
                {
                    throw new IOException($"Content is larger than {_maximumBytes} bytes");
                }
            }

            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Account(_inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Account(_inner.Read(buffer));

        public override int ReadByte()
        {
            var value = _inner.ReadByte();
            if (value >= 0)
            {
                Account(1);
            }

            return value;
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
