using OnePdf.Tests.Creation;

namespace OnePdf.Tests.PdfUA;

/// <summary>Small tagged documents holding one table written from a compact description, for the header-cell checks.</summary>
internal static class HeaderScopeDocs
{
    /// <summary>
    /// Builds a one-page tagged document with a title, a language and one table. Each cell is written <c>TH</c> or <c>TD</c>, optionally
    /// followed by <c>:Row</c>, <c>:Column</c> or <c>:Both</c> (a scope), <c>*CxR</c> (column and row span), <c>#id</c> (an identifier) and <c>&gt;id1,id2</c> (a
    /// <c>/Headers</c> list). A cell written <c>TH!</c> holds no text (as one inserted by a remediation tool does).
    /// </summary>
    public static byte[] Table(params string[][] rows)
    {
        byte[] font = CreationSupport.RequireFont("LiberationSans-Regular.ttf");
        using var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], Language = "en-US", DisplayDocumentTitle = true, LeaveOpen = true }))
        {
            w.SetInfo("Title", "Table document");
            PdfCreatedFont f = w.AddFont(font, new PdfEmbedFontOptions());
            PdfStructureElement doc = w.BeginStructureElement("Document");
            PdfStreamPageBuilder p = w.BeginPage(300, 400);
            p.SetFont(f, 11);
            double y = 360;
            PdfStructureElement table = w.BeginStructureElement(doc, "Table");
            foreach (string[] row in rows)
            {
                PdfStructureElement tr = w.BeginStructureElement(table, "TR");
                double x = 20;
                foreach (string spec in row)
                {
                    string s = spec;
                    bool empty = s.StartsWith("TH!", StringComparison.Ordinal);
                    if (empty)
                    {
                        s = "TH" + s[3..];
                    }

                    string? headers = null;
                    int gt = s.IndexOf('>', StringComparison.Ordinal);
                    if (gt >= 0)
                    {
                        headers = s[(gt + 1)..];
                        s = s[..gt];
                    }

                    int colSpan = 1;
                    int rowSpan = 1;
                    int star = s.IndexOf('*', StringComparison.Ordinal);
                    if (star >= 0)
                    {
                        string[] sp = s[(star + 1)..].Split('x');
                        colSpan = int.Parse(sp[0], System.Globalization.CultureInfo.InvariantCulture);
                        rowSpan = int.Parse(sp[1], System.Globalization.CultureInfo.InvariantCulture);
                        s = s[..star];
                    }

                    string? id = null;
                    int hash = s.IndexOf('#', StringComparison.Ordinal);
                    if (hash >= 0)
                    {
                        id = s[(hash + 1)..];
                        s = s[..hash];
                    }

                    string? scope = null;
                    int colon = s.IndexOf(':', StringComparison.Ordinal);
                    if (colon >= 0)
                    {
                        scope = s[(colon + 1)..];
                        s = s[..colon];
                    }

                    PdfDictionary? attr = null;
                    if (scope is not null || headers is not null || colSpan > 1 || rowSpan > 1)
                    {
                        attr = scope is not null ? PdfStructureAttributes.TableHeader(scope, colSpan, rowSpan) : PdfStructureAttributes.Table(colSpan, rowSpan);
                        if (headers is not null)
                        {
                            var arr = new PdfArray();
                            foreach (string h in headers.Split(','))
                            {
                                arr.Add(PdfString.FromText(h));
                            }

                            attr.SetEntry("Headers", arr);
                        }
                    }

                    PdfStructureElement cell = w.BeginStructureElement(tr, s, new PdfStructureElementOptions { Id = id, Attributes = attr });
                    if (!empty)
                    {
                        p.BeginTaggedContent(cell);
                        p.DrawText("cell", x, y);
                        p.EndMarkedContent();
                    }

                    w.EndStructureElement(cell);
                    x += 60;
                }

                w.EndStructureElement(tr);
                y -= 16;
            }

            w.EndStructureElement(table);
            p.Commit();
            w.EndStructureElement(doc);
            w.Finish();
        }

        return ms.ToArray();
    }
}
