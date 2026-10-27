using System.Text;
using OnePdf.Text;
using static OnePdf.Tests.Tagging.TagDocs;

namespace OnePdf.Tests.Tagging;

/// <summary>How the content streams are rewritten: Form XObjects, inline images, shared streams, split text operators, nesting.</summary>
public class TaggingContentTests
{
    private const string TwoOwnerForm = "BT /F1 12 Tf 72 700 Td (Heading in form) Tj ET\nBT /F1 12 Tf 72 500 Td (Body in form) Tj ET\n";

    private static void TagTwoOwners(PdfDocument doc, IReadOnlyList<PdfPageText> pages, PdfTaggingPlan plan)
    {
        foreach (PdfPageText p in pages)
        {
            plan.Add(PdfTagRole.H1, PdfTagContent.Block(p, Blk(p, "Heading")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(p, Blk(p, "Body")));
        }
    }

    [Fact]
    public void FormWithOneOwnerIsWrappedAtItsUseAndNotCopied()
    {
        byte[] src = Make([new PageSpec("/Fm1 Do\n")], b => Form(b, 100, "BT /F1 12 Tf 72 700 Td (Only owner here) Tj ET\n"), "/Fm1 100 0 R");
        byte[] tagged = Tag(src, (_, pages, plan) => plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 0)), out PdfTaggingResult r);
        Assert.Equal(0, r.FormsCopied);
        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Contains("BDC", PageContent(doc, 0));
        Assert.Equal("P", Roles(doc));
        AssertConforms(tagged);
    }

    [Fact]
    public void FormWithTwoOwnersIsCopiedAndTheOriginalIsLeftAlone()
    {
        byte[] src = Make([new PageSpec("/Fm1 Do\n")], b => Form(b, 100, TwoOwnerForm), "/Fm1 100 0 R");
        byte[] tagged = Tag(src, TagTwoOwners, out PdfTaggingResult r);
        Assert.Equal(1, r.FormsCopied);
        using PdfDocument doc = PdfDocument.Open(tagged);
        Assert.Equal("H1 P", Roles(doc));
        // The page draws the copy; it has its own StructParents and the tree reaches its marked content through MCR with /Stm.
        string c = PageContent(doc, 0);
        Assert.Contains(" Do", c);
        AssertConforms(tagged);

        using PdfDocument original = PdfDocument.Open(src);
        Assert.Equal(TwoOwnerForm, Encoding.Latin1.GetString(((PdfStream)original.Pages[0].Resources.GetDictionary("XObject")!.Get("Fm1")!).GetDecodedBytes().Span));
    }

    [Fact]
    public void SameFormOnTwoPagesIsCopiedPerUse()
    {
        byte[] src = Make([new PageSpec("/Fm1 Do\n"), new PageSpec("/Fm1 Do\n" + T(72, 300, "Page two text"))], b => Form(b, 100, TwoOwnerForm), "/Fm1 100 0 R");
        byte[] tagged = Tag(src, (doc, pages, plan) =>
        {
            TagTwoOwners(doc, pages, plan);
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[1], Blk(pages[1], "Page two")));
        }, out PdfTaggingResult r);
        Assert.Equal(2, r.FormsCopied);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("H1 P H1 P P", Roles(d));
        AssertConforms(tagged);
    }

    [Fact]
    public void SameFormTwiceOnOnePageGetsTwoCopiesWithDifferentOwners()
    {
        byte[] src = Make([new PageSpec("/Fm1 Do\nq 1 0 0 1 200 -300 cm /Fm1 Do Q\n")], b => Form(b, 100, TwoOwnerForm), "/Fm1 100 0 R");
        byte[] tagged = Tag(src, (_, pages, plan) =>
        {
            PdfPageText p = pages[0];
            Assert.Equal(4, p.Blocks.Count);
            foreach (PdfTextBlock b in p.Blocks)
            {
                bool heading = b.Lines[0].Text.StartsWith("Heading", StringComparison.Ordinal);
                plan.Add(heading ? PdfTagRole.H1 : PdfTagRole.P, PdfTagContent.Block(p, b));
            }
        }, out PdfTaggingResult r);
        Assert.Equal(2, r.FormsCopied);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal(4, Roles(d).Split(' ').Length);
        AssertConforms(tagged);
    }

    [Fact]
    public void FormsThatMixOwnersAndArtifactsAreSplit()
    {
        // The form holds a rule (artifact), a heading and a paragraph; the paragraph is left out of the plan, so it is an artifact too.
        string form = "0.5 G 72 690 m 540 690 l S\n" + TwoOwnerForm;
        byte[] src = Make([new PageSpec("/Fm1 Do\n")], b => Form(b, 100, form), "/Fm1 100 0 R");
        byte[] tagged = Tag(src, (_, pages, plan) => plan.Add(PdfTagRole.H1, PdfTagContent.Block(pages[0], Blk(pages[0], "Heading"))), out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("H1", Roles(d));
        AssertConforms(tagged);
    }

    [Fact]
    public void NestedFormsWithMixedOwnersAreCopiedAtEveryLevel()
    {
        byte[] src = Make([new PageSpec("/Fm1 Do\n")], b =>
        {
            Form(b, 100, "BT /F1 12 Tf 72 740 Td (Outer heading) Tj ET\n/Fm2 Do\n", xobjects: "/Fm2 101 0 R");
            Form(b, 101, "BT /F1 12 Tf 72 600 Td (Inner heading) Tj ET\nBT /F1 12 Tf 72 500 Td (Inner body) Tj ET\n");
        }, "/Fm1 100 0 R");
        byte[] tagged = Tag(src, (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.H1, PdfTagContent.Block(pages[0], Blk(pages[0], "Outer")));
            plan.Add(PdfTagRole.H2, PdfTagContent.Block(pages[0], Blk(pages[0], "Inner heading")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "Inner body")));
        }, out PdfTaggingResult r);
        Assert.Equal(2, r.FormsCopied);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("H1 H2 P", Roles(d));
        AssertConforms(tagged);
    }

    [Fact]
    public void FormThatDrawsItselfIsRefusedAsUnreadable()
    {
        byte[] src = Make([new PageSpec("/Fm1 Do\n")], b => Form(b, 100, TwoOwnerForm + "/Fm1 Do\n", xobjects: "/Fm1 100 0 R"), "/Fm1 100 0 R");
        PdfTaggingException ex = Assert.Throws<PdfTaggingException>(() => Tag(src, TagTwoOwners, out _));
        Assert.Equal(PdfTaggingFailure.ContentUnreadable, ex.Reason);
    }

    [Fact]
    public void InlineImageIsKeptWholeInsideItsFigure()
    {
        // The image data contains the bytes " EI " in the middle; the length from the dictionary, not a scan for EI, ends it.
        byte[] data = [0x20, (byte)'E', (byte)'I', 0x20];
        var sb = new StringBuilder();
        sb.Append(T(72, 700, "Above the picture."));
        sb.Append("q 80 0 0 80 100 500 cm\nBI /W 2 /H 2 /CS /G /BPC 8 ID ").Append(Encoding.Latin1.GetString(data)).Append("\nEI Q\n");
        sb.Append(T(72, 400, "Below the picture."));
        byte[] src = Make(sb.ToString());
        byte[] tagged = Tag(src, (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "Above")));
            plan.Add(PdfTagRole.Figure, PdfTagContent.Graphics(pages[0], new PdfRectangle(99, 499, 181, 581))).Alt = "A small grey picture";
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "Below")));
        }, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        string c = PageContent(d, 0);
        Assert.Equal(1, Count(c, "BI "));
        Assert.Contains("ID " + Encoding.Latin1.GetString(data), c);
        Assert.Equal("P Figure P", Roles(d));
        AssertConforms(tagged);
    }

    [Fact]
    public void UnreferencedInlineImageIsAnArtifact()
    {
        string content = T(72, 700, "Text.") + "q 80 0 0 80 100 500 cm BI /W 2 /H 2 /CS /G /BPC 8 ID abcd\nEI Q\n";
        byte[] tagged = Tag(Make(content), (_, pages, plan) => plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 0)), out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Contains("/Artifact", PageContent(d, 0));
        AssertConforms(tagged);
    }

    [Fact]
    public void ContentStreamSharedByTwoPagesIsRewrittenPerPage()
    {
        string shared = T(72, 700, "Shared heading") + T(72, 500, "Shared body");
        byte[] src = Make([new PageSpec(null, 60), new PageSpec(null, 60)], b => b.Stream(60, string.Empty, Encoding.Latin1.GetBytes(shared)));
        byte[] tagged = Tag(src, (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.H1, PdfTagContent.Block(pages[0], Blk(pages[0], "Shared heading")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "Shared body")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[1], Blk(pages[1], "Shared heading")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[1], Blk(pages[1], "Shared body")));
        }, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("H1 P P P", Roles(d));
        Assert.NotEqual(d.Pages[0].Dictionary.GetRaw("Contents")?.ToString(), d.Pages[1].Dictionary.GetRaw("Contents")?.ToString());
        AssertConforms(tagged);
    }

    [Fact]
    public void ContentSplitOverSeveralStreamsOfOnePageIsHandled()
    {
        // Contents as an array: a text object that starts in one stream and ends in the next.
        string a = "BT /F1 12 Tf 72 700 Td (First half) Tj\n";
        string c = "0 -200 Td (Second half) Tj ET\n";
        byte[] file = MakeArrayContents(a, c);
        byte[] tagged = Tag(file, (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "First")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "Second")));
        }, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("P P", Roles(d));
        AssertConforms(tagged);
    }

    private static byte[] MakeArrayContents(string a, string b2)
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [10 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.Obj(10, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents [50 0 R 51 0 R] /Resources << /Font << /F1 3 0 R >> >> >>");
        b.Stream(50, string.Empty, Encoding.Latin1.GetBytes(a));
        b.Stream(51, string.Empty, Encoding.Latin1.GetBytes(b2));
        long x = b.XrefTable(60);
        b.Trailer("/Size 60 /Root 1 0 R", x);
        return b.ToArray();
    }

    [Fact]
    public void OneShowOperatorSplitBetweenTwoElements()
    {
        string content = "BT /F1 12 Tf 72 700 Td [(Alpha beta ) -300 (gamma delta)] TJ ET\n";
        byte[] tagged = Tag(Make(content), (_, pages, plan) =>
        {
            PdfTextLine line = pages[0].Blocks[0].Lines[0];
            Assert.Equal(4, line.Words.Count);
            plan.Add(PdfTagRole.P, PdfTagContent.Words(pages[0], line, 0, 2));
            plan.Add(PdfTagRole.P, PdfTagContent.Words(pages[0], line, 2, 2));
        }, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("P P", Roles(d));
        Assert.True(Count(PageContent(d, 0), " BDC") >= 2);
        AssertConforms(tagged);
    }

    [Fact]
    public void WordInTheMiddleOfAStringSplitsItIntoThreeParts()
    {
        string content = "BT /F1 12 Tf 72 700 Td (one two three) Tj ET\n";
        byte[] tagged = Tag(Make(content), (_, pages, plan) =>
        {
            PdfTextLine line = pages[0].Blocks[0].Lines[0];
            plan.Add(PdfTagRole.P, PdfTagContent.Words(pages[0], line, 0, 1));
            plan.Add(PdfTagRole.Span, PdfTagContent.Words(pages[0], line, 1, 1));
            plan.Add(PdfTagRole.P, PdfTagContent.Words(pages[0], line, 2, 1));
        }, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("P Span P", Roles(d));
    }

    [Fact]
    public void MarkedContentNestsInsideGraphicsStateAndTextObjects()
    {
        string content =
            "q 1 0 0 1 10 0 cm\nBT /F1 12 Tf 62 700 Td (In saved state) Tj ET\nQ\n" +
            "BT /F1 12 Tf 14 TL 72 600 Td (Line one of text) Tj T* (Line two of text) Tj ET\n" +
            "q 0.9 g 60 400 200 30 re f Q BT /F1 12 Tf 72 410 Td (Over a box) Tj ET\n" +
            "BT /F1 12 Tf 72 300 Td (First object) Tj ET BT /F1 12 Tf 72 280 Td (Second object) Tj ET\n";
        byte[] tagged = Tag(Make(content), (_, pages, plan) =>
        {
            foreach (PdfTextBlock b in pages[0].Blocks)
            {
                plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], b));
            }
        }, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        AssertConforms(tagged);
        // Every BDC has its EMC and nothing straddles q/Q: the interpreter would not otherwise report balanced groups.
        string c = PageContent(d, 0);
        Assert.Equal(Count(c, " BDC") + Count(c, " BMC"), Count(c, "EMC"));
    }

    [Fact]
    public void ExistingArtifactSequencesAreDissolvedAndRebuilt()
    {
        string content = "/Artifact BMC\n" + T(72, 760, "Header words") + "0.5 g 0 0 10 10 re f\nEMC\n" + T(72, 600, "Body words");
        byte[] tagged = Tag(Make(content), (_, pages, plan) =>
        {
            // The text the file called an artifact is real content as far as the plan is concerned.
            plan.Add(PdfTagRole.H1, PdfTagContent.Block(pages[0], Blk(pages[0], "Header")));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], Blk(pages[0], "Body")));
        }, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Equal("H1 P", Roles(d));
        AssertConforms(tagged);
    }

    [Fact]
    public void ExistingOrdinaryMarkedContentIsKept()
    {
        string content = "/Span <</Lang (en)>> BDC\n" + T(72, 700, "Spanned words") + "EMC\n" + T(72, 600, "More words");
        byte[] tagged = Tag(Make(content), (_, pages, plan) =>
        {
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 0));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 1));
        }, out _);
        using PdfDocument d = PdfDocument.Open(tagged);
        Assert.Contains("/Span", PageContent(d, 0));
        AssertConforms(tagged);
    }

    [Fact]
    public void TaggedPageIsStillDrawnTheSameWithHiddenTextAndClipping()
    {
        string content = "BT /F1 12 Tf 3 Tr 72 700 Td (Invisible layer) Tj ET\nBT /F1 12 Tf 0 Tr 72 600 Td (Visible words) Tj ET\n";
        byte[] tagged = Tag(Make(content), (_, pages, plan) =>
        {
            foreach (PdfTextBlock b in pages[0].Blocks)
            {
                plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], b));
            }
        }, out _);
        AssertConforms(tagged);
    }
}
