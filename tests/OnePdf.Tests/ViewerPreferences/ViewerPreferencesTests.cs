using OnePdf.Tests.Writing;

namespace OnePdf.Tests.ViewerPreferences;

public class ViewerPreferencesTests
{
    private static readonly byte[] Id = new byte[16];

    private static byte[] Build(PdfDocumentWriterOptions? options = null, int pages = 3, string? title = null)
    {
        var ms = new MemoryStream();
        PdfDocumentWriterOptions o = options ?? new PdfDocumentWriterOptions();
        // Options are init-only, so a copy keeps the caller's preferences and adds the stream settings.
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions
        {
            DocumentId = Id,
            LeaveOpen = true,
            DisplayDocumentTitle = o.DisplayDocumentTitle,
            ViewerPreferences = o.ViewerPreferences,
        }))
        {
            if (title is not null)
            {
                w.SetInfo("Title", title);
            }

            for (int i = 0; i < pages; i++)
            {
                w.BeginPage(200, 200).Commit();
            }

            w.Finish();
        }

        return ms.ToArray();
    }

    private static PdfDocument Edit(byte[] bytes) => PdfDocument.Open(bytes, new PdfOpenOptions { AllowEditing = true });

    private static PdfViewerPreferences Full() => new()
    {
        PrintScaling = PdfPrintScaling.None,
        Duplex = PdfDuplex.DuplexFlipLongEdge,
        PrintPageRange = [new PdfPrintPageRange(1, 1), new PdfPrintPageRange(3, 3)],
        NumCopies = 3,
        PickTrayByPdfSize = true,
        PrintArea = PdfViewerBox.TrimBox,
        PrintClip = PdfViewerBox.BleedBox,
        ViewArea = PdfViewerBox.ArtBox,
        ViewClip = PdfViewerBox.CropBox,
        HideToolbar = true,
    };

    private static void AssertFull(PdfViewerPreferences? p)
    {
        Assert.NotNull(p);
        Assert.Equal(PdfPrintScaling.None, p.PrintScaling);
        Assert.Equal(PdfDuplex.DuplexFlipLongEdge, p.Duplex);
        Assert.Equal([new PdfPrintPageRange(1, 1), new PdfPrintPageRange(3, 3)], p.PrintPageRange);
        Assert.Equal(3, p.NumCopies);
        Assert.True(p.PickTrayByPdfSize);
        Assert.Equal(PdfViewerBox.TrimBox, p.PrintArea);
        Assert.Equal(PdfViewerBox.BleedBox, p.PrintClip);
        Assert.Equal(PdfViewerBox.ArtBox, p.ViewArea);
        Assert.Equal(PdfViewerBox.CropBox, p.ViewClip);
        Assert.True(p.HideToolbar);
        Assert.Null(p.HideMenubar);
    }

    [Fact]
    public void NewDocumentRoundTrips()
    {
        byte[] pdf = Build(new PdfDocumentWriterOptions { ViewerPreferences = Full() });
        using PdfDocument doc = PdfDocument.Open(pdf);
        AssertFull(doc.ViewerPreferences);
        Assert.Equal(0, WriterSupport.QpdfRun(pdf).Code);
    }

    [Fact]
    public void WriterKeepsDisplayDocTitleAndNoPreferencesMeansNone()
    {
        using (PdfDocument doc = PdfDocument.Open(Build(new PdfDocumentWriterOptions { DisplayDocumentTitle = true, ViewerPreferences = new PdfViewerPreferences { NumCopies = 2 } })))
        {
            Assert.True(doc.ViewerPreferences!.DisplayDocTitle);
            Assert.Equal(2, doc.ViewerPreferences.NumCopies);
        }

        using (PdfDocument doc = PdfDocument.Open(Build(new PdfDocumentWriterOptions { DisplayDocumentTitle = true, ViewerPreferences = new PdfViewerPreferences { DisplayDocTitle = false } })))
        {
            Assert.False(doc.ViewerPreferences!.DisplayDocTitle);
        }

        using (PdfDocument doc = PdfDocument.Open(Build()))
        {
            Assert.Null(doc.ViewerPreferences);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditedExistingDocumentRoundTrips(bool incremental)
    {
        byte[] original = Build();
        using PdfDocument doc = Edit(original);
        doc.SetViewerPreferences(Full());
        byte[] saved = Save(doc, incremental);
        using PdfDocument back = PdfDocument.Open(saved);
        AssertFull(back.ViewerPreferences);
        Assert.Equal(0, WriterSupport.QpdfRun(saved).Code);
        if (incremental)
        {
            Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "an incremental save appends");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HappyPathAndUnknownEntriesSurvive(bool incremental)
    {
        byte[] start;
        using (PdfDocument doc = Edit(Build(new PdfDocumentWriterOptions { DisplayDocumentTitle = true })))
        {
            doc.Catalog.GetDictionary("ViewerPreferences")!.SetEntry("Enforce", new PdfArray());
            doc.Catalog.GetDictionary("ViewerPreferences")!.SetEntry("Direction", new PdfName("R2L"));
            start = Save(doc, false);
        }

        using PdfDocument edit = Edit(start);
        edit.SetPrintPreferences(PdfPrintScaling.None, PdfDuplex.Simplex, 2, [new PdfPrintPageRange(2, 3)]);
        byte[] saved = Save(edit, incremental);

        using PdfDocument back = PdfDocument.Open(saved);
        PdfViewerPreferences p = back.ViewerPreferences!;
        Assert.Equal(PdfPrintScaling.None, p.PrintScaling);
        Assert.Equal(PdfDuplex.Simplex, p.Duplex);
        Assert.Equal(2, p.NumCopies);
        Assert.Equal([new PdfPrintPageRange(2, 3)], p.PrintPageRange);
        Assert.True(p.DisplayDocTitle);
        Assert.NotNull(p.Dictionary!.GetArray("Enforce"));
        Assert.Equal("R2L", p.Dictionary.GetName("Direction"));
        Assert.Equal(0, WriterSupport.QpdfRun(saved).Code);
    }

    [Fact]
    public void SecondCallChangesOnlyWhatIsSet()
    {
        using PdfDocument doc = Edit(Build());
        doc.SetPrintPreferences(PdfPrintScaling.None, copies: 4);
        doc.SetPrintPreferences(duplex: PdfDuplex.DuplexFlipShortEdge);
        PdfViewerPreferences p = doc.ViewerPreferences!;
        Assert.Equal(PdfPrintScaling.None, p.PrintScaling);
        Assert.Equal(4, p.NumCopies);
        Assert.Equal(PdfDuplex.DuplexFlipShortEdge, p.Duplex);
    }

    [Fact]
    public void InvalidValuesThrowOnWriteAndChangeNothing()
    {
        using PdfDocument doc = Edit(Build());
        Assert.Throws<ArgumentException>(() => doc.SetPrintPreferences(copies: 0));
        Assert.Throws<ArgumentException>(() => doc.SetPrintPreferences(copies: -2));
        Assert.Throws<ArgumentException>(() => doc.SetPrintPreferences(pageRanges: [new PdfPrintPageRange(0, 1)]));
        Assert.Throws<ArgumentException>(() => doc.SetPrintPreferences(pageRanges: [new PdfPrintPageRange(3, 2)]));
        Assert.Throws<ArgumentException>(() => doc.SetPrintPreferences(pageRanges: [new PdfPrintPageRange(1, 3), new PdfPrintPageRange(3, 3)]));
        Assert.Throws<ArgumentException>(() => doc.SetPrintPreferences(pageRanges: [new PdfPrintPageRange(2, 2), new PdfPrintPageRange(1, 1)]));
        Assert.Throws<ArgumentException>(() => doc.SetPrintPreferences(pageRanges: [new PdfPrintPageRange(1, 4)]));
        Assert.Throws<ArgumentException>(() => doc.SetPrintPreferences(pageRanges: []));
        Assert.Throws<ArgumentException>(() => doc.SetPrintPreferences((PdfPrintScaling)7));
        Assert.Throws<ArgumentException>(() => doc.SetViewerPreferences(new PdfViewerPreferences { NumCopies = 2, ViewArea = (PdfViewerBox)9 }));
        Assert.Null(doc.ViewerPreferences);
    }

    [Fact]
    public void WriterRejectsInvalidValues()
    {
        Assert.Throws<ArgumentException>(() => Build(new PdfDocumentWriterOptions { ViewerPreferences = new PdfViewerPreferences { NumCopies = 0 } }));
        Assert.Throws<ArgumentException>(() => Build(new PdfDocumentWriterOptions { ViewerPreferences = new PdfViewerPreferences { PrintPageRange = [new PdfPrintPageRange(1, 9)] } }));
    }

    [Fact]
    public void NotEditableThrows()
    {
        using PdfDocument doc = PdfDocument.Open(Build());
        Assert.Throws<InvalidOperationException>(() => doc.SetPrintPreferences(copies: 2));
    }

    [Fact]
    public void MalformedEntriesAreIgnoredWithDiagnostics()
    {
        using PdfDocument doc = Edit(Build());
        var vp = new PdfDictionary();
        vp.SetEntry("PrintScaling", new PdfName("Shrink"));
        vp.SetEntry("Duplex", new PdfInteger(1));
        vp.SetEntry("NumCopies", new PdfInteger(0));
        vp.SetEntry("PrintPageRange", new PdfArray());
        vp.SetEntry("PrintArea", new PdfName("BogusBox"));
        vp.SetEntry("PickTrayByPDFSize", new PdfInteger(1));
        vp.SetEntry("ViewClip", new PdfName("CropBox"));
        vp.SetEntry("FitWindow", new PdfBoolean(true));
        doc.Catalog.SetEntry("ViewerPreferences", vp);

        PdfViewerPreferences p = doc.ViewerPreferences!;
        Assert.Null(p.PrintScaling);
        Assert.Null(p.Duplex);
        Assert.Null(p.NumCopies);
        Assert.Null(p.PrintPageRange);
        Assert.Null(p.PrintArea);
        Assert.Null(p.PickTrayByPdfSize);
        Assert.Equal(PdfViewerBox.CropBox, p.ViewClip);
        Assert.True(p.FitWindow);
        Assert.Equal(6, doc.Diagnostics.Count(d => d.Message.Contains("/ViewerPreferences")));

        foreach (PdfObject bad in new PdfObject[]
        {
            Arr(new PdfInteger(1)), // odd count
            Arr(new PdfInteger(3), new PdfInteger(2)), // reversed
            Arr(new PdfInteger(1), new PdfInteger(4), new PdfInteger(3), new PdfInteger(5)), // overlap
            Arr(new PdfInteger(1), new PdfName("x")),
            new PdfInteger(5),
        })
        {
            vp.SetEntry("PrintPageRange", bad);
            Assert.Null(doc.ViewerPreferences!.PrintPageRange);
        }

        doc.Catalog.SetEntry("ViewerPreferences", new PdfInteger(3));
        Assert.Null(doc.ViewerPreferences);
    }

    [Fact]
    public void PdfUAStillSetsDisplayDocTitleAndKeepsPrintEntries()
    {
        byte[] src;
        using (PdfDocument doc = Edit(Build(new PdfDocumentWriterOptions { ViewerPreferences = new PdfViewerPreferences { NumCopies = 5 } }, title: "Titled")))
        {
            doc.PreparePdfUA(new PdfUAOptions { Language = "en-US" });
            src = Save(doc, false);
        }

        using PdfDocument back = PdfDocument.Open(src);
        Assert.True(back.ViewerPreferences!.DisplayDocTitle);
        Assert.Equal(5, back.ViewerPreferences.NumCopies);
    }

    private static PdfArray Arr(params PdfObject[] items)
    {
        var a = new PdfArray();
        foreach (PdfObject o in items)
        {
            a.Append(o);
        }

        return a;
    }

    private static byte[] Save(PdfDocument doc, bool incremental)
    {
        using var ms = new MemoryStream();
        if (incremental)
        {
            doc.SaveIncremental(ms);
        }
        else
        {
            doc.Save(ms);
        }

        return ms.ToArray();
    }
}
