# SVG pictures in layout

`RecursiveAI.OnePdf.Layout` draws SVG as native PDF vector content: paths, fills, strokes, shadings, clips, soft masks and real text. Nothing is rasterised except the pictures the SVG itself embeds. Core gains no SVG code; the code is in the layout package, over core's public content builder.

```csharp
// In a layout: sized like an image (contain, cover, stretch, fit width, fit height, a DPI for the natural size).
doc.Page(page => page.Content().Column(col =>
{
    col.Item().Width(120).Svg(File.ReadAllBytes("logo.svg")).Contain().AltText("Company logo");
}));

// With the content builder (a canvas, a page overlay, a form).
SvgImage logo = SvgImage.Parse(File.ReadAllBytes("logo.svg"));
IReadOnlyList<string> notes = logo.Draw(builder, x: 40, y: 700, width: 120, height: 60);
// or inside a canvas element: surface.DrawSvg(logo, 0, 0, surface.Width, surface.Height);
```

- `SvgImage.Parse(bytes | string, SvgLimits?)` reads the document at once. A malformed or over-limit document throws `PdfFormatException` or `PdfLimitException` there, before layout. `Width`/`Height` are the natural size in points (one CSS pixel is 0.75 pt), `Title` and `Description` come from the root's `<title>` and `<desc>`.
- `Draw` returns the notes (what was dropped or approximated), each once. In layout the notes go to `LayoutDocument.Diagnostics`.
- `SvgPicture` is the element (`Slot.Svg(...)` in the fluent API). It never splits across pages.
- Text is shaped through OneFont with the layout's registered fonts, then the Liberation defaults, and is extractable (`/ToUnicode` as for layout text). `SvgDrawOptions.Fonts` chooses the registry for a plain builder.

## Tagging

A tagged layout document writes an SVG picture as a `Figure`. Alternate text is the author's `AltText`, else the SVG's `<title>`, else its `<desc>`. With none of them, and the picture not marked `Artifact`, layout records the same diagnostic as for an image and PDF/UA-1 saves refuse the document (checkpoint 13). A tagged PDF/UA-1 document with an SVG passes veraPDF's `ua1` profile.

## Supported

- **Structure:** `svg` (nested too), `g`, `defs`, `use`, `symbol`, `switch`, `a`, `viewBox`, `preserveAspectRatio`, `transform`, `transform-origin`, `display`, `visibility`, `overflow`, root `background-color`.
- **Shapes:** `rect` (with rounded corners), `circle`, `ellipse`, `line`, `polyline`, `polygon`, `path` with every command including arcs (arcs and quadratics become cubics).
- **Paint:** colours (hex, `rgb()`, `hsl()`, names, `currentColor`), `fill-rule`, `fill-opacity`, `stroke` width, dash and offset, caps, joins, miter limit, `stroke-opacity`, `paint-order`, `context-fill`/`context-stroke`, `opacity` (group opacity through a transparency group), `mix-blend-mode`, `isolation`.
- **Paint servers:** linear and radial gradients (with `href` inheritance, `gradientTransform`, both unit systems, `fx`/`fy`/`fr`, stop opacity) as PDF shadings; patterns (`patternUnits`, `patternContentUnits`, `patternTransform`, `viewBox`); gradients and patterns on fills and on strokes.
- **Clipping and masks:** `clipPath` (units, `transform`, `clip-path` on the element and on its children, `clip-rule` per child, text children). A clip of one non-even-odd shape is a PDF clip path; a clip with text, nested clips or several shapes with even-odd rules is built as a luminosity soft mask instead, `mask` (luminance, nested) as PDF soft masks.
- **Markers:** `marker-start/mid/end`, `markerUnits`, `orient`, `refX/refY`, `viewBox`.
- **Images:** `data:` URIs holding PNG or JPEG, through core's PNG reader and JPEG passthrough.
- **Text:** `text`, `tspan`, `x/y/dx/dy` lists, `text-anchor`, `font-family/size/weight/style`, `letter-spacing`, `word-spacing`, `text-decoration`, `baseline-shift`, `dominant-baseline` (approximate), fill and stroke, `direction`/`unicode-bidi`.
- **CSS:** presentation attributes, the `style` attribute and `<style>` sheets (type, class, id, attribute, descendant, child and sibling selectors, structural pseudo-classes such as `:first-child` and `:nth-child()`, `:not()`, `!important`) and `inherit`. CSS custom properties (`var()`) are not supported.

## Approximations

- `spreadMethod="repeat"` and `"reflect"` are unrolled into stops (up to 64 periods; beyond that the last band extends, with a note).
- A stroke with a gradient whose stops have different opacities is drawn opaque (fills handle varying stop opacity with a soft mask).
- Patterns are drawn as repeated forms clipped to the shape (up to 4096 tiles per shape); on strokes they are PDF tiling patterns.
- Blend modes are PDF's; the backdrop is the page, which in a document is usually white. `hue`, `saturation`, `color`, `luminosity` and `plus-lighter` are not mapped.
- Gradients and patterns on text are drawn as one solid colour, with a note.
- `shape-rendering` and `image-rendering` are ignored (PDF viewers choose their own anti-aliasing and interpolation). Glyph positions and baselines can differ from a browser by a fraction of a point.

## Dropped, never run, never fetched

Scripts, SMIL animation, event attributes, `foreignObject`, external references (files, URLs, other SVG files, `use` of another document) are dropped with a note. A filter draws the unfiltered content with a note (no filter is mapped). Also not drawn: `textPath`, `tref`, vertical writing, `rotate` and `textLength` on text, SVG or GIF/WebP data URIs inside an `image`. The XML reader refuses DTD entity expansion and external entities; nothing opens a file or a connection.

## Limits

`SvgLimits` (all checked before the work they guard; a document over one fails with `PdfLimitException`): document bytes (32 MiB), elements (500,000), depth (256), attributes per element, names, namespace bindings, instantiated elements through `use` (500,000), `use` chain depth (32), layer depth (32), path segments (4,000,000), CSS bytes, rules and matching steps, data-URI image bytes (16 MiB each, 64 MiB together), text characters (1,000,000, counted again for each `use` copy). Element visits for bounding boxes share the instance bound. Gradient stops are capped at 250 and tiles at 4096 per shape. The `svg` fuzz target (`fuzz/`) runs the whole path.

## Checked against

`tools/OnePdf.Layout.SvgCompare` renders each SVG twice: through a layout PDF (viewed by `RecursiveAI.OneImage.Pdf`) and directly by `RecursiveAI.OneImage.Svg`, both black boxes, and counts pixels that differ. A page passes when at most 0.5 % of its pixels differ by more than 32 levels. `testdata/layout-svg/` holds our own sample files and `testdata/layout-references/documents/svg.pdf` a reference document.
