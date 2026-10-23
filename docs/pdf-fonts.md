# PDF fonts

How a font dictionary becomes glyphs. Code: `src/OnePdf/Text/Fonts/`. The font programs, CMaps and encodings underneath are in `FontPrograms/` and `FontData/`.

## What a font gives the caller

`PdfDocument.GetFont(dictionary)` (internal) returns a `PdfFont`, built once per font object and shared by all threads. `font.Next(bytes, position, out glyph)` / `font.Decode(bytes, list)` split a shown string into `FontGlyph`s. A glyph is immutable and shared by every use of the same code:

| Member | Meaning |
|---|---|
| `Code`, `CodeLength` | the code and the bytes it took (1 for simple fonts, 1–4 for CMaps) |
| `Cid`, `Gid` | CID (the code for simple fonts); glyph index in the font program, -1 without a program |
| `Unicode`, `HasUnicode` | text, or U+FFFD with `HasUnicode` false |
| `Exists` | the program has a real glyph for it (not .notdef); for Type 3, a glyph procedure exists |
| `Advance` | horizontal advance for font size 1 (width / 1000; Type 3: width through `FontMatrix`) |
| `VerticalAdvance`, `OriginX`, `OriginY` | vertical writing: `W2`/`DW2` w1y and position vector v, size 1 |
| `OutlineMatrix` | glyph space to text space, with the horizontal scale of a substitute applied |
| `GetOutline()` | unhinted outline in glyph space (see Tricky TrueType fonts for the exception), empty when missing or damaged |
| `CharProc` | Type 3 glyph content stream |

Word spacing applies to a single-byte code 32 (`CodeLength == 1 && Code == 32`), as ISO 32000-2 9.3.3 says; the caller decides.

## Unicode chain

1. `/ToUnicode` (a NUL-only or empty mapping counts as no mapping).
2. Simple fonts: the glyph name from the encoding through the Adobe Glyph List (`uniXXXX`, `uXXXX`, suffixes and `_` components included; ZapfDingbats names first for that font).
3. Composite fonts: a Unicode-coded CMap (`Uni*-UCS2/UTF16/UTF32`): the code is the character.
4. The embedded program: a Unicode cmap read backwards (lowest code wins), then the program's glyph name through the AGL.
5. Composite fonts: the character collection's CID to Unicode table (Japan1, GB1, CNS1, Korea1, KR), then the CIDs that table leaves out: vertical variants and half-width forms, found by reading the `-V` and half-width predefined CMaps backwards; for the single-byte range of a legacy CMap, ASCII (and half-width katakana for Shift-JIS).
6. U+FFFD, glyph kept.

Ligature glyphs report their ligature character (`fi` is U+FB01); `Ligatures.Decompose` gives the letters for the extractor. Presentation forms and compatibility ideographs are reported as the file says; folding them is the extractor's job.

## Glyph selection

- **Type 1 / CFF (simple):** name from the encoding → program glyph; then the name before a `.` suffix; then the program's built-in encoding; then, if Unicode is known, a `uniXXXX` glyph. Encoding start: `/BaseEncoding` or the named encoding; else the program's built-in encoding; else (non-embedded Standard 14) its AFM encoding; else StandardEncoding for non-symbolic fonts.
- **TrueType (simple), ISO 32000-2 9.6.5.4:** with an `/Encoding` and not symbolic: name → Unicode → (3,1) cmap (or any Unicode cmap); name → MacRoman code → (1,0); name → `post` table. Otherwise (3,0) with the code, `F000`, `F100`, `F200` offsets; then (1,0) with the code; then a Unicode cmap reading the code as WinAnsi, then raw. Each step is tried only when the previous gave glyph 0.
- **Composite:** CMap → CID. CIDFontType2 with a TrueType program: `/CIDToGIDMap` stream or identity. Everything else: the CFF charset (`GlyphForCid`; identity for CFF that is not CID-keyed). A code the CMap does not map is CID 0.
- **Widths:** simple: `/Widths` by `FirstChar`, else `MissingWidth`; without `/Widths` the Standard 14 AFM, then the program's own advance. Composite: `/W`, else `/DW` (1000). Vertical: `/W2`, else `/DW2` (default 880, -1000) with v = (w0/2, DW2[0]).

## Substitution

A font with no usable program (none embedded, or the embedded one failed to parse, recorded as a warning diagnostic) asks the provider with a `PdfFontRequest` (name without subset tag, family, Standard 14 alias, bold/italic/serif/fixed/symbolic, CID ordering, vertical). The substitute's glyph is found by name, then by the glyph's Unicode through its cmap, and scaled horizontally so its advance equals the PDF width (ratio limited to 0.4–2.5, ignored within 1%). A substitute may list `Fallbacks` (more fonts tried in order for characters the first lacks, simple fonts only) and a `MapCodePoint` function applied to the Unicode value before the cmap lookup; the `RecursiveAI.OnePdf.Fonts` package uses both for Symbol and ZapfDingbats (`FontsPackage.Register()`). Without a substitute the glyph has no outline but keeps its width and Unicode.

`PdfFontProviders.Default` = close matches among installed fonts, then providers added with `PdfFontProviders.Register(provider)` (one call, no scanning), then any installed sans, serif, monospace or symbol family by flags. `PdfDocument.FontProvider` overrides it for one document; `PdfFontProviders.UseSystemFonts = false` turns the installed fonts off. Installed fonts are listed once (name tables only) from the platform's font directories plus `ONEPDF_FONT_DIRS`; nothing is fetched.

## Tricky TrueType fonts

Outlines are not hinted, and no font code runs, with one narrow exception. A few CJK TrueType fonts (the DynaLab-designed MingLiU, PMingLiU and DFKai-SB families that ship with Windows) store composite glyphs whose component offsets are provisional: the composite's own instructions move the components into place. Read without running them, a glyph such as U+6700 in a MingLiU subset comes out with strokes displaced or missing (in the committed `cjk-04` sample its width is 893 units instead of 964 and it overlaps the real shape by 46% instead of 91%).

For a font recognised as one of these, `SfntFont` runs the font program and control value program once and then each glyph's instructions (components first, then the composite's, with its phantom points). It runs them at one pixel per font unit, so that rounding to the grid moves a point by at most one unit and nothing is grid-fitted in practice; the result stays in font units. Every other font takes the plain path and never executes font code.

Recognition uses the font's own data only (`TrickyFonts`):

1. a family, full or PostScript name in the `name` table (IDs 1, 4, 6, 16) that, without a subset tag and ignoring case and punctuation, starts with `mingliu`, `pmingliu`, `dfkaisb` or `dfkaishu` (DFKai-SB's PostScript name is `DFKaiShu-SB-Estd-BF`); or
2. when subsetting removed or renamed the names, the checksums of the `fpgm`, `prep` and `cvt ` tables (computed from the bytes; these tables are copied unchanged into subsets) equal those of the 2001 MingLiU/PMingLiU release (`28233BF1`, `A344A1EB`, `05BCF058`) or of DFKai-SB (`5A30CA3B`, `13A42602`, `11E5EAD4`).

The interpreter (`TrueTypeHinter`) is a safe-code implementation of the instruction set in the OpenType specification, in version 35 semantics (movement along both axes). It is a budgeted sandbox: at most 2²⁰ instructions for each of the font and control value programs and for all programs of one glyph together (components and composite), 2¹⁸ per single glyph program, call depth 32, stack, storage, CVT, function and twilight sizes from `maxp` and fixed caps, 2¹⁴ points per program, and every index, jump target and instruction length checked. A program that exceeds a bound, misbehaves, or turns instructions off in `prep` makes the font fall back to its plain outlines when it happens at load. At glyph time the scope is narrower: a program that fails on its own leaves just its component or composite unhinted (its points are untouched, the rest of the glyph keeps the other programs' results); only when the shared per-glyph 2²⁰ budget is spent is the whole glyph drawn from its plain outline. Either way one warning diagnostic names the first reason. Nothing else about the font changes: advance widths, Unicode and glyph selection are the same as before.

## Limits

Font programs ≤ 64 MiB, CMap/ToUnicode streams ≤ 8 MiB, `CIDToGIDMap` ≤ 128 KiB, `/W` and `/W2` ≤ 2²⁰ entries, ≤ 2¹⁸ cached glyphs per composite font, ≤ 4096 cached outlines per font, ≤ 4096 direct (unnumbered) font dictionaries cached per document. Decoding errors in a font's streams are diagnostics, never exceptions.

## In the interpreter

The content interpreter reads each shown string with `font.Next` (so the Unicode chain, widths and vertical metrics above are what text positioning uses). A glyph run's `PdfGlyph` carries the glyph id, Unicode (null when unknown) and the outline as a path in text-space units for font size 1 (the font's outline matrix applied; quadratic segments raised to cubics); a missing glyph has no outline and still advances. In vertical writing the offsets are those of the glyph origin and the glyph carries v and w1. A Type 3 run carries the text with no outlines, then each glyph with a procedure runs as content between `BeginType3Glyph` and `EndType3Glyph`: CTM = font matrix x text space, the font's `/Resources` (else the current ones), patterns anchored to glyph space, `d1` ignoring later colour operators, nesting and cycles limited with forms and patterns. Only render mode 3 affects Type 3 text.
