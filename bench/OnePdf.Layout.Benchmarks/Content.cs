using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace LayoutBench;

/// <summary>The workload rows. The same content is generated for every engine; each engine lays it out with its own elements.</summary>
public enum Workload
{
    Invoice,
    Report50,
    Table10k,
    Book500,
    Mixed20,
}

/// <summary>Deterministic content shared by every engine: text, table data, the picture and the font files.</summary>
internal static class Content
{
    public const string Sans = "Liberation Sans";
    public const string Serif = "Liberation Serif";
    public const string Arabic = "Noto Sans Arabic";
    public const string Devanagari = "Noto Sans Devanagari";

    private static readonly string[] LatinWords =
    [
        "the", "of", "and", "a", "to", "in", "is", "you", "that", "it", "he", "was", "for", "on", "are", "as", "with", "his", "they", "at",
        "be", "this", "have", "from", "or", "one", "had", "by", "word", "but", "not", "what", "all", "were", "we", "when", "your", "can",
        "said", "there", "use", "an", "each", "which", "she", "do", "how", "their", "if", "will", "up", "other", "about", "out", "many",
        "then", "them", "these", "so", "some", "her", "would", "make", "like", "him", "into", "time", "has", "look", "two", "more", "write",
        "go", "see", "number", "no", "way", "could", "people", "my", "than", "first", "water", "been", "call", "who", "oil", "its", "now",
        "find", "long", "down", "day", "did", "get", "come", "made", "may", "part", "ledger", "quarterly", "revenue", "forecast", "inventory",
        "calibration", "assessment", "harmonic", "framework", "delivery", "operational", "margin", "estimate", "baseline", "schedule",
    ];

    private static readonly string[] ArabicWords =
    [
        "مرحبا", "بالعالم", "كتاب", "جميل", "الكتابة", "سلام", "عليكم", "اللغة", "العربية", "مدرسة", "الطالب", "يقرأ", "في", "المكتبة", "كل", "يوم",
        "الشمس", "تشرق", "من", "الشرق", "والقمر", "يظهر", "في", "الليل", "الماء", "حياة", "البحر", "كبير", "والسماء", "صافية",
    ];

    private static readonly string[] DevanagariWords =
    [
        "नमस्ते", "दुनिया", "भारत", "हिन्दी", "भाषा", "पुस्तक", "विद्यालय", "छात्र", "पढ़ता", "है", "पुस्तकालय", "में", "हर", "दिन",
        "सूरज", "पूर्व", "से", "उगता", "और", "चाँद", "रात", "को", "दिखता", "पानी", "जीवन", "समुद्र", "बड़ा", "आकाश", "साफ़",
    ];

    /// <summary>A small deterministic generator, so every run and every engine sees the same text.</summary>
    public sealed class Rng(uint seed)
    {
        private uint _s = seed;

        public int Next(int n)
        {
            _s = unchecked((_s * 1664525u) + 1013904223u);
            return (int)((_s >> 8) % (uint)n);
        }
    }

    public static string Words(Rng rng, string[] list, int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(list[rng.Next(list.Length)]);
        }

        return sb.ToString();
    }

    private static string Sentence(Rng rng, int words)
    {
        string s = Words(rng, LatinWords, words);
        return string.Concat(char.ToUpperInvariant(s[0]).ToString(), s.AsSpan(1), ".");
    }

    /// <summary>A Latin paragraph of about <paramref name="words"/> words, built of sentences.</summary>
    public static string Paragraph(Rng rng, int words)
    {
        var sb = new StringBuilder();
        int left = words;
        while (left > 0)
        {
            int n = Math.Min(left, 6 + rng.Next(10));
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(Sentence(rng, n));
            left -= n;
        }

        return sb.ToString();
    }

    public static string ArabicParagraph(Rng rng, int words) => Words(rng, ArabicWords, words) + ".";

    public static string DevanagariParagraph(Rng rng, int words) => Words(rng, DevanagariWords, words) + "।";

    public sealed record Item(string Description, int Quantity, decimal UnitPrice)
    {
        public decimal Total => Quantity * UnitPrice;
    }

    public static string Money(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>The invoice: the same fourteen lines for every document, a header block, a table and totals.</summary>
    public static List<Item> InvoiceItems(int seed = 7)
    {
        var rng = new Rng((uint)seed);
        var items = new List<Item>();
        for (int i = 0; i < 14; i++)
        {
            items.Add(new Item(Sentence(rng, 3 + rng.Next(5)), 1 + rng.Next(9), 5 + (rng.Next(900) / 4m)));
        }

        return items;
    }

    public static string[] TableRow(int i)
    {
        var rng = new Rng((uint)(i * 2654435761u));
        return
        [
            (100000 + i).ToString(CultureInfo.InvariantCulture),
            Words(rng, LatinWords, 2 + rng.Next(3)),
            Money(rng.Next(1_000_000) / 100m),
            rng.Next(3) == 0 ? "Pending" : "Settled",
        ];
    }

    /// <summary>A 240 x 140 RGB PNG (non-interlaced, 8-bit), made here with the BCL so every engine gets identical bytes.</summary>
    public static byte[] Picture { get; } = MakePng(240, 140);

    private static byte[] MakePng(int w, int h)
    {
        var raw = new byte[(w * 3 + 1) * h];
        int p = 0;
        for (int y = 0; y < h; y++)
        {
            raw[p++] = 0;
            for (int x = 0; x < w; x++)
            {
                raw[p++] = (byte)(x * 255 / (w - 1));
                raw[p++] = (byte)(y * 255 / (h - 1));
                raw[p++] = (byte)(((x / 20) + (y / 20)) % 2 == 0 ? 200 : 90);
            }
        }

        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
        {
            zs.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body, 4);
        s.Write(body);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in body)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, ~crc);
        s.Write(len);
    }

    // --- fonts ---------------------------------------------------------------------------------------------------------------------

    public static string FontDir { get; } = Path.Combine(AppContext.BaseDirectory, "fonts");

    public static string FontFile(string name) => Path.Combine(FontDir, name);

    public static byte[] FontBytes(string name) => File.ReadAllBytes(FontFile(name));

    /// <summary>Family name, weight-bold flag and italic flag to the Liberation file (and the two Noto subsets, regular only).</summary>
    public static string FileFor(string family, bool bold, bool italic)
    {
        if (family == Arabic)
        {
            return "NotoSansArabic-Subset-Regular.ttf";
        }

        if (family == Devanagari)
        {
            return "NotoSansDevanagari-Subset-Regular.ttf";
        }

        string stem = family == Serif ? "LiberationSerif" : "LiberationSans";
        string style = bold ? (italic ? "BoldItalic" : "Bold") : (italic ? "Italic" : "Regular");
        return $"{stem}-{style}.ttf";
    }

    public static readonly string[] AllFontFiles =
    [
        "LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf", "LiberationSans-Italic.ttf", "LiberationSans-BoldItalic.ttf",
        "LiberationSerif-Regular.ttf", "LiberationSerif-Bold.ttf", "LiberationSerif-Italic.ttf", "LiberationSerif-BoldItalic.ttf",
        "NotoSansArabic-Subset-Regular.ttf", "NotoSansDevanagari-Subset-Regular.ttf",
    ];

    // --- sizes of the rows (the same for every engine) -------------------------------------------------------------------------------

    public const int ReportSections = 50;
    public const int TableRows = 10_000;
    public const int BookParagraphs = 1_900;
    public const int MixedParagraphs = 219;
}

/// <summary>A stream that discards what it is given and counts it, so output size is known without holding the file.</summary>
internal class CountingStream : Stream
{
    public long Count { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => Count;

    public override long Position
    {
        get => Count;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Count += count;

    public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
}

/// <summary>A counting stream that reports its size when disposed.</summary>
internal sealed class SizeSink(Action<long> done) : CountingStream
{
    private bool _reported;

    protected override void Dispose(bool disposing)
    {
        // An engine may close the stream itself and the caller's using closes it again; the size is reported once.
        if (disposing && !_reported)
        {
            _reported = true;
            done(Count);
        }

        base.Dispose(disposing);
    }
}
