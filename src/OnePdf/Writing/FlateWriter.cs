using System.Buffers;
using System.IO.Compression;

namespace OnePdf.Writing;

/// <summary>Flate (zlib, RFC 1950) compression for streams the writer produces.</summary>
internal static class FlateWriter
{
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        // The compressed bytes collect in a pooled buffer and are copied once into an array of exactly their size, so a large stream
        // allocates its result and nothing else (a growing MemoryStream would leave a copy of every doubling behind).
        using var sink = new PooledSink(Math.Max(256, data.Length / 4));
        using (var z = new ZLibStream(sink, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return sink.ToArray();
    }

    private sealed class PooledSink : Stream
    {
        private byte[] _buf;
        private int _length;

        public PooledSink(int capacity) => _buf = ArrayPool<byte>.Shared.Rent(capacity);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _buf.AsSpan(0, _length).ToArray();

        public override void Write(ReadOnlySpan<byte> source)
        {
            if (source.Length > _buf.Length - _length)
            {
                byte[] bigger = ArrayPool<byte>.Shared.Rent(checked(Math.Max(_buf.Length * 2, _length + source.Length)));
                _buf.AsSpan(0, _length).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(_buf);
                _buf = bigger;
            }

            source.CopyTo(_buf.AsSpan(_length));
            _length += source.Length;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && _buf.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_buf);
                _buf = [];
            }

            base.Dispose(disposing);
        }
    }
}
