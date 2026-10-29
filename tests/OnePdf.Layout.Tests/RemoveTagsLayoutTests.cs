namespace OnePdf.Layout.Tests;

/// <summary>A tagged layout document loses its tags without any change to what it shows, and can be tagged again.</summary>
public class RemoveTagsLayoutTests
{
    private static string Display(PdfDocument doc)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            var list = new PdfDisplayList();
            doc.Pages[i].Interpret(list, new PdfInterpretOptions { GenerateAppearances = false });
            foreach (string raw in list.ToText().Split('\n'))
            {
                string l = raw.TrimStart();
                if (!l.StartsWith("begin-mc ", StringComparison.Ordinal) && l != "end-mc")
                {
                    sb.Append(l).Append('\n');
                }
            }
        }

        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "[0-9]+ 0 R", "N 0 R");
    }

    private static string Text(PdfDocument doc) =>
        string.Join("\n", doc.ExtractText(new OnePdf.Text.PdfTextOptions { UseStructureTree = false }).Select(static t => t.Text));

    [Fact]
    public void LayoutOutputLosesItsTags()
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("Report").Language("en-US");
            doc.Page(page =>
            {
                page.Size(300, 400);
                page.Content().Column(col =>
                {
                    col.Item().Heading(1).Text("Quarterly report").FontSize(18);
                    col.Item().Text("Some body text that is long enough to fill a line or two of the page.");
                    col.Item().Table(t =>
                    {
                        t.Columns(c => { c.Relative(1); c.Relative(1); });
                        t.HeaderRow(r => { r.Cell().Text("Name"); r.Cell().Text("Value"); });
                        t.Row(r => { r.Cell().Text("alpha"); r.Cell().Text("1"); });
                    });
                    col.Item().Hyperlink("https://example.com/").Text("A link");
                    col.Item().AltText("A bar").Height(20).Background(Colors.Blue).Text("");
                });
            });
        });
        byte[] pdf = Support.Save(layout);
        using PdfDocument d = PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });
        Assert.NotNull(d.StructureTreeRoot);
        string display = Display(d);
        string text = Text(d);
        PdfRemoveTagsResult r = d.RemoveTags();
        Assert.True(r.StructureTreeRemoved);
        Assert.True(r.SequencesRemoved > 3);
        Assert.Equal(display, Display(d));
        Assert.Equal(text, Text(d));
        Assert.Null(d.StructureTreeRoot);

        // The result tags again.
        d.Tag(d.ExtractText(new OnePdf.Text.PdfTextOptions { RetainGlyphs = true, UseStructureTree = false }), static _ => PdfBlockRole.Paragraph, new PdfTagOptions { Title = "Report", Language = "en-US" });
        Assert.NotNull(d.StructureTreeRoot);
        Assert.Equal(display, Display(d));
    }
}
