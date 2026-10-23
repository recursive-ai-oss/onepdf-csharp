# Appearance generation

When an annotation has no usable normal appearance, `page.Interpret` builds one in memory and draws it through the same path as a stored `/AP` (ISO 32000-2 12.5.5). The same appearance is available as data from `PdfAnnotation.GenerateAppearance()`. Nothing is written to the document.

## When it applies

- `PdfInterpretOptions.GenerateAppearances` is true (the default). Set it to false for the 1.0 behaviour: nothing is drawn and an Info diagnostic says so.
- The annotation is visible by its flags, in the selected annotation mode, and not hidden by optional content.
- Its normal appearance is missing: no `/AP`, no `/N`, or a state dictionary with no stream for the current `/AS`. Links are never generated.
- Widgets also when the form sets `/NeedAppearances true` (12.7.3): every text and choice field is rebuilt from its value, replacing a stored appearance; check boxes, radio buttons and push buttons keep a stored appearance for their state and are generated only when there is none, because their stored art (icons, custom marks) cannot be rebuilt from the dictionary.
- Text extraction does not interpret annotation appearances and is unaffected.
- Every generated appearance records an Info diagnostic `Drew a generated appearance (...)`; problems record Warning or Info diagnostics and never throw.

## Result

`PdfGeneratedAppearance` has `Dictionary` (a form XObject dictionary with `/BBox`, `/Matrix` for rotated widgets, `/Resources`), `Content` (unfiltered bytes; numbers have at most four decimals, so output is deterministic), `Rect` and `Description`. Fonts in `/Resources` are the field's own dictionaries when the `/DA` names one found in the field's or the form's `/DR`, otherwise direct standard 14 dictionaries. For markup, `Rect` is the annotation rectangle grown to hold the generated geometry (line endings, stroke width); `/BBox` equals `Rect` and the drawing is in page coordinates. For widgets `Rect` is the annotation rectangle and the box is `[0 0 w h]`.

## Widgets (12.7.4)

Field attributes (`/FT /Ff /V /DA /Q /MaxLen /Opt /TI /I /DR`) are inherited through `/Parent`, at most `PdfLimits.MaxRecursionDepth` levels. `/DA` and `/Q` fall back to the AcroForm's.

- **Frame**: `/MK /BG` fills; `/MK /BC` strokes with width and style from `/BS` (S solid, D dashed with `/D`, B beveled, I inset, U underline) or `/Border`. No `/BC` means no border. Radio buttons use circles. `/MK /R` (0, 90, 180, 270) rotates through `/Matrix`.
- **Text field**: value `/V` (string, name or text stream), default `/DA` colour, left, centred or right by `/Q`. Auto size (`0 Tf`): single line is three quarters of the inner height, reduced to fit the width, minimum 4; multi-line starts at 12 and shrinks to fit, minimum 4. Multi-line wraps at spaces, inside over-long words, and at CR/LF. Comb (`/MaxLen`, no multi-line or password) centres one character per cell and draws cell separators. Password shows `*` per character. Text is clipped to the inner box and wrapped in `/Tx BMC ... EMC`.
- **Combo box**: the display string for the exported value (`/Opt` pairs). **List box**: the options from `/TI`, one row per `1.15 x size` (12 for auto), selected rows (`/V`, `/I`) highlighted. No drop-down arrow is drawn.
- **Check box / radio button**: drawn when on. On is `/AS` not `Off`; without `/AS`, `/V` not `Off` (radio buttons also compare with the on-state key of `/AP /N`, `/Opt` or the widget's index among `/Kids`). The mark comes from `/MK /CA` and is drawn as vectors for the ZapfDingbats check (4), cross (8), circle (l), diamond (u) and square (n), so it needs no font; other characters use ZapfDingbats text. Colour is the `/DA` colour.
- **Push button**: `/MK /CA` caption centred, auto size up to 12. Icons (`/MK /I`, `/TP`) are not drawn.
- **Signature fields** get nothing (Info diagnostic).
- **Fonts and characters**: the `/DA` font if it is a simple (single-byte) font, else the standard 14 face named by the alias (`Helv`, `TiRo`, `Cour`, `ZaDb`, `Symb`, bold and italic forms), else Helvetica. A character the font cannot encode is tried in Helvetica and Symbol (Omega, Delta and micro in their Adobe-list spellings too); if none can, it is dropped and one Warning counts the dropped characters. Right-to-left shaping is not done.

## Markup (12.5.6)

Common: `/C` stroke (default black; yellow for Highlight), `/IC` fill, `/CA` opacity through an ExtGState, `/BS` or `/Border` width (default 1), style and dashes. Highlight uses blend mode Multiply.

| Subtype | Drawn |
|---|---|
| Square, Circle | The rectangle reduced by `/RD`, inset by half the width, filled and stroked. The cloudy border `/BE` is drawn as a plain border. |
| Line | `/L` with `/LE` endings (None, Square, Circle, Diamond, OpenArrow, ClosedArrow, Butt, ROpenArrow, RClosedArrow, Slash) and leader lines `/LL /LLE /LLO`. Captions (`/Cap`) are not drawn. |
| Polygon, PolyLine | `/Vertices`, closed and filled (Polygon) or open with `/LE` endings (PolyLine). |
| Ink | Each `/InkList` stroke with round caps and joins. |
| Highlight, Underline, StrikeOut, Squiggly | One shape per `/QuadPoints` quadrilateral in the order viewers write (upper left, upper right, lower left, lower right); with no quad points the rectangle. |
| FreeText | Background `/C`, border only when `/BS` or `/Border` is present, colour from `/DA`, text from `/Contents` wrapped and aligned by `/Q`, callout `/CL` with the `/LE` ending. Rich text `/RC` is not used. |
| Text | A 20 by 20 note icon at the upper left of the rectangle: Note, Comment and Help have their own shapes, other names draw as Note. Colour `/C`, default yellow. |

Other subtypes (Stamp, Caret, FileAttachment, Sound, Redact, 3D, ...) keep the old behaviour: nothing is drawn and an Info diagnostic says so.

## Limits

All checked before the work they guard, with a diagnostic when something is cut off: field and annotation text at `PdfLimits.MaxAppearanceText` characters (65,536); vertices, ink points, quadrilaterals, list rows, comb cells, wrapped lines and squiggle steps at `PdfLimits.MaxAppearanceItems` (65,536); `/DA` at 512 tokens and font size at 1000; rectangles and coordinates beyond 10^6 (widgets) or 10^7 (markup) points, empty rectangles and non-finite numbers skip the annotation with a Warning. `/Parent` chains stop at the recursion limit. A font that is not a simple font is replaced by a standard one.

## Checked against

Widgets of real authoring-tool files (US federal forms made by Acrobat-family tools and a veraPDF sample) with the stored appearance stripped in memory: text equal, drawn bounds within 4 points (about 1 point for fixed-size text). See `AppearanceGenerationTests`.
