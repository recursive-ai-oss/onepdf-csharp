# Display-list text format

**Format version: 2**

The text form of a `PdfDisplayList` (or of any interpretation, written straight from the interpreter). It is used for
snapshot tests and is meant to be comparable across implementations: it is plain UTF-8 text, one record per line, with
`\n` line ends. Any change to what is written bumps the version below and the number in the header line.

## Header

The first line is `onepdf-display-list 2`: the magic word and the format version.

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
| `text font=<name> size=<n> mode=<0-7> tc=<n> tw=<n> th=<n> rise=<n> tm=<matrix> vert=<0\|1> adv=<x> <y> codes=<hex> glyphs=[<glyph> ...]` | One shown string. `th` is the horizontal scale as a factor. `tm` is the text matrix at the start of the string. `adv` is the total displacement of the text matrix. `codes` are the string bytes in lower-case hex. Each glyph is `<code>@<offsetX>,<offsetY>,<width>` optionally followed by `:g<glyph id>` and `:u<UTF-8 hex>` when known. |
| `image <inline\|/Name> bytes=<n> fnv=<hex> data=<finished> dict=<object>` | An image drawn into the unit square of the CTM: the length and FNV-1a 64-bit hash of its encoded data (`bytes=-1` when unreadable), the finished image and its dictionary. `<finished>` is `decoded:<w>x<h>:<colour space name>:<fnv of the 8-bit samples>`, `stencil:<w>x<h>:<fnv of the coverage plane>`, `encoded:<w>x<h>` (no decoder for its codec), `none` (unusable dictionary) or `limit` (over an image limit); a mask or colour key adds `:alpha=<w>x<h>:<fnv>`. Decoding uses the default decoder registry, which ships empty. |
| `shading /Name obj=<object>` | The `sh` operator with the unevaluated shading. |
| `begin-group bbox=<box> isolated=<0\|1> knockout=<0\|1> cs=<object>` / `end-group` | A transparency group. |
| `begin-softmask id=<n> type=<luminosity\|alpha> bbox=<box> bc=<object> tr=<object>` / `end-softmask` | A soft mask definition; its content sits between the two records. |
| `begin-mc /Tag props=<object>` / `end-mc` | A marked-content sequence. `props` is `null` without properties. |
| `begin-annot /Subtype rect=<box> flags=<n>` / `end-annot` | An annotation appearance. |

## Paths

A path is a space-separated list of `M x y`, `L x y`, `C x1 y1 x2 y2 x3 y3` and `Z`, in the user space of the CTM in force.

## Colors

`gray(g)`, `rgb(r g b)`, `cmyk(c m y k)`, `calgray(g)`, `calrgb(r g b)`, `lab(L a b)`, `icc<N>:<profile length>:<FNV-1a 64 of the profile>(c ...)`, and for
pattern colors `pattern(/Name)` (followed by the base space name and tint components for uncolored patterns).

## Objects

Dictionaries, arrays and other objects print in a canonical form: dictionary keys sorted by ordinal, `/Name` with
non-regular characters as `#xx`, strings as `<hex>`, references as `n g R`, streams as `stream<<...>>`, `true`, `false`,
`null`; nesting beyond eight levels prints `...`.

## Versioning

The version is an integer that changes whenever the output for any input could change (new records, new fields, different
rounding). Readers must reject a version they do not know.

History: 2 added the `data=` field to image lines and the `calgray`/`calrgb` colour spaces (before, CalGray and CalRGB printed as `gray` and `rgb`); an ICC profile that is damaged or does not match its component count now prints as the device space with that many components.
