using System;
using System.Collections.Generic;
using System.Text;

namespace OnePdf.Security;

/// <summary>
/// Turns a typed password into the byte strings the standard security handler hashes.
/// Each method returns candidates, best first: the encoding the specification prescribes, then lenient fallbacks that
/// writers are known to have used, so a file made by a sloppy producer still opens.
/// </summary>
internal static class PasswordPreparation
{
    // ISO 32000-1 7.6.3.3 Algorithm 2 step (a): the padding string.
    internal static ReadOnlySpan<byte> Padding =>
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    // ISO 32000-2 Annex D.2 (PDFDocEncoding), code points 0x18-0x1F and 0x80-0xA0. Every other code from 0x20 to 0xFF
    // (except 0x7F and 0xAD) equals Latin-1.
    private static readonly char[] PdfDocLow = ['\u02D8', '\u02C7', '\u02C6', '\u02D9', '\u02DD', '\u02DB', '\u02DA', '\u02DC'];

    private static readonly char[] PdfDocHigh =
    [
        '\u2022', '\u2020', '\u2021', '\u2026', '\u2014', '\u2013', '\u0192', '\u2044',
        '\u2039', '\u203A', '\u2212', '\u2030', '\u201E', '\u201C', '\u201D', '\u2018',
        '\u2019', '\u201A', '\u2122', '\uFB01', '\uFB02', '\u0141', '\u0152', '\u0160',
        '\u0178', '\u017D', '\u0131', '\u0142', '\u0153', '\u0161', '\u017E', '\uFFFF', '\u20AC',
    ];

    /// <summary>R 2 to 4 candidates: PDFDocEncoding, then Latin-1, then UTF-8 (each only when it differs and is representable).</summary>
    public static List<byte[]> ForLegacy(string password)
    {
        var list = new List<byte[]>(3);
        if (TryPdfDoc(password, out byte[]? pdfDoc))
        {
            list.Add(pdfDoc);
        }

        byte[]? latin1 = TryLatin1(password);
        if (latin1 is not null)
        {
            AddDistinct(list, latin1);
        }

        AddDistinct(list, Encoding.UTF8.GetBytes(password));
        return list;
    }

    /// <summary>
    /// R 5 and 6 candidates (ISO 32000-2 7.6.4.3.3 Algorithm 2.A step a): UTF-8 of the SASLprep form, truncated to 127
    /// bytes, then UTF-8 of the raw text.
    /// SASLprep (RFC 4013) here is a documented subset: non-ASCII spaces map to U+0020, characters "mapped to nothing"
    /// (soft hyphen, zero-width and variation selectors, BOM) are dropped, and the result is NFKC-normalized. The
    /// prohibited-character and bidirectional checks are not applied, so odd input is accepted instead of rejected.
    /// </summary>
    public static List<byte[]> ForAes256(string password)
    {
        var list = new List<byte[]>(2);
        AddDistinct(list, Truncate127(Encoding.UTF8.GetBytes(SaslPrep(password))));
        AddDistinct(list, Truncate127(Encoding.UTF8.GetBytes(password)));
        return list;
    }

    /// <summary>Pads or truncates to exactly 32 bytes with the standard padding string (Algorithm 2 step a).</summary>
    public static byte[] Pad32(ReadOnlySpan<byte> password)
    {
        byte[] r = new byte[32];
        int n = Math.Min(32, password.Length);
        password.Slice(0, n).CopyTo(r);
        Padding.Slice(0, 32 - n).CopyTo(r.AsSpan(n));
        return r;
    }

    internal static string SaslPrep(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (IsMappedToSpace(c))
            {
                sb.Append(' ');
            }
            else if (!IsMappedToNothing(c))
            {
                sb.Append(c);
            }
        }

        try
        {
            return PasswordNormalizer.NormalizeKc(sb.ToString());
        }
        catch (ArgumentException)
        {
            // Unpaired surrogates cannot be normalized; use the text as typed.
            return sb.ToString();
        }
    }

    private static bool IsMappedToSpace(char c) =>
        c is '\u00A0' or '\u1680' or (>= '\u2000' and <= '\u200B') or '\u202F' or '\u205F' or '\u3000';

    private static bool IsMappedToNothing(char c) =>
        c is '\u00AD' or '\u034F' or '\u1806' or (>= '\u180B' and <= '\u180D') or (>= '\u200C' and <= '\u200D')
            or '\u2060' or (>= '\uFE00' and <= '\uFE0F') or '\uFEFF';

    private static byte[] Truncate127(byte[] b) => b.Length <= 127 ? b : b.AsSpan(0, 127).ToArray();

    private static void AddDistinct(List<byte[]> list, byte[] candidate)
    {
        foreach (byte[] existing in list)
        {
            if (existing.AsSpan().SequenceEqual(candidate))
            {
                return;
            }
        }

        list.Add(candidate);
    }

    private static byte[]? TryLatin1(string s)
    {
        byte[] r = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] > 0xFF)
            {
                return null;
            }

            r[i] = (byte)s[i];
        }

        return r;
    }

    private static bool TryPdfDoc(string s, out byte[] result)
    {
        result = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            int code = c switch
            {
                (>= '\u0020' and <= '\u007E') or (>= '\u00A1' and <= '\u00FF' and not '\u00AD') => c,
                '\u0009' or '\u000A' or '\u000D' => c,
                _ => FindSpecial(c),
            };
            if (code < 0)
            {
                return false;
            }

            result[i] = (byte)code;
        }

        return true;
    }

    private static int FindSpecial(char c)
    {
        int i = Array.IndexOf(PdfDocLow, c);
        if (i >= 0)
        {
            return 0x18 + i;
        }

        if (c == '\uFFFF')
        {
            return -1;
        }

        i = Array.IndexOf(PdfDocHigh, c);
        return i >= 0 ? 0x80 + i : -1;
    }
}
