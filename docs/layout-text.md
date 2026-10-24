# Layout text: the paragraph engine

The rules as built. The engine turns styled inline text into lines and draws them; it does not depend on the element tree (`src/OnePdf.Layout/Paragraphs/`), and the `Text` element is one client of it. Shaping, bidi and break data come from OneFont.

```csharp
var fonts = TextFontRegistry.CreateDefault();            // Liberation Sans/Serif/Mono
fonts.Add(File.ReadAllBytes("NotoSansArabic-Regular.ttf"), "Noto Arabic");
fonts.FallbackFamilies.Add("Noto Arabic");

var paragraph = new TextParagraph(
    [new TextSpan("Plain, "), new TextSpan("bold", new TextStyle { Weight = 700 }), new TextSpan(" and مرحبا.")])
    { Alignment = TextAlignment.Justify, Hyphenator = myHyphenator };

ShapedParagraph shaped = paragraph.Shape(fonts);          // width-independent
ParagraphLayout layout = shaped.Layout(300);              // lines, heights, baselines
(ParagraphLayout first, ParagraphLayout rest) = layout.Split(3);

var renderer = new TextRenderer(document);                // one per document or writer
renderer.Draw(builder, first, x: 50, top: 780);
```

## Fonts

- A registry holds faces by family, weight, width class and italic. A style asks for families in order; the face of a family is the one with the right italic flag, then the nearest width class, then the nearest weight (an equally near heavier face wins for a requested weight of 500 or more). Weights are never synthesized.
- `CreateDefault` registers the twelve Liberation faces from `RecursiveAI.OnePdf.Fonts` and the aliases `Sans`, `Serif`, `Mono`, `Monospace`. The default family is `Sans`. Text in them is shaped and embedded as a subset of the glyphs used (a one-page Latin page is about 13 kB).
- Standard-14 names (`Helvetica`, `Arial`, `Times-Roman`, `Courier-Bold`, ...) select the unshaped path: advances come from the metric-compatible Liberation face, no kerning, no ligatures, no fallback, drawn with the PDF standard font and not embedded. A standard font cannot be combined with others in one chain.
- Characters a family lacks come from the later families of the style, then from the registry's `FallbackFamilies`. OneFont picks the face per grapheme cluster. A character no face has is drawn as the first face's missing-glyph box and reported in `ShapedParagraph.Warnings`.
- System fonts are consulted only after `UseSystemFonts(provider)`.

## Pipeline

1. **Normalise.** `\r\n`, `\r`, U+2028, U+2029 become `\n`; tabs become spaces; other control characters are dropped; U+00AD is removed and remembered. Each `\n`-separated line is its own bidi paragraph and its own list of lines; the line before a `\n` is never justified.
2. **Itemise.** Within a line, runs of one bidi level (UAX #9, `Direction` forced or taken from the first strong character) and one shaping group (same families, weight, italic, width, language and script position) are shaped together, so Arabic joining and kerning continue across a change of colour or size. Spans that differ only in size, colour, spacing or decoration still share a run.
3. **Shape.** `FontFallbackChain.ShapeRun` per run. Each cluster becomes one item with its glyphs, advance and the text it stands for. Letter spacing is added to the cluster's advance.
4. **Superscript and subscript.** Shaped once with `sups`/`subs` on and once without; if the glyphs differ the font's own forms are used at full size. Otherwise the text is scaled to 0.65 and shifted by the font's `ySuperscriptYOffset`/`ySubscriptYOffset` (0.35 em up and 0.14 em down when the font gives none).
5. **Break.** Opportunities are UAX #14 (`TextSegmentation`) plus soft hyphens and `IHyphenator` points, which are used only where UAX #14 offers no break. Thai and similar scripts break at spaces and punctuation only (no dictionary); ideographs break between characters.
6. **Fill, greedy first fit.** A line takes the last opportunity at which its content, without trailing spaces, fits; trailing spaces hang. At a hyphenation point the hyphen's width counts. If no opportunity fits, the piece up to the next one stays whole on a line and the line has `Overflows = true`.
7. **Align.** `Start`/`End` follow the direction of that bidi paragraph; `Left`/`Right` do not. The first-line indent is on the start side and shrinks the width of the first line. `Justify` adds equal extra width to every stretchable space (U+0020, U+00A0, U+3000) so a justified line is exactly the width (±1e-9); a line with no space stays at the start edge unless `JustifyWithLetterSpacing` spreads the extra between characters.
8. **Line boxes.** Each item contributes the ascent and descent of its font at its size (`hhea`, or the typographic values when `USE_TYPO_METRICS` is set) plus half the line gap on each side. With `LineHeight` set (em of the style's size) the box is that tall, extra space split above and below; the tallest span decides. Raised or lowered script extends a line only when no `LineHeight` is set. An empty line has the height of the font at its position.

## Measure, split, content widths

- `ShapedParagraph.MinContentWidth`: the widest piece between break opportunities, counting a hyphen where the piece ends at a hyphenation point. `MaxContentWidth`: the longest line with nothing wrapped, with the first-line indent.
- `ParagraphLayout.Lines` give `Top`, `Height`, `Baseline`, `Width`, `X` and the text range of each line; any boundary between two lines is a place to split. `Split(n)` returns the first `n` lines and a continuation holding the rest exactly as an unsplit layout laid them out, moved up to the top; drawing the two parts draws the same lines. `HeightOf(first, count)` sums heights for widow/orphan decisions.

## Drawing

`TextRenderer.Draw` draws a range of lines. Per line it orders the items visually (UAX #9 rule L2 on the item levels) and groups neighbours of one font and level into one `DrawGlyphRun` each, with glyph advances that include letter spacing and justification, so the glyphs sit where the layout put them. Right-to-left runs are drawn in display order; the text given with each run is in logical order, so extraction returns logical text.

- A line-end hyphen is the font's hyphen glyph (U+2010 if the font has it, else `-`) mapped to U+00AD, as tagged PDF asks for incidental hyphens (ISO 32000-2 14.8.2.2.3). Standard-font text draws `-`.
- Underline and strike-through are filled rectangles in the text colour, at the position and thickness of `post` and `OS/2`, at least 0.25 pt thick.
- Spans with `TextStyle.Link` are returned as `TextLinkArea` rectangles (line-box height); placing the annotation is up to the caller.
- The builder's graphics state is restored.

## Limits and behaviour

A paragraph holds at most 2^24 characters. Layout work is linear in the text for any number of lines: the scan for a break stops once the content has passed the width. The shaped paragraph is immutable; `Layout` caches hyphen glyphs, so use one shaped paragraph from one thread at a time. Output is byte-identical between runs (no fused multiply-add, fixed evaluation order).

Not included: vertical writing, Knuth–Plass breaking, bundled hyphenation patterns, colour glyphs, inline elements, tab stops.
