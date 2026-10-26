# Tagging existing PDFs

OnePDF can add a structure tree to a PDF that has none. **You decide the roles; OnePDF writes the structure.** A tool that works out what each block is (a heading, a paragraph, a list item, a table cell, a figure) builds a plan from a text extraction, and `ApplyTagging` turns that plan into marked content in the page streams, a structure tree, a parent tree and the catalog entries a tagged PDF needs. OnePDF never guesses a role, never invents alternate text and never judges whether the roles are right. A clean PDF/UA check afterwards is evidence, not proof of conformance (see [pdfua.md](pdfua.md)).

Pages look the same and extract the same afterwards. Only marked-content operators are added, text-showing operators are split at glyph boundaries without moving a glyph, and nothing is drawn differently. The tests check this against the extraction, the display list and two independent renderers.

## Flow

```csharp
using PdfDocument doc = PdfDocument.Open("in.pdf", new PdfOpenOptions { AllowEditing = true });

// 1. Extract with the glyphs kept: the plan refers to this extraction.
IReadOnlyList<PdfPageText> pages = doc.ExtractText(new PdfTextOptions { RetainGlyphs = true, UseStructureTree = false });

// 2. Assign roles.
PdfTaggingPlan plan = doc.CreateTaggingPlan();
plan.Title = "Harbor tide tables 2026";
plan.Language = "en";
plan.Add(PdfTagRole.H1, PdfTagContent.Block(pages[0], 0));
plan.Add(PdfTagRole.P, PdfTagContent.Block(pages[0], 1));
PdfTagElement list = plan.Add(PdfTagRole.L);
foreach (PdfTextLine line in pages[0].Blocks[2].Lines)
{
    PdfTagElement item = list.Add(PdfTagRole.LI);
    item.Add(PdfTagRole.Lbl, PdfTagContent.Words(pages[0], line, 0));
    item.Add(PdfTagRole.LBody, PdfTagContent.Words(pages[0], line, 1, line.Words.Count - 1));
}

plan.Add(PdfTagRole.Figure, PdfTagContent.Graphics(pages[0], new PdfRectangle(100, 300, 300, 450))).Alt = "Chart of the tide heights";
plan.ArtifactHeadersAndFooters(pages);

// 3. Write it, then save.
PdfTaggingResult result = doc.ApplyTagging(plan);
doc.Save("out.pdf", new PdfSaveOptions { PdfUA = new PdfUAOptions() });   // or Save / SaveIncremental
```

The document must be opened with `AllowEditing`. `ApplyTagging` changes only the editable view, so nothing is written until you save. Both `Save` and `SaveIncremental` work; an incremental save appends to the original bytes. A save with `PdfSaveOptions.PdfUA` also repairs the clerical parts (XMP identification, display of the title, tab order) and refuses the file with every violation listed when something else is missing, for example fonts that are not embedded.

## Roles and elements

`PdfTagRole` names the standard structure types: `P`, `H`, `H1`–`H6`, `L`, `LI`, `Lbl`, `LBody`, `Table`, `THead`, `TBody`, `TFoot`, `TR`, `TH`, `TD`, `Caption`, `Figure`, `Formula`, `Link`, `Span`, `Div`, `Part`, `Art`, `Sect`, `BlockQuote`, `Quote`, `Note`, `Reference`, `Code`, `Form`, `TOC`, `TOCI`. The enum names are the PDF names.

An element (`PdfTagElement`) has a role and children that are other elements or content. It can carry `Alt`, `ActualText`, `Lang`, and for `TH`/`TD` a `Scope`, `RowSpan` and `ColSpan`. `plan.Add(role, content…)` adds a top-level element, `element.Add(role, content…)` a child, and `element.Add(content)` content to an existing element. The writer puts everything under one `Document` element. An element may hold content from several pages; a table can continue over pages.

Nesting is checked: rows belong in a table, cells in a row, list items in a list, `Lbl` and `LBody` in a list item, and content cannot sit directly in a `L`, `LI`, `Table` or `TR`. A violation throws `PdfTaggingException` with `PdfTaggingFailure.InvalidNesting` before anything is changed.

## Content references

References name things the extraction found, so no new identifiers exist. They come from `PdfTagContent`:

| Factory | Names |
|---|---|
| `Block(page, block or index)` | every glyph of a block |
| `Lines(page, block, firstLine, count)` | a run of lines of a block, for example one list item of a block that holds a whole list |
| `Words(page, line, firstWord, count)` | a run of words in one line, for example a bullet or a link's text |
| `Cell(page, line, cellIndex)` | the words of one table cell of a line (`PdfTextLine.CellStarts`) |
| `Graphics(page or pageIndex, area)` | the non-text marks (images, paths, shadings) whose bounds lie inside the area, in upright page space, give or take one point |
| `Annotation(annotation)` | a link or other annotation, tied to the element by an object reference |

Rules the writer enforces:

- The extraction must have been made with `RetainGlyphs = true` and must come from the same document; otherwise the call throws `ForeignContent`.
- The page must still show what the extraction saw. A page edited after the extraction throws `ContentChanged`.
- Each glyph, mark and annotation belongs to at most one element. A second reference to the same thing, or two graphics areas that both contain a mark, throws `DuplicateContent`.
- A blank glyph that nobody claimed (a space the font draws) goes with the text next to it, so a paragraph is not cut up by its spaces.
- A `Link` element should hold the link's text and its `Annotation`. The annotation gets an object reference (`OBJR`) and a `/StructParent`. If it has no `/Contents`, the writer sets it from the element's `Alt`, else from the link's words (ISO 14289-1 7.18.5).

## Artifacts

Anything painted that no element references becomes an artifact: rules, backgrounds, shapes, invisible text and text the plan leaves out. You can also say what it is:

- `plan.Artifact(content, kind)` with `PdfArtifactKind.Header`, `Footer`, `Watermark`, `Layout`, `Page` or `Plain`. The first three are written as pagination artifacts, `Layout` and `Page` with their type, `Plain` as a bare `/Artifact`.
- `plan.ArtifactHeadersAndFooters(pages)` marks every block extraction flagged as a running header or footer (whole-document extraction detects them by repetition). An element's claim on the same content wins.

Artifact sequences the file already had are removed and rebuilt, so text the file called an artifact can be real content in the plan, and nothing ends up as tagged content inside an artifact. Other marked content the file had (`/Span`, `/OC` optional content and so on) is kept as it was.

## How the content is rewritten

- **Splitting.** A text-showing operator whose glyphs belong to different elements is split at glyph boundaries into several operators with the same positions. A `TJ` array is split at its string boundaries and adjustments are carried over. Marked content never straddles `q`/`Q`, and a sequence that starts inside a text object ends inside the same one.
- **Form XObjects.** A form drawn by `Do` whose content belongs to one element is wrapped whole at the `Do`. A form whose content belongs to several elements, or to elements and artifacts, is copied for that use: the page draws the copy, which carries its own marked content and `/StructParents`, and the tree reaches it through `MCR` entries with `/Stm`. The original form is left alone, so the same form drawn twice on a page or on two pages gets one copy per use, and nested mixed forms are copied at every level. A form that draws itself is refused as unreadable.
- **Shared content.** Pages that share one content stream, or whose content is spread over several streams, get their own rewritten streams; the shared original is not modified.
- **Inline images** are wrapped whole; their data is never rewritten.
- **Hidden content.** Text and marks that optional content hides produce no drawing events, so they are wrapped as artifacts.
- **Operators.** The operator count of the tagged page is checked against `PdfLimits.MaxOperatorsPerPage` before the page is written.

## Refusals

`ApplyTagging` throws `PdfTaggingException` (with `Reason` and, where it applies, `PageIndex`) and leaves the document as it was:

| Reason | When |
|---|---|
| `AlreadyTagged` | the catalog has a `/StructTreeRoot` or `/MarkInfo /Marked true`, or a page has marked content with an `/MCID` |
| `ForeignContent` | a reference is from another document, from an extraction without glyphs, to a page the document does not have, or to an annotation that is not a page's |
| `DuplicateContent` | the same glyph, mark or annotation is referenced twice |
| `InvalidNesting` | an element is placed where its role cannot go |
| `ContentChanged` | the page shows a different number of glyphs than the extraction saw |
| `ContentUnreadable` | the page cannot be rewritten safely: a form draws itself or nests deeper than the recursion limit, or glyphs do not match their string |

Calls also throw `PdfLimitException` when the tree would be deeper than `PdfLimits.MaxNestingDepth` or hold too many elements (while the plan is built), or when a tagged page would exceed the operator limit; `InvalidOperationException` when the document is not editable or the plan was applied before; `ArgumentException` for a plan from another document; and `OperationCanceledException` on cancellation. Documents that are tagged already are not retagged or merged: remove the structure yourself first if you mean to replace it.

## What is not done

- No role is chosen and no alternate text is invented. A `Figure` without `Alt` makes the PDF/UA check, and a PDF/UA save, report it.
- No reading order is derived from the plan beyond the order of elements and their content.
- The visual result is unchanged by design, which also means defects in the source (fonts that are not embedded, text without a Unicode mapping) stay and are reported by the PDF/UA check.
- Annotation appearance streams are not tagged; annotations are tied to the tree by an object reference.
