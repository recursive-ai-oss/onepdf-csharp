using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OnePdf;
using OnePdf.PdfFonts;

namespace OnePdf.Fuzz;

/// <summary>Counters for one kind of font in a sweep, keyed by subtype and program kind.</summary>
internal sealed class FontStats
{
    public int Fonts;
    public int EmbeddedFailed;
    public int Substituted;
    public int NoProgram;
    public long Glyphs;
    public long Counted;
    public long NoUnicode;
    public long Outlines;
    public long EmptyOutlines;
    public long Bad;
    public int Samples;
}

/// <summary>
/// Walks every font resource reachable from the pages of a document (page resources, form XObjects, Type 3 glyph resources),
/// decodes each font's whole code range and exercises metrics, Unicode and outlines. Used by the <c>fonts-dir</c> sweep and the
/// <c>pdffont</c> fuzz target.
/// </summary>
internal static class FontSweep
{
    private const int MaxOutlinesPerFont = 20000;

    public static readonly List<string> Examples = [];

    /// <summary>Exercises every font of the document. Only the library's typed exceptions escape.</summary>
    public static void Document(PdfDocument doc, Dictionary<string, FontStats> stats, int compositeCodes, string label)
    {
        var seenDicts = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var seenFonts = new HashSet<PdfFont>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfDictionary Res, int Depth)>();
        int pages = Math.Min(doc.PageCount, 2000);
        for (int i = 0; i < pages; i++)
        {
            PdfPage page;
            try
            {
                page = doc.Pages[i];
            }
            catch (PdfException)
            {
                continue;
            }

            stack.Push((page.Resources, 0));
            while (stack.Count > 0)
            {
                (PdfDictionary res, int depth) = stack.Pop();
                if (!seenDicts.Add(res))
                {
                    continue;
                }

                if (res.GetDictionary("Font") is { } fonts)
                {
                    foreach (string key in fonts.Keys.ToList())
                    {
                        PdfDictionary? fd = fonts.GetDictionary(key);
                        PdfFont font = doc.GetFont(fd);
                        if (seenFonts.Add(font))
                        {
                            Font(font, stats, compositeCodes, label);
                            if (font is Type3Font t3 && t3.Resources is { } r3 && depth < 6)
                            {
                                stack.Push((r3, depth + 1));
                            }
                        }
                    }
                }

                if (depth < 6 && res.GetDictionary("XObject") is { } xo)
                {
                    foreach (string key in xo.Keys.ToList())
                    {
                        if (xo.Get(key) is PdfStream { } s && s.Dictionary.GetName("Subtype") == "Form" && s.Dictionary.GetDictionary("Resources") is { } fr)
                        {
                            stack.Push((fr, depth + 1));
                        }
                    }
                }
            }
        }
    }

    private static void Font(PdfFont font, Dictionary<string, FontStats> stats, int compositeCodes, string label)
    {
        string key = font.Subtype + ":" + (font.Program?.Kind.ToString() ?? "none") + (font.IsSubstituted ? "(subst)" : string.Empty);
        if (!stats.TryGetValue(key, out FontStats? st))
        {
            stats[key] = st = new FontStats();
        }

        st.Fonts++;
        if (font.EmbeddedFailed)
        {
            st.EmbeddedFailed++;
        }

        if (font.IsSubstituted)
        {
            st.Substituted++;
        }

        if (font.Program is null && !font.IsType3)
        {
            st.NoProgram++;
        }

        var seen = new HashSet<FontGlyph>(ReferenceEqualityComparer.Instance);
        var gidsDone = new HashSet<int>();
        int outlines = 0;
        int limit = 256;
        if (font.IsComposite)
        {
            // Identity-coded fonts have as many codes as glyphs; CID-keyed CFF has sparse CIDs, so it gets the whole range.
            limit = font.Program is null || font.Program.IsCidKeyed ? compositeCodes : Math.Min(compositeCodes, font.Program.GlyphCount + 16);
        }
        byte[] buf = new byte[2];
        for (int code = 0; code < limit; code++)
        {
            ReadOnlySpan<byte> s;
            if (font.IsComposite)
            {
                buf[0] = (byte)(code >> 8);
                buf[1] = (byte)code;
                s = buf.AsSpan();
            }
            else
            {
                buf[0] = (byte)code;
                s = buf.AsSpan(0, 1);
            }

            font.Next(s, 0, out FontGlyph? g);
            if (g is null || !seen.Add(g))
            {
                continue;
            }

            st.Glyphs++;
            bool hasShape = false;
            if (g.Exists && g.Gid >= 0 && outlines < MaxOutlinesPerFont && gidsDone.Add(g.Gid))
            {
                outlines++;
                st.Outlines++;
                hasShape = GlyphOutlineCheck(g, st);
            }
            else if (g.Exists && g.Gid >= 0)
            {
                hasShape = true;
            }

            // A glyph counts when it has an outline (or a glyph procedure): subsets keep unused glyph slots that are empty and unmapped.
            bool real = font.IsType3 ? g.CharProc is not null : font.Program is null ? g.Advance > 0 : hasShape;
            if (real)
            {
                st.Counted++;
                if (!g.HasUnicode)
                {
                    st.NoUnicode++;
                    if (st.Samples < 6)
                    {
                        st.Samples++;
                        Examples.Add($"NO-UNICODE {key} {label} {font.Name} code={g.Code:X} name={g.Name} gid={g.Gid} cid={g.Cid}");
                    }
                }
            }

            if (!double.IsFinite(g.Advance) || !double.IsFinite(g.VerticalAdvance) || !double.IsFinite(g.OriginX) || !double.IsFinite(g.OriginY) || g.Unicode is null)
            {
                st.Bad++;
            }

            if (g.OutlineMatrix is { } m && m.Any(static v => !double.IsFinite(v)))
            {
                st.Bad++;
            }

            if ((code & 0x3FFF) == 0x3FFF)
            {
                font.ClearGlyphCache();
                seen.Clear();
            }
        }

        font.ClearGlyphCache();
    }

    private static bool GlyphOutlineCheck(FontGlyph g, FontStats st)
    {
        var o = g.GetOutline();
        if (o.IsEmpty)
        {
            st.EmptyOutlines++;
        }

        foreach (float c in o.Coords)
        {
            if (!float.IsFinite(c))
            {
                st.Bad++;
                break;
            }
        }

        return !o.IsEmpty;
    }

    /// <summary>The <c>fonts-dir</c> mode: <c>fonts-dir &lt;dir&gt;...</c>.</summary>
    public static int RunDirs(string[] args)
    {
        var stats = new Dictionary<string, FontStats>();
        int files = 0, opened = 0, typed = 0, crashes = 0, slow = 0;
        var clock = Stopwatch.StartNew();
        foreach (string dir in args.Skip(1))
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                files++;
                if (new FileInfo(f).Length > 100L << 20)
                {
                    continue;
                }

                var sw = Stopwatch.StartNew();
                try
                {
                    using PdfDocument doc = PdfDocument.Open(f);
                    Document(doc, stats, 65536, Path.GetFileName(f));
                    foreach (PdfDiagnostic d in doc.Diagnostics.Where(static d => d.Message.Contains("embedded font program", StringComparison.Ordinal)).Take(2))
                    {
                        Examples.Add($"EMBEDDED-FAILED {Path.GetFileName(f)}: {d.Message}");
                    }
                    opened++;
                }
                catch (PdfException)
                {
                    typed++;
                }
#pragma warning disable CA1031 // The sweep reports every other exception as a crash.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    crashes++;
                    Console.WriteLine($"CRASH {f}: {ex}");
                }

                if (sw.Elapsed.TotalSeconds > 10)
                {
                    slow++;
                    Console.WriteLine($"SLOW {f}: {sw.Elapsed.TotalSeconds:F1}s");
                }
            }
        }

        Console.WriteLine($"files={files} opened={opened} typed-errors={typed} crashes={crashes} slow={slow} seconds={clock.Elapsed.TotalSeconds:F0}");
        Console.WriteLine("type | fonts | embeddedFailed | substituted | noProgram | glyphs | counted | noUnicode | outlines | emptyOutlines | bad");
        foreach ((string k, FontStats s) in stats.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{k} | {s.Fonts} | {s.EmbeddedFailed} | {s.Substituted} | {s.NoProgram} | {s.Glyphs} | {s.Counted} | {s.NoUnicode} | {s.Outlines} | {s.EmptyOutlines} | {s.Bad}");
        }

        var total = new FontStats();
        foreach (FontStats s in stats.Values)
        {
            total.Fonts += s.Fonts;
            total.EmbeddedFailed += s.EmbeddedFailed;
            total.Substituted += s.Substituted;
            total.NoProgram += s.NoProgram;
            total.Counted += s.Counted;
            total.NoUnicode += s.NoUnicode;
            total.Bad += s.Bad;
        }

        Console.WriteLine($"TOTAL fonts={total.Fonts} embeddedFailed={total.EmbeddedFailed} substituted={total.Substituted} noProgram={total.NoProgram} counted={total.Counted} noUnicode={total.NoUnicode} bad={total.Bad}");
        foreach (string e in Examples.OrderBy(static e => e, StringComparer.Ordinal).Take(120))
        {
            Console.WriteLine(e);
        }

        return crashes == 0 && total.Bad == 0 ? 0 : 1;
    }

    /// <summary>Fuzz target: the bytes are a PDF; every font in it is loaded and decoded over a reduced code range.</summary>
    public static void Target(ReadOnlySpan<byte> data)
    {
        try
        {
            using PdfDocument doc = PdfDocument.Open(data.ToArray(), new PdfOpenOptions { Limits = PdfLimits.Default });
            Examples.Clear();
            Document(doc, [], 1024, "fuzz");
        }
        catch (PdfException)
        {
        }
    }
}
