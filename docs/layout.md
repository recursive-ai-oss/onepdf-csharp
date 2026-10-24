# Layout

`OnePdf.Layout` generates documents from code: you describe pages with nested delegates, it lays them out (shaped text, tables, lists, images, headers and footers, page numbers) and writes a tagged PDF through OnePDF's streaming writer, one page at a time. It ships as the package `RecursiveAI.OnePdf.Layout` (`dotnet add package RecursiveAI.OnePdf.Layout`), which depends on `RecursiveAI.OnePdf`, `RecursiveAI.OnePdf.Fonts` and `RecursiveAI.OneFont`.

```csharp
LayoutDocument.Compose(doc =>
{
    doc.Title("Quarterly report").Language("en");
    doc.Page(page =>
    {
        page.Size(PageSize.A4);
        page.Margin(2.Cm());
        page.Header().Text("Acme Corp").FontSize(9).FontColor(Colors.Grey);
        page.Content().Column(col =>
        {
            col.Spacing(10);
            col.Item().Heading(1).Text("Quarterly report").FontSize(22).Bold();
            col.Item().Text("Sales rose in every region.").Justify();
            col.Item().Table(table =>
            {
                table.Columns(c => { c.Relative(2); c.Constant(60); });
                table.CellBorder(0.5, Colors.LightGrey);
                table.HeaderRow(r =>
                {
                    r.Cell().Text("Region").Bold();
                    r.Cell().Text("Sales").Bold();
                });
                table.Row(r => { r.Cell().Text("North"); r.Cell().AlignEnd().Text("1,204"); });
                table.Row(r => { r.Cell().Text("South"); r.Cell().AlignEnd().Text("987"); });
            });
        });
        page.Footer().AlignCenter().Text(t =>
        {
            t.FontSize(9);
            t.Span("Page ");
            t.CurrentPageNumber();
            t.Span(" of ");
            t.TotalPages().ReservedDigits(2);
        });
    });
}).Save("report.pdf");
```

## The fluent API

`LayoutDocument.Compose` runs your delegate once and returns the finished description (the C# 14 extension `PdfDocument.Compose` does the same). Nothing is laid out until you save.

- **Document** (`DocumentComposer`): `Title`, `Language`, `Direction`/`RightToLeft`, `Fonts` (the font registry, see [layout-text.md](layout-text.md)), `Debug`, `Tagged(false)`, `WidowsAndOrphans`, `MaxPages`, and `Page` once per section.
- **Page** (`PageComposer`): `Size` (`PageSize.A4` and the other constants, or width and height in points), `Landscape`, `Margin`, `Direction`, page numbering (`NumberStyle`, `StartNumber`, `LabelPrefix`) and the slots `Header`, `Footer`, `FirstPageHeader`/`FirstPageFooter`, `EvenPageHeader`/`EvenPageFooter`, `Background`, `Foreground` and `Content`. Each `Page` call starts a new section on a new page.
- **Slots.** Every place that holds one child is a `Slot`. You chain decorators on it and end with one thing that fills it: `Text`, `Column`, `Row`, `Layers`, `List`, `Table`, `Image`, `Canvas`, `Spacer`, `PageBreak`, `Component` or `Content`. A slot takes exactly one such call.
- **Units** are points. `10.Mm()`, `2.Cm()` and `1.Inch()` convert; `Colors` has named colours and `LayoutColor.FromHex("#336699")` parses a hex colour.

### Decorators

Decorators wrap the slot from the outside in, in the order you write them:

```csharp
LayoutDocument.Compose(doc => doc.Page(page =>
{
    page.Content().Column(col =>
    {
        // Read from the outside in: a margin, then a rounded border with a background, then padding, then the text.
        col.Item()
            .Margin(8)
            .Border(1.5, Colors.Blue).Rounded(6).Background(Colors.LightGrey)
            .Padding(10)
            .Text("A rounded, padded box");
    });
})).Save("chain.pdf");
```

`Margin`, `Border`, `Background`, `Rounded`, `Opacity`, `Width`/`Height` (and `Min`/`Max`), `AspectRatio`, `Padding` and the nine-point `Align*` methods all become one box when the order allows it: margin first, then border and background and size, then padding, then alignment. Writing them in another order gives nested boxes, which look the same on the page and cost a little more; the result is correct either way. `Rounded` can sit anywhere. Spelled-out sides exist for every inset (`PaddingTop`, `MarginStart`, `BorderEnd` and so on); `Start` and `End` flip in right-to-left documents.

Other decorators: `Rotate`, `Scale`, `Translate`, `KeepTogether`, `EnsureSpace`, `KeepWithNext`, `ExpandHeight`, `Name` (the label the debug overlay and errors use). `AlignMiddle` and `AlignBottom` make the slot as tall as the space offered, because there is nothing to centre in otherwise.

- `Rotate` and `Scale` do not split their child: if the content would need a page break inside them, layout throws `PdfLayoutException`. Rotating by 90 degrees makes the child's width the page height, so put a `Width` box inside the rotation.
- Text fills the width it is offered, so `AlignCenter().Text(...)` aligns the text, not a box around it.

### Gradient backgrounds

`Background` also takes a `LayoutGradient`, linear at any angle or radial, with two or more colour stops:

```csharp
LayoutDocument.Compose(doc => doc.Page(page =>
{
    page.Content().Column(col =>
    {
        col.Spacing(10);

        // 90 degrees runs left to right; the first stop is reached in one corner and the last in the opposite one.
        col.Item().Height(60).Rounded(8)
            .Background(LayoutGradient.Linear(90, Colors.Blue, Colors.LightGrey))
            .Padding(10).Text("Linear").FontColor(Colors.White);

        // Several stops, each with a position from 0 to 1, here in DeviceCMYK (DeviceGray works too; one space per gradient).
        col.Item().Height(60).Background(LayoutGradient.Horizontal(
            new LayoutGradientStop(0, PdfGradientColor.Cmyk(0, 1, 1, 0)),
            new LayoutGradientStop(0.3, PdfGradientColor.Cmyk(0, 0, 1, 0)),
            new LayoutGradientStop(1, PdfGradientColor.Cmyk(1, 0.6, 0, 0))));

        // Circles around a centre given as fractions of the box.
        col.Item().Height(60).Background(LayoutGradient.Circle(
            [new LayoutGradientStop(0, Colors.White), new LayoutGradientStop(1, Colors.Blue)],
            centerX: 0.25, centerY: 0.5, extent: RadialExtent.FarthestCorner));
    });
})).Save("gradients.pdf");
```

- **Linear.** `Linear(angle, stops)`: the angle is in degrees clockwise from up, so 0 runs bottom to top, 90 left to right, 180 top to bottom (`Horizontal` and `Vertical` are 90 and 180). The gradient line passes through the centre of the box and is `|w sin a| + |h cos a|` long, so the first stop is reached exactly in one corner and the last in the opposite one. `Linear(angle, from, to)` is the two-colour form.
- **Radial.** `Circle(stops, centerX, centerY, extent)`: the first stop is at the centre, the last on the circle that touches the closest or farthest side or corner (`RadialExtent`, default the farthest corner). Circles only; no ellipses.
- **Stops.** Two or more `LayoutGradientStop(position, colour)` with positions from 0 to 1 that do not decrease, all in one colour space (a `LayoutColor` is DeviceRGB; `PdfGradientColor.Gray` and `.Cmyk` give the others). The end colours are held before the first and after the last stop. Two stops at one position make a hard edge. A bad list throws `ArgumentException` where the gradient is created.
- **With the rest of the box.** The gradient fills the area inside the margin and is clipped to rounded corners; the border paints over it; `Opacity` fades it (the content stays opaque); `Rotate`, `Scale` and `Translate` carry it along; it works as the background of a layer, a page (`page.Background()`), a table row (`TableRowDescriptor.Background`) and a table cell (`CellBackground`). `BackgroundGradient` replaces `Background` when both are set on an element.
- **Pages.** A box that splits over pages paints the whole gradient again in each piece, scaled to that piece, so every page shows the full transition. (A gradient that continues across the break would need the box's total height, which is unknown when the first piece is placed.) A table row or cell does the same.
- **Output.** Each gradient is a PDF shading (axial type 2 or radial type 3, ISO 32000-2 8.7.4.5) painted with `sh` under the box's clip; stops become one exponential function or a stitching function. It is an artifact in tagged output and passes PDF/A and PDF/UA-1 checks (a PDF/A save refuses a CMYK gradient, as it does any DeviceCMYK colour, because OnePDF has no CMYK profile to offer). To fill any other shape in a canvas, see [Content creation](content-creation.md#gradients).

### Text

`Text("...")` returns a `TextDescriptor` with `FontSize`, `FontFamily`, `FontColor`, `Bold`, `Italic`, `Underline`, `Strike`, `LetterSpacing`, `LineHeight`, `Justify`, `AlignStart`/`AlignCenter`/`AlignEnd`, `FirstLineIndent`, `Language` and `Hyphenator`. `Text(t => ...)` builds rich text from `Span`, `Line`, `CurrentPageNumber`, `TotalPages` and `SectionPages`; a span has the same formatting methods plus `Superscript` and `Subscript`. Settings made on the descriptor are the default for its spans.

A page number is shaped like any other text. A paragraph that holds a page number stays on one line (it does not wrap) and is as wide as its content. "Total pages" is not known until the end, so its width is reserved: `ReservedDigits` (default 4) says how many digits to leave room for. In a centred footer that reserve shows as a gap, so use `ReservedDigits(2)` for documents under 100 pages. Details of shaping, fallback fonts, bidirectional text and hyphenation are in [layout-text.md](layout-text.md).

### Rows, columns, layers, lists, images

- `Column(c => ...)` with `Spacing`, `ItemAlignment` and `Item()`; `Row(r => ...)` with `Spacing`, `AlignTop`/`AlignMiddle`/`AlignBottom`, and `ConstantItem(width)`, `RelativeItem(weight)` or `AutoItem()`.
- `Layers(l => ...)`: `PrimaryLayer()` sets the size and may span pages; `Layer()` slots (a watermark, a frame) are laid out at its size.
- `List(l => ...)`: `Bulleted`, `Numbered(style)`, `Bullet`, `Start`, `Suffix`, `MarkerFormat`, marker font, size, colour, width and gap, and `Item()` (or `Item("custom marker")`). Lists nest by putting a `List` inside an item.
- `Image(bytes | path | PdfCreatedImage)` for JPEG and PNG with `FitWidth` (the default), `FitHeight`, `Contain`, `Cover`, `Stretch`, `Dpi` and `Align`. Give figures `AltText(...)`, or `Artifact()` for decoration.
- `Canvas(height, surface => ...)` hands you a clipped drawing surface.

```csharp
LayoutDocument.Compose(doc => doc.Page(page =>
{
    page.Content().Column(col =>
    {
        col.Spacing(8);
        col.Item().Width(120).Image(File.ReadAllBytes("logo.png")).AltText("Company logo");
        col.Item().Width(200).Height(80).Image("photo.jpg").Cover().AltText("A photo");
    });
})).Save("images.pdf");
```

### Multi-column flow

`MultiColumn(cols => ...)` pours its items through equal columns: down the first, then down the next, and onto the next page after the last column. It goes anywhere a container does: the page's content slot, a section, a box, or between full-width items of a `Column`, as in the sample below.

```csharp
string text = string.Concat(Enumerable.Repeat("Text poured through columns breaks between them the way it breaks between pages. ", 12));

LayoutDocument.Compose(doc =>
{
    doc.Title("Newsletter").Language("en");
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(28);
        page.Content().Column(col =>
        {
            col.Spacing(8);
            col.Item().Heading(1).Text("Harbour Gazette").FontSize(20).Bold();

            // Two columns that flow down, then across, then onto the next page. The last page is balanced.
            col.Item().MultiColumn(cols =>
            {
                cols.Count(2).Gap(16).Rule(0.5, Colors.Grey).Spacing(6);
                cols.Item().Heading(2).Text("Tides").FontSize(12).Bold();
                cols.Item().Text(text).FontSize(9).Justify();
                cols.ColumnBreak();
                cols.Item().Text("Starts the second column.").FontSize(9);
                cols.Item().Text(text).FontSize(9).Justify();
            });

            col.Item().Text("Back to full width.").FontSize(8);
        });
    });
}).Save("columns.pdf");
```

- **Set-up:** `Count` (1 to 32, default 2), `Gap` (default 12 pt), `Rule(width, colour)` for a line in the middle of each gap (decoration, drawn only between columns that have content), `Spacing` between items inside a column, `Item()` for content and `ColumnBreak()` to end the column (ignored at the top of a column, so no empty column appears; after the last column it moves on to the next page). The element objects are `MultiColumn` and `ColumnBreak`.
- **Breaking:** a column plays the part of a page. Paragraphs split with the widow and orphan rules, tables split between rows and repeat header rows in every column they reach, `KeepTogether`, `KeepWithNext` and `EnsureSpace` apply, and a heading never ends a column. An element taller than a column splits or raises `PdfLayoutException` exactly as it would on a page.
- **Fill:** `Balanced()` (the default) shortens the columns of the last region, where the content ends, to the least height that still holds it, so they end at about the same level. Regions that run to the bottom of the page are unaffected. `Sequential()` fills each column to the bottom before starting the next. The search is a bisection with at most 12 trial layouts of the remaining content and a resolution of a quarter of a point, whatever the content. Content that cannot be divided (a paragraph shorter than the widow and orphan rules need, one tall picture) stays in as few columns as it needs and the others stay empty; balancing never breaks a rule to even the columns out.
- **Direction:** in a right-to-left document the first column is the rightmost; the flow still reads column 1, 2, 3.
- **Tagging and extraction:** the structure tree and the content order follow the flow, column by column. A paragraph that splits across columns is one `P` with content in both. Text extraction returns the flow order.
- **Limits:** with unbounded width a flow has one column (a note is recorded). A `PageBreak` placed directly in the flow ends the column like a `ColumnBreak`.

### Tables

`Table(t => ...)` takes `Columns` (`Auto`, `Constant`, `Relative`), `HeaderRow`, `Row` and `FooterRow`, `CellPadding`, `CellBorder`, `RowBackground` for striping and `MeasuredRows`. Each row has `Cell()` slots; a cell can `Span(columns, rows)`, set its own padding, background, borders and vertical alignment, and mark itself a row header (`RowHeader`). Header and footer rows repeat on every page. The sample at the top of this page has a small table; the layout rules (column widths, splitting, collapsed borders) are in [Tables](#tables-1) below.

### Pagination, links, structure

`KeepTogether`, `KeepWithNext`, `EnsureSpace(height)` and `PageBreak()` control breaks (rules and defaults under [Pagination core](#pagination-core)). `Hyperlink(uri)`, `LinkToAnchor(id)`, `Anchor(id)` and `Bookmark(title, level)` make links and the outline. `Heading(level)`, `AltText`, `Language` and `Artifact` set the tags; tagging is on by default.

```csharp
LayoutDocument.Compose(doc => doc.Page(page =>
{
    page.Size(PageSize.A5);
    page.Content().Column(col =>
    {
        col.Item().Bookmark("Contents").Anchor("top").Heading(1).Text("Contents");
        col.Item().List(list =>
        {
            list.Numbered();
            list.Item().LinkToAnchor("top").Text("Back to the top");
            list.Item().Hyperlink("https://example.com/").Text("A web page");
        });
    });
})).Save("navigation.pdf");
```

### Table of contents

`TableOfContents(toc => ...)` lists the document's headings, with page numbers, leaders and links. Entries come from `Heading` elements and from `TocEntry(title, level)` wrappers for things that are not headings; `Heading(level, inToc, tocTitle)` leaves a heading out or lists it under another text. `Levels(min, max)` (default 1 to 3), `Leader` (default a dot; null for none), `Indent`, `Gap`, `Spacing`, `Style` and `Level(n, style)` (font, size, colour, line spacing and `SpaceBefore` per level) shape it. The number is the page's label, so a roman front section shows "iv" and a `LabelPrefix` of "A-" shows "A-12", exactly as a viewer's page-label box does.

```csharp
LayoutDocument.Compose(doc =>
{
    doc.Title("Field notes").Language("en");
    doc.Page(page =>
    {
        page.NumberStyle(NumberingStyle.LowerRoman);
        page.Content().Column(col =>
        {
            col.Item().Text("Contents").FontSize(20).Bold();
            col.Item().TableOfContents(toc => toc.Levels(1, 2).Level(1, s => s.Weight = 700));
        });
    });
    doc.Page(page =>
    {
        page.StartNumber(1);
        page.Content().Column(col =>
        {
            col.Item().Heading(1).Text("The harbour").FontSize(16).Bold();
            col.Item().Text("Gulls and ropes.");
            col.Item().PageBreak();
            col.Item().Heading(1).Text("The orchard").FontSize(16).Bold();
            col.Item().Heading(2).Text("Windfalls").Bold();
            col.Item().TocEntry("A note on cider", 2).Text("Pressed in October.");
        });
    });
}).Save("toc.pdf");
```

### Components

Implement `IComponent` to reuse a piece of layout. `Component<T>()` creates one with its parameterless constructor.

```csharp
sealed class Address(string name, string city) : IComponent
{
    public void Compose(Slot slot) => slot.Column(column =>
    {
        column.Item().Text(name).Bold();
        column.Item().Text(city);
    });
}

LayoutDocument.Compose(doc => doc.Page(page =>
    page.Content().Component(new Address("Ada Lovelace", "London"))))
    .Save("address.pdf");
```

### Right to left and fonts

`doc.RightToLeft()` mirrors rows, table columns, start/end alignment and insets, and sets the base direction of text. Fonts are registered on `doc.Fonts` (grouped by family, weight and style); the default is Liberation Sans from `RecursiveAI.OnePdf.Fonts`, embedded as a subset. System fonts are never used unless you add them.

```csharp
LayoutDocument.Compose(doc =>
{
    doc.RightToLeft().Language("ar");
    doc.Fonts.Add(File.ReadAllBytes("NotoSansArabic-Regular.ttf"), "Noto Sans Arabic");
    doc.Fonts.FallbackFamilies.Add("Noto Sans Arabic");
    doc.Page(page => page.Content().Row(row =>
    {
        row.ConstantItem(80).Text("\u0645\u0631\u062D\u0628\u0627");
        row.RelativeItem().Text("first item sits at the right");
    }));
}).Save("rtl.pdf");
```

### Saving

`Save(path)` and `ToBytes()` stream the document page by page with the small memory footprint described under [Output](#output). Everything that needs the finished file goes through core: `Save(stream, PdfSaveOptions)` lays the document out, then rewrites it with the options (PDF/A, PDF/UA-1, encryption, object streams); the whole file is in memory while it does. For signing, `ToPdfDocument()` returns an open `PdfDocument` to sign with the core API. Use a fixed `PdfDocumentWriterOptions.DocumentId` when you need byte-identical output.

```csharp
LayoutDocument layout = LayoutDocument.Compose(doc =>
{
    doc.Title("Archive copy").Language("en");
    doc.Page(page => page.Content().Text("Archive copy"));
});

layout.Save("plain.pdf");                                // streamed page by page

using (var archive = File.Create("archive.pdf"))         // laid out, then rewritten by the core library
{
    layout.Save(archive, new PdfSaveOptions
    {
        PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.B },
    });
}

using (var secret = File.Create("secret.pdf"))
{
    layout.Save(secret, new PdfSaveOptions
    {
        Encryption = PdfEncryptionAction.Set,
        NewEncryption = new PdfEncryptionOptions { UserPassword = "open sesame" },
    });
}
```

### The element tree underneath

The facade builds the element objects described below; they stay public. They are the lower layer for code that builds trees from data, for custom elements' tests, and an escape hatch: `slot.Element(element)` puts any element object into a slot. A facade document and the same document built from element objects produce identical bytes (a test lays out a 50-page report both ways).

## Element tree

A tree of plain element objects. Elements hold properties and no layout state, so one element may appear several times and a tree can be laid out again; two runs give the same bytes (use a fixed `PdfDocumentWriterOptions.DocumentId`).

- **Containers:** `Column` (top to bottom, spacing, item alignment), `Row` (constant, relative and automatic cell widths, spacing), `Layers` (one primary layer sets the size and may split; the others are laid out at its size on every page it occupies).
- **Box:** margin, border (per side, solid/dashed/dotted), padding, background, corner radius, background colour or [gradient](#gradient-backgrounds), opacity, fixed/min/max width and height, aspect ratio, nine-point alignment of the child. Logical `Start`/`End` insets and borders sit beside the physical ones.
- **Transforms:** `Rotate` (box = bounding box of the turned child), `Scale`, `Translate` (moves the drawing, not the layout space).
- **Leaves:** `Text` (shaped paragraphs on OneFont: fonts with fallback, rich spans, bidi, justification, hyphenation hook (bundled patterns in `RecursiveAI.OnePdf.Hyphenation`), widow/orphan control; see [layout-text.md](layout-text.md)), `Image` (JPEG or PNG; fit width, fit height, contain, cover, stretch), `Canvas` (a callback with the content builder inside a clipped box), `Spacer`.
- **Structure:** `Table` and `ItemList` (below), `Heading`, `PageNumber`, and the navigation wrappers `Link`, `Anchor` and `Bookmark`.
- **Flow rules:** `PageBreak`, `KeepTogether`, `EnsureSpace`, and `Element.KeepWithNext`. A wrapper that only adds something to its child (`Box`, `Link`, `Anchor`, `Bookmark`, `Heading`, `KeepTogether`, `Component`) passes a keep-with-next request on, so it is enough to set it on the heading's text.
- **Columns:** `MultiColumn` pours items through equal columns with a gap, an optional rule and balanced or sequential fill; `ColumnBreak` ends a column (see [Multi-column flow](#multi-column-flow)).
- **Reuse:** derive from `Component` and return a composed tree from `Compose`.
- **Pages:** `LayoutDocument.Pages` holds `LayoutPage` sections. Each has size, orientation, margin, direction, `Header`, `Footer`, `Background`, `Foreground` and `Content`, and starts on a new page. Header and footer are laid out on every page and their height comes off the content area; background and foreground use the whole page. They are written as artifacts (see below). `FirstHeader`/`FirstFooter` replace the ordinary ones on a section's first page, `EvenHeader`/`EvenFooter` on pages with an even displayed number (a first page counts as first); an empty `Spacer` gives no header.
- **Direction:** `LayoutDocument.Direction`, `LayoutPage.Direction` (null: the document's) and `Element.Direction`; see [Tagging and direction](#tagging-and-direction).

## Tables

`Table` has `Columns` (constant, relative or automatic), `HeaderRows`, `Rows`, `FooterRows`, a default `CellPadding` and `CellBorder`, and a `RowBackground` delegate from the body row index to a colour for striping. A `TableRow` holds `TableCell`s (content, `ColumnSpan`, `RowSpan`, padding, background colour or gradient, borders, vertical alignment); cells fill the grid from the start and skip places taken by row spans above.

- **Widths.** Constant columns keep their width. Automatic columns take the widest content of their cells (from `MeasureWidths`: the longest word to the unwrapped line) when there is room, and shrink towards the narrowest content when there is not. Relative columns share what the automatic ones leave by weight, never below their narrowest content. If even the narrowest contents do not fit, all non-constant columns are scaled down and words may spill (a note is recorded). A cell over several columns that needs more than they have asks the rest of the columns that can still grow, evenly. Content decides widths only for automatic columns and for the minimum of relative ones, so a table whose columns are all constant measures nothing, and one with relative columns but no automatic column measures the header, the footer and the first 100 body rows (just enough to keep a relative column from being narrower than its longest word; a later row with a longer word wraps inside its column). Any automatic column makes every body row count. `MeasuredRows` overrides both.
- **Pages.** Header and footer rows are laid out again on each page the table occupies and their height comes off every page; they never split. A page always holds at least the start of a body row besides them. Body rows split between lines of their cells; `TableRow.KeepTogether` moves a row instead (a row taller than a page splits with a note). Rows joined by row spans form one unit that moves whole; a unit taller than a page raises `PdfLayoutException`, as does any cell that cannot fit an empty page (`Page[3] > Content > Table > Row 14 > Cell 2 > Image`; header and footer rows are `Header Row n` and `Footer Row n`).
- **Borders** are collapsed: two cells that meet share one line centred on the grid line, half its width in each cell's inset. The wider border wins; at equal width solid beats dashed beats dotted; then the cell before (above, or at the start side) wins. Cell borders are `Start`/`End`, so a right-to-left table mirrors. Lines on the outside of a table, and at the top and bottom of each page's piece, are drawn inside it.
- **Cost.** The grid and column widths are made once when the table starts; a page then costs its own rows plus header and footer. A 10,000-row table of four cells per row (about 250 pages) lays out in time proportional to its rows: 1,000 rows in 0.30 s, 10,000 rows in 3.0 s (Debug build, shared host), allocating 116 and 1,190 MiB; peak resident about 355 MiB for the whole test process.

## Lists

`ItemList` holds `ListItem`s whose content is any element (a nested list goes in a `Column` after the entry's text). Bulleted lists use `Bullet` (default a bullet at depth 0, an en dash at depth 1, alternating); numbered lists use `NumberStyle` (decimal, letters, roman), `Start` and `Suffix`; `MarkerFormat` and `ListItem.MarkerText`/`Marker` give custom markers. All entries share one marker column as wide as the widest marker, so contents line up on a hanging indent and markers end at the same place. A continued entry repeats no marker.

## Pages, numbers, links

- **Page numbers.** `PageNumber` shows the current number, or a total. A total is not known while pages are laid out: it is a form XObject reserved on first use and drawn on each page, filled once the document (or section) ends, with the number right-aligned inside a box of `ReservedDigits` digits. More digits than reserved are drawn condensed with a note. `Render(PdfDocument)` cannot fill a form later, so it lays the document out once into a throw-away writer to count pages, then renders. Put "Page N of M" in a `Row` with `Spacing`; a trailing space does not count towards a text's width.
- **Numbering.** Each section has `NumberStyle`, `StartNumber` (default: continue) and `LabelPrefix`. The document gets page labels (ISO 32000-2 12.4.2) where numbering does not simply continue. `SectionTotal` counts a section's pages in its own style.
- **Table of contents.** A table shows page numbers that depend on its own length and on pages not yet laid out. A document that contains one is laid out without drawing, as many times as it takes for the entries a run finds (title, level, page, label) to equal the entries the table drew in that run: the first run draws an empty table, the second the numbers of the first, a third confirms; at most six, with a note if they do not settle. One more run then writes the file. The number column is as wide as the widest label seen and never narrows between runs, so a label gaining a digit cannot make the runs chase each other. Only the entry list is kept between runs, so streaming memory is unchanged, and a document without a table pays nothing. Headings and entries are collected from the placed body pages in drawing order (not from table cells, repeated header rows, headers or footers), and each gets a named destination `onepdf-toc-N`. The search for a table before layout follows this package's own containers and components; set `LayoutOptions.TableOfContents` to true for a table it cannot see. An entry is not split across pages: a long title wraps and the number sits on its last line; the leader is a grid anchored at the number's edge, so dots line up from entry to entry. In right-to-left content the numbers move to the left. Structure: `TOC` holding one `TOCI` per entry, each a `Reference` holding a `Link` that owns the link annotation (ISO 32000-2 14.8.4.3.3, 14.8.4.4.2); the leader is an artifact.
- **Links and anchors.** `Link` covers its child's box (every piece of a split child) with a URI or the name of an `Anchor`; anchors are named destinations at the top of their child's first piece, and links may precede them. Undefined and duplicate names are noted. `Bookmark` adds an outline entry (levels lifted to one below the previous entry when they jump). Links and anchors inside `Rotate` or `Scale` are not recorded. Only the streaming writer can record links, destinations, bookmarks and labels; `Render(PdfDocument)` leaves them out with a note.

## Tagging and direction

**Tagged PDF is on by default** (`LayoutOptions.Tagged`; set it false for no structure tree and no marked content at all). The structure tree is written from the element tree while the pages are drawn (ISO 32000-2 14.7, 14.8):

| Element | Structure |
|---|---|
| `Text`, `PageNumber` | `P` (inside a `Heading`, `Link` or list marker the text is that element's own content) |
| `Heading` | `H1`–`H6` from `Level` |
| `Table` | `Table` > `THead`, `TBody`, `TFoot` > `TR` > `TH`/`TD`; header-row cells are `TH` with `/Scope /Column`, `TableCell.IsHeader` makes a `TH` with `/Scope /Row`; spans become `/ColSpan` and `/RowSpan` |
| `ItemList` | `L` (`/ListNumbering`) > `LI` > `Lbl`, `LBody`; nested lists sit in the `LBody` |
| `Image` | `Figure` with `/Alt` from `AltText` |
| `Link` | `Link` holding what it covers; its annotation is tied to it (`OBJR`) and its `/Contents` is `AltText` or the text it covers |
| anything with `AltText` | one `Figure` with that description |

Boxes, backgrounds, borders, rules, a `Canvas` without `AltText`, the debug overlay and anything with `Element.Artifact` set are artifacts. Header, footer, background and foreground are pagination artifacts. An element that splits over pages stays one structure element with content on each page: a paragraph, a table (`TBody`, a row and each cell too, so a row that splits is one `TR`), a list entry. **Repeated table rows:** the header rows are written as structure on the table's first page and as artifacts on the pages that repeat them, the footer rows as structure on the last page and artifacts before it, so a reader that follows the tree meets each row once (a single `THead` and `TFoot`, rather than one per page).

`Element.AltText`, `Element.Language` (BCP 47; `/Lang` is written where it differs from the surrounding content) and `Element.Artifact` are plain properties; `LayoutDocument.Title` and `LayoutDocument.Language` set the information dictionary's title, `/ViewerPreferences /DisplayDocTitle` and the catalog's `/Lang` in `Save` (render into your own writer with `PdfDocumentWriterOptions.Language` and `DisplayDocumentTitle`). An image without `AltText` that is not an artifact is a `LayoutDiagnostic`, once per element. A document built in memory with `Render(PdfDocument)` cannot write a structure tree and is untagged. Tagging costs little time and memory and a good deal of file size. Measured on one run (Release, Ryzen 9 7950X3D, .NET 10.0.11, median of five): a 48-page report with a 500-row table took 145 ms tagged against 138 ms untagged, allocating 183 against 166 MiB, and its file grew from 125 KB to 523 KB (every table cell is a structure element); a 498-page justified book took 1.69 s against 1.68 s, allocating 2,572 against 2,558 MiB, and grew from 1.53 MB to 1.94 MB. Nothing is ever left untagged: every painted mark is tagged content or an artifact, which the tests check by interpreting the written pages. 

**PDF/UA-1 and PDF/A level A.** `Save(stream, new PdfSaveOptions { PdfUA = new PdfUAOptions() })` writes PDF/UA-1, and `PdfA = new PdfAOptions { Conformance = PdfAConformance.A }` writes PDF/A-2a (`Part = PdfAPart.Part3` for 3a); both together give one file for both. The save repairs `/Lang`, `DisplayDocTitle`, tab order, link descriptions and the XMP identification, and **refuses with every violation listed** what the document itself must provide: a title, a figure's `AltText` (mark decoration `Artifact`), heading levels without gaps starting at H1, tables with header cells, and so on. These are checked in the finished file, so this path holds the whole file in memory (see [Output](#output)). `LayoutDocument.Author`, `Subject` and `Keywords` (and `doc.Author(...)` in the fluent API) fill the information dictionary and the XMP packet. This is for documents OnePDF laid out; a clean result is not proof of conformance. Read [pdfua.md](pdfua.md) for what is and is not checked.

**Right to left.** `LayoutDocument.Direction` (or a section's or element's `Direction`) mirrors everything that has a start and an end: rows run right to left, table columns and cell borders, list markers hang on the right with the content to their left, `Start`/`End` alignment, `PaddingStart`/`End`, `MarginStart`/`End`, `BorderStart`/`End`, `Translate` and a paragraph's first-line indent. Paragraphs take their base direction from it and are reordered by UAX #9. An element marked `Direction = LeftToRight` inside a right-to-left document stays left to right. `Left` and `Right` stay physical, and so do page margins.

## Measure, then place

Every element answers one question: *placed in this much width and height, what do you take?* The answer is `Complete`, `Partial` (a continuation object says where the next page resumes), `NoFit`, or `Break`. The offered height is the space left on the page; width is the container's width (infinite when unbounded, for natural sizes). The result is a tree of placed boxes (parent-relative, y down) that is drawn after the page is complete and then dropped.

`Constraint.AtTop` tells an element that nothing sits above it on its page, so a new page would not give it more room. An element that cannot fit when `AtTop` is set throws `PdfLayoutException` itself, so the error names the real culprit: `Page[1] > Content > Column > Item 2 > Image`, with the width and height offered and wanted.

## Pagination core

`Pagination/Flow.cs` knows nothing about element types. A container exposes its children as an `IFlowSource`; `FlowPaginator.Fill` places them top to bottom for one page and returns where to continue. `PageSequencer` repeats that over pages and hands each page out before the next is laid out, which is what lets the streaming writer drop pages as they are done.

Rules:

- Items go in order; spacing separates neighbours on a page and is never added at a page top. An item whose gap alone would pass the bottom starts the next page.
- A partial item ends the page. Its continuation is the first thing on the next one.
- Keep-with-next: an item with the flag is placed only if the chain up to the first item without it fits whole, and that item at least begins, on the same page. Otherwise the page ends before it. At the top of a page the rule cannot help and the chain is placed as it comes.
- A break ends the page; at the top of a page (nothing placed yet) it is ignored. A break with nothing after it still starts a final blank page.
- Items made of countable units (text lines) take the widow and orphan rules (default 2 and 2, `LayoutOptions`): a paragraph starts on a page only with at least *orphans* lines there and leaves at least *widows* for the next; a paragraph shorter than both together is not split.
- `KeepTogether` moves a child that would split to the next page. A child taller than a page splits anyway and a `LayoutDiagnostic` is recorded.
- A `Box` that splits keeps top margin, border and padding on its first piece and the bottom ones on its last; corners are rounded only at those outer ends. A box with a fixed or maximum height, or an aspect ratio, never splits and cuts off taller content with a note.

## Output

`LayoutDocument.Save(Stream)` writes through `PdfDocumentWriter`: each page is committed when laid out. `Render(PdfDocument)` appends to an editable in-memory document; `Render(PdfDocumentWriter)` streams into a writer the caller finishes. Debug mode (`LayoutOptions.Debug`) draws every placed element's box and name in red.

Memory, one-line pages through the streaming path (NativeAOT, linux-x64): 1,000 pages 27 MB peak resident, 10,000 pages 68 MB.
