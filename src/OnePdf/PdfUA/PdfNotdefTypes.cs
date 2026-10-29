namespace OnePdf;

/// <summary>Which text <see cref="PdfDocument.RemoveNotdefText"/> may remove.</summary>
public enum PdfNotdefScope
{
    /// <summary>
    /// Only text nobody sees: text rendering mode 3 (invisible, as in the hidden text layer of a scanned page) and text in optional
    /// content that is switched off. Nothing the page draws changes. This is the default.
    /// </summary>
    Invisible = 0,

    /// <summary>
    /// Every shown <c>.notdef</c> glyph, including visible text. A visible <c>.notdef</c> usually draws the font's "missing glyph"
    /// box or nothing, so removing it changes what the page looks like; the surrounding text keeps its place.
    /// </summary>
    All = 1,
}

/// <summary>Where the content stream that shows a <c>.notdef</c> glyph belongs.</summary>
public enum PdfNotdefSource
{
    /// <summary>The page's own content stream(s).</summary>
    PageContent = 0,

    /// <summary>A form XObject drawn with <c>Do</c> (it may be drawn from several pages).</summary>
    Form = 1,

    /// <summary>The appearance stream of an annotation.</summary>
    AnnotationAppearance = 2,
}

/// <summary>Why <see cref="PdfDocument.RemoveNotdefText"/> left a text operator as it was.</summary>
public enum PdfNotdefKeepReason
{
    /// <summary>The text is drawn and <see cref="PdfNotdefRemoveOptions.Scope"/> is <see cref="PdfNotdefScope.Invisible"/>.</summary>
    Visible = 0,

    /// <summary><see cref="PdfNotdefRemoveOptions.Include"/> returned false.</summary>
    ExcludedByCaller = 1,

    /// <summary>
    /// The operator sits in a form XObject that is drawn more than once and the uses disagree: one use draws it, the fonts differ, or
    /// the text state (font size, spacing) differs so the same edit would not keep the text in place for all of them.
    /// </summary>
    SharedFormDisagrees = 2,

    /// <summary>The operator cannot be rewritten safely (a zero font size with character spacing, content that did not decode, a limit).</summary>
    CannotRewrite = 3,
}

/// <summary>
/// One text-showing operator whose string selects the <c>.notdef</c> glyph of an embedded font: a character code the font program has
/// no glyph for. ISO 14289-1 7.21.8 forbids showing it, whatever the text rendering mode.
/// </summary>
public sealed class PdfNotdefRun
{
    internal PdfNotdefRun(int pageIndex, PdfNotdefSource source, int streamObject, int offset, string fontResource, int fontObject, string baseFont,
        int[] codes, int[] shownCodes, PdfTextRenderMode renderMode, bool visible, string[] groups, bool groupsVisible, string text, string notdefText)
    {
        PageIndex = pageIndex;
        Source = source;
        StreamObject = streamObject;
        Offset = offset;
        FontResource = fontResource;
        FontObject = fontObject;
        BaseFont = baseFont;
        Codes = codes;
        ShownCodes = shownCodes;
        RenderMode = renderMode;
        IsVisible = visible;
        OptionalContentGroups = groups;
        OptionalContentVisible = groupsVisible;
        Text = text;
        NotdefText = notdefText;
    }

    /// <summary>The zero-based page the operator was reached from. A form drawn from several pages is listed once per page.</summary>
    public int PageIndex { get; }

    /// <summary>Whether the operator is in page content, a form XObject or an annotation appearance.</summary>
    public PdfNotdefSource Source { get; }

    /// <summary>The object number of the form or appearance stream; -1 for page content.</summary>
    public int StreamObject { get; }

    /// <summary>
    /// The byte offset of the operator (its first operand) in the decoded content stream. For page content with several streams, the
    /// streams are joined with one space after each, as the interpreter reads them.
    /// </summary>
    public int Offset { get; }

    /// <summary>The name the font has in the resource dictionary (<c>F1</c>), without the slash.</summary>
    public string FontResource { get; }

    /// <summary>The object number of the font dictionary; -1 for a direct dictionary.</summary>
    public int FontObject { get; }

    /// <summary>The font's <c>/BaseFont</c> as written, subset tag included; empty when absent.</summary>
    public string BaseFont { get; }

    /// <summary>The character codes in the string that select <c>.notdef</c>, in order (a code may repeat). For a composite font, the code is the value read from the string, not the CID.</summary>
    public IReadOnlyList<int> Codes { get; }

    /// <summary>All character codes the string shows, in order.</summary>
    public IReadOnlyList<int> ShownCodes { get; }

    /// <summary>The text rendering mode in force.</summary>
    public PdfTextRenderMode RenderMode { get; }

    /// <summary>True when the text is drawn: not rendering mode 3 and not in optional content that is switched off.</summary>
    public bool IsVisible { get; }

    /// <summary>The names of the optional content groups the operator is inside (<c>/OC</c> marked content), outermost first; empty when none.</summary>
    public IReadOnlyList<string> OptionalContentGroups { get; }

    /// <summary>False when the operator is inside optional content that the default configuration switches off.</summary>
    public bool OptionalContentVisible { get; }

    /// <summary>The text the whole string extracts as (from the font's Unicode mapping); the replacement character stands for a code with no mapping.</summary>
    public string Text { get; }

    /// <summary>The text of the <c>.notdef</c> codes alone, which is what extraction loses when they are removed.</summary>
    public string NotdefText { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"page {PageIndex + 1} {Source}{(StreamObject >= 0 ? " " + StreamObject.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)} @{Offset}: font {FontResource} ({BaseFont}), {Codes.Count} of {ShownCodes.Count} codes are .notdef, mode {(int)RenderMode}{(IsVisible ? string.Empty : ", not drawn")}";
}

/// <summary>A <c>.notdef</c> run that <see cref="PdfDocument.RemoveNotdefText"/> left in place, and why.</summary>
public sealed class PdfNotdefKept
{
    internal PdfNotdefKept(PdfNotdefRun run, PdfNotdefKeepReason reason)
    {
        Run = run;
        Reason = reason;
    }

    /// <summary>The operator that was left.</summary>
    public PdfNotdefRun Run { get; }

    /// <summary>Why it was left.</summary>
    public PdfNotdefKeepReason Reason { get; }
}

/// <summary>Options for <see cref="PdfDocument.RemoveNotdefText"/>.</summary>
public sealed class PdfNotdefRemoveOptions
{
    /// <summary>Which text may go. Default <see cref="PdfNotdefScope.Invisible"/>.</summary>
    public PdfNotdefScope Scope { get; init; } = PdfNotdefScope.Invisible;

    /// <summary>When true nothing is changed (the document need not be editable); the result says what a real call would do. Default false.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Called for every operator the scope allows; return false to keep it (for example to remove only the text of one font or of one
    /// optional content group). Null, the default, removes them all.
    /// </summary>
    public Func<PdfNotdefRun, bool>? Include { get; init; }
}

/// <summary>What <see cref="PdfDocument.RemoveNotdefText"/> did.</summary>
public sealed class PdfNotdefRemoval
{
    internal readonly List<PdfNotdefRun> RemovedList = [];
    internal readonly List<PdfNotdefKept> KeptList = [];
    internal readonly List<string> WarningList = [];

    /// <summary>The operators that were rewritten (or, with <see cref="PdfNotdefRemoveOptions.DryRun"/>, would be), one entry per page they were reached from.</summary>
    public IReadOnlyList<PdfNotdefRun> Removed => RemovedList;

    /// <summary>The operators that show <c>.notdef</c> and were left as they are.</summary>
    public IReadOnlyList<PdfNotdefKept> Kept => KeptList;

    /// <summary>The number of <c>.notdef</c> codes taken out of strings.</summary>
    public int GlyphsRemoved { get; internal set; }

    /// <summary>The number of text operators rewritten (each distinct operator counts once, however many pages draw it).</summary>
    public int OperatorsChanged { get; internal set; }

    /// <summary>The number of content streams written anew.</summary>
    public int StreamsChanged { get; internal set; }

    /// <summary>Content that could not be examined or rewritten, in plain words.</summary>
    public IReadOnlyList<string> Warnings => WarningList;
}
