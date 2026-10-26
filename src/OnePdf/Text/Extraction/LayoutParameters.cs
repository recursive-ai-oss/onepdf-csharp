namespace OnePdf.Text;

/// <summary>
/// The thresholds of the layout analysis. They are written down in <c>docs/text-layout.md</c>; change both together. Distances are in
/// units of the size of the glyphs concerned (em), so the analysis does not depend on the page scale.
/// </summary>
internal static class LayoutParameters
{
    /// <summary>A gap wider than this (em) between two glyphs on a line starts a new word.</summary>
    internal const double WordGapEm = 0.15;

    /// <summary>The same for ideographs, kana and hangul, which are written without spaces.</summary>
    internal const double CjkWordGapEm = 0.6;

    /// <summary>A gap wider than this (em) between two glyphs on a line splits the line into fragments (cells or columns).</summary>
    internal const double FragmentGapEm = 1.25;

    /// <summary>Space glyphs whose boxes span a gap, with holes of at most this many em, make it a word space even when it is wider than <see cref="FragmentGapEm"/>.</summary>
    internal const double SpaceCoverSlackEm = 0.1;

    /// <summary>A space glyph that overlaps the glyph before it by more than this fraction of its own width does not end the word.</summary>
    internal const double SpaceOverlapRatio = 0.5;

    /// <summary>An empty vertical stripe at least this wide (em), crossing every fragment of a region, separates columns.</summary>
    internal const double ColumnGutterEm = 1.25;

    /// <summary>
    /// A narrower empty stripe than <see cref="ColumnGutterEm"/> still separates columns when it runs clear through a region of many lines and every
    /// column it leaves is running text. Tightly set pages have gutters of 0.5 to 1.25 em, and fragments are joined across anything under
    /// <see cref="FragmentGapEm"/>, so without this such pages are read row by row across their columns. Word spaces are about 0.25 em, so a stripe that
    /// no glyph of a dozen lines touches is not a gap between words.
    /// </summary>
    internal const double SoftGutterEm = 0.5;

    /// <summary>A region needs at least this many distinct lines before a narrow stripe can separate its columns.</summary>
    internal const int SoftMinLines = 10;

    /// <summary>Every column cut at a narrow stripe has at least this many lines ...</summary>
    internal const int SoftColumnLines = 5;

    /// <summary>... and its median fragment is at least this many em wide (full lines of running text, not labels, bullets or cells).</summary>
    internal const double SoftColumnWidthEm = 12;

    /// <summary>A narrow-gutter cut makes at most this many columns; a region with more stripes is not newspaper columns.</summary>
    internal const int SoftMaxColumns = 8;

    /// <summary>An empty horizontal band at least this tall (em) separates blocks.</summary>
    internal const double BlockGapEm = 0.5;

    /// <summary>Two glyphs are on the same line when their baselines differ by at most this fraction of the smaller size.</summary>
    internal const double BaselineToleranceEm = 0.3;

    /// <summary>A smaller-size run (superscript, subscript) joins the line it touches when it is at most this fraction of that line's size.</summary>
    internal const double ScriptSizeRatio = 0.85;

    /// <summary>... and its baseline lies within this many em (of the larger size) of the line's baseline.</summary>
    internal const double ScriptOffsetEm = 0.7;

    /// <summary>... and it is at most this far (em) from a glyph of the line.</summary>
    internal const double ScriptTouchEm = 0.4;

    /// <summary>A region is a table when its fragments' median width is at most this many em ...</summary>
    internal const double TableCellWidthEm = 10;

    /// <summary>... it has at least this many rows ...</summary>
    internal const int TableMinRows = 3;

    /// <summary>... and at least this fraction of the rows have fragments in more than one column.</summary>
    internal const double TableRowFraction = 0.6;

    /// <summary>A repeated glyph closer than this (em, both axes) to an identical one is a fake-bold duplicate.</summary>
    internal const double DuplicateOffsetEm = 0.1;

    /// <summary>A glyph whose centre lies more than this many points outside the page is off-page.</summary>
    internal const double OffPageMargin = 1.0;

    /// <summary>The structure tree is used when it covers at least this fraction of the visible glyphs of the page.</summary>
    internal const double StructureCoverage = 0.9;

    /// <summary>Header and footer bands: the top and bottom this fraction of the page height.</summary>
    internal const double FurnitureBand = 0.12;

    /// <summary>Header and footer text must repeat on at least this fraction of the pages (and at least two).</summary>
    internal const double FurnitureRepeat = 0.5;

    /// <summary>Layout mode: one character cell is this fraction of the median glyph size wide.</summary>
    internal const double LayoutCellEm = 0.5;

    /// <summary>Content-order mode: a glyph whose baseline differs by more than this (em) from the previous glyph's starts a new line.</summary>
    internal const double ContentLineEm = 0.5;

    /// <summary>Ideographs set one by one with horizontal matrices form a column when each origin is within this many em of the previous one across the column ...</summary>
    internal const double StackColumnDriftEm = 0.35;

    /// <summary>... and advances along the column by at least this many em ...</summary>
    internal const double StackStepMinEm = 0.4;

    /// <summary>... and at most this many.</summary>
    internal const double StackStepMaxEm = 2.0;

    /// <summary>The page needs at least this many such downward steps before its stacked ideographs are read as vertical columns.</summary>
    internal const int StackMinSteps = 2;
}
