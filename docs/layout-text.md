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

## Knuth–Plass line breaking

By default lines are broken greedily (first fit). A paragraph can opt in to total-fit breaking after Knuth and Plass, "Breaking Paragraphs into Lines" (Software: Practice and Experience 11, 1981): the breaks of the whole paragraph are chosen together, so a loose line is not the price of a tight one three lines later. Greedy stays the default and its output does not change.

```csharp
using OnePdf.Layout;

LayoutDocument.Compose(doc =>
{
    doc.LineBreaking(LineBreakMode.KnuthPlass);              // every text of the document, or
    doc.Page(p => p.Content().Text(t =>
    {
        t.Justify();
        t.Hyphenator(myHyphenator);
        t.OptimalLineBreaks(new KnuthPlassOptions { Tolerance = 300 });   // this text only (GreedyLineBreaks() opts out)
        t.Span(longText);
    }));
});

// the paragraph engine directly
var paragraph = new TextParagraph(text) { Alignment = TextAlignment.Justify, LineBreakMode = LineBreakMode.KnuthPlass };
```

- **Model.** The paper's boxes are the shaped clusters, its glue the spaces (stretching by half and shrinking by a third of their width) and its penalties the break opportunities: UAX #14 breaks cost nothing, a break after a hyphen in the text costs `ExplicitHyphenPenalty`, and a soft hyphen or `IHyphenator` point costs `HyphenPenalty` and adds the hyphen's width to its line. A line's adjustment ratio is the change its spaces must make over the change they can make; badness is `100·|r|³` up to 10000; demerits are `(LinePenalty + badness)² ± penalty²`, plus `ConsecutiveHyphenDemerits`, `FinalHyphenDemerits` (the last line follows a hyphen) and `AdjacentFitnessDemerits` (neighbouring lines of very different looseness: very loose, loose, decent, tight). The dynamic programme keeps one node per break and looseness class and prunes as the paper does.
- **Text it applies to.** Justified lines use their spaces; a justified line with no space to stretch is as bad as can be, so single-word lines are avoided. Left, right and centred text counts as ragged: lines cannot shrink, and slack is judged against a quarter of the line width. The last line of a paragraph and lines before a hard line end are free to be short. Lines of different widths (a first-line indent) are handled; mixed fonts and sizes, bidi paragraphs and clusters are used as shaped. Justification then spreads each line's slack evenly over its spaces, as for greedy lines, and a line the breaker squeezed has its spaces narrowed.
- **Defaults** (`KnuthPlassOptions`, TeX's plain values): `Tolerance` 200, `PreTolerance` 100, `LinePenalty` 10, `HyphenPenalty` 50, `ExplicitHyphenPenalty` 50, `ConsecutiveHyphenDemerits` 10000, `FinalHyphenDemerits` 5000, `AdjacentFitnessDemerits` 10000, `Looseness` 0, `EmergencyStretch` 0.1 of the line width. `Looseness` asks for that many lines more or fewer than the optimum when the paragraph can be set so. With `PreTolerance` 100 a paragraph that sets well without hyphens gets none; a negative value always considers them.
- **Passes and fallbacks.** Pass 1 has no hyphens and accepts badness up to `PreTolerance` (skipped when the paragraph has no hyphenation points or `PreTolerance` is negative). Pass 2 allows hyphens up to `Tolerance`. Pass 3 repeats it with emergency stretch. Pass 4 accepts any line that is not too full and, when a piece wider than the line leaves no choice, puts that piece alone on an overfull line as greedy does, so a paragraph is always set. Each hard line (separated by a line end) is solved on its own.
- **Bounds.** Time and memory are proportional to the number of break opportunities times the candidate line starts within one line width. A hard line of more than 500000 clusters, or a search with more than 4096 live candidates (looseness multiplies them), is broken greedily instead and `ShapedParagraph.Warnings` says so. There is no recursion. Pagination is unaffected: a Knuth–Plass paragraph is an ordinary list of lines, so splitting, widows and orphans and keep rules work on it as before. The breaker never looks beyond its paragraph.
- **Determinism.** Plain double arithmetic in a fixed order; ties go to the earlier break; output is byte-identical between runs.
- **Cost.** Compared with greedy, `book500` (500 justified pages) allocates about 21 % more and takes between no measurable difference and about 15 % more time (measured locally on a busy machine; see `bench/OnePdf.Layout.Benchmarks/README.md`).

## Measure, split, content widths

- `ShapedParagraph.MinContentWidth`: the widest piece between break opportunities, counting a hyphen where the piece ends at a hyphenation point. `MaxContentWidth`: the longest line with nothing wrapped, with the first-line indent.
- `ParagraphLayout.Lines` give `Top`, `Height`, `Baseline`, `Width`, `X` and the text range of each line; any boundary between two lines is a place to split. `Split(n)` returns the first `n` lines and a continuation holding the rest exactly as an unsplit layout laid them out, moved up to the top; drawing the two parts draws the same lines. `HeightOf(first, count)` sums heights for widow/orphan decisions.

## Drawing

`TextRenderer.Draw` draws a range of lines. Per line it orders the items visually (UAX #9 rule L2 on the item levels) and groups neighbours of one font and level into one `DrawGlyphRun` each, with glyph advances that include letter spacing and justification, so the glyphs sit where the layout put them. Right-to-left runs are drawn in display order; the text given with each run is in logical order, so extraction returns logical text.

- A line-end hyphen is the font's hyphen glyph (U+2010 if the font has it, else `-`) mapped to U+00AD, as tagged PDF asks for incidental hyphens (ISO 32000-2 14.8.2.2.3). Standard-font text draws `-`.
- Underline and strike-through are filled rectangles in the text colour, at the position and thickness of `post` and `OS/2`, at least 0.25 pt thick.
- Spans with `TextStyle.Link` are returned as `TextLinkArea` rectangles (line-box height); placing the annotation is up to the caller.
- The builder's graphics state is restored.

## Inline objects

A span can hold an object instead of text: `TextSpan.ForInline(InlineObject, style)`. `InlineBox(width, height)` is the standalone kind, drawn by a `Paint` callback (a `CanvasSurface` clipped to the box) and/or an `Image` (`PdfCreatedImage`); `InlineElement` (layout `Text` only) holds an element tree. Common settings: `Alignment` (`InlineAlignment`: baseline, top, middle, bottom, text top, text bottom), `BaselineOffset`, `InnerBaseline`, `GrowsLine`, `KeepWithNeighbours`, `AltText`, `Artifact` and `Link`.

- **In the paragraph** the object is one character, U+FFFC OBJECT REPLACEMENT CHARACTER, one item as wide as the object. It has line break class CB (a break is allowed before and after, UAX #14 rule LB20) and bidi class ON (it takes the level of its position, UAX #9), so right-to-left lines place it correctly. `KeepWithNeighbours` clears the two opportunities after the line breaker has found them. A U+FFFC in ordinary span text is dropped. Hyphenation points never fall next to an object.
- **Line box.** The object takes the font of the span it stands in, which gives the line its minimum height and the ascent and descent that middle, text-top and text-bottom use. Objects against the baseline or the text come first and raise the line's ascent and descent as far as they reach; objects aligned to the top then add what they lack below the baseline, and objects aligned to the bottom what they lack above it, in that order. With `GrowsLine` false the object is placed by the same rules but changes nothing. `ParagraphLine.Baseline` and `Height` are the result.
- **Drawing.** `TextRenderer.Draw` closes its saved graphics state around each stretch of text and draws the object between stretches, so the object may open marked content of its own. An object with `AltText` is preceded by an invisible space inside a `/Span` with `/ActualText`, so extraction returns the description; one with `Link` is reported in the `TextLinkArea`s. Without a layout host nothing is tagged.
- **Content widths** (`MinContentWidth`, `MaxContentWidth`) count the object like a word.

## Limits and behaviour

A paragraph holds at most 2^24 characters. Layout work is linear in the text for any number of lines: the scan for a break stops once the content has passed the width. The shaped paragraph is immutable; `Layout` caches hyphen glyphs, so use one shaped paragraph from one thread at a time. Output is byte-identical between runs (no fused multiply-add, fixed evaluation order).

Not included: vertical writing, colour glyphs, inline elements, tab stops.

## Hyphenation patterns

The `RecursiveAI.OnePdf.Hyphenation` package supplies `IHyphenator`s built on Liang's algorithm (the method of TeX). Layout does not depend on it, and without a hyphenator only soft hyphens break words.

```csharp
using OnePdf.Hyphenation;

page.Content().Text(t =>
{
    t.Language("de-DE");
    t.Hyphenator(Hyphenators.Auto());     // picks patterns from each span's language; no language, no hyphenation
    t.Justify();
    t.Span("Donaudampfschifffahrtsgesellschaft ...");
});
```

- `Hyphenators.For("en-GB")` gives one language's `PatternHyphenator` (null when none is bundled); `Hyphenators.Auto(options, defaultLanguage)` dispatches on the language of each word's style. `Hyphenators.Languages` lists the 26 bundled ids; `Resolve` maps a tag to one (`en` is American unless the region is GB, AU, NZ, IE, ZA, IN, SG or HK; `de` is the 1996 spelling, `de-1901` the traditional one, `de-CH` the Swiss traditional patterns; `pt-BR` is `pt`).
- **Defaults.** Words under 5 characters are not hyphenated. At least `LeftMin` letters stay before a hyphen and `RightMin` after it; by default these are the language's own typesetting minimums from its pattern file (2 and 3 for American and British English, Portuguese, Slovak, Estonian, Welsh and Irish; 2 and 2 for most others, 1 and 1 for monotonic Greek). All three are set through `HyphenationOptions`. Words over 255 characters are left alone.
- **Exceptions** in the pattern files replace the patterns for that word, and are held to the same minimums. Custom data goes through `PatternHyphenator.FromPatterns`.
- **Output.** The engine draws the hyphen from the line's font and maps it to U+00AD, so the text layer reads `hyphen\u00ADation`; extraction with `JoinHyphenatedWords` returns the whole word. Tagged and PDF/A output stay compliant (checked with veraPDF).
- **Data.** Each language is a compressed embedded resource read the first time it is used. Sources, licences and per-file notices are in the package's `THIRD-PARTY-NOTICES.md`; the data is regenerated with `tools/hyphenation/generate.py`. Not bundled, for licence reasons: Swedish, Finnish, Icelandic, Catalan, Croatian, Russian, Turkish, Esperanto, Czech, Hungarian, Latvian and Romanian.
- A word is the run of letters and combining marks the engine finds, so apostrophes and hyphens split words (`l'avion` is `l` and `avion`).
