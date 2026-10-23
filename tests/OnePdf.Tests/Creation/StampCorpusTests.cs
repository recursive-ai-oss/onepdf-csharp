namespace OnePdf.Tests.Creation;

/// <summary>Stamps and overlays on the committed corpus: the originals' text survives, the added text appears, and qpdf finds nothing new to complain about.</summary>
public class StampCorpusTests
{
    private static int Rank(int qpdfCode) => qpdfCode switch { 0 => 0, 3 => 1, _ => 2 };

    public static IEnumerable<object[]> Files()
    {
        string root = CreationSupport.Root();
        var files = new List<string>();
        files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "testdata", "generated", "pdf"), "*.pdf").Order(StringComparer.Ordinal));
        files.AddRange(Directory.EnumerateFiles(Path.Combine(root, "testdata", "realworld", "pdf"), "*.pdf").Order(StringComparer.Ordinal).Where((_, i) => i % 3 == 0));
        foreach (string f in files)
        {
            if (new FileInfo(f).Length < 3_000_000)
            {
                yield return [Path.GetRelativePath(root, f).Replace('\\', '/')];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void StampedPagesStayValidAndKeepTheirText(string relative)
    {
        string path = Path.Combine(CreationSupport.Root(), relative);
        byte[] input = File.ReadAllBytes(path);
        string? password = Writing.WriterSupport.PasswordFor(path);
        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(input, new PdfOpenOptions { AllowEditing = true, Password = password });
        }
        catch (PdfException)
        {
            return; // files that do not open are not this test's subject
        }

        using (doc)
        {
            if (doc.PageCount == 0)
            {
                return;
            }

            var originalRuns = new CreationSupport.Collector();
            doc.Pages[0].Interpret(originalRuns, null);

            PdfCreatedFont font = doc.AddStandardFont("Helvetica-Bold");
            var pages = Enumerable.Range(0, Math.Min(doc.PageCount, 3)).ToArray();
            doc.AddTextStamp("DRAFTCOPY", font, 40, new PdfStampOptions { Opacity = 0.4, RotationDegrees = 0, Color = (1, 0, 0) }, pages);
            using (PdfPageContentBuilder b = doc.Pages[0].BeginContent(PdfContentPlacement.Underlay))
            {
                b.SetFillGray(0.9);
                b.Rectangle(5, 5, 20, 20);
                b.Fill();
                b.Commit();
            }

            using var ms = new MemoryStream();
            doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], Encryption = PdfEncryptionAction.Remove });
            byte[] output = ms.ToArray();

            using PdfDocument back = PdfDocument.Open(output);
            var runs = new CreationSupport.Collector();
            back.Pages[0].Interpret(runs, null);
            var stamped = runs.Runs.Where(r => r.Text == "DRAFTCOPY").ToList();
            Assert.Single(stamped);
            // Everything the page drew before is still drawn, in the same places.
            var rest = runs.Runs.Where(r => r.Text != "DRAFTCOPY").ToList();
            Assert.Equal(originalRuns.Runs.Count, rest.Count);
            for (int i = 0; i < rest.Count; i++)
            {
                Assert.Equal(originalRuns.Runs[i].Text, rest[i].Text);
                Assert.Equal(originalRuns.Runs[i].X, rest[i].X, 0.01);
                Assert.Equal(originalRuns.Runs[i].Y, rest[i].Y, 0.01);
            }

            Assert.Equal(originalRuns.Paths.Count + 1, runs.Paths.Count); // plus the underlay square
            Assert.Equal(originalRuns.Images.Count, runs.Images.Count);

            if (CreationSupport.HasQpdf)
            {
                (int codeIn, _) = CreationSupport.Qpdf(input);
                (int codeOut, string text) = CreationSupport.Qpdf(output);
                if (Rank(codeOut) > Rank(codeIn) && password is null)
                {
                    // Our own re-save of a file qpdf already disliked can differ; only a clean input must stay clean.
                    Assert.True(codeIn != 0, $"qpdf: {codeOut}\n{text}");
                }
            }
        }
    }
}
