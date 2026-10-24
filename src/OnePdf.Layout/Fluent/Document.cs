namespace OnePdf.Layout;

public sealed partial class LayoutDocument
{
    /// <summary>
    /// Describes a document in the fluent style: pages with a header, content and footer, each filled by delegates. The returned document
    /// is the element tree behind it; save it with <see cref="Save(Stream, PdfDocumentWriterOptions?)"/> or <see cref="Save(string, PdfDocumentWriterOptions?)"/>.
    /// </summary>
    /// <param name="compose">Sets document-wide settings and adds pages.</param>
    /// <returns>The document. Nothing is laid out until it is saved.</returns>
    /// <example>
    /// <code>
    /// LayoutDocument.Compose(doc =&gt;
    /// {
    ///     doc.Title("Hello");
    ///     doc.Page(page =&gt;
    ///     {
    ///         page.Size(PageSize.A4);
    ///         page.Margin(36);
    ///         page.Content().Text("Hello, world").FontSize(24);
    ///     });
    /// }).Save("hello.pdf");
    /// </code>
    /// </example>
    public static LayoutDocument Compose(Action<DocumentComposer> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        var document = new LayoutDocument();
        compose(new DocumentComposer(document));
        return document;
    }

    /// <summary>Lays the document out and writes the file at <paramref name="path"/>, replacing it. The file is only kept if layout and writing succeed.</summary>
    /// <param name="path">Where to write.</param>
    /// <param name="options">Writer options; set a fixed <see cref="PdfDocumentWriterOptions.DocumentId"/> for reproducible bytes.</param>
    /// <exception cref="PdfLayoutException">Something cannot be placed even on an empty page.</exception>
    public void Save(string path, PdfDocumentWriterOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string temp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Save(fs, options);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Lays the document out and returns the file's bytes.</summary>
    /// <param name="options">Writer options.</param>
    /// <exception cref="PdfLayoutException">Something cannot be placed even on an empty page.</exception>
    public byte[] ToBytes(PdfDocumentWriterOptions? options = null)
    {
        using var ms = new MemoryStream();
        Save(ms, new PdfDocumentWriterOptions
        {
            CompressStreams = options?.CompressStreams ?? true,
            Version = options?.Version ?? "1.7",
            XrefFormat = options?.XrefFormat ?? default,
            DocumentId = options?.DocumentId,
            Language = options?.Language,
            DisplayDocumentTitle = options?.DisplayDocumentTitle ?? false,
            Limits = options?.Limits ?? PdfLimits.Default,
            LeaveOpen = true,
        });
        return ms.ToArray();
    }

    /// <summary>
    /// Lays the document out and returns it as an editable <see cref="PdfDocument"/>, the way to reach what the streaming writer does not do:
    /// encryption, PDF/A (<see cref="PdfSaveOptions.PdfA"/>), object streams, signing and page operations. The whole file is held in memory.
    /// </summary>
    /// <param name="options">Writer options for the intermediate file.</param>
    /// <exception cref="PdfLayoutException">Something cannot be placed even on an empty page.</exception>
    /// <example>
    /// <code>
    /// using PdfDocument doc = layout.ToPdfDocument();
    /// doc.Save("secret.pdf", new PdfSaveOptions
    /// {
    ///     Encryption = PdfEncryptionAction.Set,
    ///     NewEncryption = new PdfEncryptionOptions { UserPassword = "open sesame" },
    /// });
    /// </code>
    /// </example>
    public PdfDocument ToPdfDocument(PdfDocumentWriterOptions? options = null) =>
        PdfDocument.Open(ToBytes(options), new PdfOpenOptions { AllowEditing = true });

    /// <summary>
    /// Lays the document out and saves it through <see cref="PdfDocument"/> with the given options, for encryption, PDF/A and object streams,
    /// which the streaming writer behind <see cref="Save(Stream, PdfDocumentWriterOptions?)"/> does not offer. The whole file is held in memory
    /// while it is rewritten, so memory grows with the document. Signing is not an option here: sign the result of <see cref="ToPdfDocument"/>.
    /// </summary>
    /// <param name="output">Where the file goes; left open.</param>
    /// <param name="save">How to save, for example <c>new PdfSaveOptions { PdfA = new PdfAOptions { ... } }</c>.</param>
    /// <param name="options">Writer options for the intermediate file.</param>
    /// <exception cref="PdfLayoutException">Something cannot be placed even on an empty page.</exception>
    public void Save(Stream output, PdfSaveOptions save, PdfDocumentWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(save);
        if (save.PdfUA?.Part == PdfUAPart.Part2)
        {
            // The intermediate file is PDF 2.0 too, so anchors and bookmarks are written as structure destinations (ISO 32000-2 12.3.2.3).
            options = new PdfDocumentWriterOptions
            {
                CompressStreams = options?.CompressStreams ?? true,
                Version = options is not null && string.CompareOrdinal(options.Version, "2.0") > 0 ? options.Version : "2.0",
                XrefFormat = options?.XrefFormat ?? default,
                DocumentId = options?.DocumentId,
                Language = options?.Language,
                DisplayDocumentTitle = options?.DisplayDocumentTitle ?? false,
                Limits = options?.Limits ?? PdfLimits.Default,
            };
        }

        using PdfDocument doc = ToPdfDocument(options);
        doc.Save(output, save);
    }
}

/// <summary>Document-wide settings and the pages of a fluent document. Given to the delegate of <see cref="LayoutDocument.Compose(Action{DocumentComposer})"/>.</summary>
public sealed class DocumentComposer
{
    private readonly LayoutDocument _document;

    internal DocumentComposer(LayoutDocument document) => _document = document;

    /// <summary>The title: the information dictionary's <c>Title</c>, shown by viewers instead of the file name. Tagged documents should have one.</summary>
    public DocumentComposer Title(string title)
    {
        _document.Title = title;
        return this;
    }

    /// <summary>The author: the information dictionary's <c>Author</c>.</summary>
    public DocumentComposer Author(string author)
    {
        _document.Author = author;
        return this;
    }

    /// <summary>The subject: the information dictionary's <c>Subject</c>.</summary>
    public DocumentComposer Subject(string subject)
    {
        _document.Subject = subject;
        return this;
    }

    /// <summary>The keywords: the information dictionary's <c>Keywords</c>, as one string.</summary>
    public DocumentComposer Keywords(string keywords)
    {
        _document.Keywords = keywords;
        return this;
    }

    /// <summary>The document language, a BCP 47 tag such as <c>en-US</c>: the catalog's <c>/Lang</c> and the default for every structure element.</summary>
    public DocumentComposer Language(string tag)
    {
        _document.Language = tag;
        return this;
    }

    /// <summary>The direction of every page that does not set its own.</summary>
    public DocumentComposer Direction(LayoutDirection direction)
    {
        _document.Direction = direction;
        return this;
    }

    /// <summary>Right-to-left content: rows, table columns and list markers mirror, start/end alignment flips and paragraphs get a right-to-left base direction.</summary>
    public DocumentComposer RightToLeft() => Direction(LayoutDirection.RightToLeft);

    /// <summary>
    /// The fonts text can use. The first access makes a registry of the default faces (Liberation Sans, Serif and Mono) that belongs to this
    /// document; add fonts with <see cref="TextFontRegistry.Add(byte[], string?, int?, bool?, int, int)"/> and fallbacks to
    /// <see cref="TextFontRegistry.FallbackFamilies"/>. System fonts are used only if you ask with <see cref="TextFontRegistry.UseSystemFonts(IPdfFontProvider)"/>.
    /// </summary>
    public TextFontRegistry Fonts => _document.Options.Fonts ??= TextFontRegistry.CreateDefault();

    /// <summary>Draws every placed element's box and name over the page, to see why something sits where it does.</summary>
    public DocumentComposer Debug(bool on = true)
    {
        _document.Options.Debug = on;
        return this;
    }

    /// <summary>Writes a structure tree (tagged PDF). On by default; <c>Tagged(false)</c> writes pages with no marked content.</summary>
    public DocumentComposer Tagged(bool on)
    {
        _document.Options.Tagged = on;
        return this;
    }

    /// <summary>Fewest lines of a split paragraph at the bottom (orphans) and top (widows) of a page. Default 2 and 2.</summary>
    public DocumentComposer WidowsAndOrphans(int widows, int orphans)
    {
        _document.Options.Widows = widows;
        _document.Options.Orphans = orphans;
        return this;
    }

    /// <summary>
    /// How text breaks its lines unless a text chooses for itself (<see cref="TextDescriptor.OptimalLineBreaks"/>). Default greedy.
    /// </summary>
    /// <param name="mode">The mode.</param>
    /// <param name="options">The knobs of <see cref="LineBreakMode.KnuthPlass"/>; null for the defaults.</param>
    public DocumentComposer LineBreaking(LineBreakMode mode, KnuthPlassOptions? options = null)
    {
        _document.Options.LineBreakMode = mode;
        _document.Options.KnuthPlass = options;
        return this;
    }

    /// <summary>The most pages the document may produce before layout stops with <see cref="PdfLayoutException"/>. Default 100000.</summary>
    public DocumentComposer MaxPages(int pages)
    {
        _document.Options.MaxPages = pages;
        return this;
    }

    /// <summary>Adds a run of pages with the same setup (size, margins, header, footer, content). Each starts on a new page.</summary>
    /// <param name="compose">Describes the pages.</param>
    public DocumentComposer Page(Action<PageComposer> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        var page = new LayoutPage();
        _document.Pages.Add(page);
        var composer = new PageComposer(page);
        compose(composer);
        composer.Seal();
        return this;
    }
}

/// <summary>Describes one run of pages: size, margins, direction, numbering and the slots for header, footer, background, foreground and content.</summary>
public sealed class PageComposer
{
    private readonly LayoutPage _page;
    private readonly Dictionary<string, Slot> _slots = [];

    internal PageComposer(LayoutPage page) => _page = page;

    internal void Seal()
    {
        foreach (Slot s in _slots.Values)
        {
            s.Seal();
        }
    }

    private Slot SlotFor(string key, Action<Element> set)
    {
        if (!_slots.TryGetValue(key, out Slot? s))
        {
            s = new Slot(set);
            _slots[key] = s;
        }

        return s;
    }

    /// <summary>The page size before orientation. Default A4.</summary>
    public PageComposer Size(PageSize size)
    {
        _page.Size = size;
        return this;
    }

    /// <summary>A page size in points.</summary>
    public PageComposer Size(double width, double height) => Size(new PageSize(width, height));

    /// <summary>Turns the page to landscape (the size's sides are swapped if need be).</summary>
    public PageComposer Landscape()
    {
        _page.Orientation = PageOrientation.Landscape;
        return this;
    }

    /// <summary>Portrait pages. This is the default.</summary>
    public PageComposer Portrait()
    {
        _page.Orientation = PageOrientation.Portrait;
        return this;
    }

    /// <summary>The same margin on all four sides, in points. Default 36.</summary>
    public PageComposer Margin(double points)
    {
        _page.Margin = Insets.All(points);
        return this;
    }

    /// <summary>Margins: <paramref name="horizontal"/> left and right, <paramref name="vertical"/> top and bottom.</summary>
    public PageComposer Margin(double horizontal, double vertical)
    {
        _page.Margin = Insets.Symmetric(horizontal, vertical);
        return this;
    }

    /// <summary>Margins per side, in points.</summary>
    public PageComposer Margin(double top, double right, double bottom, double left)
    {
        _page.Margin = new Insets(top, right, bottom, left);
        return this;
    }

    /// <summary>The content direction of these pages; the document's when not set.</summary>
    public PageComposer Direction(LayoutDirection direction)
    {
        _page.Direction = direction;
        return this;
    }

    /// <summary>Right-to-left content on these pages.</summary>
    public PageComposer RightToLeft() => Direction(LayoutDirection.RightToLeft);

    /// <summary>How page numbers of this section are written, in page-number elements and the document's page labels. Default decimal.</summary>
    public PageComposer NumberStyle(NumberingStyle style)
    {
        _page.NumberStyle = style;
        return this;
    }

    /// <summary>The number of the section's first page; the numbering continues from the section before when not set.</summary>
    public PageComposer StartNumber(int number)
    {
        _page.StartNumber = number;
        return this;
    }

    /// <summary>Text before the page number in the document's page labels, such as <c>A-</c>.</summary>
    public PageComposer LabelPrefix(string prefix)
    {
        _page.LabelPrefix = prefix;
        return this;
    }

    /// <summary>The header, laid out at the top of every page inside the margins. Its height comes off the content area.</summary>
    public Slot Header() => SlotFor("h", e => _page.Header = e);

    /// <summary>The footer, laid out at the bottom of every page inside the margins. Its height comes off the content area.</summary>
    public Slot Footer() => SlotFor("f", e => _page.Footer = e);

    /// <summary>Replaces <see cref="Header"/> on the first page of this section. Put <c>Spacer(0)</c> in it for no header there.</summary>
    public Slot FirstPageHeader() => SlotFor("fh", e => _page.FirstHeader = e);

    /// <summary>Replaces <see cref="Footer"/> on the first page of this section.</summary>
    public Slot FirstPageFooter() => SlotFor("ff", e => _page.FirstFooter = e);

    /// <summary>Replaces <see cref="Header"/> on pages whose displayed number is even (but not the section's first page).</summary>
    public Slot EvenPageHeader() => SlotFor("eh", e => _page.EvenHeader = e);

    /// <summary>Replaces <see cref="Footer"/> on pages whose displayed number is even (but not the section's first page).</summary>
    public Slot EvenPageFooter() => SlotFor("ef", e => _page.EvenFooter = e);

    /// <summary>Drawn first on every page at the full page size, margins ignored. Written as an artifact.</summary>
    public Slot Background() => SlotFor("b", e => _page.Background = e);

    /// <summary>Drawn last on every page at the full page size, margins ignored: watermarks and stamps. Written as an artifact.</summary>
    public Slot Foreground() => SlotFor("fg", e => _page.Foreground = e);

    /// <summary>The flowing content, which continues onto as many pages as it needs.</summary>
    public Slot Content() => SlotFor("c", e => _page.Content = e);
}

/// <summary>Makes the fluent entry point reachable from <see cref="PdfDocument"/>: <c>PdfDocument.Compose(doc =&gt; ...)</c>.</summary>
public static class PdfDocumentLayoutExtensions
{
    extension(PdfDocument)
    {
        /// <summary>Describes a document in the fluent style; the same as <see cref="LayoutDocument.Compose(Action{DocumentComposer})"/>. The result is a <see cref="LayoutDocument"/> (the layout), not a <see cref="PdfDocument"/>: save it, or call <see cref="LayoutDocument.ToPdfDocument(PdfDocumentWriterOptions?)"/> for an editable one.</summary>
        /// <param name="compose">Sets document-wide settings and adds pages.</param>
        public static LayoutDocument Compose(Action<DocumentComposer> compose) => LayoutDocument.Compose(compose);
    }
}
