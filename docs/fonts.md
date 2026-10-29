# Embedding fonts into existing documents

A tagged-PDF or PDF/A file needs every font program embedded (ISO 14289-1 7.21.4.1, ISO 19005-2 6.2.11.4.1), and the advance widths in the program must match the font dictionary's `/Widths` (7.21.5). Many real files name Arial, Times New Roman, Courier New or a standard-14 face and embed nothing. OnePDF can put a program into those font dictionaries without changing the text or where any glyph sits.

The document must be opened with `PdfOpenOptions.AllowEditing`.

## Happy path

```csharp
using PdfDocument doc = PdfDocument.Open(path, new PdfOpenOptions { AllowEditing = true });
doc.FontProvider = OnePdf.Fonts.FontsPackage.Provider;      // the Liberation substitutes
PdfSubstituteEmbedReport report = doc.EmbedSubstituteFonts();
foreach (PdfSubstituteEntry e in report.Entries)
    Console.WriteLine($"{e.BaseFont}: {e.Outcome} {e.Message}");
doc.Save(output);
```

Or let a PDF/UA or PDF/A save do it:

```csharp
doc.Save(output, new PdfSaveOptions { PdfUA = new PdfUAOptions { Language = "en-US", EmbedSubstituteFonts = true } });
```

`EmbedSubstituteFonts` goes through every font dictionary that is not embedded and picks a substitute from the provider: Arial, Helvetica and the other sans names get Liberation Sans, Times and its aliases Liberation Serif, Courier and Courier New Liberation Mono, with bold and italic taken from the name and the descriptor flags. Symbol and ZapfDingbats try each face the provider offers. A `PdfSubstituteEntry` says per font whether it was `Embedded`, `AlreadyEmbedded`, had `NoSubstitute` (for example Verdana, which has no metric-compatible face) or was `Refused` and why. A Type 0 font is only done when it has a `/ToUnicode` to map its codes to glyphs.

The option is off by default in `PdfUAOptions` and `PdfAOptions` (`EmbedSubstituteFonts`), because the letter shapes in a viewer change from the viewer's own Arial to the embedded Liberation face. `EmbedLibraryFonts` is a different option: it only covers standard fonts that OnePDF's own writer added.

## What is written

- A subset of the substitute's TrueType program, as `/FontFile2` in a **new** font descriptor (descriptors shared between fonts are never edited). Only the glyphs of the codes the document uses are kept (every code of the font's `/FirstChar`..`/LastChar` range when the font is not found in any content stream).
- The base font name keeps the document's own name behind a six-letter subset tag (`ABCDEF+Arial`). The font program has no `name` table.
- Simple fonts become `/TrueType`. Non-symbolic fonts are mapped through their WinAnsi, MacRoman or `/Differences` encoding (glyph names including `uniXXXX`); symbolic ones through a (3,0) cmap at 0xF000+code and no `/Encoding`. Type 0 fonts with a `CIDFontType2` descendant get a `/CIDToGIDMap` stream.
- `/ToUnicode` is created or extended where the codes have a known Unicode value (7.21.7).

## Widths

`/Widths` is never rewritten (it is what the page layout was made with). Instead the subset's `hmtx` advances are set to the PDF's widths, so the program agrees with the dictionary (7.21.5) and every glyph keeps its position. Glyphs are drawn at their own shape; the substitute outline is not scaled to the width. Where one glyph is used by codes with different widths, the first width wins and the codes are listed in `WidthConflicts`. `PdfFontWidthPolicy.KeepProgram` leaves the program's own advances (and reports the differences in `WidthChanges`), `Refuse` throws `PdfFontEmbedException` (`WidthMismatch`) when they differ by more than `MaxWidthDifference` (1/1000 em).

Fonts that stay unembedded are drawn exactly as before: the provider's substitute glyphs are scaled to `/Widths` by the interpreter. Only a font that got a program embedded takes its outlines (and advances) from that program.

## Low level

```csharp
PdfFontFile file = PdfFontFile.Open(File.ReadAllBytes("MyFont.ttf"));     // PdfFormatException when damaged
if (!file.AllowsEmbedding || !file.AllowsSubsetting) { /* fsType forbids it */ }
PdfDictionary font = doc.Pages[0].Dictionary.GetDictionary("Resources")!.GetDictionary("Font")!.GetDictionary("F1")!;
PdfFontEmbedResult r = doc.EmbedFontProgram(font, file, new PdfFontEmbedOptions
{
    CodeToUnicode = new Dictionary<int, string> { [65] = "A" },   // for codes the font cannot map itself
});

doc.SetToUnicode(font, new Dictionary<int, string> { [0x20] = " " });   // merge entries into /ToUnicode
```

`PdfFontFile` reads TrueType, OpenType-CFF and bare CFF (`Kind`), exposes metrics, `fsType` (`EmbeddingRights`, `AllowsEmbedding`, `AllowsSubsetting`) and cmap and glyph-name lookups. Embedding supports TrueType programs only; a CFF program is read but refused (`UnsupportedProgram`). `PdfFontEmbedOptions` has `Subset`, `Codes`, `Replace` (swap the program of an already embedded font), `IgnoreEmbeddingRestrictions`, `Widths`, `MaxWidthDifference`, `MissingGlyphs` (`Refuse` throws `NoGlyphForCode`; `UseNotdef` draws .notdef and lists the codes in `MissingCodes`), `CodeToUnicode`, `GlyphForCode`, `CidsAreGlyphIds`, `OverrideToUnicode` and `WriteToUnicode`. The happy path uses `UseNotdef` and reports; the low level refuses.

Refusals are `PdfFontEmbedException` with a `Reason`: `NoGlyphForCode`, `ProgramForbidsEmbedding`, `AlreadyEmbedded`, `Type3Font`, `UnsupportedFont`, `UnsupportedProgram`, `WidthMismatch`, `TooLarge`.

## Limits

- Only TrueType programs are written. An already embedded Type1C/CFF font whose widths or `/ToUnicode` fail 7.21.5 or 7.21.7 is not repaired.
- A `/ToUnicode` is only written for codes with a known Unicode value; guessing is never done.
- Embedded programs follow the font licence: `fsType` is honoured, and the substitute faces are under the SIL OFL 1.1. Embedding a subset in a document is covered by OFL-FAQ 1.12; see `THIRD-PARTY-NOTICES.md`.
