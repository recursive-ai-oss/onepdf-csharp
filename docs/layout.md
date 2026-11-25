# Layout

`OnePdf.Layout` generates documents from code: you describe pages with nested delegates, it lays them out (shaped text, tables, lists, images, headers and footers, page numbers) and writes a tagged PDF through OnePDF's streaming writer, one page at a time. It ships as the package `RecursiveAI.OnePdf.Layout` (`dotnet add package RecursiveAI.OnePdf.Layout`), which depends on `RecursiveAI.OnePdf`, `RecursiveAI.OnePdf.Fonts` and `RecursiveAI.OneFont`. [Which package](#which-package) explains `RecursiveAI.OnePdf.Layout.Core`, the same API without bundled fonts.

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
        page.Footer().PageNumberText("Page {page} of {total}").FontSize(9).AlignCenter();
    });
}).Save("report.pdf");
```

## Which package

| | `RecursiveAI.OnePdf.Layout` | `RecursiveAI.OnePdf.Layout.Core` |
|---|---|---|
| API and namespace | `OnePdf.Layout` | the same |
| Default fonts | Liberation Sans, Serif and Mono, registered for you (`Sans`, `Serif`, `Mono`) | none |
| Standard-14 names (`Helvetica`, `Times-Roman`, ...) | resolve, unshaped and not embedded | do not resolve |
| Depends on `RecursiveAI.OnePdf.Fonts` | yes | no |

Use `RecursiveAI.OnePdf.Layout` unless your application ships its own fonts and you do not want the Liberation files (about 4 MB) in your output. Install one of the two, not both: they define the same types in different assemblies. `RecursiveAI.OnePdf.Hyphenation` works with either, because `IHyphenator` lives in the core `OnePdf` assembly. Since 1.24.0 it no longer brings in a layout package: a project that referenced only `RecursiveAI.OnePdf.Hyphenation` and used the layout types through it must add `RecursiveAI.OnePdf.Layout` (or `RecursiveAI.OnePdf.Layout.Core`) itself.

With Core, register at least one font and make it the default, or name it in every text style:

```csharp
byte[] brand = File.ReadAllBytes("BrandSans-Regular.ttf");
LayoutDocument.Compose(doc =>
{
    doc.Fonts.Add(brand, "Brand Sans");
    doc.Fonts.DefaultFamily = "Brand Sans";   // used when a style names no family, or a family that is not registered
    doc.Page(page => page.Content().Text("Set in my own font."));
}).Save("core.pdf");
```

Text that resolves to no registered font, with the registry's default family missing too, throws `PdfFontNotRegisteredException` when the document is laid out or saved. Its message names the requested family and says to register a font with `TextFontRegistry.Add` or use the full package; `RequestedFamilies` and `DefaultFamily` carry the same. `TextFontRegistry.CreateDefault()` and `Shared` return empty registries in Core. The type exists only in Core: in the full package a missing default family still throws `InvalidOperationException`, as before.

## The fluent API

`LayoutDocument.Compose` runs your delegate once and returns the finished description (the C# 14 extension `PdfDocument.Compose` does the same). Nothing is laid out until you save.

- **Document** (`DocumentComposer`): `Title`, `Language`, `Direction`/`RightToLeft`, `Fonts` (the font registry, see [layout-text.md](layout-text.md)), `Debug`, `Tagged(false)`, `WidowsAndOrphans`, `MaxPages`, and `Page` once per section.
- **Page** (`PageComposer`): `Size` (`PageSize.A4` and the other constants, or width and height in points), `Landscape`/`Portrait` (without either call the size is used as given, so `Size(1260, 810)` is a landscape page), `Margin`, `Direction`, page numbering (`NumberStyle`, `StartNumber`, `LabelPrefix`) and the slots `Header`, `Footer`, `FirstPageHeader`/`FirstPageFooter`, `EvenPageHeader`/`EvenPageFooter`, `Background`, `Foreground` and `Content`. Each `Page` call starts a new section on a new page.
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

Other decorators: `ShowOnce`, `SkipOnce`, `ShowIf` (see [Showing content once, or conditionally](#showing-content-once-or-conditionally)), `Rotate`, `Scale`, `ScaleToFit`, `Shrink`, `Unconstrained`, `Translate`, `KeepTogether`, `EnsureSpace`, `KeepWithNext`, `ExpandHeight`, `Name` (the label the debug overlay and errors use). `AlignMiddle` and `AlignBottom` make the slot as tall as the space offered, because there is nothing to centre in otherwise.

- `Rotate` and `Scale` do not split their child: if the content would need a page break inside them, layout throws `PdfLayoutException`. Rotating by 90 degrees makes the child's width the page height, so put a `Width` box inside the rotation.
- Text fills the width it is offered, so `AlignCenter().Text(...)` aligns the text, not a box around it.

#### Fitting and sizing wrappers

```csharp
// A table that is too big for its box is scaled down until it fits; real text, so it still extracts and tags.
col.Item().Width(240).Height(150).ScaleToFit().Table(t => { /* ... */ });

// A box that is as wide as its text, instead of as wide as the column.
col.Item().Shrink().Border(1).Padding(4).Text("Paid");

// A wide element that overflows its parent without moving what follows it.
col.Item().Unconstrained().Width(900).Height(40).Background(Colors.LightGrey).Text("Wider than the page");
```

- **`ScaleToFit(minScale = 0.05)`** scales its child down, uniformly, until it fits the space offered, and never up. Content that already fits is laid out and drawn exactly as without the wrapper (same output bytes). The child is laid out at its natural size first (as wide as the space offered, as tall as it needs, no page break inside; a page break raises `PdfLayoutException`, as in `Scale`). If it is too wide or too tall it is laid out again at a smaller scale in a space wider and taller by the same factor, so text reflows as it would at that scale, and the largest scale that fits is found by bisection. A child that fills whatever width it is offered (an image set to `FitWidth`) cannot get smaller that way, so it is scaled after the first layout instead. When even `minScale` does not fit, layout throws `PdfLayoutException` (or moves to the next page). In flowing content, a child too tall for what is left of a page moves to the next page first, and only on an empty page is it scaled to the height; too wide is scaled wherever it sits. The scale is a transform of the page content: text stays text, extraction, tags and the structure tree are unchanged, and links, anchors and bookmarks inside are placed through the scale. (Inside `Rotate` and non-uniform `Scale`, they are still not recorded.)
- **`Shrink()`** makes the wrapper no larger than its content. The child is measured for its widest unbroken width and laid out in at most that width (or the space offered, if less), so a `Border` and `Padding` box around short text hugs it. The height of every element is already its content's, so there is no per-axis form. A child that splits across pages keeps that width on every page. Put `Shrink()` outside the decoration that should hug the content: `Shrink().Border(1).Text("x")`.
- **`Unconstrained(space = UnconstrainedSpace.None)`** lays its child out as if the space were unlimited: it keeps its natural size, is drawn from the slot's top-left corner, may overflow the parent and is not clipped. By default the parent reserves nothing for it, so siblings are placed and paginated as if it were absent. `UnconstrainedSpace.Offered` reserves the width offered and the child's own height (at most the height offered), for content that should take its normal row but may be wider than the column. A page break inside the child raises `PdfLayoutException`.

### Default text style

`page.DefaultTextStyle(t => t.FontFamily("Serif").FontSize(10).FontColor(Colors.Grey))` sets the style all text on those pages starts from, and `doc.DefaultTextStyle(...)` does the same for every page. The descriptor has `FontFamily`, `FontSize`, `FontColor`, `Bold`/`Weight`, `Italic`, `Underline`, `Strike`, `LetterSpacing`, `LineHeight`, `Palette` and the font-feature methods below.

- **Reach.** Header, footer, first and even page variants, background, foreground and content, including tables, lists (text and markers), page numbers, `PageNumberText`, `DynamicText` and the table of contents. Text drawn by a `Canvas` or an SVG, and the content of a hand-written `Component` subclass, are not covered (an `IComponent` is).
- **Inheritance is field by field**, as between a text and its spans: text that sets its own size keeps it and still takes the page's font and colour; a span that sets a colour takes the text's size. An explicit setting always wins, and a text with no default anywhere looks as before. Elements made directly (`new Text { ... }`) inherit the settings they never set.
- **Order.** A page's settings win over the document's, which win over the built-in defaults (and over the font registry's default family, which applies only when no family is set). Defaults are applied when `Compose` returns, so they may be set before or after the slots are filled. Settings made by several calls add up; a later call for the same setting wins. Font features add up the same way, tag by tag, and a text's own features win over the page's.
- A document without a default text style produces the same bytes as before.

### Font features

A text or span can switch OpenType features on or off: `Feature("smcp")`, `Feature("liga", false)`, `Feature("ss01", 2)` (a value picks an alternate), `Ligatures(false)` (turns `liga` and `clig` off, so "fi" is two glyphs) and `Kerning(false)`. `TextStyle.Features` takes `TextFeature` values (`TextFeature.On("tnum")`, `TextFeature.Off("kern")`) for the paragraph engine. Features not mentioned keep the shaper's defaults (`liga`, `clig`, `calt`, `kern`, the script's own features); a tag the font lacks is ignored; a later setting of a tag replaces an earlier one. They go to the shaper, so advances, clusters and line breaking follow. Extraction stays correct: a ligature, or a discretionary one switched on with `Feature("dlig")`, maps back to its letters through `/ToUnicode` and `/ActualText` as before, and the text is the same with the feature on or off.

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

`Text("...")` returns a `TextDescriptor` with `FontSize`, `FontFamily`, `FontColor`, `Bold`, `Italic`, `Underline`, `Strike`, `LetterSpacing`, `LineHeight`, `Justify`, `AlignStart`/`AlignCenter`/`AlignEnd`, `FirstLineIndent`, `Language` and `Hyphenator`. `Text(t => ...)` builds rich text from `Span`, `Line`, `CurrentPageNumber`, `TotalPages` and `SectionPages`; a span has the same formatting methods plus `Superscript`, `Subscript`, `Hyperlink(uri)` and `LinkToAnchor(id)`. A link on a span covers exactly its glyphs: one annotation rectangle per line it wraps over (and per page it is split over), each tied to a `Link` structure element that holds the span's text and is the annotation's `/Contents`; nothing is drawn, so add `Underline` and a colour. Settings made on the descriptor are the default for its spans.

`PageNumberText("{page}/{total}")` is the one-line way to write "3/17" or "Page 3 of 17". `{page}` is the page's number in its section's numbering style, `{total}` the document's page count in decimal and `{sectionTotal}` the section's count in the section's style; `{{` and `}}` are literal braces, and an unknown placeholder or an unbalanced brace throws `ArgumentException` at the call. The element is one line as wide as its slot, drawn once the counts are known as a single shaped run, aligned by its real width (`AlignEnd` by default; `AlignStart`, `AlignCenter`, `AlignLeft` and `AlignRight` too; `FontSize`, `FontColor`, `Bold`, `Weight`, `Palette`, `FontFamily`, `Italic`, `LetterSpacing` and `LineHeight` style it). There is no reserved gap and no squeeze, and kerning across the whole text holds. Only in a slot that offers no width (an auto-sized row item) are the placeholders measured as `ReservedDigits` zeros each (default 4). Text wider than its box is condensed with a note. In right-to-left layouts start and end mirror while the digits keep their order. It is tagged as a paragraph with the final text, or an artifact in a header or footer, and the document is still laid out once.

The older route, a page number inside a rich `Text`, still works. A page number is shaped like any other text. A text with page numbers that fits on one line is laid out as that line, each number exactly as wide as its digits. A text that does not fit (or has a line break or an inline object) is an ordinary paragraph: it wraps, justifies and splits across pages by the usual rules, and each number is an unbreakable object in it. Lines are broken once, before the page count is known, so every number reserves room for `ReservedDigits` digits (default 4), starting at the left edge of its room; a shorter number leaves a gap after it, and more digits than reserved are squeezed into the room with a note. The current number is drawn on the page its line lands on, so a paragraph split by a page break shows the right number on each page; a total is filled in when the last page is done. In running text use `ReservedDigits(2)` or `(3)` for tighter gaps. Details of shaping, fallback fonts, bidirectional text and hyphenation are in [layout-text.md](layout-text.md).

### Inline objects

An icon, a small picture, a badge or a drawn checkbox can sit in a line of text and flow with it. In `Text(t => ...)`, `Inline(slot => ...)` puts a small element tree in the line (the slot takes any content, so a padded, bordered box with its own text works), `InlineImage(bytes, width, height)` a JPEG or PNG of a fixed size, and `InlineCanvas(width, height, surface => ...)` a drawing. Each returns an `InlineDescriptor` for the object's size, alignment, description and link.

```csharp
LayoutDocument.Compose(doc => doc.Page(page =>
{
    page.Content().Text(t =>
    {
        t.Span("Build ");
        t.InlineCanvas(10, 10, s =>
        {
            s.Builder.SetFillRgb(0.1, 0.6, 0.2);
            s.Builder.Ellipse(0, 0, s.Width, s.Height);
            s.Builder.Fill();
        }).AltText("passing").AlignTextBottom();
        t.Span(" passed; the release notes ");
        t.Inline(s => s.Background(Colors.LightGrey).Padding(3, 1).Text("NEW").FontSize(8).Bold()).AltText("new");
        t.Span(" and a logo ");
        t.InlineImage(File.ReadAllBytes("logo.png"), 12, 12).AltText("Company logo").AlignMiddle();
        t.Span(" flow with the words around them.");
    });
})).Save("inline.pdf");
```

- **Size.** A picture or canvas has the size you give. An `Inline` element takes its content's widest natural width (no wrapping) and the height that gives; `Width`, `Height` and `MaxWidth` override that, and with a `MaxWidth` the content wraps inside it. The element is laid out once and never splits: a page break inside it raises `PdfLayoutException`.
- **Vertical alignment.** `Baseline(offset)` (the default) puts the object's own baseline on the line's baseline, raised by `offset` points; a picture's baseline is its bottom edge, and an element that contains text takes the baseline of its first line, so a badge's text lines up with the words around it (`InnerBaseline` sets it by hand). `AlignTop` and `AlignBottom` align to the top and bottom of the line box; `AlignMiddle` centres the object on the middle of the text (halfway between the font's ascent and descent); `AlignTextTop` and `AlignTextBottom` put its edges at the font's ascent and descent. The font is that of the text the object is in, and `FontSize` and the other format methods of the descriptor change it for that spot.
- **Line height.** A line grows to hold an object taller than its text, above the baseline, below it, or (for top and bottom) at the side the alignment names; a line is never shorter than its text would make it. `FixedLineHeight()` keeps the height the text gives, and a taller object sticks out into the lines around it.
- **Breaking and justification.** An object is one unbreakable unit, as wide as it is, with a line break allowed before and after it (it is U+FFFC, line break class CB in UAX #14), by first fit and by total fit alike. `KeepWithNeighbours()` removes both breaks. Justification widens the spaces and leaves the object alone. An object wider than the line sits alone on a line that overflows, and a note says so. Underlines and strike-throughs of the text around it stop at the object.
- **Right to left.** The object has the direction of its position (bidi class ON, UAX #9), so between right-to-left words it is placed with them.
- **Tagging and extraction.** In a tagged document an object with `AltText` is a `Figure` inside the paragraph, between the paragraph's content, with `/Alt`; the text extractor returns the description in its place (an invisible space under `/ActualText`, ISO 32000-2 14.9.4), and never a U+FFFC. `Artifact()` leaves the object out of the tree and out of extraction; a drawn canvas without a description is an artifact. A picture with neither description nor `Artifact()` is reported, as `Image` is. Put an `AltText` on the object (not only inside its content) when extraction should return it.
- **Links.** `Hyperlink(uri)` and `LinkToAnchor(id)` on the descriptor link the object's box; inside an `Inline` slot, `Hyperlink`, `LinkToAnchor` and `Anchor` work as they do anywhere. The link annotation is tied to a `Link` structure element that holds the figure.
- **Limits.** One text can hold as many objects as it has characters (the paragraph limit of 16 million); a text with page numbers that does not fit on one line can hold them too. Outside layout, `TextParagraph` takes `TextSpan.ForInline(new InlineBox(width, height) { Paint = ... })`, and `TextRenderer.Draw` draws it; see [layout-text.md](layout-text.md#inline-objects).

### Rows, columns, layers, lists, images

- `Column(c => ...)` with `Spacing`, `ItemAlignment` and `Item()`; `Row(r => ...)` with `Spacing`, `AlignTop`/`AlignMiddle`/`AlignBottom`, and `ConstantItem(width)`, `RelativeItem(weight)` or `AutoItem()`.
- `Layers(l => ...)`: `PrimaryLayer()` sets the size and may span pages; `Layer()` slots (a watermark, a frame) are laid out at its size.
- `List(l => ...)`: `Bulleted`, `Numbered(style)`, `Bullet`, `Start`, `Suffix`, `MarkerFormat`, marker font, size, colour, width and gap, and `Item()` (or `Item("custom marker")`). Lists nest by putting a `List` inside an item.
- `Image(bytes | path | PdfCreatedImage)` for JPEG and PNG with `FitWidth` (the default), `FitHeight`, `Contain`, `Cover`, `Stretch`, `Dpi` and `Align`. Give figures `AltText(...)`, or `Artifact()` for decoration.
- `Svg(bytes | string | SvgImage)` places an SVG as vector content, with the same fits, `Dpi`, `Align` and `AltText` (the SVG title is the default); see [layout-svg.md](layout-svg.md).
- `Canvas(height, surface => ...)` hands you a clipped drawing surface; `surface.DrawSvg(svg, x, y, width, height)` draws an SVG into it.

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

### Barcodes

`QrCode(value)` and `Barcode(symbology, value)` draw a barcode as vector rectangles: no image, and no font unless human-readable text is on. The encoders are part of the package and follow the standards: QR Code (ISO/IEC 18004), Code 128 and GS1-128 (ISO/IEC 15417, GS1 General Specifications), EAN-13, EAN-8 and UPC-A (ISO/IEC 15420) and Code 39 (ISO/IEC 16388).

```csharp
page.Content().Column(col =>
{
    col.Spacing(10);
    col.Item().Width(90).QrCode("https://example.com/invoice/1042").ErrorCorrection(QrErrorCorrection.Quartile).Contain();
    col.Item().Barcode(BarcodeSymbology.Code128, "ORDER-2041").BarHeight(36).HumanReadableText();
    col.Item().Barcode(BarcodeSymbology.Ean13, "400638133393");            // the check digit is added
    col.Item().Barcode(BarcodeSymbology.Gs1128, "(01)09501101530003(17)150119(10)AB-123");
});
```

- **QR Code.** Byte mode (UTF-8 by default, or ISO 8859-1, or UTF-8 announced by an ECI header with `CharacterSet`), numeric and alphanumeric segments chosen by a cost search, versions 1 to 40, levels L, M (the default), Q and H. The smallest version that holds the data is used; `MinVersion`, `MaxVersion` or `Version(n)` bound it. All eight masks are scored with the penalty rules of ISO/IEC 18004 7.8.3 and the lowest wins (the first on a tie). Data that does not fit version 40 at the chosen level raises `PdfBarcodeException`.
- **Code 128.** Code sets A, B and C are switched, and shifted, for the fewest symbol characters (characters 0 to 127; FNC4 for higher characters is not supported). GS1-128 takes application identifiers in parentheses: the symbol starts with FNC1, FNC1 follows the data of every variable-length identifier that is not last, and the lengths of the fixed-length identifiers (00, 01, 02, 11 to 13, 15 to 17, 20, 31 to 36, 41) are checked. Check digits inside GS1 data are not verified.
- **EAN-13, EAN-8, UPC-A.** Give the digits without the check digit (it is added) or with it (it is verified; a wrong one raises `PdfBarcodeException`). Guard bars run lower than the others; the digits are set in the layout of the standard, the first (and for UPC-A the last) outside the bars.
- **Code 39.** The 43 characters `0-9 A-Z - . space $ / + %`; start and stop `*` are added; `Code39CheckDigit()` adds the modulo-43 check character. Narrow to wide is 1 : 3.
- **Size.** Everything is measured in modules (the narrowest bar, or one dark square of a QR Code). All modules of a symbol have the same size and it is never rounded, so a symbol is exactly a whole number of modules wide. `ModuleSize(points)` sets it; `Contain()` takes the largest size at which the symbol, quiet zone included, fits the width (and, for QR Code, the height) offered; otherwise a module is 2 pt for QR Code and 1 pt for the linear symbologies. A symbol too wide or tall for its space is shrunk to fit, with a note. Linear bars are `BarHeight(points)` tall, 40 modules by default (with `Contain` and a bounded height, what is left of it).
- **Quiet zone.** The margin the standard requires is part of the element's box: 4 modules for QR Code, 10 for Code 128 and Code 39, 11 left and 7 right for EAN-13, 9 for UPC-A and 7 for EAN-8. `QuietZone(modules)` overrides it on every side (zero is allowed). With human-readable digits the outside digits of EAN-13 and UPC-A sit in the quiet zone, so that side is at least 7 modules.
- **Colours.** `Colors(foreground, background)`: bars and text, and an optional fill for the whole box. Readers need contrast and a light background; the library does not check it.
- **Human-readable text.** `HumanReadableText()` (on by default for EAN and UPC) sets the characters under a linear symbol in the document's text pipeline, with the page's default font family (`TextFont(family, size)` sets them for this barcode; 9 pt default). EAN and UPC digits go in their groups; others are centred. The size shrinks, for all groups alike, when the digits would not fit under the bars. Without a registered font (`RecursiveAI.OnePdf.Layout.Core`) the usual `PdfFontNotRegisteredException` is raised; a QR Code needs no font.
- **Tagging.** In a tagged document a barcode is a `Figure` whose alternate text is the value, or `AltText(...)`; `Artifact()` makes it decoration. The human-readable text is an artifact inside it (the figure carries the value). Untagged output has no structure.
- **Errors.** A character the symbology cannot encode, a wrong length or check digit, a QR version too small, or too much data raise `PdfBarcodeException` (with the `Symbology` and a message naming the problem) when the document is laid out, never a symbol that does not read.
- **Element classes.** `Barcode` (properties for everything above) can be put into a slot with `slot.Element(...)` or built into a tree like any element.

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

`KeepTogether`, `KeepWithNext`, `EnsureSpace(height)` and `PageBreak()` control breaks (rules and defaults under [Pagination core](#pagination-core)). `Hyperlink(uri)`, `LinkToAnchor(id)`, `Anchor(id)` and `Bookmark(title, level)` make links and the outline. `Heading(level)`, `AltText`, `Language` and `Artifact` set the tags; tagging is on by default. `Aside()`, `Footnote()`, `Title()` and `Group(type)` add the PDF 2.0 grouping types `Aside`, `FENote`, `Title`, `Sect`, `Div`, `Part` and `BlockQuote` (`StructureGroup`); the 2.0-only ones can be saved as PDF/UA-2 but not as PDF/UA-1 (see [pdfua.md](pdfua.md)).

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

### Showing content once, or conditionally

`ShowOnce()`, `SkipOnce()` and `ShowIf(...)` wrap the content in the slot and decide whether it is there. What they do to content that is left out is the same for all three: it takes **no space** (a column's spacing is not added around it either), draws nothing and writes **no structure elements**, so a tagged file has nothing for it; a header that is wholly hidden on a page gives the body its height back. Content that is shown keeps its normal tagging, and bands stay artifacts.

**What "once" means.** Layout is stateless: the answer depends on where the content is laid out, never on how often it was laid out before. Two kinds of content repeat:

| Content | First time | Repeat |
|---|---|---|
| `Header`, `Footer`, `Background`, `Foreground` of a page (also the first/even variants) | the first page of the section | every later page of the same section; the next section's first page is a first time again |
| Header and footer rows of a table | the piece that starts the table, wherever it lands (a table moved whole to the next page is still first) | the pieces that continue it |

Everywhere else (flowing content, body rows) nothing repeats: `ShowOnce` shows its content and `SkipOnce` hides it. A paragraph or other element that splits over pages is one showing, so `ShowOnce` never cuts a split element off after its first piece, and a `SkipOnce` that is shown (in a repeat) shows all its pieces.

```csharp
page.Header().ShowOnce().Text("Quarterly report");          // first page of the section only
page.Footer().SkipOnce().Text("Continued from the first page"); // every page after the first
page.Header().Column(h =>
{
    h.Item().Text("Acme Corp");
    h.Item().SkipOnce().Text("(continued)");                // only where the header repeats
});
// In a table header cell: shown on the first piece only
cell.Column(c => { c.Item().Text("Item"); c.Item().ShowOnce().Text("see note 3").FontSize(8); });
```

`ShowIf(bool)` includes or omits its content. `ShowIf(Func<PagePosition, bool>)` decides per page while the content is laid out; `PagePosition` has `Index`, `Section`, `IndexInSection`, `Number` and the shortcuts `IsFirstPage`, `IsFirstInSection` and `IsEvenNumber`. For a header or footer it is the page the band goes on; for flowing content it is the page the element is placed on (an element that moves to the next page is asked again there). The predicate may run more than once per page and should be free of side effects. "Last page" is not offered, because the page count is not known while pages are laid out; use `DynamicText` or a `FirstPageFooter`/`EvenPageFooter` for that. The element objects are `ShowOnce`, `SkipOnce` and `ShowIf` (`Child`, `Condition`, `Predicate`).

Use `FirstPageHeader()`/`EvenPageHeader()` when the first or even page needs *different* content; use these wrappers when only part of a band differs.

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

### Values that depend on the page

Headers and footers are laid out for every page, so they can show numbers that depend on what landed on that page: "Brought forward" at the head and "Carried forward" at the foot of a statement, a page subtotal, the first and last entry of a dictionary page, the section a page is in. Content contributes while it is placed, and a `DynamicText` formats the result when the page is drawn.

- `Accumulate(account, amount)` wraps content (a table cell is the usual place) and adds a `decimal` to a named account on the page where the content starts. `Marker(key, text)` records a text the same way. Neither draws anything or changes size. Because they ride on the content, a row that a keep rule or a page break moves to the next page takes its amount with it.
- `DynamicText(v => ...)` is one line of text whose string comes from a callback given the page's `PageValues`: `Sum(account)` (this page), `Total(account)` (up to the end of this page, what a footer carries forward), `BroughtForward(account)` (up to the end of the previous page, zero on the first), `First(key)` and `Last(key)` (on this page, in reading order, else null), `Current(key)` (the last one at or before the end of this page) and `Entering(key)` (in force when the page starts), plus `PageIndex`, `PageNumber`, `PageLabel` and `SectionIndex`. Culture and number format are the callback's. The style methods are `FontSize`, `FontColor`, `Bold`, `Weight`, `FontFamily`, `Sample` (a typical text, for sizing in an unbounded space) and `AlignStart`, `AlignEnd`, `AlignLeft`, `AlignRight` and `AlignCenter`; a text in an aligned slot takes that alignment, as `Text` does.
- Totals run across sections; use another account name to start again. Sums are `decimal`, so money adds exactly; a sum beyond `decimal` range is a `PdfLayoutException`.

```csharp
var culture = System.Globalization.CultureInfo.InvariantCulture;
LayoutDocument.Compose(doc =>
{
    doc.Title("Account statement").Language("en-GB");
    doc.Page(page =>
    {
        page.Size(PageSize.A5);
        page.Margin(28);
        page.Header().DynamicText(v => $"Statement, {v.First("month")} - brought forward {v.BroughtForward("amount").ToString("N2", culture)}");
        page.Footer().DynamicText(v => $"Carried forward {v.Total("amount").ToString("N2", culture)}").AlignEnd().Bold();
        page.Content().Table(t =>
        {
            t.Columns(c => { c.Constant(60); c.Relative(); c.Constant(70); });
            for (int i = 1; i <= 90; i++)
            {
                int n = i;
                t.Row(r =>
                {
                    r.KeepTogether();
                    r.Cell().Marker("month", n <= 45 ? "October" : "November").Text($"{n:00}.{(n <= 45 ? 10 : 11)}");
                    r.Cell().Text($"Payment {n}");
                    r.Cell().Accumulate("amount", n * 12.35m).AlignEnd().Text((n * 12.35m).ToString("N2", culture));
                });
            }
        });
    });
}).Save("statement.pdf");
```

How it works. The height of a `DynamicText` is one line of its font, fixed before the page is known (a header's height decides how much room the body has), so the callback cannot change the layout. Contributions are collected from the placed pieces of the page's flowing content, walked in reading order (columns left to right, or right to left in right-to-left documents), after page breaking and before the page is drawn; the header, footer, background and foreground do not contribute, but table header rows repeated on each page do (they are placed on each page). An element that splits over pages contributes on the page where it starts. The callback runs once for each page that is written: the count-only runs a table of contents makes do not draw, so nothing is counted twice, and a page's values are known when the page is written, which keeps streaming output streaming. A text wider than its box is squeezed horizontally, with a note, rather than wrapped. In a header, footer, background or foreground it is an artifact like the rest of that band; in the body it is a tagged paragraph. The callback should have no side effects and must be deterministic for output to be.

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

`doc.RightToLeft()` mirrors rows, table columns, start/end alignment and insets, and sets the base direction of text. Fonts are registered on `doc.Fonts` (grouped by family, weight and style); the default is Liberation Sans from `RecursiveAI.OnePdf.Fonts`, embedded as a subset (`RecursiveAI.OnePdf.Layout.Core` has no default: see [Which package](#which-package)). System fonts are never used unless you add them.

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

### Print geometry

A page size is the trim size. Bleed and slug go around it, so margins, headers, footers and content are laid out exactly as without them and nothing moves on the trimmed page; the media box grows by bleed plus slug on every side, the bleed box by the bleed, and the trim box sits inside (ISO 32000-2 14.11.2; see [page-geometry.md](page-geometry.md) for the crop box choice). Links, bookmarks and anchors follow the trim box.

```csharp
LayoutDocument layout = LayoutDocument.Compose(doc =>
{
    doc.Title("Brochure").Language("en");
    doc.Print(bleed: 9, slug: 18);                       // every page that does not set its own
    doc.ViewerPreferences(new PdfViewerPreferences { PrintScaling = PdfPrintScaling.None, NumCopies = 2 });
    doc.Page(page =>
    {
        page.Size(PageSize.A5);                          // the trim size
        page.Background().Element(new Box { Background = LayoutColor.Black });   // runs into the bleed
        page.Content().Text("Hello");
    });
    doc.Page(page => page.Print(new PrintSetup { Bleed = 3, ArtBox = new PdfRectangle(10, 10, 200, 300) }));
});
```

- `PrintSetup` (on `LayoutDocument.Print` and per section as `LayoutPage.Print`, or `Print(bleed, slug)` / `Print(setup)` on the composers) holds `Bleed`, `Slug`, `BleedBackground`, and optional explicit `CropBox` and `ArtBox`. The explicit boxes are in points from the trim box's lower-left corner with y up, so a negative origin reaches into the bleed. A section's setup replaces the document's.
- **Backgrounds bleed by default.** With `BleedBackground` (default true) and a bleed above zero, a section's `Background` is laid out over the trim size grown by the bleed and drawn from the bleed box's corner. Its content (a centred logo, say) is therefore laid out against the larger area; set `BleedBackground = false` to keep it on the trim size. Content, headers, footers and the foreground never bleed.
- **Per side.** `BleedSides` and `SlugSides` (`PdfPrintMargins`: left, bottom, right, top) replace `Bleed` and `Slug` when set; `Print(new PdfPrintMargins(3, 4, 5, 6), new PdfPrintMargins(30))` is the fluent form. Content, links and anchors stay fixed relative to the trim box, and a bleeding background is laid out over the trim size grown by each side's bleed. With equal sides the bytes equal the uniform setup's.
- **Printer's marks.** `PrintSetup.Marks` (a `PdfPrinterMarks`; `Print(bleed, slug, marks)` on the composers) draws crop and registration marks in the slug of every page, outside the bleed box, in registration colour, as page artifacts, so tagged output, PDF/UA-1 and PDF/A-2a stay conformant (checked with veraPDF). A slug smaller than `marks.MinimumSlug` on any side throws `ArgumentException` when the document is laid out. Without `Marks` nothing is drawn in the slug. See [page-geometry.md](page-geometry.md).
- **Guide colours.** `CropBoxColor`, `BleedBoxColor`, `TrimBoxColor` and `ArtBoxColor` (`PdfBoxColorInfo`) write `/BoxColorInfo` entries (ISO 32000-2 14.11.2.2) for the boxes, with the same validation and bytes as `PdfPage.SetBoxColorInfo` and the writer's `SetBoxColorInfo`.
- `LayoutDocument.ViewerPreferences` (`doc.ViewerPreferences(...)` on the facade) writes the catalog's `/ViewerPreferences` ([print-preferences.md](print-preferences.md)); `DisplayDocTitle` is still set from the title when the preferences leave it unset, and a `PdfDocumentWriterOptions.ViewerPreferences` passed to a save wins. `Render(PdfDocument)` applies them to the target; with `Render(PdfDocumentWriter)` pass them in the writer's options.
- Tagging, PDF/UA and PDF/A saves work with print geometry set. Keep the media box within the 3 to 14400 point limit of ISO 19005-2 6.1.13.

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
- **Pages:** `LayoutDocument.Pages` holds `LayoutPage` sections. Each has size, orientation (optional: until `Orientation` is assigned the size is used as given), margin, direction, `Header`, `Footer`, `Background`, `Foreground` and `Content`, and starts on a new page. Header and footer are laid out on every page and their height comes off the content area; background and foreground use the whole page. They are written as artifacts (see below). `FirstHeader`/`FirstFooter` replace the ordinary ones on a section's first page, `EvenHeader`/`EvenFooter` on pages with an even displayed number (a first page counts as first); an empty `Spacer` gives no header.
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

- **Page number text.** `PageNumberText` (`Format` with `{page}`, `{total}`, `{sectionTotal}`) keeps one form XObject per page, keyed by the page and the style, reserved at layout with the full width offered and painted when the count is known: at the end of the document, or of the section when only `{sectionTotal}` is used. The painted form shapes the whole formatted string as one left-to-right run and positions it by its natural width, so it matches an ordinary `Text` of the final string in the same box. A format with only `{page}` is laid out as ordinary text. The section count needed by a document-end form comes from `LayoutContext.SectionCounts` (or, for `Render(PdfDocument)`, from its counting run).
- **Page numbers.** `PageNumber` shows the current number, or a total. A total is not known while pages are laid out: it is a form XObject reserved on first use and drawn on each page, filled once the document (or section) ends, with the number right-aligned inside a box of `ReservedDigits` digits. More digits than reserved are drawn condensed with a note. `Render(PdfDocument)` cannot fill a form later, so it lays the document out once into a throw-away writer to count pages, then renders. Put "Page N of M" in a `Row` with `Spacing`; a trailing space does not count towards a text's width.
- **Numbering.** Each section has `NumberStyle`, `StartNumber` (default: continue) and `LabelPrefix`. The document gets page labels (ISO 32000-2 12.4.2) where numbering does not simply continue. `SectionTotal` counts a section's pages in its own style.
- **Table of contents.** A table shows page numbers that depend on its own length and on pages not yet laid out. A document that contains one is laid out without drawing, as many times as it takes for the entries a run finds (title, level, page, label) to equal the entries the table drew in that run: the first run draws an empty table, the second the numbers of the first, a third confirms; at most six, with a note if they do not settle. One more run then writes the file. The number column is as wide as the widest label seen and never narrows between runs, so a label gaining a digit cannot make the runs chase each other. Only the entry list is kept between runs, so streaming memory is unchanged, and a document without a table pays nothing. Headings and entries are collected from the placed body pages in drawing order (not from table cells, repeated header rows, headers or footers), and each gets a named destination `onepdf-toc-N`. The search for a table before layout walks every container of this package (including `MultiColumn`) and components, and asks any element that holds others through one internal hook; set `LayoutOptions.TableOfContents` to true for a table it cannot see. An entry is not split across pages: a long title wraps and the number sits on its last line; the leader is a grid anchored at the number's edge, so dots line up from entry to entry. In right-to-left content the numbers move to the left. Structure: `TOC` holding one `TOCI` per entry, each a `Reference` holding a `Link` that owns the link annotation (ISO 32000-2 14.8.4.3.3, 14.8.4.4.2); the leader is an artifact.
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

**PDF/UA-1 and PDF/A level A.** `Save(stream, new PdfSaveOptions { PdfUA = new PdfUAOptions() })` writes PDF/UA-1, and `PdfA = new PdfAOptions { Conformance = PdfAConformance.A }` writes PDF/A-2a (`Part = PdfAPart.Part3` for 3a); both together give one file for both. The save repairs `/Lang`, `DisplayDocTitle`, tab order, link descriptions and the XMP identification, and **refuses with every violation listed** what the document itself must provide: a title, a figure's `AltText` (mark decoration `Artifact`), heading levels without gaps starting at H1, tables with header cells, and so on. These are checked in the finished file, so this path holds the whole file in memory (see [Output](#output)). `LayoutDocument.Author`, `Subject` and `Keywords` (and `doc.Author(...)`, `doc.Subject(...)` and `doc.Keywords(...)` in the fluent API) fill the information dictionary (`Author`, `Subject`, `Keywords`) and the XMP packet (`dc:creator`, `dc:description`, `pdf:Keywords`); `doc.Keywords(IEnumerable<string>)` and `LayoutDocument.SetKeywords` take a list and join it with `, `. A document that sets none of them is written exactly as before. This is for documents OnePDF laid out; a clean result is not proof of conformance. Read [pdfua.md](pdfua.md) for what is and is not checked.

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
