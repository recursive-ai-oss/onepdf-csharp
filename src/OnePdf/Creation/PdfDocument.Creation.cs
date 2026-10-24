using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using OnePdf.FontData;
using OnePdf.Forms;
using OnePdf.Images;

namespace OnePdf;

/// <summary>Colour model of the samples given to <see cref="PdfDocument.AddRawImage"/>.</summary>
public enum PdfImageColor
{
    /// <summary>One 8-bit sample per pixel (DeviceGray).</summary>
    Gray = 1,

    /// <summary>Three 8-bit samples per pixel: red, green, blue (DeviceRGB).</summary>
    Rgb = 3,

    /// <summary>Four 8-bit samples per pixel: cyan, magenta, yellow, black, 0 = no ink (DeviceCMYK).</summary>
    Cmyk = 4,
}

/// <summary>An image XObject added to a document by <see cref="PdfDocument.AddJpegImage"/> or <see cref="PdfDocument.AddRawImage"/>; draw it with <see cref="PdfContentBuilder.DrawImage(PdfCreatedImage, double, double, double, double)"/>, any number of times.</summary>
public sealed class PdfCreatedImage
{
    internal PdfCreatedImage(PdfDocument document, PdfReference reference, int width, int height, bool hasAlpha)
    {
        Document = document;
        Reference = reference;
        Width = width;
        Height = height;
        HasAlpha = hasAlpha;
    }

    /// <summary>The document the image belongs to.</summary>
    public PdfDocument Document { get; }

    /// <summary>The indirect reference of the image stream.</summary>
    public PdfReference Reference { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>True when the image has a soft mask made from alpha samples.</summary>
    public bool HasAlpha { get; }
}

/// <summary>A Form XObject made by <see cref="PdfDocument.BeginForm"/>: content drawn once, placed many times with <see cref="PdfContentBuilder.DrawForm(PdfFormXObject)"/>.</summary>
public sealed class PdfFormXObject
{
    internal PdfFormXObject(PdfDocument document, PdfReference reference, PdfRectangle bbox)
    {
        Document = document;
        Reference = reference;
        BoundingBox = bbox;
    }

    /// <summary>The document the form belongs to.</summary>
    public PdfDocument Document { get; }

    /// <summary>The indirect reference of the form stream.</summary>
    public PdfReference Reference { get; }

    /// <summary>The form's bounding box in its own coordinates; content outside it is clipped.</summary>
    public PdfRectangle BoundingBox { get; }
}

/// <summary>Per-document state of content creation: fonts to finish at save time and the shared <c>q</c>/<c>Q</c> streams.</summary>
internal sealed class CreationState
{
    private static readonly ConditionalWeakTable<PdfDocument, CreationState> s_states = new();

    public Dictionary<string, StandardCreatedFont> Standard { get; } = new(StringComparer.Ordinal);

    public List<EmbeddedCreatedFont> Embedded { get; } = [];

    /// <summary>Object numbers of standard 14 font dictionaries that the library itself made for generated appearances.</summary>
    public HashSet<int> LibraryFonts { get; } = [];

    public PdfReference? QRef { get; set; }

    public PdfReference? BigQRef { get; set; }

    public static CreationState For(PdfDocument doc) => s_states.GetValue(doc, static _ => new CreationState());

    /// <summary>Embedded fonts (object number and name) whose recorded text referenced glyph 0.</summary>
    public static List<(int Number, string Name)> NotdefFonts(PdfDocument doc)
    {
        var list = new List<(int, string)>();
        if (s_states.TryGetValue(doc, out CreationState? state))
        {
            foreach (EmbeddedCreatedFont f in state.Embedded)
            {
                if (f.UsedNotdef)
                {
                    list.Add((f.Reference?.Number ?? -1, f.BaseFontName));
                }
            }
        }

        return list;
    }

    public static void FinishFonts(PdfDocument doc)
    {
        if (s_states.TryGetValue(doc, out CreationState? state))
        {
            foreach (EmbeddedCreatedFont f in state.Embedded)
            {
                f.FinishForSave();
            }
        }
    }
}

public sealed partial class PdfDocument
{
    /// <summary>
    /// Adds one of the 14 standard fonts (Helvetica, Times-Roman, Courier with their bold and oblique/italic forms, Symbol,
    /// ZapfDingbats), or a common alias such as Arial, to the document. The font is not embedded: viewers use their own copy.
    /// Text is encoded in WinAnsiEncoding (Symbol and ZapfDingbats use their built-in encodings); widths come from the Adobe font
    /// metrics. A character outside the font's set is drawn as '?' and recorded in <see cref="Diagnostics"/>.
    /// </summary>
    /// <param name="name">A standard font name or alias; see <see cref="ArgumentException"/> for what is accepted.</param>
    /// <returns>The font; asking again for the same font returns the same object.</returns>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="ArgumentException">The name is not one of the standard 14 fonts or a known alias.</exception>
    public PdfCreatedFont AddStandardFont(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        EnsureEditable(0);
        string standard = Core14.Resolve(name) ?? throw new ArgumentException($"'{name}' is not one of the 14 standard fonts.", nameof(name));
        CreationState state = CreationState.For(this);
        if (state.Standard.TryGetValue(standard, out StandardCreatedFont? existing))
        {
            return existing;
        }

        var font = new StandardCreatedFont(this, standard);
        var d = new PdfDictionary();
        d.SetEntry("Type", new PdfName("Font"));
        d.SetEntry("Subtype", new PdfName("Type1"));
        d.SetEntry("BaseFont", new PdfName(standard));
        if (standard is not ("Symbol" or "ZapfDingbats"))
        {
            d.SetEntry("Encoding", new PdfName("WinAnsiEncoding"));
        }

        font.Reference = AddObject(d);
        state.Standard[standard] = font;
        return font;
    }

    /// <summary>
    /// Adds a TrueType or OpenType (CFF) font from its file bytes. Text drawn with it is written as a Type 0 font with an
    /// Identity-H encoding, a <c>/W</c> width array and a <c>/ToUnicode</c> map, so text extraction recovers the characters. By
    /// default only the glyphs used are embedded (a subset named with a six-letter tag, ISO 32000-2 9.6.4); the subset is built when
    /// the document is saved, so glyphs can keep being added until then.
    /// </summary>
    /// <remarks>
    /// <para>The font data is copied. Characters are mapped to glyphs through the font's Unicode <c>cmap</c> (or its symbol cmap);
    /// there is no shaping, so complex scripts that need it are not supported. A character with no glyph is drawn as glyph 0
    /// (<c>.notdef</c>) and recorded in <see cref="Diagnostics"/>.</para>
    /// <para>The font's <c>OS/2</c> <c>fsType</c> is honoured: a font that forbids embedding is refused unless
    /// <see cref="PdfEmbedFontOptions.IgnoreEmbeddingRestrictions"/> is set (then licensing is the caller's responsibility), and
    /// the "no subsetting" bit makes the whole font be embedded.</para>
    /// </remarks>
    /// <param name="fontData">A .ttf, .otf or .ttc file.</param>
    /// <param name="options">Null means defaults.</param>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="PdfFormatException">The data is not a usable TrueType or OpenType font.</exception>
    /// <exception cref="PdfFontEmbeddingException">The font's license flags forbid embedding it.</exception>
    /// <exception cref="PdfLimitException">The font file is larger than 256 MiB.</exception>
    public PdfCreatedFont AddFont(ReadOnlyMemory<byte> fontData, PdfEmbedFontOptions? options = null)
    {
        EnsureEditable(0);
        return AddFont(PdfFontSource.Load(fontData, options?.FaceIndex ?? 0), options);
    }

    /// <summary>
    /// Adds a font that was read once with <see cref="PdfFontSource.Load"/>; the result is the same as for the bytes, without copying,
    /// parsing and hashing the file again for every document. <see cref="PdfEmbedFontOptions.FaceIndex"/> is ignored: the source has its face.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="PdfFormatException">The font cannot be embedded as asked (a collection that forbids subsetting, no character map).</exception>
    /// <exception cref="PdfFontEmbeddingException">The font's license flags forbid embedding it.</exception>
    public PdfCreatedFont AddFont(PdfFontSource source, PdfEmbedFontOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureEditable(0);
        EmbeddedCreatedFont font;
        try
        {
            font = new EmbeddedCreatedFont(this, source, options ?? new PdfEmbedFontOptions());
        }
        catch (FontPrograms.FontProgramException ex)
        {
            throw new PdfFormatException("The font data is damaged: " + ex.Message, ex);
        }

        CreationState.For(this).Embedded.Add(font);
        return font;
    }

    /// <summary>
    /// Adds a baseline or progressive JPEG as an image XObject, embedding the file's bytes unchanged (DCTDecode). Size and the number
    /// of components (1 gray, 3 RGB, 4 CMYK) are read from the JPEG headers; Adobe CMYK files, which store inverted values, get the
    /// matching <c>/Decode</c> array. 8-bit samples only; arithmetic-coded and lossless JPEGs are refused.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="PdfFormatException">The data is not a JPEG this method can embed.</exception>
    /// <exception cref="PdfLimitException">The image has more pixels than <see cref="PdfLimits.MaxImagePixels"/>.</exception>
    public PdfCreatedImage AddJpegImage(ReadOnlyMemory<byte> jpeg)
    {
        EnsureEditable(0);
        JpegHeader h = JpegHeader.Read(jpeg.Span);
        CheckPixels(h.Width, h.Height);
        PdfStream s = CreateStream([], false);
        s.SetStoredBytes(jpeg.Span);
        PdfDictionary d = s.Dictionary;
        d.SetEntry("Type", new PdfName("XObject"));
        d.SetEntry("Subtype", new PdfName("Image"));
        d.SetEntry("Width", new PdfInteger(h.Width));
        d.SetEntry("Height", new PdfInteger(h.Height));
        d.SetEntry("ColorSpace", new PdfName(h.Components switch { 1 => "DeviceGray", 3 => "DeviceRGB", _ => "DeviceCMYK" }));
        d.SetEntry("BitsPerComponent", new PdfInteger(8));
        d.SetEntry("Filter", new PdfName("DCTDecode"));
        if (h.Components == 4 && h.Adobe)
        {
            var decode = new PdfArray();
            for (int i = 0; i < 4; i++)
            {
                decode.Append(new PdfInteger(1));
                decode.Append(new PdfInteger(0));
            }

            d.SetEntry("Decode", decode);
        }

        return new PdfCreatedImage(this, AddObject(s), h.Width, h.Height, false);
    }

    /// <summary>
    /// Adds an image from raw 8-bit samples, Flate-compressed. <paramref name="samples"/> holds <c>width * height * components</c>
    /// bytes, rows top to bottom, pixels left to right. An optional <paramref name="alpha"/> plane (<c>width * height</c> bytes,
    /// 255 opaque) becomes a soft mask.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="ArgumentException">The size is not positive or a buffer has the wrong length.</exception>
    /// <exception cref="PdfLimitException">The image has more pixels than <see cref="PdfLimits.MaxImagePixels"/>.</exception>
    public PdfCreatedImage AddRawImage(int width, int height, PdfImageColor color, ReadOnlySpan<byte> samples, ReadOnlySpan<byte> alpha = default)
    {
        EnsureEditable(0);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Width and height must be positive.");
        }

        if (color is not (PdfImageColor.Gray or PdfImageColor.Rgb or PdfImageColor.Cmyk))
        {
            throw new ArgumentOutOfRangeException(nameof(color));
        }

        CheckPixels(width, height);
        long pixels = (long)width * height;
        if (samples.Length != pixels * (int)color)
        {
            throw new ArgumentException($"Expected {pixels * (int)color} sample bytes for a {width}x{height} {color} image.", nameof(samples));
        }

        if (!alpha.IsEmpty && alpha.Length != pixels)
        {
            throw new ArgumentException($"Expected {pixels} alpha bytes.", nameof(alpha));
        }

        PdfStream s = CreateStream(samples, true);
        PdfDictionary d = s.Dictionary;
        d.SetEntry("Type", new PdfName("XObject"));
        d.SetEntry("Subtype", new PdfName("Image"));
        d.SetEntry("Width", new PdfInteger(width));
        d.SetEntry("Height", new PdfInteger(height));
        d.SetEntry("ColorSpace", new PdfName(color switch { PdfImageColor.Gray => "DeviceGray", PdfImageColor.Rgb => "DeviceRGB", _ => "DeviceCMYK" }));
        d.SetEntry("BitsPerComponent", new PdfInteger(8));
        if (!alpha.IsEmpty)
        {
            PdfStream m = CreateStream(alpha, true);
            PdfDictionary md = m.Dictionary;
            md.SetEntry("Type", new PdfName("XObject"));
            md.SetEntry("Subtype", new PdfName("Image"));
            md.SetEntry("Width", new PdfInteger(width));
            md.SetEntry("Height", new PdfInteger(height));
            md.SetEntry("ColorSpace", new PdfName("DeviceGray"));
            md.SetEntry("BitsPerComponent", new PdfInteger(8));
            d.SetEntry("SMask", AddObject(m));
        }

        return new PdfCreatedImage(this, AddObject(s), width, height, !alpha.IsEmpty);
    }

    /// <summary>
    /// Adds a PNG image (W3C PNG / ISO 15948) as an image XObject. Gray (1, 2, 4, 8, 16 bits), RGB (8, 16), palette (1 to 8 bits,
    /// as <c>/Indexed</c>) and the two alpha colour types are read; interlacing (Adam7) is accepted.
    /// </summary>
    /// <remarks>
    /// <para>A non-interlaced image without an alpha channel is not decoded: its IDAT data goes into a <c>FlateDecode</c> stream with
    /// <c>/Predictor 15</c> (ISO 32000-2 7.4.4.4), byte for byte. Interlaced images and images with alpha are decoded; the alpha
    /// channel becomes a <c>/SMask</c>. A <c>tRNS</c> chunk becomes a colour-key <c>/Mask</c> when it can (gray, RGB, and palettes
    /// whose transparent entries form one run), otherwise a soft mask. An <c>iCCP</c> chunk with an RGB or gray profile that matches
    /// the image becomes an <c>ICCBased</c> colour space. <c>gAMA</c>, <c>cHRM</c>, <c>sRGB</c>, <c>sBIT</c> and the other ancillary
    /// chunks are ignored, and samples are used as stored. APNG animation frames are ignored (the default image is used).</para>
    /// <para>A chunk with a bad CRC is an error when critical (IHDR, PLTE, IDAT); a bad <c>tRNS</c> or <c>iCCP</c> chunk is skipped
    /// with a warning in <see cref="Diagnostics"/>. Limits: <see cref="PdfLimits.MaxImagePixels"/> and
    /// <see cref="PdfLimits.MaxDecodedStreamBytes"/>, checked from the header before any pixel memory is allocated.</para>
    /// </remarks>
    /// <param name="png">The PNG file.</param>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="PdfFormatException">The data is not a valid PNG, or is one this reader does not support.</exception>
    /// <exception cref="PdfLimitException">The image is larger than the document's limits.</exception>
    public PdfCreatedImage AddPngImage(ReadOnlyMemory<byte> png)
    {
        EnsureEditable(0);
        PngImageData img = PngReader.Read(png.Span, Limits);
        foreach (string w in img.Warnings)
        {
            ReportWarning(w, -1, -1);
        }

        PdfObject colorSpace = img.Kind switch
        {
            PngKind.Gray when img.IccProfileZlib is not null => IccColorSpace(img),
            PngKind.Gray => new PdfName("DeviceGray"),
            PngKind.Rgb when img.IccProfileZlib is not null => IccColorSpace(img),
            PngKind.Rgb => new PdfName("DeviceRGB"),
            _ => IndexedColorSpace(img),
        };

        PdfStream s = CreateStream([], false);
        if (img.IsPassThrough)
        {
            s.SetStoredBytes(img.Data);
            s.Dictionary.SetEntry("Filter", new PdfName("FlateDecode"));
            var parms = new PdfDictionary();
            parms.SetEntry("Predictor", new PdfInteger(15));
            parms.SetEntry("Colors", new PdfInteger(img.Components));
            parms.SetEntry("BitsPerComponent", new PdfInteger(img.BitsPerComponent));
            parms.SetEntry("Columns", new PdfInteger(img.Width));
            s.Dictionary.SetEntry("DecodeParms", parms);
        }
        else
        {
            s.SetData(img.Data, true);
        }

        PdfDictionary d = s.Dictionary;
        d.SetEntry("Type", new PdfName("XObject"));
        d.SetEntry("Subtype", new PdfName("Image"));
        d.SetEntry("Width", new PdfInteger(img.Width));
        d.SetEntry("Height", new PdfInteger(img.Height));
        d.SetEntry("ColorSpace", colorSpace);
        d.SetEntry("BitsPerComponent", new PdfInteger(img.BitsPerComponent));
        if (img.ColorKey is { } key)
        {
            var mask = new PdfArray();
            foreach (int v in key)
            {
                mask.Append(new PdfInteger(v));
            }

            d.SetEntry("Mask", mask);
        }

        if (img.Alpha is { } alpha)
        {
            PdfStream m = CreateStream(alpha, true);
            PdfDictionary md = m.Dictionary;
            md.SetEntry("Type", new PdfName("XObject"));
            md.SetEntry("Subtype", new PdfName("Image"));
            md.SetEntry("Width", new PdfInteger(img.Width));
            md.SetEntry("Height", new PdfInteger(img.Height));
            md.SetEntry("ColorSpace", new PdfName("DeviceGray"));
            md.SetEntry("BitsPerComponent", new PdfInteger(img.AlphaBitsPerComponent));
            d.SetEntry("SMask", AddObject(m));
        }

        return new PdfCreatedImage(this, AddObject(s), img.Width, img.Height, img.Alpha is not null || img.ColorKey is not null);
    }

    private PdfArray IccColorSpace(PngImageData img)
    {
        PdfStream icc = CreateStream([], false);
        icc.SetStoredBytes(img.IccProfileZlib);
        icc.Dictionary.SetEntry("Filter", new PdfName("FlateDecode"));
        icc.Dictionary.SetEntry("N", new PdfInteger(img.IccComponents));
        var cs = new PdfArray();
        cs.Append(new PdfName("ICCBased"));
        cs.Append(AddObject(icc));
        return cs;
    }

    private PdfArray IndexedColorSpace(PngImageData img)
    {
        var cs = new PdfArray();
        cs.Append(new PdfName("Indexed"));
        cs.Append(img.IccProfileZlib is not null ? IccColorSpace(img) : new PdfName("DeviceRGB"));
        cs.Append(new PdfInteger((img.Palette!.Length / 3) - 1));
        cs.Append(new PdfString(img.Palette));
        return cs;
    }

    private void CheckPixels(int width, int height)
    {
        if ((long)width * height > Limits.MaxImagePixels)
        {
            throw new PdfLimitException($"The image has more than {Limits.MaxImagePixels} pixels.");
        }
    }

    /// <summary>
    /// Starts a Form XObject whose content is drawn once and placed many times (stamps, watermarks, repeated artwork). Draw into the
    /// returned builder in the form's own coordinates, then call <see cref="PdfFormContentBuilder.Commit"/>.
    /// </summary>
    /// <param name="boundingBox">The form's bounding box (<c>/BBox</c>); content outside it is clipped. Must not be empty.</param>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="ArgumentException">The box is empty or not finite.</exception>
    public PdfFormContentBuilder BeginForm(PdfRectangle boundingBox)
    {
        EnsureEditable(0);
        if (boundingBox.IsEmpty || !double.IsFinite(boundingBox.Left + boundingBox.Bottom + boundingBox.Right + boundingBox.Top))
        {
            throw new ArgumentException("The bounding box must be finite and not empty.", nameof(boundingBox));
        }

        return new PdfFormContentBuilder(this, boundingBox);
    }

    internal (PdfReference Q, PdfReference BigQ) SharedSaveRestore()
    {
        CreationState state = CreationState.For(this);
        state.QRef ??= AddObject(CreateStream("q\n"u8, false));
        state.BigQRef ??= AddObject(CreateStream("\nQ\n"u8, false));
        return (state.QRef, state.BigQRef);
    }
}

/// <summary>A JPEG's frame header, read without decoding (ITU-T T.81 B.2.2: SOF0/SOF1 baseline and extended sequential Huffman, SOF2 progressive).</summary>
internal readonly record struct JpegHeader(int Width, int Height, int Components, bool Adobe)
{
    public static JpegHeader Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8)
        {
            throw new PdfFormatException("The data is not a JPEG (no SOI marker).");
        }

        int p = 2;
        bool adobe = false;
        while (p + 4 <= d.Length)
        {
            if (d[p] != 0xFF)
            {
                p++;
                continue;
            }

            int m = d[p + 1];
            if (m == 0xFF)
            {
                p++;
                continue;
            }

            if (m is 0x00 or 0x01 or (>= 0xD0 and <= 0xD8))
            {
                p += 2;
                continue;
            }

            int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 2, 2));
            if (len < 2 || p + 2 + len > d.Length)
            {
                throw new PdfFormatException("The JPEG is truncated before its frame header.");
            }

            ReadOnlySpan<byte> seg = d.Slice(p + 4, len - 2);
            if (m == 0xEE && seg.Length >= 12 && seg[..5].SequenceEqual("Adobe"u8))
            {
                adobe = true;
            }

            if (m is 0xC0 or 0xC1 or 0xC2)
            {
                if (seg.Length < 6 || seg[0] != 8)
                {
                    throw new PdfFormatException("Only 8-bit JPEG samples can be embedded.");
                }

                int height = BinaryPrimitives.ReadUInt16BigEndian(seg.Slice(1, 2));
                int width = BinaryPrimitives.ReadUInt16BigEndian(seg.Slice(3, 2));
                int comps = seg[5];
                if (width == 0 || height == 0)
                {
                    throw new PdfFormatException("The JPEG declares a zero size (a DNL-defined height is not supported).");
                }

                if (comps is not (1 or 3 or 4))
                {
                    throw new PdfFormatException("The JPEG has an unsupported number of components.");
                }

                return new JpegHeader(width, height, comps, adobe);
            }

            if (m is (>= 0xC3 and <= 0xCF) and not (0xC4 or 0xC8 or 0xCC))
            {
                throw new PdfFormatException("Arithmetic-coded, lossless and hierarchical JPEGs cannot be embedded.");
            }

            if (m == 0xDA)
            {
                break;
            }

            p += 2 + len;
        }

        throw new PdfFormatException("The JPEG has no frame header.");
    }
}

/// <summary>Collects content for a page and writes it into the page when committed. Get one from <see cref="PdfPage.BeginContent"/>.</summary>
public sealed class PdfPageContentBuilder : PdfContentBuilder
{
    internal PdfPageContentBuilder(PdfPage page, PdfContentPlacement placement)
        : base(page.Document, page.Dictionary.GetDictionary("Resources") ?? page.Resources)
    {
        Page = page;
        Placement = placement;
    }

    /// <summary>The page the content is for.</summary>
    public PdfPage Page { get; }

    /// <summary>Whether the content goes over or under what the page already draws.</summary>
    public PdfContentPlacement Placement { get; }

    /// <summary>
    /// Adds the content to the page. The page's own content is left byte for byte as it was; it is wrapped in a saved graphics
    /// state so its leftovers cannot reach the new content. Resources are added to a page-owned copy of the resource dictionary, so
    /// other pages that share the original are not touched. Views taken earlier (<see cref="PdfDocument.Pages"/>) are refreshed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The builder is closed, the document is not editable, or a resource name is already used for another object.</exception>
    public void Commit()
    {
        CheckOpen();
        byte[] content = Finish();
        PdfDocument doc = Document;
        PdfDictionary pd = Page.Dictionary;

        PdfDictionary res = PageContentWriter.OwnedResources(doc, pd, Page.Resources);
        PageContentWriter.AddResources(doc, res, "Font", FontResources);
        PageContentWriter.AddResources(doc, res, "XObject", XObjectResources);
        PageContentWriter.AddResources(doc, res, "ExtGState", StateResources);
        PageContentWriter.AddResources(doc, res, "Shading", ShadingResources);

        var contents = new PdfArray();
        (PdfReference q, PdfReference bigQ) = doc.SharedSaveRestore();
        PdfStream body = doc.CreateStream(content, false);
        PdfReference bodyRef = doc.AddObject(body);
        bool has = pd.Get("Contents") is PdfArray or PdfStream;
        if (Placement == PdfContentPlacement.Underlay)
        {
            contents.Append(q);
            contents.Append(bodyRef);
            contents.Append(bigQ);
            PageContentWriter.AppendExisting(pd, contents);
        }
        else
        {
            if (has)
            {
                contents.Append(q);
                PageContentWriter.AppendExisting(pd, contents);
                contents.Append(bigQ);
                int open = PageContentWriter.UnclosedSaves(doc, pd);
                if (open > 0)
                {
                    // The page's content leaves graphics states open; close them so the new content starts from the initial state.
                    contents.Append(doc.AddObject(doc.CreateStream(Encoding.ASCII.GetBytes(new StringBuilder().Insert(0, "Q\n", open).ToString()), false)));
                }
            }

            contents.Append(q);
            contents.Append(bodyRef);
            contents.Append(bigQ);
        }

        pd.SetEntry("Contents", contents);
        doc.ResetPageViews();
        IsClosed = true;
    }
}

/// <summary>Collects the content of a Form XObject. Get one from <see cref="PdfDocument.BeginForm"/>.</summary>
public sealed class PdfFormContentBuilder : PdfContentBuilder
{
    private readonly PdfRectangle _box;
    private readonly PdfFormXObject? _reserved;

    internal PdfFormContentBuilder(PdfDocument document, PdfRectangle box, PdfFormXObject? reserved = null)
        : base(document, null)
    {
        _box = box;
        _reserved = reserved;
    }

    /// <summary>Writes the form into the document and returns it.</summary>
    /// <exception cref="InvalidOperationException">The builder is closed or the document is not editable.</exception>
    public PdfFormXObject Commit()
    {
        CheckOpen();
        byte[] content = Finish();
        PdfDocument doc = Document;
        PdfStream s = doc.CreateStream(content, false);
        PdfDictionary d = s.Dictionary;
        d.SetEntry("Type", new PdfName("XObject"));
        d.SetEntry("Subtype", new PdfName("Form"));
        var bbox = new PdfArray();
        foreach (double v in (double[])[_box.Left, _box.Bottom, _box.Right, _box.Top])
        {
            bbox.Append(new PdfReal(v));
        }

        d.SetEntry("BBox", bbox);
        var res = new PdfDictionary();
        d.SetEntry("Resources", res);
        PageContentWriter.AddResources(doc, d.GetDictionary("Resources")!, "Font", FontResources);
        PageContentWriter.AddResources(doc, d.GetDictionary("Resources")!, "XObject", XObjectResources);
        PageContentWriter.AddResources(doc, d.GetDictionary("Resources")!, "ExtGState", StateResources);
        PageContentWriter.AddResources(doc, d.GetDictionary("Resources")!, "Shading", ShadingResources);
        IsClosed = true;
        PdfFormXObject form;
        if (_reserved is not null)
        {
            doc.SetObject(_reserved.Reference.Number, s);
            form = _reserved;
        }
        else
        {
            form = new PdfFormXObject(doc, doc.AddObject(s), _box);
        }

        AfterCommit?.Invoke();
        return form;
    }
}

internal static class PageContentWriter
{
    // The page's own direct Resources is changed in place; a shared, indirect or inherited one is copied first so other pages keep theirs.
    public static PdfDictionary OwnedResources(PdfDocument doc, PdfDictionary page, PdfDictionary inherited)
    {
        if (page.GetRaw("Resources") is PdfDictionary own && own.Reference is null)
        {
            return own;
        }

        PdfDictionary source = page.GetDictionary("Resources") ?? inherited;
        var copy = new PdfDictionary();
        foreach (string k in (string[])[.. source.Keys])
        {
            if (source.GetRaw(k) is { } raw)
            {
                copy.SetEntry(k, FormFiller.Clone(doc, raw));
            }
        }

        page.SetEntry("Resources", copy);
        return page.GetDictionary("Resources")!;
    }

    public static void AddResources(PdfDocument doc, PdfDictionary resources, string category, IReadOnlyList<(string Name, PdfObject Value)> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        PdfDictionary target;
        if (resources.GetRaw(category) is PdfDictionary own && own.Reference is null)
        {
            target = own;
        }
        else
        {
            var copy = new PdfDictionary();
            if (resources.GetDictionary(category) is { } old)
            {
                foreach (string k in (string[])[.. old.Keys])
                {
                    if (old.GetRaw(k) is { } raw)
                    {
                        copy.SetEntry(k, FormFiller.Clone(doc, raw));
                    }
                }
            }

            resources.SetEntry(category, copy);
            target = resources.GetDictionary(category)!;
        }

        foreach ((string name, PdfObject value) in items)
        {
            if (target.ContainsKey(name))
            {
                if (value is PdfReference r && target.GetRaw(name) is PdfReference existing && existing.Number == r.Number)
                {
                    continue;
                }

                throw new InvalidOperationException($"The resource name /{name} is already used on this page for another object (another builder committed first).");
            }

            target.SetEntry(name, value);
        }
    }

    // Open q levels left by the page's own content streams, found by a lexical scan. A stream that cannot be decoded counts as balanced.
    public static int UnclosedSaves(PdfDocument doc, PdfDictionary page)
    {
        int depth = 0;
        try
        {
            if (page.Get("Contents") is PdfStream single)
            {
                depth = ContentDepth.Unclosed(single.GetDecodedBytes().Span, depth);
            }
            else if (page.Get("Contents") is PdfArray list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is PdfStream s)
                    {
                        depth = ContentDepth.Unclosed(s.GetDecodedBytes().Span, depth);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is PdfFormatException or PdfLimitException)
        {
            doc.ReportWarning("A content stream of the page could not be decoded to count its open graphics states; new content may inherit them.", page.Reference?.Number ?? -1, -1);
        }

        return depth;
    }

    public static void AppendExisting(PdfDictionary page, PdfArray contents)
    {
        if (page.Get("Contents") is PdfArray existing)
        {
            for (int i = 0; i < existing.Count; i++)
            {
                if (existing.GetRaw(i) is { } item)
                {
                    contents.Append(item is PdfStream { Reference: { } sr } ? sr : item);
                }
            }
        }
        else if (page.Get("Contents") is PdfStream)
        {
            PdfObject c = page.GetRaw("Contents")!;
            contents.Append(c is PdfStream { Reference: { } cr } ? cr : c);
        }
    }
}
