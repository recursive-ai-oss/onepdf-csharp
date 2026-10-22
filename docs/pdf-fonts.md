# PDF fonts

How a font dictionary becomes glyphs. Code: `src/OnePdf/Text/Fonts/`. The font programs, CMaps and encodings underneath are in `FontPrograms/` and `FontData/`.

## What a font gives the caller

`PdfDocument.GetFont(dictionary)` (internal) returns a `PdfFont`, built once per font object and shared by all threads. `font.Next(bytes, position, out glyph)` / `font.Decode(bytes, list)` split a shown string into `PdfGlyph`s. A glyph is immutable and shared by every use of the same code:

| Member | Meaning |
|---|---|
| `Code`, `CodeLength` | the code and the bytes it took (1 for simple fonts, 1–4 for CMaps) |
| `Cid`, `Gid` | CID (the code for simple fonts); glyph index in the font program, -1 without a program |
| `Unicode`, `HasUnicode` | text, or U+FFFD with `HasUnicode` false |
| `Exists` | the program has a real glyph for it (not .notdef); for Type 3, a glyph procedure exists |
| `Advance` | horizontal advance for font size 1 (width / 1000; Type 3: width through `FontMatrix`) |
| `VerticalAdvance`, `OriginX`, `OriginY` | vertical writing: `W2`/`DW2` w1y and position vector v, size 1 |
| `OutlineMatrix` | glyph space to text space, with the horizontal scale of a substitute applied |
| `GetOutline()` | unhinted outline in glyph space, empty when missing or damaged |
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

A font with no usable program (none embedded, or the embedded one failed to parse, recorded as a warning diagnostic) asks the provider with a `PdfFontRequest` (name without subset tag, family, Standard 14 alias, bold/italic/serif/fixed/symbolic, CID ordering, vertical). The substitute's glyph is found by name, then by the glyph's Unicode through its cmap, and scaled horizontally so its advance equals the PDF width (ratio limited to 0.4–2.5, ignored within 1%). Without a substitute the glyph has no outline but keeps its width and Unicode.

`PdfFontProviders.Default` = close matches among installed fonts, then providers added with `PdfFontProviders.Register(provider)` (one call, no scanning), then any installed sans, serif, monospace or symbol family by flags. `PdfDocument.FontProvider` overrides it for one document; `PdfFontProviders.UseSystemFonts = false` turns the installed fonts off. Installed fonts are listed once (name tables only) from the platform's font directories plus `ONEPDF_FONT_DIRS`; nothing is fetched.

## Limits

Font programs ≤ 64 MiB, CMap/ToUnicode streams ≤ 8 MiB, `CIDToGIDMap` ≤ 128 KiB, `/W` and `/W2` ≤ 2²⁰ entries, ≤ 2¹⁸ cached glyphs per composite font, ≤ 4096 cached outlines per font, ≤ 4096 direct (unnumbered) font dictionaries cached per document. Decoding errors in a font's streams are diagnostics, never exceptions.
