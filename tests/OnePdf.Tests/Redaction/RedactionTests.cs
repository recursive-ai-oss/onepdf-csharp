using System.Text;
using OnePdf.Content;
using OnePdf.Tests.Content;

namespace OnePdf.Tests.Redaction;

/// <summary>Shared pieces of the redaction tests: a page with Helvetica, a redact-save-reopen round trip, and a search through every byte the file holds.</summary>
internal static class RedactionSupport
{
    public const string Font = "/Resources << /Font << /F1 5 0 R >> >>";

    public static byte[] Page(string content, string pageEntries = Font, Action<PdfBuilder>? extra = null, int objects = 12) =>
        TestPdf.Build(content, pageEntries, b =>
        {
            b.Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            extra?.Invoke(b);
        }, objects: objects);

    public static PdfDocument Edit(byte[] pdf) => PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });

    public static byte[] Save(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Table, UseObjectStreams = false, DocumentId = new byte[16] });
        return ms.ToArray();
    }

    /// <summary>The file's bytes followed by the decoded bytes of every stream, so a search sees what a reader would after inflating.</summary>
    public static byte[] Everything(byte[] pdf, bool skipFonts = false)
    {
        using var all = new MemoryStream();
        all.Write(pdf);
        using PdfDocument d = PdfDocument.Open(pdf);
        for (int n = 1; n <= d.ObjectCount; n++)
        {
            if (d.GetObject(n) is PdfStream s)
            {
                if (skipFonts && (s.Dictionary.ContainsKey("Length1") || s.Dictionary.GetName("Subtype") is "Type1C" or "CIDFontType0C" or "OpenType"))
                {
                    continue;
                }

                try
                {
                    all.Write(s.GetDecodedBytes().Span);
                }
                catch (PdfException)
                {
                }
            }
        }

        return all.ToArray();
    }

    public static bool Contains(byte[] haystack, string needle)
    {
        foreach (byte[] form in Forms(needle))
        {
            if (haystack.AsSpan().IndexOf(form) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public static int Count(byte[] haystack, string needle)
    {
        int n = 0;
        foreach (byte[] form in Forms(needle))
        {
            for (int i = haystack.AsSpan().IndexOf(form); i >= 0;)
            {
                n++;
                int next = haystack.AsSpan(i + 1).IndexOf(form);
                i = next < 0 ? -1 : i + 1 + next;
            }
        }

        return n;
    }

    // ASCII, hex and UTF-16 (both byte orders) forms of a string.
    private static IEnumerable<byte[]> Forms(string s)
    {
        yield return Encoding.Latin1.GetBytes(s);
        yield return Encoding.Latin1.GetBytes(Convert.ToHexString(Encoding.Latin1.GetBytes(s)));
        yield return Encoding.Latin1.GetBytes(Convert.ToHexString(Encoding.Latin1.GetBytes(s)).ToLowerInvariant());
        yield return Encoding.Unicode.GetBytes(s);
        yield return Encoding.BigEndianUnicode.GetBytes(s);
        yield return Encoding.Latin1.GetBytes(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s)));
    }

    public static PdfRectangle WordRect(byte[] pdf, string word, int page = 0)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        OnePdf.Text.PdfTextWord w = d.Pages[page].ExtractText().Words.First(x => x.Text == word);
        return w.Bounds;
    }

    public static List<(string Text, PdfRectangle Bounds)> Words(byte[] pdf, int page = 0)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        return [.. d.Pages[page].ExtractText().Words.Select(w => (w.Text, w.Bounds))];
    }

    public static byte[] Redact(byte[] pdf, IEnumerable<PdfRedactionRegion> regions, PdfRedactionOptions? options = null)
    {
        using PdfDocument d = Edit(pdf);
        d.Redact(regions, options);
        return Save(d);
    }

    public static string DisplayList(byte[] pdf, int page = 0)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        var list = new PdfDisplayList();
        d.Pages[page].Interpret(list, new PdfInterpretOptions { Annotations = PdfAnnotationMode.None });
        return list.ToText();
    }
}

public class TextRedactionTests
{
    private const string Line = "BT /F1 12 Tf 20 100 Td (Hello secret world) Tj ET";

    [Fact]
    public void RemovesTheWordAndKeepsTheRestInPlace()
    {
        byte[] pdf = RedactionSupport.Page(Line);
        List<(string Text, PdfRectangle Bounds)> before = RedactionSupport.Words(pdf);
        PdfRectangle secret = before.First(w => w.Text == "secret").Bounds;
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, secret)]);

        List<(string Text, PdfRectangle Bounds)> words = RedactionSupport.Words(after);
        Assert.DoesNotContain(words, w => w.Text.Contains("secret"));
        foreach (string keep in (string[])["Hello", "world"])
        {
            PdfRectangle b0 = before.First(w => w.Text == keep).Bounds;
            PdfRectangle b1 = words.First(w => w.Text == keep).Bounds;
            Assert.InRange(Math.Abs(b0.Left - b1.Left), 0, 0.01);
            Assert.InRange(Math.Abs(b0.Bottom - b1.Bottom), 0, 0.01);
            Assert.InRange(Math.Abs(b0.Right - b1.Right), 0, 0.01);
        }

        Assert.False(RedactionSupport.Contains(RedactionSupport.Everything(after), "secret"));
        Assert.True(RedactionSupport.Contains(RedactionSupport.Everything(after), "Hello"));
    }

    [Fact]
    public void WorksOnTjArraysQuotesAndSpacing()
    {
        const string content =
            "BT /F1 10 Tf 14 TL 10 150 Td [(Se)-80(cret)120( one)] TJ " +
            "(secret two) ' 3 1 (alpha secret) \" 0 0 Tw 0 0 Tc 0 -30 Td (tail) Tj ET";
        byte[] pdf = RedactionSupport.Page(content);
        List<(string Text, PdfRectangle Bounds)> before = RedactionSupport.Words(pdf);
        Assert.True(before.Any(w => w.Text == "one"), string.Join("|", before.Select(w => w.Text)));
        var regions = before.Where(w => w.Text.Contains("ecret") || w.Text == "Se").Select(w => new PdfRedactionRegion(0, w.Bounds)).ToList();
        byte[] after = RedactionSupport.Redact(pdf, regions);
        List<(string Text, PdfRectangle Bounds)> words = RedactionSupport.Words(after);
        Assert.DoesNotContain(words, w => w.Text.Contains("ecret"));
        foreach (string keep in (string[])["one", "two", "alpha", "tail"])
        {
            Assert.True(words.Any(w => w.Text == keep), string.Join("|", before.Select(w => w.Text)) + " => " + string.Join("|", words.Select(w => w.Text)));
            Assert.InRange(Math.Abs(before.First(w => w.Text == keep).Bounds.Left - words.First(w => w.Text == keep).Bounds.Left), 0, 0.01);
            Assert.InRange(Math.Abs(before.First(w => w.Text == keep).Bounds.Bottom - words.First(w => w.Text == keep).Bounds.Bottom), 0, 0.01);
        }
    }

    [Fact]
    public void FullyContainedOnlyKeepsGlyphsThatStickOut()
    {
        byte[] pdf = RedactionSupport.Page(Line);
        PdfRectangle secret = RedactionSupport.WordRect(pdf, "secret");
        // A box that covers the left half of the word only.
        var half = new PdfRectangle(secret.Left, secret.Bottom - 5, secret.Left + (secret.Width / 2), secret.Top + 5);
        byte[] a = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, half)], new PdfRedactionOptions { FullyContainedOnly = true });
        byte[] b = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, half)]);
        string ta = string.Join(" ", RedactionSupport.Words(a).Select(w => w.Text));
        string tb = string.Join(" ", RedactionSupport.Words(b).Select(w => w.Text));
        Assert.True(ta.Length > tb.Length, $"'{ta}' vs '{tb}'");
    }

    [Fact]
    public void FormXObjectsAreCopiedNotChangedInPlace()
    {
        // The form is drawn twice; only the first use is redacted.
        byte[] pdf = RedactionSupport.Page(
            "q 1 0 0 1 0 100 cm /Fm1 Do Q q 1 0 0 1 0 20 cm /Fm1 Do Q",
            "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm1 6 0 R >> >>",
            b => b.Stream(6, "/Type /XObject /Subtype /Form /BBox [0 0 200 40] /Resources << /Font << /F1 5 0 R >> >>", Encoding.Latin1.GetBytes("BT /F1 12 Tf 20 10 Td (Hello secret world) Tj ET")));
        List<(string Text, PdfRectangle Bounds)> before = RedactionSupport.Words(pdf);
        PdfRectangle top = before.Where(w => w.Text == "secret").OrderByDescending(w => w.Bounds.Bottom).First().Bounds;
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, top)]);
        List<(string Text, PdfRectangle Bounds)> words = RedactionSupport.Words(after);
        Assert.Single(words, w => w.Text == "secret");
        Assert.Equal(2, words.Count(w => w.Text == "Hello"));
        Assert.InRange(words.First(w => w.Text == "secret").Bounds.Bottom, 0, 60);
    }

    [Fact]
    public void UnredactedPagesAreUntouched()
    {
        byte[] pdf = RedactionSupport.Page(Line);
        using PdfDocument d = RedactionSupport.Edit(pdf);
        string before = RedactionSupport.DisplayList(pdf);
        PdfRedactionResult r = d.Redact([new PdfRedactionRegion(0, new PdfRectangle(150, 10, 190, 30))]);
        Assert.Equal(0, r.GlyphsRemoved);
        byte[] saved = RedactionSupport.Save(d);
        Assert.Equal(before, RedactionSupport.DisplayList(saved));
    }

    [Fact]
    public void ContentTheInterpreterStopsOnFailsTheCallAndLeavesTheDocumentAlone()
    {
        byte[] pdf = RedactionSupport.Page("q Q q Q q Q q Q " + Line);
        using PdfDocument d = PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true, Limits = PdfLimits.Default with { MaxOperatorsPerPage = 3 } });
        PdfRedactionException e = Assert.Throws<PdfRedactionException>(() => d.Redact([new PdfRedactionRegion(0, new PdfRectangle(0, 0, 200, 200))]));
        Assert.Equal(0, e.PageIndex);
        Assert.False(d.IsRedacted);
    }

    [Fact]
    public void IncrementalSaveIsRefusedAfterRedaction()
    {
        using PdfDocument d = RedactionSupport.Edit(RedactionSupport.Page(Line));
        d.Redact([new PdfRedactionRegion(0, new PdfRectangle(0, 0, 200, 200))]);
        Assert.Throws<InvalidOperationException>(() => d.SaveIncremental(new MemoryStream()));
    }

    [Fact]
    public void OverlayIsDrawnAfterwards()
    {
        byte[] pdf = RedactionSupport.Page(Line);
        PdfRectangle secret = RedactionSupport.WordRect(pdf, "secret");
        byte[] after = RedactionSupport.Redact(pdf, [new PdfRedactionRegion(0, secret) { OverlayText = "REMOVED" }], new PdfRedactionOptions { Overlay = new PdfRedactionOverlay() });
        string list = RedactionSupport.DisplayList(after);
        Assert.Contains("fill", list);
        string text = string.Join(" ", RedactionSupport.Words(after).Select(w => w.Text));
        Assert.Contains("REMOVED", text);
        Assert.DoesNotContain("secret", text);
    }
}
