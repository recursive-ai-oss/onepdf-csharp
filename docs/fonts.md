# Embedding fonts into existing documents

A tagged-PDF or PDF/A file needs every font program embedded (ISO 14289-1 7.21.4.1, ISO 19005-2 6.2.11.4.1), and the advance widths in the program must match the font dictionary's `/Widths` (7.21.5). Many real files name Arial, Times New Roman, Courier New or a standard-14 face and embed nothing. OnePDF can put a program into those font dictionaries without changing the text or where any glyph sits.

The document must be opened with `PdfOpenOptions.AllowEditing`.

## Happy path

```csharp
using PdfDocument doc = PdfDocument.Open(path, new PdfOpenOptions { AllowEditing = true });
doc.FontProvider = OnePdf.Fonts.FontsPackage.Provider;      // the Liberation and Noto substitutes
PdfSubstituteEmbedReport report = doc.EmbedSubstituteFonts();
foreach (PdfSubstituteEntry e in report.Entries)
    Console.WriteLine($"{e.BaseFont}: {e.Outcome} {e.Message}");
doc.Save(output);
```

**The substitutes come from a font provider, and nothing finds one by itself.** `RecursiveAI.OnePdf` does not depend on `RecursiveAI.OnePdf.Fonts` (the OFL fonts are a separate package on purpose, and OnePDF uses no reflection or assembly scanning to discover it). So reference `RecursiveAI.OnePdf.Fonts` and do one of:

- `OnePdf.Fonts.FontsPackage.Register();` once at startup, which adds the package's provider to the process-wide chain (`PdfFontProviders`) for every document and also gives non-embedded fonts outlines when drawing; or
- `doc.FontProvider = OnePdf.Fonts.FontsPackage.Provider;` for one document; or
- `new PdfSubstituteEmbedOptions { Provider = ... }` for one call.

With none of these, `EmbedSubstituteFonts` embeds nothing: each font that names a standard face gets the outcome `NoFontProvider` (not `NoSubstitute`), `report.FontProviderMissing` is true and `report.ProviderMessage` and every such entry's `Message` name the package and the call. A PDF/UA or PDF/A save with `EmbedSubstituteFonts = true` and no provider throws `InvalidOperationException` with the same message, rather than going on to report unembedded fonts.

Or let a PDF/UA or PDF/A save do it:

```csharp
doc.Save(output, new PdfSaveOptions { PdfUA = new PdfUAOptions { Language = "en-US", EmbedSubstituteFonts = true } });
```

`EmbedSubstituteFonts` goes through every font dictionary that is not embedded and picks a substitute from the provider: Arial, Helvetica and the other sans names get Liberation Sans, Times and its aliases Liberation Serif, Courier and Courier New Liberation Mono, with bold and italic taken from the name and the descriptor flags. Symbol and ZapfDingbats try each face the provider offers. A `PdfSubstituteEntry` says per font whether it was `Embedded`, `AlreadyEmbedded`, had `NoSubstitute` (for example Verdana, which has no metric-compatible face), had `NoFontProvider` (see above) or was `Refused` and why. An `Embedded` entry also carries what changed visibly for that font: `WidthChanges` (codes whose PDF width differs from the substitute's own advance; the subset carries the PDF's), `WidthConflicts` (codes that share a glyph but have different PDF widths, which can only keep one of them) and `MissingCodes` (shown codes the substitute has no glyph for, which draw `.notdef`). A Type 0 font is only done when it has a `/ToUnicode` to map its codes to glyphs.

The option is off by default in `PdfUAOptions` and `PdfAOptions` (`EmbedSubstituteFonts`), because the letter shapes in a viewer change from the viewer's own Arial to the embedded Liberation face. `EmbedLibraryFonts` is a different option: it only covers standard fonts that OnePDF's own writer added.

## Which face each font gets

| The document names | Embedded face | Package licence notice |
|---|---|---|
| Helvetica, Arial and their bold and italic forms | Liberation Sans (Regular, Bold, Italic, Bold Italic) | SIL OFL 1.1 |
| Times, Times New Roman | Liberation Serif | SIL OFL 1.1 |
| Courier, Courier New | Liberation Mono | SIL OFL 1.1 |
| Symbol | Noto Sans Math, with Liberation Serif for the few Latin-range glyphs | SIL OFL 1.1 |
| ZapfDingbats | Noto Sans Symbols 2, then Noto Sans Math and Noto Sans Symbols for the characters it lacks | SIL OFL 1.1 |

Bold and italic follow the font name and the descriptor flags. For Symbol and ZapfDingbats the first face that has every shown character is used. A document that gets Noto glyphs embeds a subset of Noto Sans Symbols 2 or Noto Sans Math, not Liberation, so an audit list of embedded fonts names those families. The licence texts and copyright lines of all of these ship in `RecursiveAI.OnePdf.Fonts` (each font's `OFL.txt`) and in `THIRD-PARTY-NOTICES.md`; the package's licence expression is `(MIT OR Apache-2.0) AND OFL-1.1`. A subset embedded in a document is covered by OFL-FAQ 1.12, and the subset carries no `name` table, so no Reserved Font Name is used.

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

## Filling in `/ToUnicode`

```csharp
PdfToUnicodeReport report = doc.AddMissingToUnicode();
foreach (PdfToUnicodeEntry e in report.Entries)
    Console.WriteLine($"{e.BaseFont}: {e.CodesAdded} added, no Unicode for {e.CodesWithoutUnicode.Count} codes");
```

ISO 14289-1 7.21.7 wants a `/ToUnicode` map for a font whose encoding does not say what its codes mean (a `/Differences` array, a symbolic or Identity-encoded font). `AddMissingToUnicode` writes entries for the codes the document shows, taking each value from the same chain text extraction uses: the encoding and `/Differences` glyph names through the Adobe Glyph List, then the embedded font's character map. A code the chain cannot read gets no entry and is listed in `CodesWithoutUnicode`: nothing is guessed, so such a font still fails 7.21.7 and needs `SetToUnicode` with values from the caller. Entries already in a map are kept. Text extraction does not change, because it reads the same chain. Fonts whose encoding already determines Unicode (WinAnsi, MacRoman or Standard without `/Differences`; Adobe CJK collections) are skipped, as are Type 3 fonts. Needs an editable document.

## Limits

- Only TrueType programs are written. An already embedded Type1C/CFF font whose widths or `/ToUnicode` fail 7.21.5 or 7.21.7 is not repaired.
- A `/ToUnicode` is only written for codes with a known Unicode value; guessing is never done.
- Embedded programs follow the font licence: `fsType` is honoured, and the substitute faces are under the SIL OFL 1.1. Embedding a subset in a document is covered by OFL-FAQ 1.12; see `THIRD-PARTY-NOTICES.md`.
