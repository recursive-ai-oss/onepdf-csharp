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

`EmbedSubstituteFonts` goes through every font dictionary that is not embedded and picks a substitute from the provider: Arial, Helvetica and the other sans names get Liberation Sans, Times and its aliases Liberation Serif, Courier and Courier New Liberation Mono, with bold and italic taken from the name and the descriptor flags. Symbol and ZapfDingbats try each face the provider offers. A `PdfSubstituteEntry` says per font whether it was `Embedded`, `AlreadyEmbedded`, had `NoSubstitute` (for example Verdana, which has no metric-compatible face), had `NoFontProvider` (see above) or was `Refused` and why. An `Embedded` entry also carries what changed visibly for that font: `WidthChanges` (codes whose PDF width differs from the substitute's own advance; the subset carries the PDF's), `WidthConflicts` (codes that share a glyph but have different PDF widths, which can only keep one of them) and `MissingCodes` (codes the pages show that the substitute has no glyph for, which draw `.notdef`; a code the pages never show is not listed, even when the font is kept whole because a form may still be filled in with it, and when a font's use cannot be traced every code its encoding names counts as possibly shown). A Type 0 font is only done when it has a `/ToUnicode` to map its codes to glyphs.

**The report is not a render comparison.** `WidthChanges`, `WidthConflicts` and `MissingCodes` say where metrics and coverage differ. They say nothing about how the letters look: a substitute that has every glyph and every width can still draw a different shape (Liberation Sans for Arial is close; for a face like Verdana or a script font it is not). Where the look matters, render the page before and after and compare the images.

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

`PdfFontFile` reads TrueType, OpenType-CFF and bare CFF (`Kind`), exposes metrics, `fsType` (`EmbeddingRights`, `AllowsEmbedding`, `AllowsSubsetting`) and cmap and glyph-name lookups. `EmbedFontProgram` supports TrueType programs only; a CFF program is read but refused (`UnsupportedProgram`). `PdfFontEmbedOptions` has `Subset`, `Codes`, `Replace` (swap the program of an already embedded font), `IgnoreEmbeddingRestrictions`, `Widths`, `MaxWidthDifference`, `MissingGlyphs` (`Refuse` throws `NoGlyphForCode`; `UseNotdef` draws .notdef and lists the codes in `MissingCodes`), `CodeToUnicode`, `GlyphForCode`, `CidsAreGlyphIds`, `OverrideToUnicode` and `WriteToUnicode`. The happy path uses `UseNotdef` and reports; the low level refuses.

Refusals are `PdfFontEmbedException` with a `Reason`: `NoGlyphForCode`, `ProgramForbidsEmbedding`, `AlreadyEmbedded`, `Type3Font`, `UnsupportedFont`, `UnsupportedProgram`, `WidthMismatch`, `TooLarge`.

## Filling in `/ToUnicode`

```csharp
PdfToUnicodeReport report = doc.AddMissingToUnicode();
foreach (PdfToUnicodeEntry e in report.Entries)
    Console.WriteLine($"{e.BaseFont}: {e.CodesAdded} added, no Unicode for {e.CodesWithoutUnicode.Count} codes");
```

ISO 14289-1 7.21.7 wants a `/ToUnicode` map for a font whose encoding does not say what its codes mean (a `/Differences` array, a symbolic or Identity-encoded font). `AddMissingToUnicode` writes entries for the codes the document shows, taking each value from the same chain text extraction uses: the encoding and `/Differences` glyph names through the Adobe Glyph List, then the embedded font's character map. A code the chain cannot read gets no entry and is listed in `CodesWithoutUnicode`: nothing is guessed, so such a font still fails 7.21.7 and needs `SetToUnicode` with values from the caller. Entries already in a map are kept. Text extraction does not change, because it reads the same chain.

A map that already exists can still fail 7.21.7 for a reason of syntax: a `bfrange` that runs across a boundary of the last code byte, such as `<0000> <FFFF> <0000>`. ISO 32000-2 9.10.3 and Adobe Technical Note #5411 increment only the last byte and keep a range within one block of 256 codes, and readers that follow them read such a range correctly only for the first 256 codes. OnePDF reads the range as written, so extraction was never affected, but `AddMissingToUnicode` now writes such a map again with the same meaning in ranges of at most 256 codes (`PdfToUnicodeEntry.MapRewritten`). Fonts whose encoding already determines Unicode (WinAnsi, MacRoman or Standard without `/Differences`; Adobe CJK collections) are skipped, as are Type 3 fonts. Needs an editable document.

## Text for codes OnePDF cannot read

Some embedded subsets say nothing about what their glyphs are: a CFF subset whose glyphs are named `gid00132`, a TrueType subset without a character map, a composite font whose `/ToUnicode` lacks codes. The code's Unicode cannot be derived from the file, and a glyph's shape does not say which character it is, so OnePDF never guesses. `AddMissingToUnicode` reports them with what is known about each glyph, and the caller can supply the text:

```csharp
PdfToUnicodeReport report = doc.AddMissingToUnicode(new PdfToUnicodeOptions
{
    CodeToText = u => LookUp(u.BaseFont, u.Code, u.GlyphName, u.GlyphId, u.Width),   // null leaves the code unmapped
});
// Or look first: every PdfToUnicodeEntry lists its Unmapped codes and carries the font dictionary.
foreach (PdfToUnicodeEntry e in doc.AddMissingToUnicode().Entries)
    doc.SetToUnicode(e.Font!, e.Unmapped.ToDictionary(u => u.Code, u => Decide(u)));
```

`CodeToText` is asked once for each shown code the chain cannot map, and what it returns is written to the font's `/ToUnicode` (`CodesFromCaller` counts them). Both routes only write the font dictionary's `/ToUnicode`, so they work for every font kind, including embedded CFF, OpenType-CFF and Type 0 fonts with CIDFontType0 descendants; the font program is never read or changed. Extraction then returns the caller's text for those codes, and nothing else changes. Two subsets of the same font in one file are mapped separately: a code is never mapped from another font's glyph, because identical charstrings in two subsets can still belong to different characters in a producer's private numbering.

## Widths of fonts that are already embedded

A font that was subset from a variable font often carries the instance's fractional advances in its program while the PDF lists other widths, so ISO 14289-1 7.21.5 fails. For embedded CFF programs (`FontFile3` of subtype `Type1C` or `CIDFontType0C`, simple or composite) the caller can bring the program to the PDF:

```csharp
PdfWidthMatchReport report = doc.MatchEmbeddedFontWidths();
foreach (PdfWidthMatchEntry e in report.Entries)
    Console.WriteLine($"{e.BaseFont}: {e.Outcome} ({e.WidthChanges.Count} widths) {e.Note}");
```

The shown glyphs get the PDF's widths and nothing else changes: the width is the first operand of a glyph's charstring (Adobe Technical Note #5177 section 3.1), and that operand is rewritten. `defaultWidthX` and `nominalWidthX` become whole numbers, since readers differ in how they take their fractions, so every glyph is written again against the new defaults and keeps its width unless it is a shown one. Outlines, glyph names, charset and encoding are copied, the new program is read back and compared glyph by glyph with the old one before it replaces it, and the descriptor is replaced by a copy so fonts that shared the old one are not affected. Text positions come from the PDF's widths and are not touched; the page renders identically.

A glyph that reaches its width through a subroutine call is not handled, and the font is left as it was (`Refused`, with the reason in `Note`). TrueType, OpenType and Type 1 programs are examined but not rewritten (`UnsupportedProgram`); fonts embedded by OnePDF (`EmbedFontProgram`, `EmbedSubstituteFonts`) already carry the PDF's widths. `PdfWidthMatchOptions.Tolerance` (default 0.01 in 1/1000 em) sets how small a difference is ignored.

## A face of your own for fonts with no substitute

Verdana, Calibri and most other faces have no metric-compatible substitute in the Fonts package, so `EmbedSubstituteFonts` reports `NoSubstitute` for them. A caller who has a suitable face (and the right to embed it) supplies it:

```csharp
doc.EmbedSubstituteFonts(new PdfSubstituteEmbedOptions
{
    FaceForFont = r => r.FontName == "Verdana-Bold" ? PdfFontFile.Open(File.ReadAllBytes("DejaVuSans-Bold.ttf")) : null,
});
```

`FaceForFont` is asked only for fonts the provider has no face for; the provider's faces stay the default. The face is embedded as a subset with the PDF's widths forced onto its glyphs, exactly like a provider's face, and the entry says so (`FromCaller`). The request carries the font name without the subset tag and the descriptor's weight, style, serif and pitch flags, so one callback can pick a face by style. The program must have TrueType outlines (otherwise `Refused`/`UnsupportedProgram`). A shown code the face lacks draws `.notdef` and is listed in `MissingCodes`. Licensing is the caller's: `fsType` is checked, nothing else. Choose a face with similar proportions, because the PDF's advances are kept and the program's are not; see the note above on shapes.

## Limits

- Substitutes and caller faces must be TrueType programs. An already embedded CFF font gets its widths matched (`MatchEmbeddedFontWidths`), not replaced; an already embedded TrueType font whose widths disagree with the PDF is only reported.
- A `/ToUnicode` is only written for codes with a known Unicode value or one the caller supplied; guessing is never done.
- Embedded programs follow the font licence: `fsType` is honoured, and the substitute faces are under the SIL OFL 1.1. Embedding a subset in a document is covered by OFL-FAQ 1.12; see `THIRD-PARTY-NOTICES.md`.
