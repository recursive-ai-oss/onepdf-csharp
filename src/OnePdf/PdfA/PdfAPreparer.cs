using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OnePdf.FontData;
using OnePdf.FontPrograms;
using OnePdf.FontWriting;
using OnePdf.Forms;

namespace OnePdf.PdfA;

/// <summary>
/// Makes the repairs that need no judgement: it adds the output intent and the XMP packet, sets the Print flag on annotations,
/// stores generated appearances where an annotation has none, drops the appearance states PDF/A does not allow, and embeds
/// Liberation faces for the standard 14 fonts that OnePDF itself put into the document. It does not touch anything that came with
/// the file beyond that: what remains wrong is for <see cref="PdfAChecker"/> to report.
/// </summary>
internal static class PdfAPreparer
{
    public static void Prepare(PdfDocument doc, PdfAOptions options, CancellationToken ct, bool pdfUA = false)
    {
        doc.EnsureEditable(0);
        CreationState.FinishFonts(doc);

        PrepareForm(doc, ct);
        PrepareAnnotations(doc, ct);
        if (options.EmbedLibraryFonts)
        {
            EmbedLibraryFonts(doc, ct);
        }

        if (options.EmbedSubstituteFonts)
        {
            RequireProvider(doc.EmbedSubstituteFonts(cancellationToken: ct));
        }

        AddOutputIntent(doc);
        WriteMetadata(doc, options, pdfUA);
        if (options.Conformance == PdfAConformance.A)
        {
            PdfUA.PdfUAPreparer.Prepare(doc, null, options, ct);
        }

        if (options.Part == PdfAPart.Part3)
        {
            DateAttachments(doc);
        }
    }

    /// <summary>EmbedSubstituteFonts was asked for in the save options but cannot work without a font provider: fail with the fix instead of leaving unembedded fonts to a vaguer finding later.</summary>
    internal static void RequireProvider(PdfSubstituteEmbedReport report)
    {
        if (report.FontProviderMissing)
        {
            throw new InvalidOperationException("EmbedSubstituteFonts is set, but the document names fonts that need a substitute and no font provider is available. " + report.ProviderMessage);
        }
    }

    // ---- forms and annotations --------------------------------------------------------------------------------------------------

    private static void PrepareForm(PdfDocument doc, CancellationToken ct)
    {
        if (doc.Form is { } form)
        {
            // Builds the appearances widgets lack and, once every widget has one, clears /NeedAppearances (ISO 19005-2, 6.4.1).
            form.GenerateMissingAppearances(ct);
        }
    }

    private static void PrepareAnnotations(PdfDocument doc, CancellationToken ct)
    {
        var session = new FormSession();
        PdfPageCollection pages = doc.Pages;
        for (int p = 0; p < pages.Count; p++)
        {
            ct.ThrowIfCancellationRequested();
            if (pages[p].Dictionary.Get("Annots") is not PdfArray annots)
            {
                continue;
            }

            for (int i = 0; i < annots.Count; i++)
            {
                if (annots[i] is not PdfDictionary a)
                {
                    continue;
                }

                string subtype = a.GetName("Subtype") ?? string.Empty;
                if (subtype == "Popup")
                {
                    continue;
                }

                // 6.3.2: Print must be set. Hidden, NoView and the like are the author's choice and stay a violation.
                a.SetEntry("F", new PdfInteger(a.ContainsKey("F") ? a.GetInt("F") | 4 : 4));

                if (a.GetDictionary("AP") is { } ap)
                {
                    // 6.3.3: only the normal appearance may be present.
                    foreach (string key in ap.Keys.ToArray())
                    {
                        if (key != "N")
                        {
                            ap.RemoveEntry(key);
                        }
                    }
                }

                PdfRectangle? rect = PdfRectangle.From(a.Get("Rect"));
                bool empty = rect is { } r && r.Width == 0 && r.Height == 0;
                if (a.GetDictionary("AP")?.Get("N") is null && !empty && subtype is not ("Link" or "Widget"))
                {
                    var annotation = new PdfAnnotation(doc, pages[p], a);
                    PdfGeneratedAppearance? gen = annotation.GenerateAppearance(ct);
                    if (gen is not null)
                    {
                        PdfReference form = FormFiller.StoreForm(doc, gen, session);
                        if (a.GetDictionary("AP") is null)
                        {
                            a.SetEntry("AP", new PdfDictionary());
                        }

                        a.GetDictionary("AP")!.SetEntry("N", form);
                    }
                }
            }
        }
    }

    // ---- fonts ------------------------------------------------------------------------------------------------------------------

    internal static void EmbedLibraryFonts(PdfDocument doc, CancellationToken ct)
    {
        CreationState state = CreationState.For(doc);
        var numbers = new SortedSet<int>(state.LibraryFonts);
        foreach (StandardCreatedFont f in state.Standard.Values)
        {
            if (f.Reference is { } r)
            {
                numbers.Add(r.Number);
            }
        }

        var candidates = new HashSet<int>();
        foreach (int n in numbers)
        {
            if (doc.ResolveChain(n, 0) is PdfDictionary font && font.GetName("Subtype") == "Type1" && !font.ContainsKey("FontDescriptor"))
            {
                candidates.Add(n);
            }
        }

        // Only the codes the document shows are embedded. A font that is never shown, is listed in the form's default resources, or
        // is used where the scan cannot follow keeps its whole encoding (see FontUsage).
        FontUsage usage = candidates.Count > 0 ? FontUsage.Scan(doc, candidates, ct) : null!;
        var faces = new Dictionary<string, SubstituteFace>(StringComparer.Ordinal);
        foreach (int n in candidates)
        {
            ct.ThrowIfCancellationRequested();
            HashSet<int>? codes = usage.Complete && !usage.Full.Contains(n) && usage.Used.TryGetValue(n, out HashSet<int>? shown) && shown.Count > 0 ? shown : null;
            TryEmbed(doc, (PdfDictionary)doc.ResolveChain(n, 0)!, faces, codes);
        }

        foreach (SubstituteFace face in faces.Values)
        {
            face.Finish();
        }
    }

    /// <summary>
    /// One substitute face shared by every standard font dictionary that uses it. The codes each dictionary needs are collected first;
    /// <see cref="Finish"/> then writes one subset holding all of them, so every user shows the same file and the same tagged name.
    /// </summary>
    private sealed class SubstituteFace(byte[] bytes, SfntFont program, PdfStream file)
    {
        public SfntFont Program { get; } = program;

        public PdfReference? FileReference { get; set; }

        public PdfStream File { get; } = file;

        /// <summary>Unicode value to source glyph for every code of every dictionary that uses the face.</summary>
        public SortedDictionary<int, int> Map { get; } = [];

        public List<(PdfDictionary Font, PdfDictionary Descriptor, string Name)> Users { get; } = [];

        public void Finish()
        {
            byte[] data = bytes;
            string tag = string.Empty;
            try
            {
                // Glyphs in Unicode order, each once; the subsetter adds composite components itself.
                var glyphs = new List<int>();
                var seen = new HashSet<int>();
                foreach (int g in Map.Values)
                {
                    if (seen.Add(g))
                    {
                        glyphs.Add(g);
                    }
                }

                var pairs = new List<(int Unicode, int Glyph)>(Map.Count);
                foreach ((int u, int g) in Map)
                {
                    pairs.Add((u, g));
                }

                data = TrueTypeSubsetter.Subset(Program, glyphs, pairs);
                tag = SubsetTag(bytes, glyphs);
            }
            catch (FontProgramException)
            {
                // A face that cannot be subset is embedded whole under its own name.
                data = bytes;
            }

            File.SetData(data, compress: true);
            File.Dictionary.SetEntry("Length1", new PdfInteger(data.Length));
            foreach ((PdfDictionary font, PdfDictionary descriptor, string name) in Users)
            {
                // ISO 32000-2 9.6.4: a subset's name starts with a six-letter tag and a plus sign.
                string full = tag.Length > 0 ? tag + "+" + name : name;
                font.SetEntry("BaseFont", new PdfName(full));
                descriptor.SetEntry("FontName", new PdfName(full));
            }
        }

        // Six capital letters from a hash of the font and the glyphs, so the same inputs always give the same name.
        private static string SubsetTag(byte[] source, List<int> glyphs)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(SHA256.HashData(source));
            foreach (int g in glyphs)
            {
                sha.AppendData(BitConverter.GetBytes(g));
            }

            byte[] h = sha.GetHashAndReset();
            var chars = new char[6];
            for (int i = 0; i < 6; i++)
            {
                chars[i] = (char)('A' + (h[i] % 26));
            }

            return new string(chars);
        }
    }

    // codes: the codes to embed glyphs for, or null for every code of the encoding.
    private static void TryEmbed(PdfDocument doc, PdfDictionary font, Dictionary<string, SubstituteFace> faces, HashSet<int>? codes)
    {
        string? standard = Core14.Resolve(font.GetName("BaseFont") ?? string.Empty);
        if (standard is null or "Symbol" or "ZapfDingbats")
        {
            return;
        }

        bool bold = standard.Contains("Bold", StringComparison.Ordinal);
        bool italic = standard.Contains("Italic", StringComparison.Ordinal) || standard.Contains("Oblique", StringComparison.Ordinal);
        bool serif = standard.StartsWith("Times", StringComparison.Ordinal);
        bool mono = standard.StartsWith("Courier", StringComparison.Ordinal);
        var request = new PdfFontRequest
        {
            FontName = standard,
            FamilyName = standard.Split('-')[0],
            StandardName = standard,
            IsBold = bold,
            IsItalic = italic,
            IsSerif = serif,
            IsFixedPitch = mono,
        };

        PdfSubstituteFont? sub = null;
        if (doc.FontProvider is { } own)
        {
            sub = own.FindFont(request);
        }
        else
        {
            foreach (IPdfFontProvider p in PdfFontProviders.RegisteredProviders)
            {
                sub = p.FindFont(request);
                if (sub is not null)
                {
                    break;
                }
            }
        }

        if (sub is null)
        {
            return;
        }

        byte[] bytes = sub.Data.ToArray();
        SfntFont program;
        try
        {
            program = SfntFont.Parse(bytes, sub.FaceIndex, GlyphPrograms.Never);
        }
        catch (FontProgramException)
        {
            return;
        }

        int fsType = EmbeddedCreatedFont.EmbeddingFlags(program);
        if (program.Kind != FontProgramKind.TrueType || ((fsType & 0x0002) != 0 && (fsType & 0x000C) == 0) || (fsType & 0x0200) != 0)
        {
            return; // CFF-flavoured or licence-restricted substitutes would need a different embedding; leave the font to be reported
        }

        CmapSubtable? cmap = program.FindCmap(3, 1) ?? program.FindUnicodeCmap();
        if (cmap is null)
        {
            return;
        }

        // The code -> glyph name table of the font as the document declares it.
        PdfObject? enc = font.Get("Encoding");
        PredefinedEncoding basePredefined = PredefinedEncoding.Standard;
        string?[] names;
        PdfDictionary? encDict = enc as PdfDictionary;
        string? encName = enc is PdfName en ? en.Value : encDict?.GetName("BaseEncoding");
        if (encName == "WinAnsiEncoding")
        {
            basePredefined = PredefinedEncoding.WinAnsi;
        }
        else if (encName == "MacRomanEncoding")
        {
            basePredefined = PredefinedEncoding.MacRoman;
        }
        else if (encName == "MacExpertEncoding")
        {
            return;
        }

        names = [.. Encodings.Get(basePredefined)];
        PdfArray? diff = encDict?.Get("Differences") as PdfArray;
        if (diff is not null)
        {
            int code = 0;
            for (int i = 0; i < diff.Count; i++)
            {
                if (diff[i] is PdfInteger num)
                {
                    code = (int)Math.Clamp(num.Value, 0, 255);
                }
                else if (diff.GetName(i) is { } gn && code < 256)
                {
                    names[code++] = gn;
                }
            }
        }

        StandardFontMetrics metrics = Core14.Get(standard);
        int first = -1, last = -1;
        int[] widths = new int[256];
        var unicodeToGlyph = new Dictionary<int, int>();
        for (int c = 0; c < 256; c++)
        {
            string? name = names[c];
            if (codes is not null && !codes.Contains(c))
            {
                continue;
            }

            if (name is null || !GlyphList.TryGetUnicode(name, out string? text) || text.Length == 0 || !Rune.TryGetRuneAt(text, 0, out Rune r) || r.Utf16SequenceLength != text.Length)
            {
                continue;
            }

            int gid = cmap.Lookup(r.Value);
            if (gid <= 0)
            {
                continue;
            }

            program.TryGetAdvance(gid, out float adv);
            int fromProgram = (int)Math.Round(adv * 1000.0 / program.UnitsPerEm);
            int width = metrics.GetWidth(name) is { } afm && Math.Abs(afm - fromProgram) <= 1 ? afm : fromProgram;
            widths[c] = width;
            unicodeToGlyph[r.Value] = gid;
            if (first < 0)
            {
                first = c;
            }

            last = c;
        }

        if (first < 0)
        {
            return;
        }

        var widthArray = new PdfArray();
        for (int c = first; c <= last; c++)
        {
            widthArray.Append(new PdfInteger(widths[c]));
        }

        if (!faces.TryGetValue(sub.Name, out SubstituteFace? face))
        {
            PdfStream file = doc.CreateStream([], compress: true);
            faces[sub.Name] = face = new SubstituteFace(bytes, program, file);
            face.FileReference = doc.AddObject(file);
        }

        foreach ((int u, int g) in unicodeToGlyph)
        {
            face.Map[u] = g;
        }

        PdfReference fileRef = face.FileReference!;

        string psName = PostScriptName(program, sub.Name);
        ReadOnlyMemory<byte> hhea = program.GetTable("hhea");
        ReadOnlyMemory<byte> os2 = program.GetTable("OS/2");
        ReadOnlyMemory<byte> post = program.GetTable("post");
        int upem = program.UnitsPerEm;
        double scale = 1000.0 / upem;
        int ascent = hhea.Length >= 8 ? (int)Math.Round(BinaryPrimitives.ReadInt16BigEndian(hhea.Span[4..]) * scale) : 800;
        int descent = hhea.Length >= 8 ? (int)Math.Round(BinaryPrimitives.ReadInt16BigEndian(hhea.Span[6..]) * scale) : -200;
        int capHeight = os2.Length >= 90 && BinaryPrimitives.ReadUInt16BigEndian(os2.Span) >= 2 ? (int)Math.Round(BinaryPrimitives.ReadInt16BigEndian(os2.Span[88..]) * scale) : (int)(ascent * 0.7);
        double italicAngle = post.Length >= 8 ? BinaryPrimitives.ReadInt32BigEndian(post.Span[4..]) / 65536.0 : 0;
        double[]? bbox = program.FontBBox;

        int flags = 32; // nonsymbolic (ISO 32000-1 9.8.2)
        if (mono)
        {
            flags |= 1;
        }

        if (serif)
        {
            flags |= 2;
        }

        if (italic)
        {
            flags |= 64;
        }

        var fd = new PdfDictionary();
        fd.SetEntry("Type", new PdfName("FontDescriptor"));
        fd.SetEntry("FontName", new PdfName(psName));
        fd.SetEntry("Flags", new PdfInteger(flags));
        var box = new PdfArray();
        foreach (double v in bbox is { Length: 4 } ? bbox : [0, descent, 1000, ascent])
        {
            box.Append(new PdfInteger((long)Math.Round(v * (bbox is { Length: 4 } ? scale : 1))));
        }

        fd.SetEntry("FontBBox", box);
        fd.SetEntry("ItalicAngle", new PdfReal(Math.Round(italicAngle, 2)));
        fd.SetEntry("Ascent", new PdfInteger(ascent));
        fd.SetEntry("Descent", new PdfInteger(descent));
        fd.SetEntry("CapHeight", new PdfInteger(capHeight));
        fd.SetEntry("StemV", new PdfInteger(bold ? 140 : 80));
        fd.SetEntry("FontFile2", fileRef);
        PdfReference fdRef = doc.AddObject(fd);

        // The encoding for a TrueType font must be WinAnsi or MacRoman, possibly with a Differences array of glyph names that are
        // in the Adobe Glyph List (ISO 19005-2 6.2.11.6). A standard-encoded font becomes WinAnsi plus the codes that differ.
        PdfObject newEncoding;
        if (basePredefined == PredefinedEncoding.Standard)
        {
            string?[] win = Encodings.Get(PredefinedEncoding.WinAnsi);
            var d = new PdfArray();
            int prev = -2;
            for (int c = 0; c < 256; c++)
            {
                if (names[c] is { } nm && nm != win[c])
                {
                    if (c != prev + 1)
                    {
                        d.Append(new PdfInteger(c));
                    }

                    d.Append(new PdfName(nm));
                    prev = c;
                }
            }

            if (d.Count == 0)
            {
                newEncoding = new PdfName("WinAnsiEncoding");
            }
            else
            {
                var ed = new PdfDictionary();
                ed.SetEntry("Type", new PdfName("Encoding"));
                ed.SetEntry("BaseEncoding", new PdfName("WinAnsiEncoding"));
                ed.SetEntry("Differences", d);
                newEncoding = ed;
            }
        }
        else
        {
            newEncoding = enc is PdfName ? new PdfName(encName!) : enc!;
        }

        font.SetEntry("Subtype", new PdfName("TrueType"));
        font.SetEntry("BaseFont", new PdfName(psName));
        face.Users.Add((font, fd, psName));
        font.SetEntry("Encoding", newEncoding is PdfDictionary nd && nd.Reference is null && enc is PdfDictionary ? enc : newEncoding);
        font.SetEntry("FirstChar", new PdfInteger(first));
        font.SetEntry("LastChar", new PdfInteger(last));
        font.SetEntry("Widths", widthArray);
        font.SetEntry("FontDescriptor", fdRef);
    }

    private static string PostScriptName(SfntFont program, string fallback)
    {
        string name = program.FontName ?? fallback;
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_')
            {
                sb.Append(c);
            }
        }

        return sb.Length > 0 ? sb.ToString() : "Substitute";
    }

    // ISO 19005-3 6.8: an embedded file stream carries the file's modification date. Without one from the caller the Info dates are used.
    private static void DateAttachments(PdfDocument doc)
    {
        DateTimeOffset? date = doc.Info.ModificationDate ?? doc.Info.CreationDate;
        if (date is null)
        {
            return;
        }

        foreach (PdfEmbeddedFile f in doc.EmbeddedFiles)
        {
            if (f.Stream is { } s)
            {
                PdfDictionary pars = s.Dictionary.GetDictionary("Params") ?? new PdfDictionary();
                if (!pars.ContainsKey("ModDate"))
                {
                    pars.SetEntry("ModDate", PdfString.FromText(PdfDocument.FormatDate(date.Value)));
                    s.Dictionary.SetEntry("Params", pars);
                }
            }
        }
    }

    // ---- output intent and metadata ---------------------------------------------------------------------------------------------

    private static void AddOutputIntent(PdfDocument doc)
    {
        PdfDictionary catalog = doc.Catalog;
        PdfArray? existing = catalog.Get("OutputIntents") as PdfArray;
        if (existing is not null)
        {
            for (int i = 0; i < existing.Count; i++)
            {
                if (existing[i] is PdfDictionary d && d.GetName("S") == "GTS_PDFA1")
                {
                    return;
                }
            }
        }

        PdfStream profile = doc.CreateStream(SrgbProfile.Bytes, compress: true);
        profile.Dictionary.SetEntry("N", new PdfInteger(3));
        PdfReference profileRef = doc.AddObject(profile);

        var intent = new PdfDictionary();
        intent.SetEntry("Type", new PdfName("OutputIntent"));
        intent.SetEntry("S", new PdfName("GTS_PDFA1"));
        intent.SetEntry("OutputConditionIdentifier", PdfString.FromText(SrgbProfile.Identifier));
        intent.SetEntry("Info", PdfString.FromText(SrgbProfile.Identifier));
        intent.SetEntry("RegistryName", PdfString.FromText("http://www.color.org"));
        intent.SetEntry("DestOutputProfile", profileRef);

        if (existing is not null)
        {
            existing.Append(intent);
        }
        else
        {
            var arr = new PdfArray();
            arr.Append(intent);
            catalog.SetEntry("OutputIntents", arr);
        }
    }

    /// <summary>
    /// Writes the XMP packet for a PDF/A save (<paramref name="options"/>), a PDF/UA save (<paramref name="pdfUA"/>) or both. A packet that
    /// already makes every requested claim is kept as it is; any other packet is replaced by one built from the Info dictionary.
    /// </summary>
    internal static void WriteMetadata(PdfDocument doc, PdfAOptions? options, bool pdfUA, int uaPart = 1)
    {
        string conf = options is null ? string.Empty : ConformanceLetter(options.Conformance);
        PdfStream? current = doc.Catalog.GetStream("Metadata");
        if (current is not null && current.FindUnreadableReason(CancellationToken.None) is not null)
        {
            // A packet that exists but cannot be read is not replaced by one built from Info: its properties would be lost. The content-loss
            // scan of the checkers reports it, so neither the check nor the save passes.
            return;
        }

        if (current is not null && current.FilterNames.Count == 0 && current.Dictionary.GetName("Type") == "Metadata" && current.Dictionary.GetName("Subtype") == "XML")
        {
            XmpMetadata? xmp = doc.Xmp;
            bool aOk = options is null || (xmp is { IsWellFormed: true } && xmp.PdfAPart == (int)options.Part && xmp.PdfAConformance == conf);
            bool uaOk = !pdfUA || (xmp is { IsWellFormed: true, PdfUAPart: var havePart } && havePart == uaPart && (uaPart == 1 || xmp.PdfUARevision == PdfUA.PdfUA2.Revision) && !string.IsNullOrWhiteSpace(xmp.Title)
                && (options is null || Encoding.UTF8.GetString(xmp.RawBytes.Span).Contains("<pdfaSchema:namespaceURI>http://www.aiim.org/pdfua/ns/id/", StringComparison.Ordinal)));
            if (aOk && uaOk)
            {
                return; // already identifies the file; its other properties stay as they are
            }
        }

        // doc.Info is created before the first SetInfo, so it reflects the dictionary as it stands now.
        byte[] packet = XmpWriter.Write(doc.Info, options is null ? 0 : (int)options.Part, conf, pdfUA, uaPart);
        doc.SetXmp(packet);
        PdfStream meta = doc.Catalog.GetStream("Metadata")!;
        meta.Dictionary.SetEntry("Filter", null);
        meta.Dictionary.SetEntry("DecodeParms", null);
    }

    internal static string ConformanceLetter(PdfAConformance c) => c switch { PdfAConformance.A => "A", PdfAConformance.U => "U", _ => "B" };
}
