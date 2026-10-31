using System.Linq;
using OnePdf.FontData;
using OnePdf.FontPrograms;

namespace OnePdf.PdfA;

// Repairs of font dictionaries and other small dictionary-level fixes.
internal sealed partial class ConversionRepairs
{
    private void RepairFontDictionary(PdfDictionary d, PdfStream? stream)
    {
        if (stream is not null)
        {
            return;
        }

        // ISO 19005-2 6.1.8: names are UTF-8 once their #xx escapes are expanded. A font name with other bytes cannot be kept; the name is only a label for an embedded font.
        foreach (string key in s_nameKeys)
        {
            if (d.Get(key) is PdfName n && NeedsSanitising(n.Value))
            {
                d.SetEntry(key, new PdfName(Sanitise(n.Value)));
                _log.Add(PdfAChangeKind.FontsRepaired, "Font names with characters outside ASCII were rewritten.");
            }
        }

        string? type = d.GetName("Type");
        string? subtype = d.GetName("Subtype");
        if (type == "FontDescriptor" || (d.ContainsKey("FontName") && d.ContainsKey("Flags") && d.ContainsKey("FontBBox")))
        {
            // 6.2.11.4.2: CIDSet and CharSet must list every glyph of the program. They are optional hints, so a wrong one is dropped.
            if (d.ContainsKey("CIDSet") || d.ContainsKey("CharSet"))
            {
                d.RemoveEntry("CIDSet");
                d.RemoveEntry("CharSet");
                _log.Add(PdfAChangeKind.FontsRepaired, "Font descriptors' /CIDSet and /CharSet hints were removed.");
            }
        }

        if (subtype == "CIDFontType2" && !d.ContainsKey("CIDToGIDMap") && d.GetDictionary("FontDescriptor") is { } fd && fd.ContainsKey("FontFile2"))
        {
            // ISO 32000-1 9.7.4.2: without /CIDToGIDMap the mapping is Identity, which 6.2.11.3.2 asks to see written.
            d.SetEntry("CIDToGIDMap", new PdfName("Identity"));
            _log.Add(PdfAChangeKind.FontsRepaired, "The default /CIDToGIDMap Identity was written for embedded TrueType CID fonts.");
        }

        if (subtype is "Type1" or "MMType1" or "CIDFontType0" && d.GetDictionary("FontDescriptor")?.GetStream("FontFile3") is { } ff3)
        {
            RepairFontFile3Subtype(ff3, subtype == "CIDFontType0");
        }

        if (subtype == "TrueType" && d.GetDictionary("FontDescriptor") is { } desc)
        {
            RepairTrueTypeEncoding(d, desc);
        }
    }

    // ISO 19005-2 6.2.11.2: a FontFile3 stream names what it holds. A bare CFF program is Type1C (CIDFontType0C when CID-keyed); a whole OpenType
    // file with CFF outlines is OpenType. Validators read the program by that name, so a wrong one reads as no program at all.
    private void RepairFontFile3Subtype(PdfStream file, bool cidFont)
    {
        ReadOnlyMemory<byte> data;
        try
        {
            data = file.GetDecodedBytes(_ct);
        }
        catch (PdfException)
        {
            return;
        }

        if (data.Length < 4)
        {
            return;
        }

        ReadOnlySpan<byte> h = data.Span;
        bool openType = h[..4].SequenceEqual("OTTO"u8);
        string? current = file.Dictionary.GetName("Subtype");
        string wanted = openType ? "OpenType" : cidFont ? "CIDFontType0C" : "Type1C";
        if (current is "OpenType" == openType && current is "Type1C" or "CIDFontType0C" or "OpenType")
        {
            return;
        }

        file.Dictionary.SetEntry("Subtype", new PdfName(wanted));
        _log.Add(PdfAChangeKind.FontsRepaired, "The /Subtype of embedded CFF and OpenType font programs was corrected.");
    }

    private static readonly string[] s_nameKeys = ["BaseFont", "FontName"];

    private static bool NeedsSanitising(string name)
    {
        foreach (char c in name)
        {
            if (c is < '!' or > '~')
            {
                return true;
            }
        }

        return false;
    }

    private static string Sanitise(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] is < '!' or > '~')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    // ISO 19005-2 6.2.11.6: a symbolic TrueType font has no /Encoding; a non-symbolic one has WinAnsiEncoding or MacRomanEncoding.
    private void RepairTrueTypeEncoding(PdfDictionary font, PdfDictionary desc)
    {
        long flags = desc.GetInt("Flags");
        bool symbolic = (flags & 4) != 0;
        if (!symbolic)
        {
            // Glyph names outside the Adobe Glyph List (a1, a2... of a Dingbats font) cannot be a non-symbolic encoding; such a font is a symbolic one.
            if (font.Get("Encoding") is PdfDictionary ed && ed.Get("Differences") is PdfArray diff && HasNonGlyphListName(diff)
                && desc.GetStream("FontFile2") is { } ttf && HasSymbolicCmap(ttf))
            {
                font.RemoveEntry("Encoding");
                desc.SetEntry("Flags", new PdfInteger((flags & ~32L) | 4));
                _log.Add(PdfAChangeKind.FontsRepaired, "TrueType fonts with glyph names outside the Adobe Glyph List were made symbolic.");
            }
            else if (desc.GetStream("FontFile2") is { } ttf3 && HasOnlySymbolicCmap(ttf3))
            {
                // A non-symbolic font is read through a (3,1) or (1,0) cmap and the glyph names (ISO 32000-2 9.6.5.4); a program that offers only a (3,0) cmap
                // is read through the code itself, which is what a symbolic font does.
                font.RemoveEntry("Encoding");
                desc.SetEntry("Flags", new PdfInteger((flags & ~32L) | 4));
                _log.Add(PdfAChangeKind.FontsRepaired, "TrueType fonts whose program has only a symbolic (3,0) cmap were made symbolic.");
            }
            else
            {
                DropDifferencesWithoutUnicodeCmap(font, desc);
            }

            return;
        }

        PdfObject? enc = font.Get("Encoding");
        if (enc is null)
        {
            return;
        }

        if ((flags & 32) != 0)
        {
            // Both flags set cannot be true; an Encoding is present, so the font was meant as non-symbolic.
            desc.SetEntry("Flags", new PdfInteger(flags & ~4L));
            _log.Add(PdfAChangeKind.FontsRepaired, "TrueType fonts flagged both symbolic and non-symbolic were made non-symbolic.");
            return;
        }

        string? baseEnc = enc is PdfName en ? en.Value : (enc as PdfDictionary)?.GetName("BaseEncoding");
        bool hasUnicodeCmap = false;
        if (baseEnc is "WinAnsiEncoding" or "MacRomanEncoding" && desc.GetStream("FontFile2") is { } file)
        {
            try
            {
                SfntFont program = SfntFont.Parse(file.GetDecodedBytes(_ct), 0, GlyphPrograms.Never);
                hasUnicodeCmap = program.FindCmap(3, 1) is not null;
            }
            catch (PdfException)
            {
                // an unreadable program is reported by the check
            }
            catch (FontProgramException)
            {
            }
        }

        if (hasUnicodeCmap)
        {
            desc.SetEntry("Flags", new PdfInteger((flags & ~4L) | 32));
            _log.Add(PdfAChangeKind.FontsRepaired, "Symbolic TrueType fonts with a named encoding and a Unicode cmap were made non-symbolic.");
            DropDifferencesWithoutUnicodeCmap(font, desc);
        }
        else
        {
            font.RemoveEntry("Encoding");
            _log.Add(PdfAChangeKind.FontsRepaired, "The /Encoding of symbolic TrueType fonts was removed.");
        }
    }

    private void DropDifferencesWithoutUnicodeCmap(PdfDictionary font, PdfDictionary desc)
    {
        if (font.Get("Encoding") is PdfDictionary enc && enc.ContainsKey("Differences") && desc.GetStream("FontFile2") is { } program && !HasUnicodeCmap(program))
        {
            // Validators resolve /Differences through the (3,1) cmap of the program (ISO 19005-2 6.2.11.6); without one they find no mapping.
            // The named encoding stays, so only the codes the array changed fall back to it.
            enc.RemoveEntry("Differences");
            _log.Add(PdfAChangeKind.FontsRepaired, "The /Differences of TrueType fonts whose program has no Unicode cmap were removed.");
            _log.Warn("A TrueType font without a Unicode cmap lost its /Differences; codes the array remapped now use the named encoding.");
        }
    }

    private static bool HasNonGlyphListName(PdfArray diff)
    {
        for (int i = 0; i < diff.Count; i++)
        {
            if (diff.GetName(i) is { } name && !GlyphList.TryGetUnicode(name, out _))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasUnicodeCmap(PdfStream file)
    {
        try
        {
            return SfntFont.Parse(file.GetDecodedBytes(_ct), 0, GlyphPrograms.Never).FindCmap(3, 1) is not null;
        }
        catch (PdfException)
        {
            return true;
        }
        catch (FontProgramException)
        {
            return true;
        }
    }

    private bool HasOnlySymbolicCmap(PdfStream file)
    {
        try
        {
            SfntFont program = SfntFont.Parse(file.GetDecodedBytes(_ct), 0, GlyphPrograms.Never);
            return program.FindCmap(3, 1) is null && program.FindCmap(3, 0) is not null;
        }
        catch (PdfException)
        {
            return false;
        }
        catch (FontProgramException)
        {
            return false;
        }
    }

    private bool HasSymbolicCmap(PdfStream file)
    {
        try
        {
            SfntFont program = SfntFont.Parse(file.GetDecodedBytes(_ct), 0, GlyphPrograms.Never);
            return program.FindCmap(3, 0) is not null || program.FindCmap(1, 0) is not null;
        }
        catch (PdfException)
        {
            return false;
        }
        catch (FontProgramException)
        {
            return false;
        }
    }

    // A signature's byte range describes the file as it was; a conversion writes a different file.
    private void RemoveSignature(PdfDictionary d, PdfStream? stream)
    {
        if (stream is null && d.ContainsKey("V") && d.ContainsKey("FT") && InheritedName(d, "FT") == "Sig")
        {
            d.RemoveEntry("V");
            _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "Digital signatures were removed: they cannot survive a rewrite of the file.");
            _log.Warn("A digital signature was removed; the signature field is kept, empty.");
        }
    }

    // ISO 19005-2 6.9: when a configuration has an /Order array it must list every optional content group.
    private void CompleteOrder(PdfDictionary ocp, List<PdfDictionary> configs)
    {
        if (ocp.Get("OCGs") is not PdfArray groups)
        {
            return;
        }

        foreach (PdfDictionary c in configs)
        {
            if (c.Get("Order") is not PdfArray order)
            {
                continue;
            }

            var listed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<PdfArray>();
            stack.Push(order);
            int steps = 0;
            while (stack.Count > 0 && steps++ < 100_000)
            {
                PdfArray a = stack.Pop();
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is PdfArray inner)
                    {
                        stack.Push(inner);
                    }
                    else if (a[i] is PdfDictionary g)
                    {
                        listed.Add(g);
                    }
                }
            }

            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i] is PdfDictionary g && !listed.Contains(g) && groups.GetRaw(i) is PdfReference r)
                {
                    order.Append(r);
                    _log.Add(PdfAChangeKind.ForbiddenEntriesRemoved, "Optional content groups missing from a configuration's /Order were added to it.");
                }
            }
        }
    }

    // ISO 19005-2 6.2.2: a form XObject or pattern that uses resources has its own /Resources. One without borrows the page's, which is what a reader does.
    public void GiveResourcesToForms()
    {
        var done = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        PdfPageCollection pages = _doc.Pages;
        for (int p = 0; p < pages.Count; p++)
        {
            _ct.ThrowIfCancellationRequested();
            PdfDictionary page = pages[p].Dictionary;
            PdfObject? raw = null;
            PdfDictionary? cur = page;
            for (int depth = 0; cur is not null && depth < 64; depth++)
            {
                if (cur.ContainsKey("Resources"))
                {
                    raw = cur.GetRaw("Resources");
                    break;
                }

                cur = cur.GetDictionary("Parent");
            }

            if (_doc.Resolve(raw) is not PdfDictionary pageResources)
            {
                continue;
            }

            // ISO 19005-2 6.2.2 wants the resources "explicitly associated" with the content, so an inherited dictionary is written on the page itself.
            if (!page.ContainsKey("Resources") && raw is not null)
            {
                page.SetEntry("Resources", raw);
                _log.Add(PdfAChangeKind.Other, "Pages that inherited their resources were given them directly.");
            }

            PdfObject shared = raw is PdfReference ? raw : pageResources;
            var stack = new Stack<PdfDictionary>();
            stack.Push(pageResources);
            while (stack.Count > 0)
            {
                PdfDictionary res = stack.Pop();
                foreach (string category in s_xobjectCategories)
                {
                    if (res.GetDictionary(category) is not { } members)
                    {
                        continue;
                    }

                    foreach (KeyValuePair<string, PdfObject> e in members.Entries.ToArray())
                    {
                        if (_doc.Resolve(e.Value) is not PdfStream form || !(form.Dictionary.GetName("Subtype") == "Form" || form.Dictionary.GetInt("PatternType") == 1) || !done.Add(form))
                        {
                            continue;
                        }

                        if (form.Dictionary.Get("Resources") is not PdfDictionary own)
                        {
                            form.Dictionary.SetEntry("Resources", shared);
                            _log.Add(PdfAChangeKind.Other, "Form XObjects and patterns without their own /Resources were given the page's.");
                        }
                        else
                        {
                            stack.Push(own);
                        }
                    }
                }
            }
        }
    }

    private static readonly string[] s_xobjectCategories = ["XObject", "Pattern"];
}
