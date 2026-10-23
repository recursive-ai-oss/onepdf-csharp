# Forms: filling and flattening

Documents opened with `PdfOpenOptions.AllowEditing = true` can have their form fields filled and their annotations baked into the page. Both work with `Save` and `SaveIncremental`, and on encrypted documents (new objects are encrypted like the rest). Field scripts (`/AA` calculate, format, validate, keystroke) are data: they never run, and values are stored exactly as given.

```csharp
using var doc = PdfDocument.Open("form.pdf", new PdfOpenOptions { AllowEditing = true });
PdfAcroForm form = doc.Form!;
form.SetValue("applicant.name", "Ada Lovelace");     // text
form.SetValue("country", "Norway");                  // combo or list: export value or display text
form.SetValues("interests", ["art", "music"]);       // multi-select list box
form.SetChecked("agree", true);                      // check box
form.SetValue("size", "M");                          // radio group: on-state name or /Opt export value
doc.SaveIncremental(File.Create("filled.pdf"));

doc.Flatten();                                       // bake everything into the page, drop the form
doc.Save("flat.pdf");
```

## Filling

`PdfFormField.SetValue`, `SetValues`, `SetChecked` and `Reset`, and the same by fully qualified name on `PdfAcroForm` (plus `ResetAll` and `GenerateMissingAppearances`). A value that cannot be stored throws `PdfFormValueException` (an `ArgumentException`) with a `Reason` and changes nothing:

| Reason | When |
|---|---|
| `UnknownField` | no field has that fully qualified name |
| `WrongType` | the call does not fit the field (a check for a text field, a value for a push button or signature) |
| `ReadOnly` | the field has the read-only flag; `PdfFillOptions.OverrideReadOnly` fills it anyway |
| `NotInOptions` | the value is not in `/Opt` (combo, list), or not an on-state of the widgets (check box, radio) |
| `TooLong` | a text value is longer than `/MaxLen` |
| `NotMultiSelect` | several values for a field that is not a multi-select list box |
| `NotTerminal` | the field has child fields; fill the leaves |

A document that is not editable throws `InvalidOperationException`; a value larger than `PdfLimits.MaxMetadataBytes` throws `PdfLimitException`.

- **Text** (ISO 32000-2 12.7.4.3): `/V` becomes a text string (PDFDocEncoding when possible, else UTF-16BE) and a stale `/RV` is removed. `/MaxLen` counts Unicode code points; a longer value is refused, or cut to the first `/MaxLen` code points with `TruncateToMaxLength`. Comb fields, multi-line, password and `/Q` alignment are drawn from the flags by the appearance generator. A value of `null` removes `/V`.
- **Combo and list boxes** (12.7.4.4): the value is matched against the export values of `/Opt`, then against display texts; `/V` stores the export value. An editable combo box (flag bit 19) accepts any text. A multi-select list box takes several values: `/V` is an array and `/I` the selected indices, both in option order, duplicates dropped. Entries of `/Opt` that are neither strings nor two-string arrays are ignored.
- **Check boxes** (12.7.4.2.3): the value is an on-state name (a key of a widget's `/AP /N` other than `Off`), `Off`, or null; `SetChecked(true)` uses the first widget's on-state, or `Yes` when no appearance names one. `/V` and every widget's `/AS` are set (widgets that have the state go on, others off).
- **Radio buttons** (12.7.4.2.4): as for check boxes, by state name; when `/Opt` is present the state names are indices and the option text is accepted as the value. Only the first widget with the state turns on, unless the radios-in-unison flag (bit 26) is set, which turns on every widget that has it. `Off` or `SetChecked(false)` turns all off.
- **Appearances**: for each widget the normal appearance is built by the appearance generator (`docs/appearance-generation.md`) and stored as a Form XObject in `/AP /N` (text and choice: replaced; buttons: added under the on-state when the stored dictionary has no stream for it; stored art for the state is kept). Fonts named by `/DA` come from `/DR`; built-in fonts are stored once per form as indirect objects. `BBox` and `Matrix` are rounded to four decimals like the generated content. `PdfFillOptions.GenerateAppearances = false` changes only `/V` and `/AS`.
- **`/NeedAppearances`** is cleared only when every widget has a normal appearance (a button that is off needs none). When that moment arrives, text and choice fields not filled by the caller are rebuilt first, so clearing the flag never makes an old appearance authoritative. `GenerateMissingAppearances()` stores whatever is missing and applies the same rule.
- **Reset** sets `/V` to the inherited `/DV` (or removes it; off for buttons). A default that is not among the options is stored as it is. `ResetAll` skips read-only fields unless overridden, push buttons and signatures.
- Pages, `Form` and other views taken before an edit are not refreshed by value changes to the same objects; `Flatten` rebuilds `Pages` and `Form`, so take them again afterwards.

## Flattening

`PdfDocument.Flatten(PdfFlattenOptions?)` draws the normal appearance of widgets (and, with `IncludeMarkup`, of every annotation except links and pop-ups) into the page and removes the annotations.

- **Placement** (12.5.5): the appearance's `/Matrix`-transformed `/BBox` is fitted onto `/Rect`; the result is `q a b c d e f cm /Name Do Q` in a content stream appended to the page. The original content is wrapped in `q` … `Q` so a leaked graphics state cannot move the art. A stored appearance stream is used as the XObject itself (`/Subtype /Form` is added when missing); a missing one is generated in memory, exactly as `page.Interpret` does (including the `/NeedAppearances` rule), and stored. `NoRotate` on a rotated page keeps the art upright, pinned at the upper left corner of `/Rect`; `NoZoom` has no meaning in a page.
- **Visibility**: `Visibility = View` (default) draws what is not Hidden and not NoView; `Print` draws what has the Print flag and is not Hidden. Annotations hidden by optional content are not drawn. Annotations not drawn are still removed.
- **Selection**: `Fields` (default true) and `FieldNames` (fully qualified names, each with the fields under it; null means all, including widgets outside the field tree). `IncludeMarkup` adds the other annotation types; hidden ones are removed too.
- **Fields**: flattened leaves leave the field tree and `/CO`; parents left without children go too; `/AcroForm` is dropped when no field is left (`PdfFlattenResult.FormRemoved`). Signed signature fields stay, with their widgets (`SignedKept`).
- **Pop-ups** go with their parent annotation. Structure-tree entries pointing at removed widgets are left as they are.
- The flattened page interprets as the original page with its annotations, except that flattened art is drawn under annotations that are kept, and text extraction now includes the field values.

## Limits

Everything is bounded before work: values by `PdfLimits.MaxMetadataBytes`, value lists and widget counts by `PdfLimits.MaxObjects`, drawn text and items by the generator's limits (`MaxAppearanceText`, `MaxAppearanceItems`); the field tree is walked iteratively with cycle detection and the nesting limit; copying of font dictionaries is iterative and counted. Names are looked up in a dictionary built once, so filling a form of 100,000 fields is linear.
