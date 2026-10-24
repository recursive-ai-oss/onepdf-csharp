# PDF/UA and PDF/A level A

OnePDF can write **PDF/UA-1** (ISO 14289-1) and **PDF/A-2a / 3a** (ISO 19005-2 and -3, level A: tagged), alone or in one file, and can check a document against the requirements it is able to verify.

## What is promised, and what is not

- **Only for documents OnePDF laid out or created with tags.** The input must already be a tagged document: a structure tree, marked content on every painted mark, alternate text where it is needed, a title. OnePDF checks that and repairs the clerical parts; it **does not tag untagged content, guess alternate text, invent titles or guess a language**. It is not a converter for arbitrary files. An untagged file is refused with the reasons.
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

An XMP packet that already claims everything requested is kept; any other packet is replaced by one built from the Info dictionary (as for PDF/A).

## What is checked (and refused)

Each finding names the Matterhorn checkpoint and the ISO 14289-1 clause.

- **Tagging (01, 06; 7.1):** `/MarkInfo /Marked true`, `/Suspects` not true, a `/StructTreeRoot`; every painted mark (paths, text, images, shadings, forms, inline images) is inside marked content that is either an `/Artifact` or carries an `/MCID`; tagged content is not nested in an artifact; every `/MCID` used on a page is referenced by the structure tree; a page with tagged content has `/StructParents`. Content inside form XObjects is taken to inherit the tagging of the `Do` that paints it and is not scanned.
- **Structure types (02; 7.1):** every type is one of the ISO 32000-1 standard types or reaches one through the `/RoleMap`; no circular mapping; no standard type remapped. PDF 2.0-only types (`Aside`, `Title`, …) need a mapping.
- **Metadata and dictionaries (06, 07; 7.1):** XMP packet with `pdfuaid:part` 1 and a non-empty `dc:title`; `DisplayDocTitle` true.
- **Language (11; 7.2):** a catalog `/Lang` that is a plausible BCP 47 tag.
- **Figures (13; 7.3, 7.7):** `Figure` and `Formula` elements have `/Alt` or `/ActualText`.
- **Headings (14; 7.4):** the first numbered heading is H1; a numbered heading is at most one level deeper than the one before; `H` and `H1`…`H6` are not mixed.
- **Tables (15; 7.5):** at least one `TH` cell; `TR` in `Table`/`THead`/`TBody`/`TFoot`; cells in `TR`; every row covers the same number of columns once `ColSpan` and `RowSpan` are counted.
- **Lists (16; 7.6):** `L` contains `LI` (or a `Caption`); `LI` is in `L`; `Lbl` and `LBody` are in `LI`.
- **Annotations (28; 7.18):** every link annotation is an object reference inside a `Link` structure element and has `/Contents` (or its element has `/Alt`); widgets are in `Form` elements and have a `/TU`; other annotations (except Popup, TrapNet, PrinterMark) are in the tree and have `/Contents`; each has a `/StructParent`; pages with annotations have `/Tabs /S`.
- **Fonts (31; 7.21):** every font other than Type 3, in page and form resources, has an embedded font program; a font has a `/ToUnicode` map, or an encoding that determines Unicode (a standard encoding without `/Differences`, or an Adobe CJK ordering).
- **Security (26; 7.16):** an encrypted document, or new encryption in the save options, must keep the accessibility-extraction permission.

Not checked: reading order, contrast and colour use, the meaning of alternate text, heading semantics beyond the level sequence, `Headers`/`ID` association of complex tables, scope attributes, optional content names, embedded file descriptions, media and flicker rules, XFA and JavaScript rules, fonts used only in invisible text, the Unicode mapping of foreign symbolic fonts, forms inside XObjects. Bookmarks for long documents (ISO 14289-1 7.17) are not required by this check.

## PDF/A level A

Level A is level U plus a tagged logical structure (ISO 19005-2, 6.7). A save as 2a or 3a repairs `/MarkInfo` and the language fallback, and checks: `/MarkInfo` (6.7.2.2), a structure tree (6.7.3.3), standard or role-mapped types and no circular or remapped types (6.7.3.4), a language (6.7.4), content that is tagged or an artifact, and alternate text for figures. Everything in [pdfa.md](pdfa.md) applies too. XMP carries `pdfaid:conformance` `A`.

## Verification

`tests/OnePdf.Layout.Tests/PdfUATests.cs` lays out a document with headings, a multi-page table with header cells and spans, nested lists, figures with alternate text, internal and external links, a header and a footer with page totals, and saves it as PDF/UA-1, PDF/A-2a, PDF/A-3a, and 2a and 3a combined with PDF/UA-1; each file passes veraPDF (the combined ones under both profiles). `tests/OnePdf.Tests/PdfUA` does the same with documents made by `PdfDocumentWriter`, and has a test for every refusal above and for `CheckPdfUA` listing all violations of a deliberately bad document. veraPDF is run through `ONEPDF_VERAPDF`; the tests skip without it. The `pdfa` fuzz target also covers PDF/UA and level A: a file that is written must pass the checkers.
