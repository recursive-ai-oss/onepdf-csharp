# Migrating from QuestPDF

`OnePdf.Layout` generates documents from code in the same style as QuestPDF: you describe pages with nested delegates, chain
decorators on a place that holds one child, and fill it with text, a column, a table and so on. This guide maps the concepts a
QuestPDF program uses to their `OnePdf.Layout` counterparts and lists what differs and what is missing. It is based on QuestPDF's
public documentation ([features overview](https://www.questpdf.com/features-overview.html) and the pages it links to); check those
for details of QuestPDF's behavior. The names here are OnePDF's own, so this is a map, not a drop-in replacement: expect to
rename calls, not to rethink the structure.

`OnePdf.Layout` ships as the package `RecursiveAI.OnePdf.Layout` (see [layout.md](layout.md)).

## What is the same

- A document is composed in a delegate, once; layout and drawing happen at save time.
- Each page has a size, margins and named slots for header, content, footer, background and foreground.
- A slot takes decorators (padding, border, background, alignment, size limits) and one child.
- `Column`, `Row`, `Layers`, `Table` and `List` take delegates that add items.
- Reusable pieces are classes that implement a one-method interface.
- Page numbers, hyperlinks, internal links, page breaks and keep-together rules exist, and a layout that cannot fit throws an
  exception that names the element path.
- Units are points, with helpers for millimetres, centimetres and inches.

## Concept map

| QuestPDF concept | OnePDF.Layout |
|---|---|
| Compose a document | `LayoutDocument.Compose(doc => ...)`, or `PdfDocument.Compose(...)` |
| Generate a file / stream / bytes | `layout.Save(path)`, `layout.Save(stream, PdfSaveOptions)`, `layout.ToBytes()` |
| Page with size and margin | `doc.Page(page => { page.Size(PageSize.A4); page.Margin(2.Cm()); ... })` |
| Header / content / footer / background / foreground slots | `page.Header()`, `Content()`, `Footer()`, `Background()`, `Foreground()` |
| Different first or even page header | `FirstPageHeader()`, `EvenPageHeader()` (and the footers) |
| Padding, border, background, rounded corners | `Padding`, `Border`, `Background`, `Rounded` (per-side variants, `Start`/`End` for direction-aware sides) |
| Width / height, min / max | `Width`, `Height`, `MinWidth`, `MaxHeight` and so on |
| Aspect ratio | `AspectRatio` |
| Alignment | `AlignLeft`, `AlignCenter`, `AlignRight`, `AlignTop`, `AlignMiddle`, `AlignBottom`, plus `AlignStart`/`AlignEnd` |
| Extend to the available space | `ExpandHeight()` (`AlignMiddle` and `AlignBottom` imply it) |
| Column, Row | `Column` with `Item()`; `Row` with `ConstantItem`, `RelativeItem`, `AutoItem` |
| Layers (watermark, letterhead) | `Layers` with `Layer()` and `PrimaryLayer()` |
| Table, spans, repeating header and footer | `Table`: `Columns`, `HeaderRow`, `Row`, `FooterRow`, `cell.Span(columns, rows)` |
| Lists | `List`: `Bulleted`, `Numbered`, custom markers |
| Image | `Image(bytes or path)` with `FitWidth`, `FitHeight`, `Contain`, `Cover`, `Stretch` (JPEG and PNG) |
| Canvas-style drawing | `Canvas(height, surface => ...)` |
| Rotate, scale, offset | `Rotate`, `Scale`, `Translate` |
| Prevent page break, ensure space, page break | `KeepTogether`, `EnsureSpace`, `PageBreak` (and `KeepWithNext`) |
| Text, spans, styles | `Text(...)` with `FontSize`, `Bold`, `FontColor`, ...; `Text(t => t.Span(...))` |
| Page numbers | `t.CurrentPageNumber()`, `t.TotalPages()`, `t.SectionPages()` inside `Text(t => ...)` |
| Hyperlink | `slot.Hyperlink(uri)` |
| Section and internal links | `Anchor(id)`, `LinkToAnchor(id)`, `Bookmark(title, level)` |
| Components | `IComponent` with `Compose(Slot slot)`; `slot.Component(instance)` or `slot.Component<T>()` |
| Content direction | `doc.RightToLeft()`, `page.RightToLeft()`, `slot.RightToLeft()` |
| Debug area, debug pointer | `doc.Debug()` draws every element's box and name; `slot.Name("...")` labels an element in errors |
| Document metadata, PDF/A | `doc.Title(...)`, `doc.Language(...)`; PDF/A and encryption through `Save(stream, PdfSaveOptions)` |

## A page, side by side

A header, a body with a table and a footer with page numbers, written the OnePDF way:

```csharp
LayoutDocument.Compose(doc =>
{
    doc.Title("Invoice 1042").Language("en");
    doc.Page(page =>
    {
        page.Size(PageSize.A4);
        page.Margin(2.Cm());
        page.Header().Text("Acme Corp").FontSize(9).FontColor(Colors.Grey);
        page.Content().Column(col =>
        {
            col.Spacing(8);
            col.Item().Heading(1).Text("Invoice 1042").FontSize(20).Bold();
            col.Item().Table(table =>
            {
                table.Columns(c => { c.Relative(3); c.Constant(70); });
                table.HeaderRow(r => { r.Cell().Text("Item").Bold(); r.Cell().AlignEnd().Text("Price").Bold(); });
                table.Row(r => { r.Cell().Text("Widget"); r.Cell().AlignEnd().Text("12.00"); });
            });
        });
        page.Footer().AlignCenter().Text(t =>
        {
            t.CurrentPageNumber();
            t.Span(" / ");
            t.TotalPages().ReservedDigits(2);
        });
    });
}).Save("invoice.pdf");
```

If you know QuestPDF, the shape is the same: slots, decorator chains and nested delegates. The differences are in the details below.

## Differences that matter first

| | QuestPDF | OnePDF.Layout |
|---|---|---|
| Decorator order | Each decorator wraps what follows (see its documentation for details) | The same, and neighbouring box decorators are merged into one box when their order allows it. The result is identical either way. |
| Text alignment | Chosen on the text | Chosen on the text. Text fills the width it is offered, so `AlignCenter().Text(...)` centres the text itself. |
| Total pages | Part of the text | Width is reserved before the total is known: `ReservedDigits` (default 4). Use `ReservedDigits(2)` in short documents so a centred footer has no gap. |
| Tagged PDF | Opt in for accessibility | On by default: paragraphs, tables, lists, figures and links are tagged; headers, footers, borders and backgrounds are artifacts. Add `Heading(n)`, `AltText`, `Language`; `Tagged(false)` turns it off. |
| Shaping | Fonts and shaping from its native stack | OneFont, managed: OpenType GSUB/GPOS, Arabic, Indic, USE and Thai shapers, UAX #9 bidi, UAX #14 line breaking, per-span font fallback. |
| Fonts | Fonts registered with the library | `doc.Fonts.Add(bytes, family)`; default Liberation Sans from `RecursiveAI.OnePdf.Fonts`, embedded as a subset. System fonts are never used unless you add them, so output is the same on every machine. |
| Dependencies | Native binaries | Pure managed, BCL only, NativeAOT- and trim-compatible |
| Memory | See its documentation | Pages are written as they are laid out; 10,000 one-line pages need about 68 MB peak resident (NativeAOT, linux-x64) |
| Overflow | Layout exception with element path | `PdfLayoutException` with the path (`Page[3] > Content > Table > Row 14 > Cell 2 > Image`), and the space offered and wanted |
| Previewing | A companion app with hot reload | None; render with your own viewer, or with `RecursiveAI.OneImage.Pdf` |

## Licensing

QuestPDF is licensed under its Community, Professional and Enterprise licenses; whether you need to pay depends on who you are
(for example an organisation's annual gross revenue). Read the current terms on its
[license selection guide](https://www.questpdf.com/license/guide.html) and
[legal terms page](https://www.questpdf.com/license/). `OnePdf.Layout` is `MIT OR Apache-2.0` like the rest of OnePDF, with no
revenue test and no registration. The font packages are OFL 1.1 and ship their notices.

## What is missing

Things QuestPDF's public documentation lists that `OnePdf.Layout` does not have yet:

- Shadows and scale-to-fit, flip, unconstrained and shrink wrappers.
- Inline flow of arbitrary elements (an inlined layout) and elements embedded in text; hyperlinks inside a text span (put
  `Hyperlink` on the slot that holds the text instead).
- Multi-column flow, table of contents generation, show-once / skip-once / show-if
  helpers and lazy content. Plain C# `if` statements and loops in your delegates replace the conditional ones; header-only-on-first-page
  is `FirstPageHeader()`.
- A page number inside a paragraph keeps the paragraph on one line; text with page numbers does not wrap.
- SVG images, charts, barcodes and maps (draw them with `Canvas` or pass the rendered image).
- A previewer or hot reload.
- Document operations such as merging and encryption are not part of the layout types, but OnePDF core has them:
  [page operations](page-operations.md), [writing](writing.md) and `Save(stream, PdfSaveOptions)`.
- Author, subject and keywords on `LayoutDocument` (set them on the `PdfDocument` from `ToPdfDocument()`).

## Moving a component

```csharp
sealed class Address(string name, string city) : IComponent
{
    public void Compose(Slot slot) => slot.Column(column =>
    {
        column.Item().Text(name).Bold();
        column.Item().Text(city);
    });
}
```

Use it with `slot.Component(new Address("Ada Lovelace", "London"))`. A component receives the slot it fills, so it chooses its own
content and not the decorators around it.
