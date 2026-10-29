# PDF/UA and PDF/A level A

OnePDF can write **PDF/UA-1** (ISO 14289-1), **PDF/UA-2** (ISO 14289-2, on PDF 2.0; see [PDF/UA-2](#pdfua-2)) and **PDF/A-2a / 3a** (ISO 19005-2 and -3, level A: tagged), alone or in one file, and can check a document against the requirements it is able to verify.

## What is promised, and what is not

- **Only for tagged documents: ones OnePDF laid out or created with tags, and existing files tagged through [the tagging API](tagging.md).** The input must already be a tagged document: a structure tree, marked content on every painted mark, alternate text where it is needed, a title. OnePDF checks that and repairs the clerical parts; it **does not tag on its own judgement, guess alternate text, invent titles or guess a language**. The tagging API writes the structure a caller decides on (the caller supplies every role and every alternate text); OnePDF never works the roles out. It is not a converter for arbitrary files. An untagged file is refused with the reasons.
- **A clean check is not proof of conformance.** `CheckPdfUA` verifies the rules listed below and nothing else; PDF/UA also asks for things only a person can judge (a logical reading order, meaningful alternate text, sufficient contrast, correct heading use). The tests run the veraPDF validator (PDF/UA-1, PDF/A-2a, PDF/A-3a) as a black box on the documents OnePDF writes; passing it is evidence, not certification. Use a validator and a screen-reader review before claiming conformance for a document.
- It writes the identification only when every check it makes passes.

## API

```csharp
LayoutDocument layout = LayoutDocument.Compose(doc =>
{
    doc.Title("Annual report").Author("A. Writer").Language("en-US");
    doc.Page(page => { /* headings, tables with header cells, figures with .AltText(...) */ });
});

using var file = File.Create("report.pdf");
layout.Save(file, new PdfSaveOptions
{
    PdfUA = new PdfUAOptions(),                                                  // PDF/UA-1
    PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.A }, // optional: PDF/A-2a as well
});
```

| | |
|---|---|
| `PdfSaveOptions.PdfUA` (`PdfUAOptions`) | A full save prepares, checks and writes, or throws `PdfUAException` and writes nothing. With `PdfA` too, the failures of both come in one `PdfAException` (PDF/UA entries have clauses starting `PDF/UA-1`). Needs an editable document. Not available for `SaveIncremental`. |
| `PdfUAOptions.Language` | A `/Lang` to write when the catalog has none. Without it, a missing language is a violation. |
| `PdfUAOptions.EmbedLibraryFonts` | Default true: standard fonts added by `AddStandardFont` become embedded Liberation faces when the Fonts package is registered (as for PDF/A). |
| `PdfUAOptions.EmbedSubstituteFonts` | Default false: fonts the document names but does not embed (Arial, Times New Roman, Courier New, the standard 14) get a Liberation subset, with the document's widths. See [fonts.md](fonts.md). |
| `PdfUAOptions.RemoveInvisibleNotdefText` | Default false, Part 1 only: also takes out text that is never drawn and shows the font's `.notdef` glyph (7.21.8), by `PdfDocument.RemoveNotdefText` with the `Invisible` scope. Removing it changes the extracted text, so it is the caller's decision. See "Text that shows `.notdef`" below. |
| `PdfAConformance.A`, `PdfAOptions.Language` | PDF/A level A; `Language` is the same fallback. |
| `PdfDocument.PreparePdfUA(options)` | Makes the repairs below in memory and returns what is still wrong. |
| `PdfDocument.CheckPdfUA()` | Reports every violation as the document stands; changes nothing; works on read-only documents. |
| `PdfUAException.Violations` | `PdfUAViolation`: `Checkpoint` (Matterhorn Protocol checkpoint, such as `13` for graphics), `Clause` (ISO 14289-1), `Message`, `ObjectNumber`, `PageIndex`. At most 2000 are listed; more than twenty of one kind are cut off. |
| `LayoutDocument.Author`, `Subject`, `Keywords` | Information dictionary entries (and XMP `dc:creator`, `dc:description`, `pdf:Keywords` in a PDF/A or PDF/UA save). `Title` and `Language` existed already. |

A layout document reaches these through `LayoutDocument.Save(stream, PdfSaveOptions)`. That path lays the document out through the streaming writer, then reopens the finished file and saves it through `PdfDocument`; **the whole file is held in memory while it is checked and rewritten**. The streaming `Save(stream, PdfDocumentWriterOptions)` cannot do this: refusing a document needs the finished structure tree, every page's content and the final font set, which the streaming writer by design does not keep. For documents too big for that, write with the streaming path and check the result (`CheckPdfUA`) rather than rewriting.

## Repairs (no judgement needed)

| Repair | Requirement |
|---|---|
| `/MarkInfo << /Marked true >>` when a structure tree exists | ISO 14289-1 7.1 |
| `/Lang` from `PdfUAOptions.Language` when the catalog has none | 7.2 |
| `/ViewerPreferences /DisplayDocTitle true` when there is a title | 7.1 |
| XMP packet with `pdfuaid:part` 1 and `dc:title` taken from the Info title (declared as an extension schema when PDF/A is also requested, ISO 19005-2 6.6.2.3) | 5, 7.1 |
| `/Tabs /S` on pages that have annotations | 7.18.3 |
| `/Contents` on a link annotation that has none: the structure element's `/Alt` or `/ActualText`, else the link's address (layout links already carry the text they cover) | 7.18.5 |
| Liberation faces for standard fonts OnePDF added | 7.21.4.1 |
| `/Scope` on a header cell of a table that is in breach, where the layout of its row makes the answer plain (see "Header cells") | 7.5 |
| `/Name` on an optional content configuration that has none (`Default` for `/OCProperties /D`, `Configuration n` for the entries of `/Configs`) | 7.10 |

An XMP packet that already claims everything requested is kept; any other packet is replaced by one built from the Info dictionary (as for PDF/A).

## What is checked (and refused)

Each finding names the Matterhorn checkpoint and the ISO 14289-1 clause.

- **Tagging (01, 06; 7.1):** `/MarkInfo /Marked true`, `/Suspects` not true, a `/StructTreeRoot`; every painted mark (paths, text, images, shadings, forms, inline images) is inside marked content that is either an `/Artifact` or carries an `/MCID`; tagged content is not nested in an artifact; every `/MCID` used on a page is referenced by the structure tree; a page with tagged content has `/StructParents`. Content inside form XObjects is taken to inherit the tagging of the `Do` that paints it and is not scanned.
- **Structure types (02; 7.1):** every type is one of the ISO 32000-1 standard types or reaches one through the `/RoleMap`; no circular mapping; no standard type remapped. PDF 2.0-only types (`Aside`, `Title`, …) need a mapping.
- **Metadata and dictionaries (06, 07; 7.1):** XMP packet with `pdfuaid:part` 1 and a non-empty `dc:title`; `DisplayDocTitle` true.
- **Language (11; 7.2):** a catalog `/Lang` that is a plausible BCP 47 tag.
- **Figures (13; 7.3, 7.7):** `Figure` and `Formula` elements have `/Alt` or `/ActualText`.
- **Headings (14; 7.4):** the first numbered heading is H1; a numbered heading is at most one level deeper than the one before; `H` and `H1`…`H6` are not mixed.
- **Tables (15; 7.5):** at least one `TH` cell; `TR` in `Table`/`THead`/`TBody`/`TFoot`; cells in `TR`; every row covers the same number of columns once `ColSpan` and `RowSpan` are counted; header cells whose data cells cannot find them (see "Header cells").
- **Optional content (20; 7.10):** each configuration dictionary, `/OCProperties /D` and every entry of `/Configs`, has a non-empty `/Name`.
- **Lists (16; 7.6):** `L` contains `LI` (or a `Caption`); `LI` is in `L`; `Lbl` and `LBody` are in `LI`.
- **Annotations (28; 7.18):** every link annotation is an object reference inside a `Link` structure element and has `/Contents` (or its element has `/Alt`); widgets are in `Form` elements and have a `/TU`; other annotations (except Popup, TrapNet, PrinterMark) are in the tree and have `/Contents`; each has a `/StructParent`; pages with annotations have `/Tabs /S`.
- **Fonts (31; 7.21):** every font other than Type 3, in page and form resources, has an embedded font program; a font has a `/ToUnicode` map, or an encoding that determines Unicode (a standard encoding without `/Differences`, or an Adobe CJK ordering).
- **`.notdef` text (31; 7.21.8, Part 1):** a text-showing operator selects a character code for which the embedded font program has no glyph, whether or not the text is drawn. One finding per font and page lists the codes. The check interprets every page (without decoding images or building outlines) so the codes are the ones a validator names; on the largest documents tried that added roughly a third to the time and allocation of `CheckPdfUA`.
- **Content that a save would lose (31 for fonts, else 01; 7.21.4.1, 7.1):** a stream that cannot be read, decrypted or decoded (corrupt or truncated filter data) and a reference to a needed part that leads to no object, such as a missing `/FontFile2`. The finding names the object and what it is. A PDF/UA save, like a PDF/A save, never drops content: it fails with these findings instead (the full list of what counts is in [pdfa.md](pdfa.md)). In PDF/UA-2 the clause is that of the matching checkpoint.
- **Security (26; 7.16):** an encrypted document, or new encryption in the save options, must keep the accessibility-extraction permission.

Not checked: reading order, contrast and colour use, the meaning of alternate text, heading semantics beyond the level sequence, whether a `Headers` list names the right cells, embedded file descriptions, media and flicker rules, XFA and JavaScript rules, fonts used only in invisible text, the Unicode mapping of foreign symbolic fonts, forms inside XObjects. Bookmarks for long documents (ISO 14289-1 7.17) are not required by this check.

## Header cells (7.5)

A `TH` without a `/Scope` leaves the data cells of its table without a header unless something else reaches them. The check judges only tables that have an unscoped `TH` and a regular grid (irregular tables get the width finding instead). A data cell is connected when its `/Headers` names the `/ID` of a cell, when a `Column` or `Both` header lies entirely above it in its column, or when a `Row` or `Both` header lies entirely to its left in its row. An unscoped header never connects anything. The finding is per unscoped `TH` and says how many data cells are left without a header. A `/Scope` given through a class map counts.

`PreparePdfUA` sets a scope only in a table that is in breach, and only where the row makes it plain: a row made only of `TH` cells gets `Column`, and the first cell of a row that also has `TD` cells gets `Row`. Anything less clear stays a violation, because it takes a person to say what the cell heads. The usual way to meet this is a header cell added with `InsertChild("TH", ...)`: set its `Scope` at once.

## Text that shows `.notdef`

Hidden text layers (render mode 3, or content in a hidden optional content group) often show codes that map to `.notdef` in the font that carries them. ISO 14289-1 7.21.8 forbids that whether or not the text is drawn.

- `doc.FindNotdefText()` returns a `PdfNotdefRun` per text operator: page, source (page content, form XObject or annotation appearance), the stream's object number, the operator's offset, the font, the codes shown and the codes that are `.notdef`, the render mode, whether it is drawn, the optional content groups around it, and the text it extracts as. Only embedded, non-substituted, non-Type 3 fonts can show `.notdef`.
- `doc.RemoveNotdefText(options)` removes exactly the `.notdef` codes from those operators. A removed code is replaced by a `TJ` adjustment of its advance, so the following text does not move; nothing else in the stream changes. `PdfNotdefRemoveOptions.Scope` is `Invisible` by default (render mode 3, or hidden by optional content; clip modes count as drawn): that text is never seen, so removing it changes no picture. `All` also removes drawn `.notdef` text, which changes what is visible. `DryRun` reports without editing, `Include` picks runs. The result lists what was removed and what was kept with the reason.
- A form XObject used from several places is edited once, and only when every use agrees; otherwise its runs are reported as kept. Patterns and operators without a stream of their own are reported, not edited.
- What changes for readers: the extracted text loses exactly the `NotdefText` of the removed runs (a gap reads as a space). The rendering of invisible text is unchanged.

## PDF/UA-2

PDF/UA-2 (ISO 14289-2) is built on PDF 2.0. Choose it with `PdfUAOptions.Part`:

```csharp
layout.Save(file, new PdfSaveOptions { PdfUA = new PdfUAOptions { Part = PdfUAPart.Part2 } });
```

The promises are those of part 1 (above): tagged documents only (laid out or created by OnePDF, or tagged through [the tagging API](tagging.md) with roles the caller supplies), no tagging on OnePDF's own judgement, no invented titles, alternate text or language, **and a clean check is not proof of conformance**. A part-2 save is checked with veraPDF's `ua2` flavour as a black box in the tests; passing it is evidence, not certification.

| | |
|---|---|
| `PdfUAOptions.Part` | `PdfUAPart.Part1` (default) or `PdfUAPart.Part2`. Part 1 output is unchanged. |
| `PdfDocument.CheckPdfUA(PdfUAPart part, ct)` | The check against the given part. A part-2 check of a file that is not PDF 2.0, or whose elements name no namespace, says so. |
| `PdfDocument.PreparePdfUA(options)` | With `Part2`, also makes the namespace repairs below (the version is written by the save). |
| `PdfUAViolation.Part`, `.Clause` | 1 or 2, and the clause of that part. |
| `PdfDocumentWriter.SetBookmarkElement`, `.SetDestinationElement` | Tie a bookmark or named destination to the structure element its target is in; a writer producing PDF 2.0 (`Version` "2.0") then writes structure destinations. |
| `StructureGroup`, `Slot.Aside()`, `.Footnote()`, `.Title()`, `.Group(type)` | Layout: group content in a `Sect`, `Div`, `Part`, `BlockQuote`, `Aside`, `FENote` or `Title` element. |

**Repairs for part 2** (no judgement):

| Repair | Requirement |
|---|---|
| The file is written as PDF 2.0 (`%PDF-2.0`) | ISO 14289-2 requires a PDF 2.0 file |
| `/Namespaces` on the structure tree root and `/NS` on every element: the PDF 2.0 namespace (`http://iso.org/pdf2/ssn`) for the types it defines, the PDF 1.7 namespace for the 1.7 types it lacks (`TOC`, `TOCI`, `Reference`, `Art`, `BlockQuote`, `Code`, `Index`, `Quote`, `BibEntry`, `Private`) | 8.2.4 (ISO 32000-2 14.7.4.2) |
| `Note` becomes `FENote` | ISO 32000-2 14.8.4.4.2 |
| `/Ref` on a `TOCI` taken from the structure destination of the link inside it | 8.2.5.8 |
| XMP `pdfuaid:part` 2 and `pdfuaid:rev` 2024, with the title | 5, 8.11.1 |
| Everything the part-1 repairs do that part 2 also asks for: `/MarkInfo`, `/Lang` from `PdfUAOptions.Language`, `DisplayDocTitle`, `/Tabs /S`, link `/Contents`, Liberation faces for standard fonts | 6.2, 8.4.4, 8.11.2, 8.9.3.3 |

**Refused for part 2** (each finding names the clause of ISO 14289-2; the clause numbers are those that veraPDF's `ua2` profile reports the rule under, and a rule it does not check is filed under the nearest heading):

- **Tagging (8.2.2):** real content outside marked content or an artifact; MCIDs missing from the tree; a missing `/StructParents` (and the part-1 tagging findings).
- **Namespaces (8.2.4):** an element naming no namespace or a namespace that is neither the PDF 2.0 nor the PDF 1.7 one; a type its namespace does not define. Custom types and namespace role maps are not written: they are refused.
- **Headings (8.2.5.12):** the unnumbered `H` is not allowed; use `H1`…`H6`. Heading level order is **not** checked for part 2 (neither does veraPDF's profile): that is a person's judgement.
- **Table of contents (8.2.5.8):** a `TOCI` with no `/Ref` that cannot be filled from its link.
- **Links (8.2.5.20):** a link annotation inside a `Link` or `Reference` element; no `/Contents` demand (the repair supplies it).
- **Lists (8.2.5.25):** a list with labels names its numbering (`ListNumbering` other than `None`). Nesting rules of the structure table (`L` contains `LI`, `LI` contains `Lbl` and `LBody`, rows in tables) are checked as in part 1.
- **Tables (8.2.5.26):** regular tables (rows cover the same number of columns). A header cell is **not** demanded (veraPDF's profile does not demand it; layout tables still write one).
- **Figures (8.2.5.28.2):** `Figure` has `/Alt` or `/ActualText`; `Formula` as in part 1.
- **Destinations (8.8):** every link, GoTo action and outline item that goes inside the document is a structure destination, not a page. OnePDF cannot choose the element for an existing page destination, so a file with one is refused; layout and `PdfDocumentWriter` (version 2.0, `SetBookmarkElement`/`SetDestinationElement`) write structure destinations.
- **Language, metadata, viewer preferences, fonts (8.4.4, 8.11, 8.4.5.5.1):** as in part 1, with `pdfuaid:rev`.
- **Pages with annotations (8.9.3.3):** a `/Tabs` entry (A, W or S; the repair writes S).
- **PDF 2.0 file (general):** a check of a file that is not PDF 2.0 says so; a save always writes 2.0.

**With PDF/A:** PDF/A-2 and PDF/A-3 are PDF 1.7 formats, so `PdfA` together with `Part2` throws `PdfAException` (one violation) and writes nothing. PDF/A-4 is not supported.

**Layout.** A layout document saved with `Part2` is laid out as PDF 2.0: bookmarks, anchors and table-of-contents links go to the structure element they point at (for an anchor around a heading, the heading; for content with no element of its own, the element around it). Headings are `H1`…`H6` as before. `Aside()`, `Footnote()` and `Title()` write the 2.0-only types `Aside`, `FENote` and `Title`, which part 1 refuses (they have no ISO 32000-1 equivalent and OnePDF writes no role map for them). Layout has no inline `Em`/`Strong`. Author-supplied things stay the author's: title, language, alternate text.

**Inline, caption, ruby, warichu and MathML structure (part 2).** Each finding names its clause of ISO 14289-2; all but the associated-file rule are in veraPDF's `ua2` profile too, and the tests compare both.

| Rule | Clause |
|---|---|
| `Ruby` has exactly `RB`, `RT` or `RB`, `RP`, `RT`, `RP`, in that order | 8.2.5.23 |
| `Warichu` has exactly `WP`, `WT`, `WP` | 8.2.5.24 |
| A `Caption` is the first or the last child of its parent | 8.2.5.27 |
| MathML elements (namespace `http://www.w3.org/1998/Math/MathML`, accepted by the namespace check) occur only below a `Formula` or another MathML element | 8.2.5.29 |
| A file in a `Formula`'s `/AF` has an `/AFRelationship` and embeds its data (`/EF`) | 8.2.5.29 (ISO 32000-2 7.11.3, 14.13) |
| The structure type table (ISO 14289-2 / ISO/TS 32005 Table 5): a parent may not contain certain children (this is what keeps `Em` and `Strong` inline: they may not hold `P`, `L`, `Table`, `Sect` and the other block elements, and `Document`, `Sect`, `Table`, `L` and the like may not hold them directly); some children occur at most once (`Caption`, `THead`, `TFoot`, `H`, `Sect` below a heading); `Document`, `Sect`, `Table`, `TR`, `L`, `TOC` and similar do not hold marked content directly | Table 5 |

The table is data in `PdfUA2Table.cs`. Only standard elements are compared (custom types are refused by the namespace rule). An element counts as a grouping element, for the `Link`/`Annot`/`FENote`/`Reference` caption rule, when it holds no marked content itself.

Not checked, because they need a person's judgement or the clause text is not fixed enough to decide: whether `Em` and `Strong` express real emphasis; whether a `Caption` belongs to what it sits beside; whether ruby text is the reading of its base; whether MathML is a faithful or complete rendering of the formula, and whether the associated file is MathML at all; the 51 table rows that depend on whether an element is used as a grouping or non-grouping element (`Annot`, `Reference`, `Form`, `Link`, ... below), and `StructTreeRoot` holding exactly one `Document`; the rest of "Not checked" above.

## PDF/A level A

Level A is level U plus a tagged logical structure (ISO 19005-2, 6.7). A save as 2a or 3a repairs `/MarkInfo` and the language fallback, and checks: `/MarkInfo` (6.7.2.2), a structure tree (6.7.3.3), standard or role-mapped types and no circular or remapped types (6.7.3.4), a language (6.7.4), content that is tagged or an artifact, and alternate text for figures. Everything in [pdfa.md](pdfa.md) applies too. XMP carries `pdfaid:conformance` `A`.

## Verification

`tests/OnePdf.Layout.Tests/PdfUATests.cs` lays out a document with headings, a multi-page table with header cells and spans, nested lists, figures with alternate text, internal and external links, a header and a footer with page totals, and saves it as PDF/UA-1, PDF/A-2a, PDF/A-3a, and 2a and 3a combined with PDF/UA-1; each file passes veraPDF (the combined ones under both profiles). `PdfUA2Tests.cs` in both test projects does the same for PDF/UA-2 under the `ua2` flavour: a richer layout document (title, table of contents, bookmarks, aside, footnote, inline objects, lists, a table with row and column headers, figures, links) and writer-made documents, a refusal for each rule above that veraPDF also reports, a check that a file damaged in each way veraPDF finds is also found by `CheckPdfUA`, and the committed reference `testdata/layout-references/documents/accessible-ua2.pdf`. `tests/OnePdf.Tests/PdfUA` does the same with documents made by `PdfDocumentWriter`, and has a test for every refusal above and for `CheckPdfUA` listing all violations of a deliberately bad document. veraPDF is run through `ONEPDF_VERAPDF`; the tests skip without it. The `pdfa` fuzz target also covers PDF/UA and level A: a file that is written must pass the checkers.
