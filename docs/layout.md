# Layout

`OnePdf.Layout` lays out an element tree over pages and writes the result through OnePDF's content builder. It is not released yet and lives in `OnePdf.Layout.slnx`; its element types and properties may still change.

## Model

A tree of plain element objects. Elements hold properties and no layout state, so one element may appear several times and a tree can be laid out again; two runs give the same bytes (use a fixed `PdfDocumentWriterOptions.DocumentId`).

- **Containers:** `Column` (top to bottom, spacing, item alignment), `Row` (constant, relative and automatic cell widths, spacing), `Layers` (one primary layer sets the size and may split; the others are laid out at its size on every page it occupies).
- **Box:** margin, border (per side, solid/dashed/dotted), padding, background, corner radius, opacity, fixed/min/max width and height, aspect ratio, nine-point alignment of the child. Logical `Start`/`End` insets and borders sit beside the physical ones.
- **Transforms:** `Rotate` (box = bounding box of the turned child), `Scale`, `Translate` (moves the drawing, not the layout space).
- **Leaves:** `Text` (one standard font, greedy wrapping, widow/orphan control), `Image` (JPEG or PNG; fit width, fit height, contain, cover, stretch), `Canvas` (a callback with the content builder inside a clipped box), `Spacer`.
- **Flow rules:** `PageBreak`, `KeepTogether`, `EnsureSpace`, and `Element.KeepWithNext`.
- **Reuse:** derive from `Component` and return a composed tree from `Compose`.
- **Pages:** `LayoutDocument.Pages` holds `LayoutPage` sections. Each has size, orientation, margin, direction, `Header`, `Footer`, `Background`, `Foreground` and `Content`. Header and footer are laid out on every page and their height comes off the content area; background and foreground use the whole page. They are written as artifacts.
- **Direction:** `LayoutPage.Direction` and `Element.Direction`. `Start`/`End` alignment and insets flip, and rows run right to left. Text is not shaped or reordered yet.

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
