using System.Text;
using OnePdf.Internal;
using OnePdf.Security;

namespace OnePdf.Writing;

/// <summary>Pieces shared by the full and the incremental save.</summary>
internal abstract class SaverBase
{
    protected SaverBase(PdfDocument doc, PdfSaveOptions options, CancellationToken ct)
    {
        Doc = doc;
        Options = options;
        Ct = ct;
    }

    protected PdfDocument Doc { get; }

    protected PdfSaveOptions Options { get; }

    protected CancellationToken Ct { get; }

    protected ByteSink Sink { get; set; } = null!;

    protected ObjectWriter Writer { get; set; } = null!;

    // Trailers and the dictionaries of object and cross-reference streams already hold final references, which must not be mapped again.
    private ObjectWriter? _plain;

    private ObjectWriter Plain => _plain ??= new ObjectWriter(Doc, IdentityMapper.Instance, Options);

    /// <summary>Writes a stream object whose bytes are already in stored form; <c>/Length</c> is set here, after encryption.</summary>
    protected void WriteRawStream(int number, PdfDictionary dict, byte[] stored, SecurityDecryptor? enc)
    {
        if (enc is not null && stored.Length > 0)
        {
            stored = enc.EncryptStream(stored, number, 0, false, false).Data;
        }

        dict.SetLengthInternal(stored.Length);
        Sink.Long(number);
        Sink.Ascii(" 0 obj\n");
        Plain.WriteValue(Sink, dict, null, number, 0);
        Sink.Ascii("\nstream\n");
        Sink.Write(stored);
        Sink.Ascii("\nendstream\nendobj\n");
    }

    /// <summary>
    /// Writes a cross-reference section for <paramref name="entries"/> (sorted by number) followed by <c>startxref</c> and
    /// <c>%%EOF</c>. <paramref name="trailer"/> holds the trailer keys (Root, Info, Encrypt, ID, Prev). For a stream section
    /// <paramref name="xrefNumber"/> is the object number of the stream, which must be higher than every entry.
    /// </summary>
    protected void WriteXref(List<(int Num, XrefEntry E)> entries, int size, PdfDictionary trailer, bool asStream, int xrefNumber, long bias)
    {
        long start = Sink.Position;
        trailer.Set("Size", new PdfInteger(size));
        if (asStream)
        {
            entries.Add((xrefNumber, new XrefEntry(1, start - bias, 0)));
            byte[] rows = XrefWriter.StreamRows(entries, out int w2, out int w3);
            var dict = new PdfDictionary(null);
            dict.Set("Type", new PdfName("XRef"));
            for (int i = 0; i < trailer.RawCount; i++)
            {
                dict.Set(trailer.KeyAt(i), trailer.ValueAt(i));
            }

            var w = new PdfArray(null);
            w.Add(new PdfInteger(1));
            w.Add(new PdfInteger(w2));
            w.Add(new PdfInteger(w3));
            dict.Set("W", w);
            var index = new PdfArray(null);
            foreach ((int first, int count) in XrefWriter.Runs(entries))
            {
                index.Add(new PdfInteger(first));
                index.Add(new PdfInteger(count));
            }

            dict.Set("Index", index);
            dict.Set("Filter", new PdfName("FlateDecode"));
            var parms = new PdfDictionary(null);
            parms.Set("Predictor", new PdfInteger(12));
            parms.Set("Columns", new PdfInteger(1 + w2 + w3));
            dict.Set("DecodeParms", parms);
            WriteRawStream(xrefNumber, dict, FlateWriter.Compress(rows), null);
        }
        else
        {
            XrefWriter.WriteTable(Sink, entries);
            Sink.Ascii("trailer\n");
            Plain.WriteValue(Sink, trailer, null, -1, 0);
            Sink.Byte((byte)'\n');
        }

        Sink.Ascii("startxref\n");
        Sink.Long(start - bias);
        Sink.Ascii("\n%%EOF\n");
        Sink.Flush();
    }

    protected static PdfArray MakeId(byte[] first, byte[] second)
    {
        var id = new PdfArray(null);
        id.Add(new PdfString(first));
        id.Add(new PdfString(second));
        return id;
    }

    /// <summary>The two strings of the trailer's /ID, or null when it has none.</summary>
    protected static (byte[] First, byte[] Second)? ReadId(PdfDocument doc)
    {
        if (doc.Trailer.GetArray("ID") is { Count: >= 1 } ids && ids[0] is PdfString first)
        {
            byte[] second = ids.Count > 1 && ids[1] is PdfString s ? s.Bytes.ToArray() : first.Bytes.ToArray();
            return (first.Bytes.ToArray(), second);
        }

        return null;
    }

    /// <summary>Parses "1.7" style versions; anything unreadable is 1.4.</summary>
    protected static (int Major, int Minor) ParseVersion(string? v)
    {
        if (v is not null)
        {
            string[] parts = v.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor) && major is >= 1 and <= 9 && minor is >= 0 and <= 9)
            {
                return (major, minor);
            }
        }

        return (1, 4);
    }

    protected static string Higher(string a, string b)
    {
        (int, int) x = ParseVersion(a);
        (int, int) y = ParseVersion(b);
        return x.CompareTo(y) >= 0 ? $"{x.Item1}.{x.Item2}" : $"{y.Item1}.{y.Item2}";
    }

    protected static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);
}
