namespace OnePdf.PdfUA;

/// <summary>
/// The parent and child rules of the structure type table in ISO 14289-2 (the table of allowed parent/child structure types
/// that PDF/UA-2 takes from ISO/TS 32005, Table 5), as data: for each parent the standard child types it must not contain, the
/// children it may hold at most once, and the parents that must not hold content items directly. Heading types H1, H2, ... are
/// written <c>Hn</c> and the structure tree root <c>StructTreeRoot</c>.
/// </summary>
internal static class PdfUA2Table
{
    private static readonly string[] s_forbidden =
    [
        "Annot:Document Index LBody LI RB RP RT TBody TD TFoot TH THead TOC TOCI TR WP WT",
        "Art:Art BibEntry Document Em LBody LI Quote RB RP RT Ruby Span Strong Sub TBody TD TFoot TH THead TOCI TR WP WT Warichu",
        "Aside:Aside BibEntry Em LBody LI Quote RB RP RT Ruby Span Strong Sub TBody TD TFoot TH THead TOCI TR Title WP WT Warichu",
        "BibEntry:Art Aside BlockQuote Caption Code Document DocumentFragment H Hn Index L LBody LI RB RP RT Sect TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT",
        "BlockQuote:Aside BibEntry Em LBody LI Quote RB RP RT Ruby Span Strong Sub TBody TD TFoot TH THead TOCI TR Title WP WT Warichu",
        "Caption:Caption Document LBody LI RB RP RT TBody TD TFoot TH THead TOC TOCI TR Title WP WT",
        "Code:Document H Hn LBody LI RB RP RT TBody TD TFoot TH THead TOC TOCI TR Title WP WT",
        "Document:BibEntry Caption Em LBody LI Lbl Quote RB RP RT Reference Ruby Span Strong Sub TBody TD TFoot TH THead TOCI TR WP WT Warichu",
        "DocumentFragment:BibEntry Caption Em LBody LI Lbl Quote RB RP RT Reference Ruby Span Strong Sub TBody TD TFoot TH THead TOCI TR WP WT Warichu",
        "Em:Art Aside BlockQuote Caption Div Document DocumentFragment H Hn Index L LBody LI P Part RB RP RT Sect TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT",
        "FENote:Document H Hn LBody LI RB RP RT TBody TD TFoot TH THead TOC TOCI TR Title WP WT",
        "Figure:Document DocumentFragment LBody LI RB RP RT TBody TD TFoot TH THead TOC TOCI TR Title WP WT",
        "Form:Annot Art Aside BlockQuote Document DocumentFragment Em Form H Hn Index LBody LI Link P Quote RB RP RT Ruby Sect Span Strong Sub TBody TD TFoot TH THead TOC TOCI TR Title WP WT Warichu",
        "Formula:Art Document DocumentFragment LBody LI RB RP RT Sect TBody TD TFoot TH THead TOC TOCI TR Title WP WT",
        "H:Art Aside BlockQuote Caption Div Document DocumentFragment H Hn Index L LBody LI P Part RB RP RT TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT",
        "Hn:Art Aside BlockQuote Caption Div Document DocumentFragment H Hn Index L LBody LI P Part RB RP RT TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT",
        "Index:Art Aside BibEntry BlockQuote Code Document DocumentFragment Em Form Index LBody LI Lbl Link Quote RB RP RT Ruby Span Strong Sub TBody TD TFoot TH THead TOC TOCI TR Title WP WT Warichu",
        "L:Annot Art Aside BibEntry BlockQuote Code Div Document DocumentFragment Em FENote Figure Form Formula H Hn Index LBody Lbl Link Note P Part Quote RB RP RT Reference Ruby Sect Span Strong Sub TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT Warichu",
        "LBody:Document DocumentFragment LBody LI Lbl RB RP RT TBody TD TFoot TH THead TOC TOCI TR Title WP WT",
        "LI:Annot Art Aside BibEntry BlockQuote Caption Code Document DocumentFragment Em FENote Figure Form Formula H Hn Index L LI Link Note P Part Quote RB RP RT Reference Ruby Sect Span Strong Sub TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT Warichu",
        "Lbl:Art Aside BlockQuote Caption Div Document DocumentFragment H Hn Index L LBody LI Lbl P Part RB RP RT Sect TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT",
        "Link:Document Index LBody LI RB RP RT TBody TD TFoot TH THead TOC TOCI TR WP WT",
        "Note:Document H Hn LBody LI RB RP RT TBody TD TFoot TH THead TOC TOCI TR Title WP WT",
        "P:Art Aside BlockQuote Caption Div Document DocumentFragment H Hn Index LBody LI P Part RB RP RT Sect TBody TD TFoot TH THead TOC TOCI TR Title WP WT",
        "Quote:Art Aside BlockQuote Caption Document DocumentFragment H Hn Index L LBody LI P Part RB RP RT Sect TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT",
        "RB:Art Aside BibEntry BlockQuote Caption Code Div Document DocumentFragment FENote Figure Formula H Hn Index L LBody LI Lbl Note P Part RB RP RT Ruby Sect TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT Warichu",
        "RP:Art Aside BibEntry BlockQuote Caption Code Div Document DocumentFragment FENote Figure Formula H Hn Index L LBody LI Lbl Note P Part RB RP RT Ruby Sect TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT Warichu",
        "RT:Art Aside BibEntry BlockQuote Caption Code Div Document DocumentFragment FENote Figure Formula H Hn Index L LBody LI Lbl Note P Part RB RP RT Ruby Sect TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT Warichu",
        "Reference:Document Index LBody LI RB RP RT TBody TD TFoot TH THead TOC TOCI TR WP WT",
        "Ruby:Annot Art Artifact Aside BibEntry BlockQuote Caption Code Div Document DocumentFragment Em FENote Figure Form Formula H Hn Index L LBody LI Lbl Link Note P Part Quote Reference Ruby Sect Span Strong Sub TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT Warichu",
        "Sect:BibEntry Document Em LBody LI Quote RB RP RT Ruby Span Strong Sub TBody TD TFoot TH THead TOCI TR WP WT Warichu",
        "Span:Art Aside BlockQuote Caption Div Document DocumentFragment H Hn Index L LBody LI P Part RB RP RT Sect TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT",
        "Strong:Art Aside BlockQuote Caption Div Document DocumentFragment H Hn Index L LBody LI P Part RB RP RT Sect TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT",
        "StructTreeRoot:Annot Art Artifact Aside BibEntry BlockQuote Caption Code Div DocumentFragment Em FENote Figure Form Formula H Hn Index L LBody LI Lbl Link NonStruct Note P Part Private Quote RB RP RT Reference Ruby Sect Span Strong Sub TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT Warichu",
        "Sub:Art Aside BlockQuote Caption Div Document DocumentFragment H Hn Index LBody LI P Part RB RP RT Sect Sub TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT",
        "TBody:Annot Art Aside BibEntry BlockQuote Caption Code Div Document DocumentFragment Em FENote Figure Form Formula H Hn Index L LBody LI Lbl Link Note P Part Quote RB RP RT Reference Ruby Sect Span Strong Sub TBody TD TFoot TH THead TOC TOCI Table Title WP WT Warichu",
        "TD:Aside BlockQuote Caption Document DocumentFragment LBody LI Part RB RP RT Sub TBody TD TFoot TH THead TOC TOCI TR Title WP WT",
        "TFoot:Annot Art Aside BibEntry BlockQuote Caption Code Div Document DocumentFragment Em FENote Figure Form Formula H Hn Index L LBody LI Lbl Link Note P Part Quote RB RP RT Reference Ruby Sect Span Strong Sub TBody TD TFoot TH THead TOC TOCI Table Title WP WT Warichu",
        "TH:Aside BlockQuote Caption Document DocumentFragment LBody LI Part RB RP RT Sub TBody TD TFoot TH THead TOC TOCI TR Title WP WT",
        "THead:Annot Art Aside BibEntry BlockQuote Caption Code Div Document DocumentFragment Em FENote Figure Form Formula H Hn Index L LBody LI Lbl Link Note P Part Quote RB RP RT Reference Ruby Sect Span Strong Sub TBody TD TFoot TH THead TOC TOCI Table Title WP WT Warichu",
        "TOC:Annot Art Aside BibEntry BlockQuote Code Div Document DocumentFragment Em FENote Figure Form Formula H Hn Index L LBody LI Lbl Link Note P Quote RB RP RT Reference Ruby Sect Span Strong Sub TBody TD TFoot TH THead TR Table Title WP WT Warichu",
        "TOCI:Art Aside BibEntry BlockQuote Caption Code Document DocumentFragment Em FENote Figure Form Formula H Hn Index L LBody LI Link Note Part Quote RB RP RT Ruby Sect Span Strong Sub TBody TD TFoot TH THead TOCI TR Table Title WP WT Warichu",
        "TR:Annot Art Aside BibEntry BlockQuote Caption Code Div Document DocumentFragment Em FENote Figure Form Formula H Hn Index L LBody LI Lbl Link Note P Part Quote RB RP RT Reference Ruby Sect Span Strong Sub TBody TFoot THead TOC TOCI TR Table Title WP WT Warichu",
        "Table:Annot Art Aside BibEntry BlockQuote Code Div Document DocumentFragment Em FENote Figure Form Formula H Hn Index L LBody LI Lbl Link Note P Part Quote RB RP RT Reference Ruby Sect Span Strong Sub TD TH TOC TOCI Table Title WP WT Warichu",
        "Title:Art BlockQuote Document DocumentFragment H Hn Index LBody LI RB RP RT Sect Sub TBody TD TFoot TH THead TOC TOCI TR Title WP WT",
        "WP:Art Aside BibEntry BlockQuote Caption Code Div Document DocumentFragment FENote Figure Formula H Hn Index L LBody LI Lbl Note P Part RB RP RT Ruby Sect TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT Warichu",
        "WT:Art Aside BibEntry BlockQuote Caption Code Div Document DocumentFragment FENote Figure Formula H Hn Index L LBody LI Lbl Note P Part RB RP RT Ruby Sect TBody TD TFoot TH THead TOC TOCI TR Table Title WP WT Warichu",
        "Warichu:Annot Art Artifact Aside BibEntry BlockQuote Caption Code Div Document DocumentFragment Em FENote Figure Form Formula H Hn Index L LBody LI Lbl Link Note P Part Quote RB RP RT Reference Ruby Sect Span Strong Sub TBody TD TFoot TH THead TOC TOCI TR Table Title Warichu",
    ];

    private static readonly string[] s_atMostOne =
    [
        "Art:Caption H",
        "Artifact:Caption H",
        "Aside:Caption H",
        "BlockQuote:Caption H",
        "Caption:H",
        "Code:Caption",
        "Document:H",
        "DocumentFragment:H",
        "Figure:Caption H",
        "Form:Caption",
        "Formula:Caption H",
        "H:Sect",
        "Hn:Sect",
        "Index:Caption",
        "L:Caption",
        "LBody:Caption H",
        "Note:Caption",
        "Sect:H",
        "TD:H",
        "TH:H",
        "TOC:Caption",
        "Table:Caption TFoot THead",
        "Title:Caption",
    ];

    // As above, but only for an element that holds no content items itself (one used as a grouping element).
    private static readonly string[] s_atMostOneGrouping =
    [
        "Annot:Caption",
        "FENote:Caption",
        "Link:Caption",
        "Reference:Caption",
    ];

    private static readonly string s_noContent = "Art DocumentFragment Document Index L Sect StructTreeRoot TBody TFoot THead TOC TOCI TR Table";
    private static readonly Dictionary<string, HashSet<string>> s_forbiddenMap = Parse(s_forbidden);
    private static readonly Dictionary<string, HashSet<string>> s_atMostOneMap = Parse(s_atMostOne);
    private static readonly Dictionary<string, HashSet<string>> s_atMostOneGroupingMap = Parse(s_atMostOneGrouping);
    private static readonly HashSet<string> s_noContentSet = new(s_noContent.Split(' '), StringComparer.Ordinal);

    /// <summary>The name a standard type has in the table: <c>H1</c> to <c>H9</c> and beyond are all <c>Hn</c>; others are themselves.</summary>
    public static string Normalize(string type) => PdfUA2.IsHeading(type, out _) ? "Hn" : type;

    /// <summary>True when the table forbids an element of the (normalized) type <paramref name="parent"/> from containing one of type <paramref name="child"/>.</summary>
    public static bool Forbids(string parent, string child) => s_forbiddenMap.TryGetValue(parent, out HashSet<string>? set) && set.Contains(child);

    /// <summary>True when the table allows at most one <paramref name="child"/> in a <paramref name="parent"/>; <paramref name="grouping"/> is true when the parent holds no content items itself.</summary>
    public static bool AtMostOne(string parent, string child, bool grouping) =>
        (s_atMostOneMap.TryGetValue(parent, out HashSet<string>? set) && set.Contains(child))
        || (grouping && s_atMostOneGroupingMap.TryGetValue(parent, out HashSet<string>? g) && g.Contains(child));

    /// <summary>True when the table forbids a <paramref name="parent"/> from holding content items (marked content) directly.</summary>
    public static bool ForbidsContent(string parent) => s_noContentSet.Contains(parent);

    private static Dictionary<string, HashSet<string>> Parse(string[] rows)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string row in rows)
        {
            int colon = row.IndexOf(':');
            map[row[..colon]] = new HashSet<string>(row[(colon + 1)..].Split(' '), StringComparer.Ordinal);
        }

        return map;
    }
}
