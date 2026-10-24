using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OnePdf.Content;

namespace OnePdf.Tests.Content;

public class DisplayListTests
{
    private static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);

    private const string Busy =
        "q 0 0 100 100 re W n 1 0 0 rg 10 10 30 30 re f q 120 120 50 50 re W n 0 1 0 rg 130 130 10 10 re f Q " +
        "BT /F 10 Tf 150 20 Td (hi) Tj ET 0 0 1 RG 5 w 180 180 m 190 190 l S Q 0 0 5 5 re f";

    private static byte[] BusyPdf() => TestPdf.Build(Busy, "/Resources << /Font << /F << /Type /Font /Subtype /Type1 /BaseFont /Courier >> >> >>");

    [Fact]
    public void ReplayReproducesTheLiveText()
    {
        using PdfDocument doc = TestPdf.Open(BusyPdf());
        var live = new StringWriter();
        var writer = new PdfDisplayListTextWriter(live);
        writer.WriteHeader();
        TestPdf.Page(doc).Interpret(writer);
        PdfDisplayList list = TestPdf.Record(doc);
        Assert.Equal(live.ToString(), list.ToText());
        Assert.True(list.ItemCount > 5);
    }

    [Fact]
    public void LargeRecordingSpansStorageChunksAndReplaysExactly()
    {
        // Thousands of items, hundreds of distinct states, many glyph runs and one path far larger than a storage chunk.
        var sb = new StringBuilder("BT /F 8 Tf 1 0 0 1 10 10 Tm ");
        sb.Append("(start) Tj ET ");
        for (int i = 0; i < 6000; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{(i % 251) / 251.0:F3} 0 0 rg {i % 500} {i / 12} 3 3 re f ");
            if (i % 7 == 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"BT /F 8 Tf 1 0 0 1 {i % 400} {i / 20} Tm (n{i}) Tj ET ");
            }
        }

        sb.Append("0 0 m ");
        for (int i = 1; i < 30000; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{i % 600} {(i * 7) % 700} l ");
        }

        sb.Append("S 0 0 1 rg 5 5 20 20 re f");
        byte[] pdf = TestPdf.Build(sb.ToString(), "/Resources << /Font << /F << /Type /Font /Subtype /Type1 /BaseFont /Courier >> >> >>");
        using PdfDocument doc = TestPdf.Open(pdf);
        var live = new StringWriter();
        var writer = new PdfDisplayListTextWriter(live);
        writer.WriteHeader();
        TestPdf.Page(doc).Interpret(writer);
        PdfDisplayList list = TestPdf.Record(doc);
        Assert.True(list.ItemCount > 6800);
        Assert.Equal(live.ToString(), list.ToText());
    }

    [Fact]
    public void ReplayWithClipRectCullsPaintsAndWholeClipBrackets()
    {
        using PdfDocument doc = TestPdf.Open(BusyPdf());
        PdfDisplayList list = TestPdf.Record(doc);
        string full = list.ToText();

        // Only the area around (10..40, 10..40) in output space (y flipped): the red square.
        var sw = new StringWriter();
        var w = new PdfDisplayListTextWriter(sw);
        w.WriteHeader();
        list.Replay(w, new PdfBounds(5, 155, 45, 195));
        string part = sw.ToString();
        Assert.Contains("fill nonzero M 10 10 L 40 10", part);
        Assert.DoesNotContain("M 130 130", part);
        Assert.Empty(TestPdf.Lines(part, "text"));
        Assert.Empty(TestPdf.Lines(part, "stroke"));
        Assert.True(part.Length < full.Length);

        // The device still sees balanced brackets.
        var chk = new CheckingDevice();
        list.Replay(chk, new PdfBounds(5, 155, 45, 195));
        Assert.Empty(chk.Problems);
        var chk2 = new CheckingDevice();
        list.Replay(chk2, new PdfBounds(-1000, -1000, -900, -900));
        Assert.Empty(chk2.Problems);
        Assert.Equal(0, chk2.Paints + chk2.Runs);
    }

    [Fact]
    public void CullingKeepsMarkedContentBalancedAcrossSkippedClips()
    {
        byte[] data = TestPdf.Build("q 150 150 10 10 re W n /A BMC 150 150 5 5 re f Q 0 0 1 1 re f EMC");
        using PdfDocument doc = TestPdf.Open(data);
        PdfDisplayList list = TestPdf.Record(doc);
        var chk = new CheckingDevice();
        list.Replay(chk, new PdfBounds(0, 190, 5, 200));
        Assert.Empty(chk.Problems);
        Assert.Equal(1, chk.Paints);
    }

    [Fact]
    public void ItemBoundsAreInOutputSpace()
    {
        using PdfDocument doc = TestPdf.Open(TestPdf.Build("10 20 30 40 re f 0 0 m 100 0 l 10 w S"));
        PdfDisplayList list = TestPdf.Record(doc);
        int fill = Enumerable.Range(0, list.ItemCount).First(i => list.GetItemKind(i) == "Fill");
        Assert.Equal(new PdfBounds(10, 140, 40, 180), list.GetItemBounds(fill));
        int stroke = Enumerable.Range(0, list.ItemCount).First(i => list.GetItemKind(i) == "Stroke");
        PdfBounds sb = list.GetItemBounds(stroke);
        Assert.True(sb.Bottom <= 195 && sb.Top >= 205);
        Assert.Throws<ArgumentOutOfRangeException>(() => list.GetItemBounds(list.ItemCount));
    }

    [Fact]
    public void ConcurrentReplayGivesTheSameResultEverywhere()
    {
        using PdfDocument doc = TestPdf.Open(BusyPdf());
        PdfDisplayList list = TestPdf.Record(doc);
        string expected = list.ToText();
        string expectedPart = Part(list);
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            for (int k = 0; k < 20; k++)
            {
                if (list.ToText() != expected)
                {
                    errors.Add("full replay differs");
                }

                if (Part(list) != expectedPart)
                {
                    errors.Add("culled replay differs");
                }
            }
        });
        Assert.Empty(errors);
    }

    private static string Part(PdfDisplayList list)
    {
        var sw = new StringWriter();
        var w = new PdfDisplayListTextWriter(sw);
        list.Replay(w, new PdfBounds(100, 0, 200, 100));
        return sw.ToString();
    }

    [Fact]
    public void ParallelInterpretationOfOneDocumentIsDeterministic()
    {
        using PdfDocument doc = TestPdf.Open(BusyPdf());
        string expected = TestPdf.Record(doc).ToText();
        var texts = new string[32];
        Parallel.For(0, texts.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => texts[i] = TestPdf.Record(doc).ToText());
        Assert.All(texts, t => Assert.Equal(expected, t));
    }

    [Fact]
    public void LimitedStringsDoNotLeakDeviceMemoryIntoTheRecording()
    {
        // The glyph array handed to the device is reused between runs; the recording must have copied it.
        byte[] data = TestPdf.Build("BT /F 10 Tf (AB) Tj (C) Tj ET", "/Resources << /Font << /F << /Type /Font /Subtype /Type1 /BaseFont /Courier >> >> >>");
        using PdfDocument doc = TestPdf.Open(data);
        string t = TestPdf.Record(doc).ToText();
        string[] runs = TestPdf.Lines(t, "text");
        Assert.Contains("codes=4142 glyphs=[65@0,0,0.6", runs[0]);
        Assert.Contains(" 66@6,0,0.6", runs[0]);
        Assert.Contains("codes=43 glyphs=[67@0,0,0.6", runs[1]);
    }

    // ---- the format document ----

    private static string? RepoFile(string relative, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, relative);
            if (File.Exists(c))
            {
                return c;
            }
        }

        return null;
    }

    [Fact]
    public void FormatDocumentAndWriterAgree()
    {
        string? path = RepoFile("docs/display-list-format.md");
        Assert.SkipWhen(path is null, "docs/display-list-format.md is not present");
        string doc = File.ReadAllText(path!);
        Assert.Contains($"**Format version: {PdfDisplayListTextWriter.FormatVersion}**", doc);
        Assert.Contains($"`{PdfDisplayListTextWriter.Magic} {PdfDisplayListTextWriter.FormatVersion}`", doc);
        foreach (string record in PdfDisplayListTextWriter.Records)
        {
            Assert.True(Regex.IsMatch(doc, "`" + Regex.Escape(record) + @"[ `]"), $"record '{record}' is not documented");
        }

        // The other direction: every keyword in the table is one the writer knows.
        foreach (Match m in Regex.Matches(doc, @"^\|\s*`([a-z-]+)", RegexOptions.Multiline))
        {
            Assert.Contains(m.Groups[1].Value, PdfDisplayListTextWriter.Records);
        }

        // And the writer's output only uses documented keywords.
        using PdfDocument d = TestPdf.Open(BusyPdf());
        foreach (string line in TestPdf.Record(d).ToText().Split('\n').Skip(1).Where(l => l.Length > 0))
        {
            Assert.Contains(line.TrimStart().Split(' ')[0], PdfDisplayListTextWriter.Records);
        }
    }

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(-0.00001, "0")]
    [InlineData(1.5, "1.5")]
    [InlineData(-12.34567, "-12.3457")]
    [InlineData(0.00006, "0.0001")]
    [InlineData(100, "100")]
    [InlineData(1e15, "huge")]
    [InlineData(double.NaN, "nan")]
    [InlineData(double.NegativeInfinity, "-inf")]
    public void NumbersPrintRoundedToFourDecimals(double v, string expected) => Assert.Equal(expected, PdfDisplayListTextWriter.Num(v));
}
