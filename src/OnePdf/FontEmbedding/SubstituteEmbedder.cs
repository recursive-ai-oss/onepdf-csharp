using System.Linq;
using OnePdf.FontData;
using OnePdf.PdfFonts;

namespace OnePdf.FontEmbedding;

/// <summary>Finds the fonts a document names without embedding and gives each a metric-compatible substitute from the font provider.</summary>
internal static class SubstituteEmbedder
{
    public static PdfSubstituteEmbedReport Run(PdfDocument doc, PdfSubstituteEmbedOptions options, CancellationToken ct)
    {
        var entries = new List<PdfSubstituteEntry>();
        var faces = new Dictionary<string, PdfFontFile?>(StringComparer.Ordinal);
        foreach (PdfDictionary font in CollectFonts(doc, ct))
        {
            ct.ThrowIfCancellationRequested();
            int number = font.Reference?.Number ?? -1;
            string baseFont = font.GetName("BaseFont") ?? string.Empty;
            string? subtype = font.GetName("Subtype");
            if (subtype is "Type3" || !(subtype is "Type1" or "MMType1" or "TrueType" or "Type0"))
            {
                continue;
            }

            PdfDictionary? desc = subtype == "Type0"
                ? (font.Get("DescendantFonts") is PdfArray { Count: > 0 } a ? a[0] as PdfDictionary : null)?.GetDictionary("FontDescriptor")
                : font.GetDictionary("FontDescriptor");
            if (desc is not null && FontEmbedder.HasFontFile(desc))
            {
                entries.Add(new PdfSubstituteEntry(number, baseFont, PdfSubstituteOutcome.AlreadyEmbedded, null, null, "The font already has a program."));
                continue;
            }

            string? standard = Core14.Resolve(FontEmbedder.StripTag(subtype == "Type0" ? DescendantName(font) ?? baseFont : baseFont));
            if (standard is null or "Symbol" or "ZapfDingbats")
            {
                entries.Add(new PdfSubstituteEntry(number, baseFont, PdfSubstituteOutcome.NoSubstitute, null, null,
                    standard is null ? "The name is not a standard face or a common alias of one." : "No substitute with matching glyphs exists for " + standard + "."));
                continue;
            }

            standard = ApplyDescriptorStyle(standard, desc);
            PdfSubstituteFont? sub = Find(doc, options, standard);
            if (sub is null)
            {
                entries.Add(new PdfSubstituteEntry(number, baseFont, PdfSubstituteOutcome.NoSubstitute, null, null, "The font provider has no substitute for " + standard + "."));
                continue;
            }

            if (subtype == "Type0" && !font.ContainsKey("ToUnicode"))
            {
                entries.Add(new PdfSubstituteEntry(number, baseFont, PdfSubstituteOutcome.NoSubstitute, sub.Name, null, "A composite font needs a /ToUnicode map to pick glyphs of a substitute; this one has none."));
                continue;
            }

            string key = sub.CacheKey ?? sub.Name + "#" + sub.FaceIndex;
            if (!faces.TryGetValue(key, out PdfFontFile? file))
            {
                try
                {
                    file = PdfFontFile.Open(sub.Data, sub.FaceIndex);
                }
                catch (PdfFormatException)
                {
                    file = null;
                }

                faces[key] = file;
            }

            if (file is null || file.Kind != PdfFontFileKind.TrueType)
            {
                entries.Add(new PdfSubstituteEntry(number, baseFont, PdfSubstituteOutcome.NoSubstitute, sub.Name, null, "The substitute is not a TrueType-outline font."));
                continue;
            }

            try
            {
                var o = new PdfFontEmbedOptions { Subset = options.Subset, Widths = options.Widths, MissingGlyphs = PdfMissingGlyphPolicy.UseNotdef };
                PdfFontEmbedResult r = FontEmbedder.Embed(doc, font, file, o, ct);
                string note = $"Embedded {sub.Name} as {r.BaseFont}" + (r.MissingCodes.Count > 0 ? $"; {r.MissingCodes.Count} shown code(s) have no glyph in it." : ".");
                entries.Add(new PdfSubstituteEntry(number, baseFont, PdfSubstituteOutcome.Embedded, sub.Name, null, note));
            }
            catch (PdfFontEmbedException ex)
            {
                entries.Add(new PdfSubstituteEntry(number, baseFont, PdfSubstituteOutcome.Refused, sub.Name, ex.Reason, ex.Message));
            }
        }

        return new PdfSubstituteEmbedReport(entries);
    }

    private static string? DescendantName(PdfDictionary type0) =>
        type0.Get("DescendantFonts") is PdfArray { Count: > 0 } a && a[0] is PdfDictionary d ? d.GetName("BaseFont") : null;

    // The name decides the style; descriptor flags add a style the name doesn't carry (ISO 32000-2 9.8.2 Italic and ForceBold).
    private static string ApplyDescriptorStyle(string standard, PdfDictionary? desc)
    {
        int flags = desc is not null && desc.ContainsKey("Flags") ? (int)desc.GetInt("Flags") : 0;
        bool italic = standard.Contains("Italic", StringComparison.Ordinal) || standard.Contains("Oblique", StringComparison.Ordinal);
        bool bold = standard.Contains("Bold", StringComparison.Ordinal);
        bool wantItalic = italic || (flags & 64) != 0;
        bool wantBold = bold || (flags & (1 << 18)) != 0;
        if (wantItalic == italic && wantBold == bold)
        {
            return standard;
        }

        string family = standard.StartsWith("Courier", StringComparison.Ordinal) ? "Courier" : standard.StartsWith("Times", StringComparison.Ordinal) ? "Times" : "Helvetica";
        string slant = family == "Times" ? "Italic" : "Oblique";
        return family switch
        {
            "Times" => (wantBold, wantItalic) switch { (true, true) => "Times-BoldItalic", (true, false) => "Times-Bold", (false, true) => "Times-Italic", _ => "Times-Roman" },
            _ => (wantBold, wantItalic) switch { (true, true) => family + "-Bold" + slant, (true, false) => family + "-Bold", (false, true) => family + "-" + slant, _ => family },
        };
    }

    private static PdfSubstituteFont? Find(PdfDocument doc, PdfSubstituteEmbedOptions options, string standard)
    {
        var request = new PdfFontRequest
        {
            FontName = standard,
            FamilyName = standard.Split('-')[0],
            StandardName = standard,
            IsBold = standard.Contains("Bold", StringComparison.Ordinal),
            IsItalic = standard.Contains("Italic", StringComparison.Ordinal) || standard.Contains("Oblique", StringComparison.Ordinal),
            IsSerif = standard.StartsWith("Times", StringComparison.Ordinal),
            IsFixedPitch = standard.StartsWith("Courier", StringComparison.Ordinal),
        };
        if ((options.Provider ?? doc.FontProvider) is { } own)
        {
            return own.FindFont(request);
        }

        foreach (IPdfFontProvider p in PdfFontProviders.RegisteredProviders)
        {
            if (p.FindFont(request) is { } sub)
            {
                return sub;
            }
        }

        return null;
    }

    // Every font dictionary the pages can reach, once: page resources, the forms and annotation appearances they draw, and the form's default resources.
    private static List<PdfDictionary> CollectFonts(PdfDocument doc, CancellationToken ct)
    {
        var result = new List<PdfDictionary>();
        var seenFonts = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var seenRes = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfDictionary Res, int Depth)>();
        if (doc.Form?.Dictionary.GetDictionary("DR") is { } dr)
        {
            stack.Push((dr, 0));
        }

        PdfPageCollection pages = doc.Pages;
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            PdfPage page = pages[i];
            stack.Push((page.Resources, 0));
            foreach (PdfAnnotation a in page.Annotations)
            {
                if (a.Dictionary.GetDictionary("AP") is { } ap)
                {
                    foreach (string k in ap.Keys.ToArray())
                    {
                        switch (ap.Get(k))
                        {
                            case PdfStream s when s.Dictionary.GetDictionary("Resources") is { } r:
                                stack.Push((r, 1));
                                break;
                            case PdfDictionary states:
                                foreach (string st in states.Keys.ToArray())
                                {
                                    if (states.Get(st) is PdfStream ss && ss.Dictionary.GetDictionary("Resources") is { } r2)
                                    {
                                        stack.Push((r2, 1));
                                    }
                                }

                                break;
                            default:
                                break;
                        }
                    }
                }
            }

            while (stack.Count > 0)
            {
                (PdfDictionary res, int depth) = stack.Pop();
                if (!seenRes.Add(res) || depth > doc.Limits.MaxRecursionDepth)
                {
                    continue;
                }

                if (res.GetDictionary("Font") is { } fonts)
                {
                    foreach (string key in fonts.Keys.ToArray())
                    {
                        if (fonts.Get(key) is PdfDictionary f && seenFonts.Add(f))
                        {
                            result.Add(f);
                        }
                    }
                }

                if (res.GetDictionary("XObject") is { } xo)
                {
                    foreach (string key in xo.Keys.ToArray())
                    {
                        if (xo.Get(key) is PdfStream { } xs && xs.Dictionary.GetName("Subtype") == "Form" && xs.Dictionary.GetDictionary("Resources") is { } inner)
                        {
                            stack.Push((inner, depth + 1));
                        }
                    }
                }
            }
        }

        return result;
    }
}
