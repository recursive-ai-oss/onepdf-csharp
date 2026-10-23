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
- **Images.** `doc.AddJpegImage` embeds the JPEG file as it is (`DCTDecode`; size and 1, 3 or 4 components read from the header; Adobe CMYK gets the inverting `/Decode`; progressive is fine; arithmetic-coded and lossless are refused). `doc.AddRawImage` takes 8-bit Gray, RGB or CMYK samples and an optional alpha plane (Flate, and a `/SMask`). `DrawImage(img, x, y, w, h)` places an image in the box in current coordinates; `DrawImage(PdfImage, ...)` draws again an image XObject of the same document that the interpreter reported.
- **Forms.** `doc.BeginForm(bbox)` returns a builder; `Commit()` gives a `PdfFormXObject`, drawn any number of times, on any pages, with `DrawForm`, positioned by the transform stack. `DrawForm(PdfStream)` draws a Form XObject already in the document.
- **Stamps.** `doc.AddTextStamp(text, font, size, options, pages)` and `doc.AddImageStamp(image, w, h, options, pages)` draw into one shared form and place it on each chosen page (default all), centred on the CropBox (or `CenterX`/`CenterY`), rotated, at an opacity and blend mode, over or under the content.

## Text

`SetFont(font, size)`, `SetCharacterSpacing`, `SetWordSpacing`, `SetTextRenderMode`; text is painted with the fill colour (and stroke colour and line width in stroke modes). `DrawText(text, x, y)` places the start of the baseline, or give a full text matrix. `MeasureText` returns the width with the current font, size and spacing; `WrapText(text, width)` splits at spaces and `\n` and breaks a word wider than the line between characters; `DrawTextAligned` places one line in a span; `DrawTextBox` wraps into a box (first baseline one ascent below the top, `size * lineSpacing` apart) and returns the text that did not fit. Justified lines widen word gaps; the last line of a paragraph stays left aligned.

There is no shaping: no kerning, ligatures, contextual forms, bidirectional reordering or complex-script layout. Characters are placed one after another in the order of the string, using the font's `cmap` and advance widths. Arabic, Hebrew in mixed text, Indic scripts and Thai marks are therefore not supported. Control characters are skipped.

### Fonts

- **Standard 14** (`AddStandardFont`): names and the usual aliases (`Arial`, `TimesNewRoman`, `CourierNew`, ...). Text is WinAnsi; Symbol and ZapfDingbats use their built-in encodings. Widths come from the Adobe metrics; viewers draw with their own copy of the font. A character outside the encoding is drawn as `?` and recorded in `doc.Diagnostics`.
- **Embedded** (`AddFont(bytes, options)`): a `.ttf`, `.otf` or `.ttc` face (`PdfEmbedFontOptions.FaceIndex`). The font is written as a Type 0 font with `Identity-H`, a `CIDFontType2` (TrueType outlines) or `CIDFontType0` (CFF outlines) descendant, `/W` widths (rounded to whole thousandths of the size, the same numbers `MeasureText` uses) and a `/ToUnicode` map, so text extraction recovers the characters. Codes are assigned in order of first use (code 1, 2, ...) in a subset, so the same text always gives the same codes.
  - **Subset.** By default the font is cut down when the document is saved (so more text can still be added until then): the glyphs used, plus the components of composite glyphs, renumbered compactly, in a font named `ABCDEF+PostScriptName` (six capital letters derived from a hash of the font and glyph list; ISO 32000-2 9.6.4). TrueType subsets keep `head`, `hhea`, `maxp`, `hmtx`, `loca`, `glyf` and the hinting tables `cvt `, `fpgm`, `prep`; `cmap`, `name`, `post` and `OS/2` are dropped. CFF subsets are written as CID-keyed CFF (a name-keyed font becomes one FD) with a charset in which CID equals glyph index; charstrings are copied as they are, and subroutines the kept glyphs never call are emptied to a lone `return`. A glyph that uses the deprecated `endchar` accent form is refused.
  - **Whole font.** `Subset = false`, or a font whose `fsType` has the no-subsetting bit (0x0100), is embedded unchanged (CID = glyph index; for a CID-keyed CFF font, the charset CID).
  - **Licensing.** `fsType` restricted-license (0x0002 without 0x0004 or 0x0008) and bitmap-only (0x0200) fonts are refused with `PdfFontEmbeddingException`. `IgnoreEmbeddingRestrictions` embeds anyway; whether you may is your responsibility.
  - **Errors.** A damaged font gives `PdfFormatException`: an unknown sfnt version, missing tables, a `loca` that is not ascending or points past `glyf` (all checked in `AddFont`); a composite glyph that contains itself, nests deeper than 8, or names a missing component (found when the glyph is subset, at `Save`). Subsetting reads at most 65,535 glyphs and never allocates more than the font's own size times a small constant. A character with no glyph is drawn as `.notdef` (glyph 0) and recorded in `doc.Diagnostics`.

## Reproducible output

The same calls give the same bytes: content numbers are fixed-precision, subset tags and code assignment are deterministic, and nothing reads the clock. A new document gets a random trailer `/ID` unless `PdfSaveOptions.DocumentId` (16 bytes) is given; an existing document keeps its `/ID`. Encrypted output is not reproducible (random IVs and salts).

## Not covered

Text shaping and complex scripts; Type 1 and bare CFF font files as input; variable-font instances; vertical writing; emoji and colour fonts (only outlines are embedded); a general page layout engine, tables or images fitted to boxes; generating tagged structure or PDF/A; compressing content streams (use `PdfSaveOptions.CompressStreams`).
