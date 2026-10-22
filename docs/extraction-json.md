# Extraction JSON

The JSON form of text extraction: glyphs, words, lines and blocks per page. It is written by `PdfExtractionJson`
(`ToJson`, `Write`) and is meant to be compared byte for byte across implementations, so it is versioned and fully
deterministic.

- **Format:** `onepdf.extraction`, **version 1**. A reader must check both members. Additive changes (new members) keep the
  version; renaming, removing or changing the meaning of a member raises it.
- **Encoding:** UTF-8, no byte-order mark. Strings are escaped as JSON requires and nothing more (non-ASCII text is written
  as it is). Unpaired surrogates in glyph text have been replaced by U+FFFD before writing.
- **Numbers:** coordinates and sizes are rounded to three decimals (ties to even), written in invariant form without an exponent and without
  trailing zeros; negative zero is written `0`. Integers are plain.
- **Member order** is the order below. Whitespace is insignificant (the writer can indent).

## Coordinate space

All coordinates are in *upright page space*: points, origin at the bottom-left of the page as a viewer shows it (page
`/Rotate` and box already applied), y up. A box is `[left, bottom, right, top]`.

## Shape

```
{
  "format": "onepdf.extraction",
  "version": 1,
  "pageCount": <int>,
  "pages": [ Page, ... ]
}

Page = {
  "index": <int>,                    zero-based page index
  "width": <number>, "height": <number>,
  "usedStructureTree": <bool>,       true when the structure tree decided the order
  "diagnostics": [ <string>, ... ],  notes about this page (for example a structure tree that covered too little)
  "text": <string>,                  the page's reading-order plain text: blocks separated by "\n\n", lines by "\n", table cells by "\t"
  "blocks": [ Block, ... ],          reading order
  "glyphs": [ Glyph, ... ]           omitted when the writer is asked not to include glyphs
}

Block = {
  "role": "body" | "header" | "footer",
  "table": <bool>,                   a grid of short cells; lines are rows
  "bounds": [l, b, r, t],
  "lines": [ Line, ... ]
}

Line = {
  "direction": "ltr" | "rtl",        base direction used to put the line in logical order
  "vertical": <bool>,                vertical writing
  "bounds": [l, b, r, t],
  "cells": [ <int>, ... ],           indices into "words" where a cell (a run set apart by a wide gap) starts; first is 0
  "text": <string>,                  words joined by " ", cells by "\t"
  "words": [ Word, ... ]             logical order (right to left on the page for right-to-left text)
}

Word = {
  "text": <string>,                  logical order
  "bounds": [l, b, r, t],
  "visible": <bool>,                 false when every glyph is invisible
  "glyphs": [ <int>, ... ]           indices into the page's "glyphs", visual order along the reading direction
}

Glyph = {
  "text": <string>,                  Unicode (after ligature decomposition and /ActualText substitution; may be empty)
  "font": <string> | null,
  "fontId": <int>,                   -1 when unknown
  "size": <number>,                  em size in points
  "matrix": [a, b, c, d, e, f],      glyph space (em) to page space
  "advance": <number>,               em units along the writing direction
  "bounds": [l, b, r, t],
  "vertical": <bool>,
  "color": "#rrggbb" | null,         fill colour
  "renderMode": <int>,               0 to 7
  "visible": <bool>,                 false for invisible text (render mode 3, OCR layers)
  "mcid": <int>                      marked-content id, -1 for none
}
```

The glyphs listed are the ones that took part in extraction: clipped and off-page glyphs and duplicates (fake bold) are already
gone unless the options kept them, and a glyph inside an `/ActualText` span carries the replacement text (first glyph) or empty text.
