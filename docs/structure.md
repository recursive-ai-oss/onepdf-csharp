# Structure: removing tags and editing the structure tree

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
