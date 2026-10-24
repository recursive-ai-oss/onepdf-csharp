# Content creation

A document opened with `PdfOpenOptions.AllowEditing`, or made with `PdfDocument.CreateEmpty`, can have content drawn onto its pages. The same builder makes new pages, stamps existing ones and writes reusable forms.

```csharp
using var doc = PdfDocument.CreateEmpty();
PdfPage page = doc.InsertBlankPage(0, 612, 792);
PdfCreatedFont body = doc.AddFont(File.ReadAllBytes("LiberationSerif-Regular.ttf"));   // embedded, subset on save
PdfCreatedFont mono = doc.AddStandardFont("Courier");                                  // not embedded

using (PdfPageContentBuilder b = page.BeginContent())
{
    b.SetFillRgb(0.1, 0.1, 0.5);
    b.Rectangle(72, 700, 468, 2);
    b.Fill();

    b.SetFillGray(0);
    b.SetFont(body, 12);
    b.DrawTextBox("A paragraph that wraps inside a 468 pt wide box. Αβγ Привет", 72, 560, 468, 130, PdfTextAlignment.Justify);
    b.SetFont(mono, 9);
    b.DrawText("printed with PdfDocument.Save", 72, 540);
    b.Commit();                                  // nothing is written to the page before this
}

doc.AddTextStamp("DRAFT", doc.AddStandardFont("Helvetica-Bold"), 96,
    new PdfStampOptions { Opacity = 0.25, RotationDegrees = 45, Color = (1, 0, 0) });
doc.Save("out.pdf", new PdfSaveOptions { DocumentId = new byte[16] });   // reproducible bytes
```

## Coordinates and numbers

Content is written in the page's default user space: origin at the lower left of the `MediaBox` as stored, y up, 1 unit = 1/72 inch (times `/UserUnit`). `/Rotate` and the CropBox are not applied to what you draw, so a page rotated by 90 shows your content rotated too; the stamp helpers compensate for `/Rotate` so a stamp reads upright. Numbers are written rounded to four decimals in the invariant culture, with no fused multiply-add anywhere, so the same calls give the same bytes on every platform. Non-finite numbers are refused.

## Builders

`PdfContentBuilder` is the drawing surface; `PdfPageContentBuilder` (from `page.BeginContent(placement)`) and `PdfFormContentBuilder` (from `doc.BeginForm(bbox)`) add `Commit()`. A builder collects content and writes it on `Commit`; disposing it first discards it. Open `Save()` levels are closed by `Commit`.

- **Over or under.** `PdfContentPlacement.Overlay` (default) puts the new content after the page's content. The page's own content stays byte for byte as it was, but it is wrapped in `q` ... `Q`, with one extra `Q` per `q` it leaves open (found by a lexical scan), so a stray `cm`, colour, clip or unbalanced `q` cannot reach your content. `Underlay` puts yours first, in its own saved state. Both add a new content stream to the page's `/Contents` array; shared `q` and `Q` streams are created once per document.
- **Resources.** Fonts, XObjects and ExtGStates are added to a page-owned copy of the resource dictionary when the page's was inherited or shared, so other pages are not touched. Names are `OpF1`, `OpIm1`, `OpFm1`, `OpGs1`, ... skipping any name already in use.
- **Graphics.** `MoveTo`, `LineTo`, `CurveTo`, `Rectangle`, `Ellipse`, `ClosePath`; `Fill`, `Stroke`, `FillAndStroke` (nonzero or even-odd), `Clip`, `EndPath`; `SetLineWidth`, `SetLineCap`, `SetLineJoin`, `SetMiterLimit`, `SetDash`; `Save`/`Restore`, `Transform`, `Translate`, `Scale`, `Rotate` (multiples of 90 are exact).
- **Colour and transparency.** DeviceGray, DeviceRGB and DeviceCMYK for fill and stroke (components clamped to 0..1); `SetFillOpacity`/`SetStrokeOpacity`/`SetOpacity` (`/ca`, `/CA`) and `SetBlendMode`, each an ExtGState shared by value within the builder.
- **Images.** `doc.AddJpegImage` embeds the JPEG file as it is (`DCTDecode`; size and 1, 3 or 4 components read from the header; Adobe CMYK gets the inverting `/Decode`; progressive is fine; arithmetic-coded and lossless are refused). `doc.AddRawImage` takes 8-bit Gray, RGB or CMYK samples and an optional alpha plane (Flate, and a `/SMask`). `doc.AddPngImage` reads a PNG file (see [PNG images](#png-images)). `DrawImage(img, x, y, w, h)` places an image in the box in current coordinates; `DrawImage(PdfImage, ...)` draws again an image XObject of the same document that the interpreter reported.
- **Forms.** `doc.BeginForm(bbox)` returns a builder; `Commit()` gives a `PdfFormXObject`, drawn any number of times, on any pages, with `DrawForm`, positioned by the transform stack. `DrawForm(PdfStream)` draws a Form XObject already in the document.
- **Stamps.** `doc.AddTextStamp(text, font, size, options, pages)` and `doc.AddImageStamp(image, w, h, options, pages)` draw into one shared form and place it on each chosen page (default all), centred on the CropBox (or `CenterX`/`CenterY`), rotated, at an opacity and blend mode, over or under the content.

## Text

`SetFont(font, size)`, `SetCharacterSpacing`, `SetWordSpacing`, `SetTextRenderMode`; text is painted with the fill colour (and stroke colour and line width in stroke modes). `DrawText(text, x, y)` places the start of the baseline, or give a full text matrix. `MeasureText` returns the width with the current font, size and spacing; `WrapText(text, width)` splits at spaces and `\n` and breaks a word wider than the line between characters; `DrawTextAligned` places one line in a span; `DrawTextBox` wraps into a box (first baseline one ascent below the top, `size * lineSpacing` apart) and returns the text that did not fit. Justified lines widen word gaps; the last line of a paragraph stays left aligned.

`DrawText` and the text-box methods do no shaping: no kerning, ligatures, contextual forms, bidirectional reordering or complex-script layout. Characters are placed one after another in the order of the string, using the font's `cmap` and advance widths. Control characters are skipped. Text that needs shaping is drawn from glyphs a shaping engine has already chosen: see [Shaped glyph runs](#shaped-glyph-runs).

### Fonts

- **Standard 14** (`AddStandardFont`): names and the usual aliases (`Arial`, `TimesNewRoman`, `CourierNew`, ...). Text is WinAnsi; Symbol and ZapfDingbats use their built-in encodings. Widths come from the Adobe metrics; viewers draw with their own copy of the font. A character outside the encoding is drawn as `?` and recorded in `doc.Diagnostics`.
- **Embedded** (`AddFont(bytes, options)`): a `.ttf`, `.otf` or `.ttc` face (`PdfEmbedFontOptions.FaceIndex`). The font is written as a Type 0 font with `Identity-H`, a `CIDFontType2` (TrueType outlines) or `CIDFontType0` (CFF outlines) descendant, `/W` widths (rounded to whole thousandths of the size, the same numbers `MeasureText` uses) and a `/ToUnicode` map, so text extraction recovers the characters. Codes are assigned in order of first use (code 1, 2, ...) in a subset, so the same text always gives the same codes.
  - **Subset.** By default the font is cut down when the document is saved (so more text can still be added until then): the glyphs used, plus the components of composite glyphs, renumbered compactly, in a font named `ABCDEF+PostScriptName` (six capital letters derived from a hash of the font and glyph list; ISO 32000-2 9.6.4). TrueType subsets keep `head`, `hhea`, `maxp`, `hmtx`, `loca`, `glyf` and the hinting tables `cvt `, `fpgm`, `prep`; `cmap`, `name`, `post` and `OS/2` are dropped. CFF subsets are written as CID-keyed CFF (a name-keyed font becomes one FD) with a charset in which CID equals glyph index; charstrings are copied as they are, and subroutines the kept glyphs never call are emptied to a lone `return`. A glyph that uses the deprecated `endchar` accent form is refused.
  - **Whole font.** `Subset = false`, or a font whose `fsType` has the no-subsetting bit (0x0100), is embedded unchanged (CID = glyph index; for a CID-keyed CFF font, the charset CID).
  - **Licensing.** `fsType` restricted-license (0x0002 without 0x0004 or 0x0008) and bitmap-only (0x0200) fonts are refused with `PdfFontEmbeddingException`. `IgnoreEmbeddingRestrictions` embeds anyway; whether you may is your responsibility.
  - **Errors.** A damaged font gives `PdfFormatException`: an unknown sfnt version, missing tables, a `loca` that is not ascending or points past `glyf` (all checked in `AddFont`); a composite glyph that contains itself, nests deeper than 8, or names a missing component (found when the glyph is subset, at `Save`). Subsetting reads at most 65,535 glyphs and never allocates more than the font's own size times a small constant. A character with no glyph is drawn as `.notdef` (glyph 0) and recorded in `doc.Diagnostics`.

## PNG images

`doc.AddPngImage(bytes)` (also on `PdfDocumentWriter`) reads a PNG (W3C PNG / ISO 15948) and returns a `PdfCreatedImage` that `DrawImage` places like a JPEG. It is a minimal reader, not a codec: it reads what a PDF image needs and writes nothing.

| PNG | Result |
|---|---|
| Gray 1, 2, 4, 8, 16 bits; RGB 8, 16; palette 1, 2, 4, 8 | **Passed through.** The IDAT payload goes into a `FlateDecode` stream with `/Predictor 15` (ISO 32000-2 7.4.4.4) byte for byte; nothing is decoded or recompressed. Palette images become `/Indexed` over `DeviceRGB`, padded to 2^bits entries. |
| Adam7 interlaced | Decoded with the library's inflater, written as plain packed rows, Flate-compressed on save. |
| Gray+alpha, RGB+alpha (8, 16) | Decoded; colour samples as above, alpha as a `/SMask` of the same bit depth. |
| `tRNS` on gray or RGB | A colour-key `/Mask`; the image data is still passed through. |
| `tRNS` on a palette | A colour-key `/Mask` when the transparent entries are fully transparent and form one run; otherwise decoded, with an 8-bit `/SMask` built from the indices. |
| `iCCP` with an RGB or gray profile that matches the image | `ICCBased` colour space (the compressed profile is stored as it is). A profile that does not match, or is damaged, is dropped with a warning. |

`gAMA`, `cHRM`, `sRGB`, `sBIT`, text chunks and the other ancillary chunks are ignored, and samples are used as stored. APNG animation frames are ignored: the default image is used. A CRC mismatch in IHDR, PLTE or IDAT is a `PdfFormatException`; in `tRNS` or `iCCP` the chunk is dropped with a warning in `Diagnostics`. Pixel count and decoded size are checked against `PdfLimits` from the header, before any pixel memory is allocated; the decoded size of an interlaced image or an image with alpha is computed from the header and the inflater is capped at that size, so a deflate bomb cannot grow past the image.

## Shaped glyph runs

`builder.DrawGlyphRun(x, y, glyphs, text)` draws glyphs a shaping engine has already chosen and placed, with an embedded font (`SetFont` with a font from `AddFont`). It takes plain data and depends on no shaping library.

```csharp
b.SetFont(devanagari, 24);
// "कि": KA + VOWEL SIGN I. The I-matra is drawn to the left of the consonant: two glyphs, one cluster.
b.DrawGlyphRun(50, 100, [new PdfShapedGlyph(matraGid, Cluster: 0, Advance: 400),
                         new PdfShapedGlyph(kaGid,     Cluster: 0, Advance: 600)], "कि");
```

- Each `PdfShapedGlyph` has a glyph index, a **cluster** (the UTF-16 index in `text` of the first character it stands for), an **advance** and an optional x and y **offset**, all in thousandths of the font size. Glyphs go in drawing order (left to right on the page), so right-to-left text is given in visual order, as shaping engines return it. A cluster's text runs from its index to the next larger cluster index present in the run, or to the end of the text.
- **Extraction returns logical text.** A cluster of one glyph adds the cluster's text to the font's `/ToUnicode` map (one glyph to one or more characters: a ligature `fi` is one entry). A cluster of several glyphs (reordered or split Indic syllables, a base with marks), a glyph that `/ToUnicode` already ties to different text, or a cluster that returns later in the run is wrapped in a `/Span` with `/ActualText` holding the cluster's text (ISO 32000-2 14.9.4). Text that has glyphs only inside such spans has no entry of its own.
- **Placement is exact.** Pen movement is the given advances, not the font's widths; character spacing set with `SetCharacterSpacing` is compensated. Offsets move a glyph without moving the pen (x as a position adjustment, y as text rise). The call returns the distance moved in points. Glyph 0 (the missing-glyph box) is allowed; a glyph index outside the font, a cluster outside the text, or a non-finite number is an `ArgumentOutOfRangeException`.
- **Subsetting** is unchanged: the glyphs used are collected and the subset is cut at save time (or, with `PdfDocumentWriter`, at `Finish`).

## Marked content, artifacts and tagging

On any builder: `BeginMarkedContent(tag)` and `BeginMarkedContent(tag, mcid)` (`BMC`/`BDC`), `BeginSpan(actualText, alternateText, language, expansion)`, `BeginArtifact(type, subtype, boundingBox)` and `EndMarkedContent()`. Marked-content sequences and `Save`/`Restore` pairs must nest (ISO 32000-2 14.6.1): `Restore` throws while a sequence begun after its `Save` is open, and the other way round. `Commit` closes whatever is still open. A builder alone does not build a structure tree: it writes the marks. `PdfDocumentWriter` builds the tree.

## Streaming creation

`PdfDocumentWriter` writes a document to a stream as it is made, for documents too large to hold: a 10,000-page report, a 250-page table. Each page is written when committed, with its content stream, link annotations and parent-tree entry; images, forms and structure elements are written when they are made or ended. What stays in memory is bookkeeping: eight bytes per object offset, the page-tree entry, the parent-tree entry per page, bookmarks and named destinations, and the fonts. Measured with tagged pages of six paragraphs and a link each, live managed memory grows by about 580 bytes per page.

```csharp
using var file = File.Create("report.pdf");
using var w = new PdfDocumentWriter(file, new PdfDocumentWriterOptions { Language = "en" });
PdfCreatedFont font = w.AddFont(File.ReadAllBytes("LiberationSerif-Regular.ttf"));
PdfFormXObject total = w.ReserveForm(new PdfRectangle(0, 0, 40, 12));       // "of Y": filled once the count is known

PdfStructureElement document = w.BeginStructureElement("Document");
for (int i = 0; i < 10_000; i++)
{
    PdfStreamPageBuilder p = w.BeginPage(595, 842);
    PdfStructureElement para = w.BeginStructureElement("P");
    p.BeginTaggedContent(para);                       // BDC with the page's next MCID, bound to the element
    p.SetFont(font, 11);
    p.DrawText("Paragraph text", 50, 780);
    p.EndMarkedContent();
    w.EndStructureElement();

    p.BeginArtifact(PdfArtifactType.Pagination, PdfArtifactSubtype.Footer);
    p.DrawText($"Page {i + 1} of ", 250, 30);
    p.Save(); p.Translate(310, 30); p.DrawForm(total); p.Restore();
    p.EndMarkedContent();
    p.Commit();
}
w.EndStructureElement();

PdfFormContentBuilder last = w.BeginForm(total);      // fill the reserved form
last.SetFont(font, 11);
last.DrawText(w.PageCount.ToString(), 0, 2);
last.Commit();
w.Finish();
```

- **Written at once:** pages, content, annotations, images (`AddJpegImage`, `AddPngImage`, `AddRawImage`), forms (`BeginForm`), ended structure elements. **Kept until `Finish`:** fonts (their subsets are cut then, so any glyph can still be added), forms from `ReserveForm`, the page tree (nodes of 64 pages), the structure tree's root and parent tree, the outline, named destinations, the information dictionary and the catalog.
- **One page is open at a time.** A page builder that is disposed without `Commit` is dropped; one with structure bound to it must be committed. `Finish` throws if a page, a structure element or a reserved form is still open.
- **Output:** a compressed cross-reference stream by default, or a classic table (`PdfXrefFormat.Table`); no object streams, since objects are written as they become final. Reproducible with `DocumentId`. Encryption is not available on this path; save the result through `PdfDocument` for that.
- **Outline and destinations:** `AddBookmark(title, level, pageIndex, top)` (levels start at 0 and rise by at most one) and `AddNamedDestination(name, pageIndex, top)`, with `AddLinkToDestination` for links that name a destination, defined before or after the link. `AddLink(area, uri, element, description)` writes a URI link.
- **Tagged structure (ISO 32000-2 14.7, 14.8):** `BeginStructureElement(type, options)` / `EndStructureElement()` nest elements; the first one makes the file tagged (`/MarkInfo /Marked true`, `/StructTreeRoot`, the page's `/StructParents`, `/ParentTree`, `/ParentTreeNextKey`, `/Tabs /S`). `PdfStructureElementOptions` carries `/Alt`, `/ActualText`, `/Lang`, `/T`, `/E`, `/ID` and an attribute object (`PdfStructureAttributes.TableHeader("Column")`, `Table(colSpan, rowSpan)`, `List("Decimal")`). `MapRole` adds `/RoleMap` entries for custom types. An element can take content from several pages (the extra pages use `MCR` entries); a link ties its annotation to a `Link` element with an `OBJR` entry and the annotation's `/StructParent`. An element is written when it ends, so memory follows the nesting depth, not the size of the document. Content that is neither tagged nor an artifact is the caller's to avoid; the writer does not check it.
- **Reading back:** text extraction follows the tree when the page is covered by it (`PdfPageText.UsedStructureTree`); page import and redaction keep the tree consistent. veraPDF reports no structure rule failures when such a file is saved as PDF/A-2b; the PDF/UA-1 profile still wants an identification entry in the XMP metadata and an `/ID` on `Note` elements.

## Reproducible output

The same calls give the same bytes: content numbers are fixed-precision, subset tags and code assignment are deterministic, and nothing reads the clock. A new document gets a random trailer `/ID` unless `PdfSaveOptions.DocumentId` (16 bytes) is given; an existing document keeps its `/ID`. Encrypted output is not reproducible (random IVs and salts).

## Not covered

Text shaping itself (glyph runs take the result of a shaping engine); Type 1 and bare CFF font files as input; variable-font instances; vertical writing; emoji and colour fonts (only outlines are embedded); a general page layout engine, tables or images fitted to boxes; checking that all content of a tagged document is tagged or an artifact; encryption of streamed output; compressing content streams of `PdfDocument.Save` (use `PdfSaveOptions.CompressStreams`). To save a created document as PDF/A, see [PDF/A](https://github.com/recursive-ai-oss/onepdf-csharp/blob/main/docs/pdfa.md).
