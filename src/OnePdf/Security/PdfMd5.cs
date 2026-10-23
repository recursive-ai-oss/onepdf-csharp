using System;
using System.Security.Cryptography;

namespace OnePdf.Security;

/// <summary>
/// MD5 for the file format's key derivation. The standard security handler of revisions 2 to 4 is defined in terms of
/// MD5 (ISO 32000-1 7.6.3, Algorithms 1 and 2), so reading those files needs it, and so does writing the revision 4 files the
/// writer can be asked for. This is the one place the analyzer's weak-hash rule is switched off.
/// </summary>
internal static class PdfMd5
{
#pragma warning disable CA5351 // MD5 is mandated by the PDF encryption algorithms; see the type summary.
    public static byte[] Hash(ReadOnlySpan<byte> data) => MD5.HashData(data);

    public static void Hash(ReadOnlySpan<byte> data, Span<byte> destination) => MD5.HashData(data, destination);
#pragma warning restore CA5351
}
