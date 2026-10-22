# Display-list text format

**Format version: 4**

The text form of a `PdfDisplayList` (or of any interpretation, written straight from the interpreter). It is used for
snapshot tests and is meant to be comparable across implementations: it is plain UTF-8 text, one record per line, with
`\n` line ends. Any change to what is written bumps the version below and the number in the header line.

## Header

The first line is `onepdf-display-list 4`: the magic word and the format version.

## Numbers

Numbers are rounded to four decimals (ties to even), printed in plain decimal with no exponent, no trailing zeros
and no negative zero: `0`, `12`, `-0.5`, `3.1416`. The rounding and printing use integer arithmetic on the scaled value, so
all platforms print the same text. Non-finite values print as `nan`, `inf`, `-inf`; magnitudes of 1e14 or more print as
`huge` or `-huge`.

A *matrix* is `[a b c d e f]`. A *box* is `left bottom right top`.

## Lines

Each line is indented by two spaces per open bracket (page, clip, group, soft mask, marked content, annotation); the
indentation is informative. The record keyword is the first word.

| Record | Meaning |
|---|---|
| `page box=<box> name=<CropBox\|MediaBox\|BleedBox\|TrimBox\|ArtBox> rotate=<0\|90\|180\|270> unit=<n> scale=<n> size=<w> <h> base=<matrix>` | Start of a page: the selected box in default user space, the rotation and user unit applied, the total scale, the output size and the matrix from default user space to the output (y grows downward). |
| `end-page` | End of the page. |
| `gs ctm=<matrix> fill=<color> stroke=<color> lw=<n> cap=<n> join=<n> miter=<n> dash=[<n> ...] phase=<n> flat=<n> ri=<name> fa=<n> sa=<n> bm=<name> sm=<id> ais=<0\|1> op=<fill><stroke><mode>` | The graphics state that applies to the records that follow, written whenever it differs from the last one written. `ctm` maps the user space of the following paths to the output. `fa`/`sa` are the fill and stroke alpha, `bm` the blend mode, `sm` the soft mask id (0 = none), `op` three characters: fill overprint, stroke overprint (0/1) and the overprint mode. |
| `fill <nonzero\|evenodd> <path>` | Fill a path. |
| `stroke <path>` | Stroke a path. |
| `clip <nonzero\|evenodd> <path>` | Intersect the clip with a path; opens a clip bracket. |
| `text-clip` | Intersect the clip with the glyphs shown in clipping render modes since the last `BT`; opens a clip bracket. |
| `pop-clip` | Close the most recent clip bracket. |
| `text font=<name> size=<n> mode=<0-7> tc=<n> tw=<n> th=<n> rise=<n> tm=<matrix> vert=<0\|1> adv=<x> <y> codes=<hex> glyphs=[<glyph> ...]` | One shown string. `th` is the horizontal scale as a factor. `tm` is the text matrix at the start of the string. `adv` is the total displacement of the text matrix. `codes` are the string bytes in lower-case hex. Each glyph is `<code>@<offsetX>,<offsetY>,<width>` optionally followed by `:g<glyph id>` and `:u<UTF-8 hex>` when known, and, in a vertical run, `:v<vx>,<vy>,<w1>` (the position vector and vertical advance in em units; the offsets are those of the glyph origin, ISO 32000-2 9.7.4.3). Outlines are not written. A Type 3 font's run lists its text with no outlines; the glyph procedures follow as `begin-glyph` brackets. |
| `image <inline\|/Name> bytes=<n> fnv=<hex> data=<finished> dict=<object>` | An image drawn into the unit square of the CTM: the length and FNV-1a 64-bit hash of its encoded data (`bytes=-1` when unreadable), the finished image and its dictionary. `<finished>` is `decoded:<w>x<h>:<colour space name>:<fnv of the 8-bit samples>`, `stencil:<w>x<h>:<fnv of the coverage plane>`, `encoded:<w>x<h>` (no decoder for its codec), `none` (unusable dictionary) or `limit` (over an image limit); a mask or colour key adds `:alpha=<w>x<h>:<fnv>`. Decoding uses the default decoder registry, which ships empty. |
| `shading type=<1-7> cs=<final space> fn=<0\|1> bbox=<box\|none> bg=<(c ...)\|none> aa=<0\|1> ...` | The `sh` operator, or the shading inside a shading pattern. The shading is decoded and evaluated by OnePDF; the line states what a device receives. Type-specific fields follow: type 1 `domain=(x0 x1 y0 y1) matrix=<matrix> at-centre=(c ...)`; types 2 and 3 `coords=(...) domain=(t0 t1) extend=<0\|1><0\|1> at-start=(c ...) at-mid=(c ...) at-end=(c ...)` (the colours the evaluator returns at the start, middle and end of the domain, in the final space); types 4 to 7 `vertices=<n> triangles=<n>`, for patch meshes also `patches=<n> div=<n>`, then `fnv=<hash>` (FNV-1a 64 of the rounded vertex data and the triangle indices) and `truncated` when data was dropped at a limit. |
| `pattern-def #<id> /Name tiling paint=<1\|2> tile=<1-3> bbox=<box> step=<x> <y> matrix=<matrix>` / `end-pattern-def` | The first use of a tiling pattern in a colour: its definition, followed (indented, between the two records) by the records of its cell, which are written like a page's content with pattern space as user space. `matrix` maps pattern space to the output. Colours name the pattern as `pattern(#<id> ...)`. |
| `pattern-def #<id> /Name shading matrix=<matrix>` / `end-pattern-def` | The first use of a shading pattern: one `shading` record between the two. |
| `begin-group bbox=<box> isolated=<0\|1> knockout=<0\|1> cs=<object>` / `end-group` | A transparency group. |
| `begin-softmask id=<n> type=<luminosity\|alpha> bbox=<box> bc=<object> tr=<object>` / `end-softmask` | A soft mask definition; its content sits between the two records. |
| `begin-mc /Tag props=<object>` / `end-mc` | A marked-content sequence. `props` is `null` without properties. |
| `begin-glyph font=<name> code=<n> name=<glyph name\|none> adv=<n> [u=<UTF-8 hex>]` / `end-glyph` | A Type 3 glyph procedure, run as content after the `text` record that showed it (not for render mode 3 or a code without a procedure). The records between the two are written like a page's content; the `gs` line before `begin-glyph` has a CTM that already includes the font matrix and the text space, so glyph coordinates map straight to the output. |
| `begin-annot /Subtype rect=<box> flags=<n>` / `end-annot` | An annotation appearance. |

## Paths

A path is a space-separated list of `M x y`, `L x y`, `C x1 y1 x2 y2 x3 y3` and `Z`, in the user space of the CTM in force.

## Colors

`gray(g)`, `rgb(r g b)`, `cmyk(c m y k)`, `calgray(g)`, `calrgb(r g b)`, `lab(L a b)`, `icc<N>:<profile length>:<FNV-1a 64 of the profile>(c ...)`, and for
pattern colors `pattern(#<id> /Name)` (followed by the base space name and tint components for uncolored patterns); `<id>` numbers the patterns in order of first use and refers to a `pattern-def` record written before the `gs` line that uses it.

## Objects

Dictionaries, arrays and other objects print in a canonical form: dictionary keys sorted by ordinal, `/Name` with
non-regular characters as `#xx`, strings as `<hex>`, references as `n g R`, streams as `stream<<...>>`, `true`, `false`,
`null`; nesting beyond eight levels prints `...`.

## Versioning

The version is an integer that changes whenever the output for any input could change (new records, new fields, different
rounding). Readers must reject a version they do not know.

History: 2 added the `data=` field to image lines and the `calgray`/`calrgb` colour spaces (before, CalGray and CalRGB printed as `gray` and `rgb`); an ICC profile that is damaged or does not match its component count now prints as the device space with that many components. Version 3 replaced the unevaluated `shading /Name obj=` record by the decoded `shading type=...` record, added `pattern-def`, and changed pattern colours to `pattern(#<id> /Name ...)`. Version 4 added the `begin-glyph` bracket for Type 3 glyph procedures and the `:v` suffix of vertical glyphs; glyph runs now carry real glyph ids, Unicode and widths from the PDF fonts.
