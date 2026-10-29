namespace OnePdf.PdfUA;

// ISO 14289-1 7.21.8: no text-showing operator may select the .notdef glyph, whatever the rendering mode. The interpreter reads each
// page the way a viewer does (see NotdefPass), so the codes found are the ones a validator names.
internal sealed partial class PdfUAChecker
{
    private const int MaxNotdefCodesListed = 12;

    private void CheckNotdefText()
    {
        // One finding per font and page, listing every code and whether any of the text is drawn.
        var groups = new Dictionary<(int Font, string Name, int Page), (SortedSet<int> Codes, bool Drawn, PdfNotdefRun First)>();
        foreach (PdfNotdefRun r in NotdefPass.Find(_doc, _ct))
        {
            var key = (r.FontObject, r.FontResource + "/" + r.BaseFont, r.PageIndex);
            if (!groups.TryGetValue(key, out var g))
            {
                g = ([], false, r);
            }

            g.Codes.UnionWith(r.Codes);
            groups[key] = (g.Codes, g.Drawn || r.IsVisible, g.First);
        }

        foreach (((int font, _, int page), (SortedSet<int> codes, bool drawn, PdfNotdefRun first)) in groups)
        {
            if (Limited("notdef"))
            {
                break;
            }

            string name = first.BaseFont.Length > 0 ? first.BaseFont : first.FontResource;
            var shown = new List<int>(MaxNotdefCodesListed);
            foreach (int c in codes)
            {
                if (shown.Count == MaxNotdefCodesListed)
                {
                    break;
                }

                shown.Add(c);
            }

            string listed = string.Join(", ", shown) + (codes.Count > shown.Count ? ", ..." : string.Empty);
            Add("31", "7.21.8", $"Text shown with the font {name} uses character codes ({listed}) that select the .notdef glyph{(drawn ? string.Empty : "; none of that text is drawn")}. PdfDocument.RemoveNotdefText takes them out.", font, page);
        }
    }
}
