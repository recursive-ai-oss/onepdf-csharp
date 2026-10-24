using System.Globalization;
using System.Text;
using OnePdf.Security;

namespace OnePdf.Writing;

/// <summary>Decides what a reference becomes in the output: a renumbered reference, or null when it leads nowhere.</summary>
internal interface IReferenceMapper
{
    PdfReference? Map(PdfReference reference);
}

/// <summary>Leaves references as they are (incremental saves keep the numbering).</summary>
internal sealed class IdentityMapper : IReferenceMapper
{
    public static readonly IdentityMapper Instance = new();

    public PdfReference? Map(PdfReference reference) => reference;
}

/// <summary>
/// Serializes objects in the PDF syntax of ISO 32000-2 7.3. Nesting is walked with an explicit stack, so the depth of a
/// hostile or generated object costs no call stack; a depth beyond <see cref="PdfLimits.MaxNestingDepth"/> is refused.
/// </summary>
internal sealed class ObjectWriter
{
    private readonly PdfDocument _doc;
    private readonly IReferenceMapper _map;
    private readonly PdfSaveOptions _options;
    private readonly List<(PdfObject Node, int Index)> _stack = [];

    public ObjectWriter(PdfDocument doc, IReferenceMapper map, PdfSaveOptions options)
    {
        _doc = doc;
        _map = map;
        _options = options;
    }

    /// <summary>
    /// Writes <c>n g obj ... endobj</c>. Strings and stream data are encrypted with <paramref name="enc"/> when it is given.
    /// </summary>
    public void WriteIndirect(ByteSink sink, int number, int generation, PdfObject value, SecurityDecryptor? enc)
    {
        sink.Long(number);
        sink.Byte((byte)' ');
        sink.Long(generation);
        sink.Ascii(" obj\n");
        if (value is PdfStream stream)
        {
            PreparedStream p = PrepareStream(stream);
            ReadOnlyMemory<byte> data = p.Data;
            if (enc is not null && data.Length > 0)
            {
                string? type = stream.Dictionary.GetName("Type");
                data = enc.EncryptStream(data.Span, number, generation, type == "Metadata", type == "EmbeddedFile").Data;
            }

            p.Dictionary.SetLengthInternal(data.Length);
            WriteValue(sink, p.Dictionary, enc, number, generation);
            sink.Ascii("\nstream\n");
            sink.Write(data.Span);
            sink.Ascii("\nendstream");
        }
        else
        {
            WriteValue(sink, value, enc, number, generation);
        }

        sink.Ascii("\nendobj\n");
    }

    /// <summary>Writes one value (no <c>obj</c> wrapper). With <paramref name="enc"/> null, strings are written as they are.</summary>
    public void WriteValue(ByteSink sink, PdfObject root, SecurityDecryptor? enc, int number, int generation)
    {
        _stack.Clear();
        int maxDepth = _doc.Limits.MaxNestingDepth;
        Emit(sink, root, enc, number, generation, maxDepth);
        while (_stack.Count > 0)
        {
            int top = _stack.Count - 1;
            (PdfObject node, int i) = _stack[top];
            if (node is PdfDictionary d)
            {
                if (i >= d.RawCount)
                {
                    sink.Ascii(">>");
                    _stack.RemoveAt(top);
                    continue;
                }

                _stack[top] = (node, i + 1);
                string key = d.KeyAt(i);
                WriteName(sink, key);
                PdfObject entry = d.ValueAt(i);

                // ISO 32000-2 7.6.2: the /Contents string of a signature dictionary is never encrypted, since the signature covers the
                // file bytes as they lie. The /ByteRange entry is what marks the dictionary.
                SecurityDecryptor? stringKey = enc is not null && entry is PdfString && key == "Contents" && d.ContainsKey("ByteRange") ? null : enc;
                Emit(sink, entry, stringKey, number, generation, maxDepth);
            }
            else
            {
                var a = (PdfArray)node;
                if (i >= a.Count)
                {
                    sink.Byte((byte)']');
                    _stack.RemoveAt(top);
                    continue;
                }

                _stack[top] = (node, i + 1);
                Emit(sink, a.GetRaw(i), enc, number, generation, maxDepth);
            }
        }
    }

    private void Emit(ByteSink sink, PdfObject value, SecurityDecryptor? enc, int number, int generation, int maxDepth)
    {
        switch (value)
        {
            case PdfDictionary:
            case PdfArray:
                if (_stack.Count >= maxDepth)
                {
                    throw new PdfLimitException($"Arrays and dictionaries are nested deeper than {maxDepth} levels.") { ObjectNumber = number };
                }

                sink.Ascii(value is PdfDictionary ? "<<" : "[");
                _stack.Add((value, 0));
                break;

            case PdfStream:
                // A stream is always an indirect object; one found nested directly is written as null.
                sink.Ascii(" null");
                break;

            case PdfInteger i:
                sink.Byte((byte)' ');
                sink.Long(i.Value);
                break;

            case PdfReal r:
                sink.Byte((byte)' ');
                sink.Ascii(FormatReal(r.Value));
                break;

            case PdfBoolean b:
                sink.Ascii(b.Value ? " true" : " false");
                break;

            case PdfName n:
                WriteName(sink, n.Value);
                break;

            case PdfString s:
                WriteString(sink, s.Bytes.Span, enc, number, generation);
                break;

            case PdfReference rf:
                if (_map.Map(rf) is { } mapped)
                {
                    sink.Byte((byte)' ');
                    sink.Long(mapped.Number);
                    sink.Byte((byte)' ');
                    sink.Long(mapped.Generation);
                    sink.Ascii(" R");
                }
                else
                {
                    sink.Ascii(" null");
                }

                break;

            default:
                sink.Ascii(" null");
                break;
        }
    }

    /// <summary>A real as plain decimal digits (no exponent, which PDF does not allow), at most 16 fractional digits.</summary>
    internal static string FormatReal(double v)
    {
        if (!double.IsFinite(v))
        {
            return "0";
        }

        string s = v.ToString("0.################", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    private static void WriteName(ByteSink sink, string name)
    {
        sink.Byte((byte)'/');
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        foreach (byte c in bytes)
        {
            // ISO 32000-2 7.3.5: regular characters other than '#' are written as they are, all else as #xx.
            if (c is >= 0x21 and <= 0x7E && c is not ((byte)'#' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%'))
            {
                sink.Byte(c);
            }
            else
            {
                sink.Byte((byte)'#');
                sink.Byte(HexDigit(c >> 4));
                sink.Byte(HexDigit(c & 15));
            }
        }
    }

    private static byte HexDigit(int v) => (byte)(v < 10 ? '0' + v : 'A' + v - 10);

    private static void WriteString(ByteSink sink, ReadOnlySpan<byte> bytes, SecurityDecryptor? enc, int number, int generation)
    {
        byte[]? encrypted = null;
        if (enc is not null && bytes.Length > 0)
        {
            encrypted = enc.EncryptString(bytes, number, generation).Data;
            bytes = encrypted;
        }

        bool literal = encrypted is null;
        if (literal)
        {
            foreach (byte b in bytes)
            {
                if (b is < 0x20 or > 0x7E)
                {
                    literal = false;
                    break;
                }
            }
        }

        if (literal)
        {
            sink.Byte((byte)'(');
            foreach (byte b in bytes)
            {
                if (b is (byte)'(' or (byte)')' or (byte)'\\')
                {
                    sink.Byte((byte)'\\');
                }

                sink.Byte(b);
            }

            sink.Byte((byte)')');
            return;
        }

        sink.Byte((byte)'<');
        foreach (byte b in bytes)
        {
            sink.Byte(HexDigit(b >> 4));
            sink.Byte(HexDigit(b & 15));
        }

        sink.Byte((byte)'>');
    }

    private readonly record struct PreparedStream(PdfDictionary Dictionary, ReadOnlyMemory<byte> Data);

    /// <summary>
    /// The dictionary and the plain stored bytes (decrypted, still filtered) to write for a stream. A <c>Crypt</c> filter is
    /// dropped, since it only named the decryption that was just undone; unfiltered data is Flate-compressed on request.
    /// </summary>
    private PreparedStream PrepareStream(PdfStream s)
    {
        IReadOnlyList<string> filters = s.FilterNames;
        // The stored bytes are only read, so they are not copied unless they get compressed.
        ReadOnlyMemory<byte> data = s.IsExternal ? default : s.GetStoredBytes(filters);
        var dict = new PdfDictionary(null);
        bool dropCrypt = filters.Count > 0 && filters[0] == "Crypt";
        PdfDictionary src = s.Dictionary;
        for (int i = 0; i < src.RawCount; i++)
        {
            string key = src.KeyAt(i);
            if (key == "Length" || (dropCrypt && key is "Filter" or "DecodeParms"))
            {
                continue;
            }

            dict.Set(key, src.ValueAt(i));
        }

        if (dropCrypt)
        {
            RestWithoutFirst(src.Get("Filter"), "Filter", dict);
            RestWithoutFirst(src.Get("DecodeParms"), "DecodeParms", dict);
        }

        bool unfiltered = !dict.ContainsKey("Filter");
        string? type = src.GetName("Type");
        if (_options.CompressStreams && unfiltered && !s.IsExternal && data.Length >= 64 && type is not ("Metadata" or "XRef" or "ObjStm"))
        {
            byte[] packed = FlateWriter.Compress(data.Span);
            if (packed.Length < data.Length)
            {
                data = packed;
                dict.Set("Filter", new PdfName("FlateDecode"));
                dict.Remove("DecodeParms");
            }
        }

        return new PreparedStream(dict, data);
    }

    private static void RestWithoutFirst(PdfObject? value, string key, PdfDictionary into)
    {
        if (value is not PdfArray arr || arr.Count <= 1)
        {
            return;
        }

        var rest = new PdfArray(null);
        for (int i = 1; i < arr.Count; i++)
        {
            rest.Add(arr.GetRaw(i));
        }

        into.Set(key, rest.Count == 1 ? rest.GetRaw(0) : rest);
    }
}
