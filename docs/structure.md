# Structure: reading and editing the structure tree, and removing tags

Two layers, as in [tagging.md](tagging.md). The **happy path** is the handful of calls below; the **low-level layer** is the COS dictionaries (`PdfDocument.StructureTreeRoot`, `PdfStructureNode.Dictionary`) that every typed object also exposes, for anything the typed API does not cover.

## Reading and editing a tree

```csharp
using PdfDocument doc = PdfDocument.Open("in.pdf", new PdfOpenOptions { AllowEditing = true });
PdfStructureTree? tree = doc.StructureTree;        // null when the file is untagged

foreach (PdfStructureNode n in tree!.Elements())   // depth first, document order
{
    if (n.StandardType == "TH" && n.Scope is null) n.Scope = PdfTagScope.Column;
    if (n.StandardType == "Figure" && n.Alt is null) n.Alt = Describe(n.GetText(), n.PageIndex);
}

tree.MapRole("Strong", "Span");                    // custom types to standard ones
tree.Elements().First(n => n.Type == "Span" && n.Parent?.StandardType == "L").Remove(PdfStructureRemoval.Unwrap);
doc.Save("fixed.pdf", new PdfSaveOptions { PdfUA = new PdfUAOptions() });
```

`doc.StructureTree` reads the tree once. Every element is a `PdfStructureNode`:

- `Type` is `/S` as written; `StandardType` follows the role map to a standard type (null when it ends elsewhere, is circular or too long).
- `Alt`, `ActualText`, `Expansion` (`/E`), `Language` (`/Lang`), `Title` (`/T`) read and write text; `Id` reads `/ID`.
- `Scope`, `ColSpan`, `RowSpan`, `ListNumbering` read and write the `Table` and `List` attributes, looked up in the element's own attributes and then in its classes; `Headers` lists `/Headers`. `GetAttribute`/`SetAttribute(owner, key, value)` reach any other attribute, and `Attributes` returns the raw dictionaries.
- `Parent` and `Children` give the position; a child is another `PdfStructureNode`, a `PdfMarkedContentReference` (page or form stream, MCID) or a `PdfObjectReference` (annotation or XObject). `PageIndex` is the page the element belongs to.
- `GetText()` is the text of the element's marked content and of everything below it, from text extraction (each glyph knows its MCID). Spaces are inferred from glyph positions. Content in form XObjects is matched by identifier alone.

The edits:

| Edit | Call |
|---|---|
| change the type | `node.SetType("P")`, `node.SetType(PdfTagRole.H2)` |
| set or clear text and attributes | the properties above, `null` clears |
| role map entry | `tree.MapRole(custom, standard)` |
| move an element or a content item | `item.MoveTo(newParent, index)` (`null` parent is the root; the index counts after the item has left its old place; -1 appends) |
| wrap children in a new element | `node.WrapChildren("Div", first, count)` (`tree.WrapChildren` for the root) |
| remove, keep the children | `item.Remove(PdfStructureRemoval.Unwrap)`: the children take its place |
| remove, keep nothing untagged | `item.Remove(PdfStructureRemoval.ToArtifacts)`: the subtree goes, its marked content becomes `/Artifact` in the content streams, annotations lose their `/StructParent` |

Each edit changes the document at once and keeps `/K`, `/P`, the parent tree, `/StructParents` and the id tree consistent: content moved to another element is listed under it in the parent tree, a reference whose page the new owner's `/Pg` no longer implies is written as an `MCR` dictionary with its page, and an element wrapped around content gets its parent's `/Pg`. The parent tree is rewritten as one leaf (a key can be found and changed in place) the first time an edit needs it; a tree that is only read is never touched, and saving it changes no byte of the structure. Edits need `AllowEditing`; the result saves normally, incrementally and with `PdfUA`. A tree read before `RemoveTags` or `ApplyTagging` is stale: read `doc.StructureTree` again.

### Damaged trees

Reading never throws and never loops. It is iterative, stops at `PdfLimits.MaxNestingDepth` levels and at the object limit, reads each element once, and puts what it found in `tree.Diagnostics`: a cycle or a shared element (the repeat is ignored), a `/P` that is not the parent that lists the element, an entry of `/K` that is not an element or a reference, integer content in an element with no `/Pg`, the same marked content listed twice. `tree.FindMissingContent()` reads the content streams and lists the references whose MCID no sequence carries (a Link or Figure pointing at content that is not there is common in files from some producers). Edits on a damaged tree work on what was read; the diagnostics tell how much to trust it.

## Removing tags

## Removing tags

```csharp
using PdfDocument doc = PdfDocument.Open("in.pdf", new PdfOpenOptions { AllowEditing = true });
PdfRemoveTagsResult result = doc.RemoveTags();
doc.Save("untagged.pdf");
```

`RemoveTags` makes a file untagged. It removes

- the structure tree: `/StructTreeRoot` with its parent tree, id tree, role map and class map;
- `/MarkInfo`;
- every `/StructParents` and `/StructParent`, on pages, form and image XObjects and annotations;
- every marked-content sequence that carries an `/MCID`, in the content of pages, form XObjects (nested ones, shared ones, and one form used on several pages), tiling patterns, Type 3 glyph procedures, soft-mask groups and annotation appearances.

What a page paints does not change: only the `BDC`/`EMC` operators around the content are touched. The tests compare display lists, text extraction with the structure tree turned off, and two independent renderers (pixel for pixel) before and after, on documents made by the layout package, the document writer and `ApplyTagging`, and on 33 committed tagged files.

By default:

- a sequence with an `/MCID` loses its `BDC` and its `EMC` (`PdfMarkedContentRemoval.Drop`); with `ConvertToBmc` it becomes `/P BMC` with the same tag;
- `/Artifact` sequences, optional content (`/OC`) and marked content without an `/MCID` stay (`RemoveArtifactMarks = true` drops artifact marks as well);
- a sequence whose property list has an `/ActualText` keeps a `BDC` with that text alone, because text extraction reads it from the content stream and the page must read the same.

The call also cleans files that are only half tagged: a tree without `/MarkInfo`, a `/MarkInfo` alone, or marked content with identifiers and no tree. Afterwards `Tag` and `ApplyTagging` accept the file, so the usual way to replace bad tags is

```csharp
doc.RemoveTags();
IReadOnlyList<PdfPageText> text = doc.ExtractText(new PdfTextOptions { RetainGlyphs = true, UseStructureTree = false });
doc.Tag(text, block => /* your role */, options);
```

A stream that cannot be decoded, or an inline image whose end cannot be found, is left as it is and reported in `result.Warnings`; the `/MCID` sequences in it stay and `ApplyTagging` refuses the document. All edits are worked out before the first one is applied, so a cancelled call changes nothing. Properties resources (`/Properties`) that only held an `/MCID` are left in the resource dictionaries; nothing refers to them any more. Outline items and actions that point at structure elements (`/SE`, `/SD`) are left alone.

Signatures: a full save invalidates them; an incremental save keeps the signed revision.

## Artifacts for untagged graphics

```csharp
using PdfDocument doc = PdfDocument.Open("tagged.pdf", new PdfOpenOptions { AllowEditing = true });
PdfArtifactResult result = doc.ArtifactUntaggedGraphics();
doc.Save("fixed.pdf");
```

A file can be tagged and still paint paths, images, shadings, inline images and Form XObjects outside any marked content. ISO 14289-1 7.1 wants every piece of content to be real content or an artifact, and `CheckPdfUA` reports the rest ("neither tagged as real content nor marked as an artifact"). `ArtifactUntaggedGraphics` wraps each such painting operation in an `/Artifact BMC … EMC` sequence in the page content and changes nothing else: a path is wrapped together with its construction operators (`re`, `m`, `l`, … up to `f`, `S`, `B` and the rest), `Do`, `sh` and an inline image (`BI … EI`) are wrapped alone, and a clipping path that is never painted is left alone. The page paints, extracts and interprets as before (checked on display lists, extraction and two renderers).

The scan follows the same rules as the check, so the check's finding goes away when only graphics were behind it:

- a sequence with an `/MCID` is tagged, an `/Artifact` sequence is an artifact, and any other sequence (`/Span BMC`) is neither, so a graphic inside it is wrapped inside it;
- a Form XObject drawn outside marked content is wrapped where it is drawn, never inside, so the same form drawn within tagged content keeps working there;
- text is never wrapped. Whether text is decoration is the caller's decision, so untagged text is counted per page (`UntaggedTextOperators`) and the check still reports it. A form that shows text outside marked content, tags content of its own (it needs a decision about which part is which) or cannot be read is left alone and counted in `FormsLeft`.

`result.Wrapped` and `result.Pages` (page index, wrapped, text operators, forms left) say what happened; a page whose content cannot be decoded or ends inside an inline image is left as it is and named in `result.Warnings`. Annotation appearances, and content reached only from patterns or from inside other forms, are not edited. The call needs `AllowEditing`; running it again wraps nothing. Signatures: a full save invalidates them; an incremental save keeps the signed revision.
