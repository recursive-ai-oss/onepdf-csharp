# Page operations

Reorder, delete, insert, rotate and duplicate pages of a document; copy pages from other documents; merge documents; split one into
several. All of it works on an editable document (`PdfOpenOptions.AllowEditing`, see [writing.md](writing.md)) and is written with
`Save` or `SaveIncremental` like any other change.

```csharp
using var doc = PdfDocument.Open("report.pdf", new PdfOpenOptions { AllowEditing = true });
doc.MovePage(fromIndex: 0, toIndex: 4);          // the cover goes to the back
doc.RemovePages([2, 3]);                         // bookmarks, links and names that led there are repaired
doc.RotatePage(1, 90);                           // clockwise, added to the page's current /Rotate
doc.InsertBlankPage(0, 595, 842);
using var appendix = PdfDocument.Open("appendix.pdf");
doc.ImportPages(appendix);                       // all pages of another document, appended
doc.Save("out.pdf");

using var merged = PdfDocument.Merge([a, b, c]); // a new document
foreach (PdfDocument part in PdfDocument.Split(big, pagesPerPart: 10)) { part.Save(...); part.Dispose(); }
```

Page indices are zero-based. Take `doc.Pages` again after an operation: `PdfPage` objects, outlines, the form and the other views built
before it are out of date (`PageCount`, `Pages`, `Outline`, `Form`, `PageLabels`, `NamedDestinationNames` and
`OptionalContentGroups` are refreshed). Changing a document is for one thread at a time.

## The calls

| Call | What it does |
|---|---|
| `PdfDocument.CreateEmpty(options?)` | A new editable document with no pages (PDF 1.7, in memory). |
| `InsertBlankPage(index, width, height)` | A page with an empty `/Resources` and the given `/MediaBox` (points). Returns the page. |
| `RemovePage(index)`, `RemovePages(indexes)` | Deletes pages and repairs what pointed at them (below). |
| `MovePage(from, to)` | `to` is the page's index afterwards. |
| `ReorderPages(newOrder)` | `newOrder[i]` is the current index of the page that becomes page `i`; a permutation or `ArgumentException`. |
| `RotatePage(index, degrees)`, `SetPageRotation(index, degrees)` | Relative and absolute `/Rotate`; multiples of 90, normalised to 0/90/180/270. |
| `DuplicatePage(index)` | A copy right after the page, with its own annotations; content and resources are shared. |
| `ImportPages(source, indexes?, insertAt = -1, options?, ct)` | Copies pages (null = all) from `source`, which may be any document, read-only or encrypted, or the document itself. |
| `PdfDocument.Merge(sources, options?, ct)` | A new document holding all pages of the sources in order. Limits are the first source's. No `/Info`: call `SetInfo`. |
| `PdfDocument.Split(source, ranges \| pagesPerPart, ct)` | New documents, one per `PdfPageRange(start, count)`. |

`PdfImportOptions`: `ImportOutlines`, `ImportFormFields`, `ImportStructure`, all true by default.

Merge and split results live in memory and do not refer to their sources, which may be disposed straight away. Encryption plays no
part until `Save`: an encrypted source imports into a plain document, and `PdfSaveOptions.Encryption = Set` encrypts the result.

## What a copied page brings

A page is copied by walking its object graph with an explicit work stack (no recursion on graph depth, cycles end the walk). Each
object of the source is copied once however many pages use it; a later import from the same source into the same target reuses
what the earlier one copied (not when the source is editable). Streams are copied as stored (filters and bytes untouched).

- **Content and resources:** contents, fonts, images, colour spaces, patterns, shadings, XObjects, graphics states.
- **Inherited attributes (ISO 32000-2 7.7.3.4):** `/Resources`, `/MediaBox`, `/CropBox` and `/Rotate` that the page got from the page
  tree are written into the copied page. The target's page tree is rebuilt as a balanced tree (at most 32 kids per node) with every
  page carrying its own values; a resource dictionary that many pages shared stays one shared object.
- **Annotations** with their appearance streams; each imported page gets its own annotation objects (a page imported twice gets two
  sets). `/P` points at the new page.
- **Links:** a link whose destination page was imported follows it; one whose destination was left behind keeps its rectangle but
  loses `/Dest` or its go-to action. Named destinations used by imported links are added to the target's name tree, renamed with a
  `_2`, `_3` suffix when the name is taken by a different place.
- **Bookmarks:** the source's outline items that lead to an imported page are appended after the target's top-level items. An item
  whose own target was left behind but which has such items below it stays as a heading without a target.
- **Form fields:** the widgets of imported pages come with the field tree above them (only the kids that came along), merged under
  the target's `/AcroForm` `/Fields`. A top-level name already used in the target gets `_2`, `_3`, … and an Info diagnostic says so.
  The source's `/DA` and `/Q` are put on the imported fields that lack their own; the source's default resources (`/DR`) are added
  to the target's, and a name that is taken by another object gets a free name (`Helv` becomes `Helv_2`) which is also rewritten in
  the imported default appearance strings. A signature field arrives without its value (it signs the bytes of another file).
  `/XFA` of the target is dropped, since it would no longer describe the fields.
- **`/NeedAppearances`:** that flag belongs to a whole document, and setting it would redraw every field of the target. Instead the
  appearance of each text and choice field is generated from the source (as `PdfAnnotation.GenerateAppearance` does) and stored in the
  copied widget. Only if a widget cannot be given one does the target get the flag.
- **Optional content:** the groups the copied content uses are added to the target's `/OCProperties` with their default visibility.
- **Page labels:** each page keeps its label scheme (a source without labels counts as plain decimal numbers from 1). The target's
  `/PageLabels` is rewritten as the shortest list of ranges for the new order and is dropped when no page had labels.
- **Structure (tagged PDF):** the structure tree is pruned to the elements that hold marked content of imported pages or annotations
  that came along, and appended as new top-level elements of the target's tree (created when needed, with `/MarkInfo`). `/Pg`, `/Obj`,
  `/StructParents` and `/StructParent` are mapped; role and class maps are merged (the target's entry wins on a clash). Pages copied
  within the document by `DuplicatePage` are not tagged.
- **PDF version:** raised to the source's when that is higher.

Not carried: article threads, thumbnails, document-level JavaScript and named-destination trees other than the used names, the
source's metadata, output intents, signatures and permissions. `Merge` does not copy `/Info` or XMP either.

## What a deletion repairs

`RemovePages` removes the pages from the page tree and then:

- **Links** on the remaining pages that went to a deleted page lose `/Dest` or their go-to action (they stay as inert rectangles).
- **Bookmarks** whose target was deleted are removed; one that has other bookmarks below it stays as a heading without a target.
  `/First`, `/Last`, `/Prev`, `/Next`, `/Parent` and `/Count` are rewritten (a closed item stays closed).
- **Named destinations** (the catalog `/Dests` dictionary and the `/Names` `/Dests` tree, which is rewritten as one sorted node when
  anything goes) and the document's `/OpenAction` that led to a deleted page are dropped.
- **Form fields** whose widgets were all on deleted pages are removed from the form.
- **Page labels** are renumbered for the new order.
- The **structure tree and the article threads** refer to pages from deep inside and would keep the deleted content in the file, so
  they are dropped, with an Info diagnostic. (Reordering and rotating keep them.)

A full save writes only what the remaining pages reach: the content of a deleted page is not in the file. An incremental save keeps
the old objects in the file, as any incremental save does.

## Limits and failures

- The number of objects copied is bounded by the target's `PdfLimits.MaxObjects`, nesting of a single value by `MaxNestingDepth`;
  both throw `PdfLimitException`. A copy that fails while the pages are being copied (a limit, `OperationCanceledException`) leaves no
  trace: the object numbers are given back and the page list is unchanged. A failure later, while bookmarks or fields are linked into
  existing objects, leaves the page list unchanged but may leave unreferenced objects, which a full save drops.
- Cyclic `/Parent` and `/Kids`, self-referencing annotations (`/Popup`, `/IRT`, `/Parent` pointing at themselves), looping action
  `/Next` chains, looping outlines and field parents all terminate; a reference to a page that was not imported becomes null.
- A source page tree that the reader had to repair is copied as the reader sees it (`doc.Pages`).

## Known limits

- A resource dictionary shared by all pages of a document (common in slide decks) is copied whole into every part of a split, because
  the pages reach all of it. Resources are not pruned by scanning the content streams.
- Links between imported pages from *different* calls of `ImportPages` are not kept: destinations are mapped within one call.
- Fields of the same name are renamed, not merged.
