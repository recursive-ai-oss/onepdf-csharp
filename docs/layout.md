# Layout

`OnePdf.Layout` lays out an element tree over pages and writes the result through OnePDF's content builder. It is not released yet and lives in `OnePdf.Layout.slnx`; its element types and properties may still change.

## Model

A tree of plain element objects. Elements hold properties and no layout state, so one element may appear several times and a tree can be laid out again; two runs give the same bytes (use a fixed `PdfDocumentWriterOptions.DocumentId`).

- **Containers:** `Column` (top to bottom, spacing, item alignment), `Row` (constant, relative and automatic cell widths, spacing), `Layers` (one primary layer sets the size and may split; the others are laid out at its size on every page it occupies).
- **Box:** margin, border (per side, solid/dashed/dotted), padding, background, corner radius, opacity, fixed/min/max width and height, aspect ratio, nine-point alignment of the child. Logical `Start`/`End` insets and borders sit beside the physical ones.
- **Transforms:** `Rotate` (box = bounding box of the turned child), `Scale`, `Translate` (moves the drawing, not the layout space).
- **Leaves:** `Text` (shaped paragraphs on OneFont: fonts with fallback, rich spans, bidi, justification, hyphenation hook, widow/orphan control; see [layout-text.md](layout-text.md)), `Image` (JPEG or PNG; fit width, fit height, contain, cover, stretch), `Canvas` (a callback with the content builder inside a clipped box), `Spacer`.
- **Structure:** `Table` and `ItemList` (below), `Heading`, `PageNumber`, and the navigation wrappers `Link`, `Anchor` and `Bookmark`.
- **Flow rules:** `PageBreak`, `KeepTogether`, `EnsureSpace`, and `Element.KeepWithNext`. A wrapper that only adds something to its child (`Box`, `Link`, `Anchor`, `Bookmark`, `Heading`, `KeepTogether`, `Component`) passes a keep-with-next request on, so it is enough to set it on the heading's text.
- **Reuse:** derive from `Component` and return a composed tree from `Compose`.
- **Pages:** `LayoutDocument.Pages` holds `LayoutPage` sections. Each has size, orientation, margin, direction, `Header`, `Footer`, `Background`, `Foreground` and `Content`, and starts on a new page. Header and footer are laid out on every page and their height comes off the content area; background and foreground use the whole page. They are written as artifacts. `FirstHeader`/`FirstFooter` replace the ordinary ones on a section's first page, `EvenHeader`/`EvenFooter` on pages with an even displayed number (a first page counts as first); an empty `Spacer` gives no header.
- **Direction:** `LayoutPage.Direction` and `Element.Direction`. `Start`/`End` alignment and insets flip, and rows run right to left. Text paragraphs take the direction of the page or element and are reordered by UAX #9.

## Tables

`Table` has `Columns` (constant, relative or automatic), `HeaderRows`, `Rows`, `FooterRows`, a default `CellPadding` and `CellBorder`, and a `RowBackground` delegate from the body row index to a colour for striping. A `TableRow` holds `TableCell`s (content, `ColumnSpan`, `RowSpan`, padding, background, borders, vertical alignment); cells fill the grid from the start and skip places taken by row spans above.

- **Widths.** Constant columns keep their width. Automatic columns take the widest content of their cells (from `MeasureWidths`: the longest word to the unwrapped line) when there is room, and shrink towards the narrowest content when there is not. Relative columns share what the automatic ones leave by weight, never below their narrowest content. If even the narrowest contents do not fit, all non-constant columns are scaled down and words may spill (a note is recorded). A cell over several columns that needs more than they have asks the rest of the columns that can still grow, evenly. Every body row is measured unless `MeasuredRows` says otherwise.
- **Pages.** Header and footer rows are laid out again on each page the table occupies and their height comes off every page; they never split. A page always holds at least the start of a body row besides them. Body rows split between lines of their cells; `TableRow.KeepTogether` moves a row instead (a row taller than a page splits with a note). Rows joined by row spans form one unit that moves whole; a unit taller than a page raises `PdfLayoutException`, as does any cell that cannot fit an empty page (`Page[3] > Content > Table > Row 14 > Cell 2 > Image`; header and footer rows are `Header Row n` and `Footer Row n`).
- **Borders** are collapsed: two cells that meet share one line centred on the grid line, half its width in each cell's inset. The wider border wins; at equal width solid beats dashed beats dotted; then the cell before (above, or at the start side) wins. Cell borders are `Start`/`End`, so a right-to-left table mirrors. Lines on the outside of a table, and at the top and bottom of each page's piece, are drawn inside it.
- **Cost.** The grid and column widths are made once when the table starts; a page then costs its own rows plus header and footer. A 10,000-row table of four cells per row (about 250 pages) lays out in time proportional to its rows: 1,000 rows in 0.30 s, 10,000 rows in 3.0 s (Debug build, shared host), allocating 116 and 1,190 MiB; peak resident about 355 MiB for the whole test process.

## Lists

`ItemList` holds `ListItem`s whose content is any element (a nested list goes in a `Column` after the entry's text). Bulleted lists use `Bullet` (default a bullet at depth 0, an en dash at depth 1, alternating); numbered lists use `NumberStyle` (decimal, letters, roman), `Start` and `Suffix`; `MarkerFormat` and `ListItem.MarkerText`/`Marker` give custom markers. All entries share one marker column as wide as the widest marker, so contents line up on a hanging indent and markers end at the same place. A continued entry repeats no marker.

## Pages, numbers, links

- **Page numbers.** `PageNumber` shows the current number, or a total. A total is not known while pages are laid out: it is a form XObject reserved on first use and drawn on each page, filled once the document (or section) ends, with the number right-aligned inside a box of `ReservedDigits` digits. More digits than reserved are drawn condensed with a note. `Render(PdfDocument)` cannot fill a form later, so it lays the document out once into a throw-away writer to count pages, then renders. Put "Page N of M" in a `Row` with `Spacing`; a trailing space does not count towards a text's width.
- **Numbering.** Each section has `NumberStyle`, `StartNumber` (default: continue) and `LabelPrefix`. The document gets page labels (ISO 32000-2 12.4.2) where numbering does not simply continue. `SectionTotal` counts a section's pages in its own style.
- **Links and anchors.** `Link` covers its child's box (every piece of a split child) with a URI or the name of an `Anchor`; anchors are named destinations at the top of their child's first piece, and links may precede them. Undefined and duplicate names are noted. `Bookmark` adds an outline entry (levels lifted to one below the previous entry when they jump). Links and anchors inside `Rotate` or `Scale` are not recorded. Only the streaming writer can record links, destinations, bookmarks and labels; `Render(PdfDocument)` leaves them out with a note.

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
