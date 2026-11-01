using System.Buffers;
using System.Security.Cryptography;

namespace OnePdf;

/// <summary>
/// The result of <see cref="PdfDocument.AddSignaturePlaceholder(Stream, PdfSigningOptions?, int, CancellationToken)"/>: where the
/// <c>/Contents</c> hole is in the written file, and the digest of everything outside it. Offsets are from the start of the written
/// output (the original file is its exact prefix), so they survive disposing the document and moving to another process.
/// </summary>
/// <param name="FieldName">The full name of the signature field that holds the signature.</param>
/// <param name="HoleStart">Offset of the opening <c>&lt;</c> of the <c>/Contents</c> hex string. The first signed range is <c>[0, HoleStart)</c>.</param>
/// <param name="HoleEnd">Offset just after the closing <c>&gt;</c>. The second signed range is <c>[HoleEnd, SignedLength)</c>. The hole is <c>2 * ContentsReserve + 2</c> bytes long.</param>
/// <param name="ContentsReserve">Bytes of CMS the hole can take (half the number of hex digits).</param>
/// <param name="SignedLength">Total length of the written output, which is where the second range ends.</param>
/// <param name="HashAlgorithm">The algorithm of <paramref name="Digest"/>, from <see cref="PdfSigningOptions.Hash"/>.</param>
/// <param name="Digest">The digest of the two signed ranges concatenated, the value a CMS <c>messageDigest</c> attribute carries.</param>
public sealed record PdfSignaturePlaceholder(
    string FieldName,
    long HoleStart,
    long HoleEnd,
    int ContentsReserve,
    long SignedLength,
    HashAlgorithmName HashAlgorithm,
    byte[] Digest)
{
    /// <summary>Writes a CMS into this placeholder's hole in a seekable, readable, writable stream holding the output. See <see cref="PdfSignatureHole.Fill(Stream, long, long, ReadOnlySpan{byte})"/>.</summary>
    /// <param name="output">The stream the placeholder was written to, or a copy of it.</param>
    /// <param name="cms">The DER of the CMS (or time-stamp token).</param>
    public void Fill(Stream output, ReadOnlySpan<byte> cms) => PdfSignatureHole.Fill(output, HoleStart, HoleEnd, cms);

    /// <summary>Writes a CMS into this placeholder's hole in the file the placeholder was written to. See <see cref="PdfSignatureHole.Fill(string, long, long, ReadOnlySpan{byte})"/>.</summary>
    /// <param name="path">The output file.</param>
    /// <param name="cms">The DER of the CMS (or time-stamp token).</param>
    public void Fill(string path, ReadOnlySpan<byte> cms) => PdfSignatureHole.Fill(path, HoleStart, HoleEnd, cms);
}

/// <summary>
/// Completes a signature written by <see cref="PdfDocument.AddSignaturePlaceholder(Stream, PdfSigningOptions?, int, CancellationToken)"/>
/// from the hole offsets alone, so the second phase can run in another process or on another machine. Nothing here needs a
/// <see cref="PdfDocument"/>.
/// </summary>
/// <remarks>
/// The hole is the whole <c>/Contents</c> hex string including its <c>&lt;</c> and <c>&gt;</c> (ISO 32000-2 12.8.1): offsets
/// <c>holeStart</c> (the <c>&lt;</c>) and <c>holeEnd</c> (just after the <c>&gt;</c>), as in
/// <see cref="PdfSignaturePlaceholder"/>. Only the hex digits change; no other byte of the file is touched.
/// </remarks>
public static class PdfSignatureHole
{
    /// <summary>Writes <paramref name="cms"/> as uppercase hex at the start of the hole; the remaining digits stay <c>0</c>.</summary>
    /// <param name="output">A seekable, readable and writable stream holding the output; left open, with its position restored.</param>
    /// <param name="holeStart">Offset of the opening <c>&lt;</c>.</param>
    /// <param name="holeEnd">Offset just after the closing <c>&gt;</c>.</param>
    /// <param name="cms">The DER of the CMS or time-stamp token; must not be empty.</param>
    /// <exception cref="ArgumentException">The stream cannot seek, read and write, the CMS is empty, or the offsets are not a valid hole inside the stream.</exception>
    /// <exception cref="PdfSigningException"><see cref="PdfSigningError.ContentsTooSmall"/> (<see cref="PdfSigningException.RequiredBytes"/> says how much is needed) or
    /// <see cref="PdfSigningError.BadHole"/>: the offsets do not hold an empty zero-filled hex string (already filled, or a different file). In both cases nothing was written.</exception>
    public static void Fill(Stream output, long holeStart, long holeEnd, ReadOnlySpan<byte> cms)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanSeek || !output.CanRead || !output.CanWrite)
        {
            throw new ArgumentException("The stream must support seeking, reading and writing.", nameof(output));
        }

        if (cms.IsEmpty)
        {
            throw new ArgumentException("The CMS is empty.", nameof(cms));
        }

        long hole = holeEnd - holeStart;
        if (holeStart < 0 || hole < 2 || hole > (2L * MaxCapacity) + 2 || holeEnd > output.Length || ((hole - 2) & 1) != 0)
        {
            throw new ArgumentException("The offsets are not a valid /Contents hole inside the stream.");
        }

        int capacity = (int)((hole - 2) / 2);
        if (cms.Length > capacity)
        {
            throw new PdfSigningException(PdfSigningError.ContentsTooSmall,
                $"The signature needs {cms.Length} bytes but the hole holds {capacity}; sign again with a larger contents reserve (at least {cms.Length}).")
            {
                RequiredBytes = cms.Length,
            };
        }

        long position = output.Position;
        byte[] rented = ArrayPool<byte>.Shared.Rent((int)hole);
        try
        {
            Span<byte> h = rented.AsSpan(0, (int)hole);
            output.Position = holeStart;
            output.ReadExactly(h);
            if (h[0] != (byte)'<' || h[^1] != (byte)'>' || h[1..^1].IndexOfAnyExcept((byte)'0') >= 0)
            {
                throw new PdfSigningException(PdfSigningError.BadHole, "The bytes at the hole offsets are not an empty, zero-filled /Contents string.");
            }

            const string digits = "0123456789ABCDEF";
            for (int i = 0; i < cms.Length; i++)
            {
                h[1 + (2 * i)] = (byte)digits[cms[i] >> 4];
                h[2 + (2 * i)] = (byte)digits[cms[i] & 15];
            }

            // One write of the digits that change keeps a failure from leaving a half-written hole.
            int used = 2 * cms.Length;
            output.Position = holeStart + 1;
            output.Write(h.Slice(1, used));
            output.Flush();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
            output.Position = position;
        }
    }

    /// <summary>Like <see cref="Fill(Stream, long, long, ReadOnlySpan{byte})"/>, on a file opened for exclusive read and write.</summary>
    /// <param name="path">The output file.</param>
    /// <param name="holeStart">Offset of the opening <c>&lt;</c>.</param>
    /// <param name="holeEnd">Offset just after the closing <c>&gt;</c>.</param>
    /// <param name="cms">The DER of the CMS or time-stamp token.</param>
    public static void Fill(string path, long holeStart, long holeEnd, ReadOnlySpan<byte> cms)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Fill(file, holeStart, holeEnd, cms);
    }

    /// <summary>
    /// Hashes the file outside the hole: <c>[0, holeStart)</c> then <c>[holeEnd, length)</c>. This is what
    /// <see cref="PdfSignaturePlaceholder.Digest"/> holds, for a second phase that only has the offsets and the file.
    /// </summary>
    /// <param name="output">A seekable, readable stream holding the output; left open, position restored.</param>
    /// <param name="holeStart">Offset of the opening <c>&lt;</c>.</param>
    /// <param name="holeEnd">Offset just after the closing <c>&gt;</c>.</param>
    /// <param name="hash">SHA-256, SHA-384 or SHA-512 (any algorithm the platform offers).</param>
    /// <returns>The digest.</returns>
    public static byte[] ComputeDigest(Stream output, long holeStart, long holeEnd, HashAlgorithmName hash)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanSeek || !output.CanRead)
        {
            throw new ArgumentException("The stream must support seeking and reading.", nameof(output));
        }

        if (holeStart < 0 || holeEnd < holeStart + 2 || holeEnd > output.Length)
        {
            throw new ArgumentException("The offsets are not a valid /Contents hole inside the stream.");
        }

        long position = output.Position;
        byte[] rented = ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            using IncrementalHash h = IncrementalHash.CreateHash(hash);
            Feed(output, h, rented, 0, holeStart);
            Feed(output, h, rented, holeEnd, output.Length - holeEnd);
            return h.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
            output.Position = position;
        }
    }

    private const int MaxCapacity = 4 << 20;

    private static void Feed(Stream s, IncrementalHash h, byte[] buffer, long start, long length)
    {
        s.Position = start;
        while (length > 0)
        {
            int n = s.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (n <= 0)
            {
                throw new EndOfStreamException();
            }

            h.AppendData(buffer, 0, n);
            length -= n;
        }
    }
}

/// <summary>A read-only seekable stream over a signature's two signed byte ranges, read through the document's source.</summary>
internal sealed class SignedDataStream : Stream
{
    private readonly PdfDocument _doc;
    private readonly long _firstLength;
    private readonly long _secondStart;
    private readonly long _length;
    private long _position;

    public SignedDataStream(PdfDocument doc, long firstLength, long secondStart, long secondLength)
    {
        _doc = doc;
        _firstLength = firstLength;
        _secondStart = secondStart;
        _length = checked(firstLength + secondLength);
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> destination)
    {
        int total = 0;
        while (!destination.IsEmpty && _position < _length)
        {
            long file;
            long left;
            if (_position < _firstLength)
            {
                file = _position;
                left = _firstLength - _position;
            }
            else
            {
                file = _secondStart + (_position - _firstLength);
                left = _length - _position;
            }

            int n = (int)Math.Min(left, destination.Length);
            _doc.ReadInto(file, destination[..n]);
            destination = destination[n..];
            _position += n;
            total += n;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target);
        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
