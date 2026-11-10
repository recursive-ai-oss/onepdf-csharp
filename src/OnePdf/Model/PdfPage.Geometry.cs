namespace OnePdf;

// Page boxes, /Rotate and /UserUnit: reading the effective values and editing them (ISO 32000-2 7.7.3.3, Table 31; 14.11.2).
public sealed partial class PdfPage
{
    private const int MaxParentDepth = 64;

    // Reads the boxes, rotation and unit from the page node. Done at construction and again after every edit, so a page object
    // that was edited reports the new values at once.
    private void ReadGeometry(PdfPageNode node, bool report)
    {
        int objNum = node.Dictionary.Reference?.Number ?? -1;
        PdfRectangle? media = PdfRectangle.From(node.MediaBox);
        if (media is null)
        {
            if (report)
            {
                Document.ReportWarning(node.MediaBox is null ? "The page has no MediaBox; US Letter was assumed." : "The page's MediaBox is not four numbers; US Letter was assumed.", objNum, -1);
            }

            media = new PdfRectangle(0, 0, 612, 792);
        }

        MediaBox = media.Value;
        PdfRectangle? crop = PdfRectangle.From(node.CropBox);
        if (report && node.CropBox is not null && crop is null)
        {
            Document.ReportWarning("The page's CropBox is not four numbers; the MediaBox was used.", objNum, -1);
        }

        // The visible region is the CropBox clipped to the MediaBox (7.7.3.3); a CropBox outside it is unusable.
        CropBox = crop is { } c ? (c.Intersect(MediaBox) ?? MediaBox) : MediaBox;

        // Bleed, trim and art boxes default to the CropBox and are limited to the MediaBox (14.11.2).
        BleedBox = LimitedBox(node.Dictionary, "BleedBox", MediaBox, CropBox);
        TrimBox = LimitedBox(node.Dictionary, "TrimBox", MediaBox, CropBox);
        ArtBox = LimitedBox(node.Dictionary, "ArtBox", MediaBox, CropBox);

        long rot = node.Rotate switch { PdfInteger i => i.Value, PdfReal r when double.IsFinite(r.Value) && Math.Abs(r.Value) < 1e9 => (long)r.Value, _ => 0 };
        if (rot % 90 != 0)
        {
            if (report)
            {
                Document.ReportWarning($"The page's Rotate {rot} is not a multiple of 90; 0 was used.", objNum, -1);
            }

            rot = 0;
        }

        Rotate = (int)(((rot % 360) + 360) % 360);
        double uu = node.Dictionary.GetDouble("UserUnit", 1);
        UserUnit = double.IsFinite(uu) && uu > 0 ? uu : 1;
    }

    private static PdfRectangle LimitedBox(PdfDictionary page, string key, PdfRectangle media, PdfRectangle fallback) =>
        PdfRectangle.From(page.Get(key)) is { } r ? (r.Intersect(media) ?? fallback) : fallback;

    /// <summary>
    /// The effective rectangle of a box: after defaults (the crop box is the media box; bleed, trim and art boxes are the crop box)
    /// and after clipping to the media box (ISO 32000-2 14.11.2), in default user space before <c>/Rotate</c>.
    /// </summary>
    /// <param name="box">Which box.</param>
    public PdfRectangle GetBox(PdfPageBox box) => box switch
    {
        PdfPageBox.MediaBox => MediaBox,
        PdfPageBox.BleedBox => BleedBox,
        PdfPageBox.TrimBox => TrimBox,
        PdfPageBox.ArtBox => ArtBox,
        _ => CropBox,
    };

    /// <summary>
    /// True when the box is stored for this page: on the page itself, or for the media and crop boxes also inherited from the page
    /// tree. False when <see cref="GetBox"/> would answer with a default.
    /// </summary>
    /// <param name="box">Which box.</param>
    public bool HasExplicitBox(PdfPageBox box) => box switch
    {
        PdfPageBox.MediaBox => PdfRectangle.From(_node.MediaBox) is not null,
        PdfPageBox.CropBox => PdfRectangle.From(_node.CropBox) is not null,
        PdfPageBox.BleedBox => PdfRectangle.From(Dictionary.Get("BleedBox")) is not null,
        PdfPageBox.TrimBox => PdfRectangle.From(Dictionary.Get("TrimBox")) is not null,
        _ => PdfRectangle.From(Dictionary.Get("ArtBox")) is not null,
    };

    /// <summary>
    /// The size of a box as it is displayed or printed: width and height swapped for <see cref="Rotate"/> 90 and 270, and multiplied
    /// by <see cref="UserUnit"/>, in points (1/72 inch), with inch and millimetre views.
    /// </summary>
    /// <param name="box">Which box; the crop box (the visible page) by default.</param>
    /// <param name="applyRotate">False to keep the stored orientation.</param>
    /// <param name="applyUserUnit">False to leave out the user unit.</param>
    public PdfPageSize GetDisplaySize(PdfPageBox box = PdfPageBox.CropBox, bool applyRotate = true, bool applyUserUnit = true)
    {
        PdfRectangle r = GetBox(box);
        double u = applyUserUnit ? UserUnit : 1;
        double w = r.Width * u, h = r.Height * u;
        return applyRotate && Rotate is 90 or 270 ? new PdfPageSize(h, w) : new PdfPageSize(w, h);
    }

    // ---- editing ----

    /// <summary>
    /// Sets the media box on this page. A box inherited from the page tree is overridden, not changed in the tree. The rectangle is
    /// normalized when written. Bleed, trim, art and crop boxes stored on the page that reach outside the new media box are clipped to
    /// it (and removed when nothing of them is left), so what is stored agrees with what a reader reports (ISO 32000-2 14.11.2).
    /// </summary>
    /// <param name="rectangle">Any two opposite corners; finite and with an area.</param>
    /// <exception cref="ArgumentException">The rectangle is not finite or has no area.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetMediaBox(PdfRectangle rectangle)
    {
        PdfRectangle r = CheckBox(rectangle, nameof(rectangle));
        Document.EnsureEditable(0);
        Dictionary.SetEntry("MediaBox", ToArray(r));
        foreach (string key in OwnBoxKeys)
        {
            if (PdfRectangle.From(Dictionary.Get(key)) is { } own && !(own.Left >= r.Left && own.Bottom >= r.Bottom && own.Right <= r.Right && own.Top <= r.Top))
            {
                if (own.Intersect(r) is { } clipped)
                {
                    Dictionary.SetEntry(key, ToArray(clipped));
                }
                else
                {
                    Dictionary.RemoveEntry(key);
                }
            }
        }

        if (PdfRectangle.From(Dictionary.Get("CropBox")) is null && PdfRectangle.From(Inherited("CropBox")) is { } inheritedCrop && inheritedCrop.Intersect(r) is null)
        {
            // An inherited crop box that misses the new media box would read as the media box anyway; say so on the page.
            Dictionary.SetEntry("CropBox", ToArray(r));
        }

        Refresh();
    }

    /// <summary>
    /// Sets the crop box (the visible region) on this page, overriding an inherited one. Written normalized; reads back clipped to the
    /// media box.
    /// </summary>
    /// <param name="rectangle">Any two opposite corners; finite and with an area.</param>
    /// <exception cref="ArgumentException">The rectangle is not finite or has no area.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetCropBox(PdfRectangle rectangle) => SetOwnBox("CropBox", rectangle);

    /// <summary>Sets the bleed box on this page. Written normalized; reads back clipped to the media box. See <see cref="SetCropBox"/>.</summary>
    public void SetBleedBox(PdfRectangle rectangle) => SetOwnBox("BleedBox", rectangle);

    /// <summary>Sets the trim box on this page. Written normalized; reads back clipped to the media box. See <see cref="SetCropBox"/>.</summary>
    public void SetTrimBox(PdfRectangle rectangle) => SetOwnBox("TrimBox", rectangle);

    /// <summary>Sets the art box on this page. Written normalized; reads back clipped to the media box. See <see cref="SetCropBox"/>.</summary>
    public void SetArtBox(PdfRectangle rectangle) => SetOwnBox("ArtBox", rectangle);

    /// <summary>
    /// Removes the crop box, so the page shows its whole media box. A crop box inherited from the page tree is overridden with the
    /// media box rather than changed in the tree.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void ClearCropBox()
    {
        Document.EnsureEditable(0);
        Dictionary.RemoveEntry("CropBox");
        if (Inherited("CropBox") is not null)
        {
            Dictionary.SetEntry("CropBox", ToArray(MediaBoxStored()));
        }

        Refresh();
    }

    /// <summary>Removes the bleed box, so it reads as the crop box. See <see cref="ClearCropBox"/> for the exceptions.</summary>
    public void ClearBleedBox() => ClearOwnBox("BleedBox");

    /// <summary>Removes the trim box, so it reads as the crop box. See <see cref="ClearCropBox"/> for the exceptions.</summary>
    public void ClearTrimBox() => ClearOwnBox("TrimBox");

    /// <summary>Removes the art box, so it reads as the crop box. See <see cref="ClearCropBox"/> for the exceptions.</summary>
    public void ClearArtBox() => ClearOwnBox("ArtBox");

    /// <summary>
    /// Sets the clockwise display rotation on this page, overriding an inherited one. The angle is normalized to 0, 90, 180 or 270
    /// (so -90 is 270). Unlike <see cref="PdfDocument.RotatePage"/> this sets the angle rather than adding to it.
    /// </summary>
    /// <param name="degrees">A multiple of 90, negative or larger than 360 included.</param>
    /// <exception cref="ArgumentException"><paramref name="degrees"/> is not a multiple of 90.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetRotate(int degrees)
    {
        if (degrees % 90 != 0)
        {
            throw new ArgumentException("A page can only be rotated by multiples of 90 degrees.", nameof(degrees));
        }

        Document.EnsureEditable(0);
        Dictionary.SetEntry("Rotate", new PdfInteger(((degrees % 360) + 360) % 360));
        Refresh();
    }

    /// <summary>Removes the page's rotation, so it is displayed upright. An inherited rotation is overridden with 0 on the page.</summary>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void ClearRotate()
    {
        Document.EnsureEditable(0);
        Dictionary.RemoveEntry("Rotate");
        if (Inherited("Rotate") is not null)
        {
            Dictionary.SetEntry("Rotate", new PdfInteger(0));
        }

        Refresh();
    }

    /// <summary>
    /// Sets <c>/UserUnit</c>, the size of a default user space unit in 1/72 inch (ISO 32000-2 Table 31; PDF 1.6). It is not inherited.
    /// Raises the document's version to 1.6 when it is lower.
    /// </summary>
    /// <param name="userUnit">Finite and greater than zero.</param>
    /// <exception cref="ArgumentException"><paramref name="userUnit"/> is not finite or not greater than zero.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetUserUnit(double userUnit)
    {
        if (!double.IsFinite(userUnit) || userUnit <= 0)
        {
            throw new ArgumentException("The user unit must be finite and greater than zero.", nameof(userUnit));
        }

        Document.EnsureEditable(0);
        Dictionary.SetEntry("UserUnit", userUnit == Math.Floor(userUnit) && userUnit < 1e9 ? new PdfInteger((long)userUnit) : new PdfReal(userUnit));
        if (string.CompareOrdinal(Document.Version, "1.6") < 0)
        {
            Document.RaiseVersion("1.6");
        }

        Refresh();
    }

    /// <summary>Removes <c>/UserUnit</c>, so one unit is 1/72 inch again.</summary>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void ClearUserUnit()
    {
        Document.EnsureEditable(0);
        Dictionary.RemoveEntry("UserUnit");
        Refresh();
    }

    /// <summary>
    /// Sets print geometry in one call: the trim box is <paramref name="trim"/>; the media box and the bleed box are the trim box
    /// grown by <paramref name="bleed"/> on every side; the crop box is removed (the whole media box shows). Art boxes stored on the
    /// page are clipped to the new media box. Content keeps its coordinates, so draw the bleed into the margin around the trim box.
    /// </summary>
    /// <param name="trim">The finished page, in default user space; finite and with an area.</param>
    /// <param name="bleed">The bleed margin in points; finite and zero or more.</param>
    /// <exception cref="ArgumentException">The trim rectangle is not valid or the bleed is negative or not finite.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetPrintGeometry(PdfRectangle trim, double bleed = 0) => SetPrintGeometry(trim, bleed, 0);

    /// <summary>
    /// Sets print geometry with a slug: the trim box is <paramref name="trim"/>; the bleed box is the trim box grown by
    /// <paramref name="bleed"/> on every side; the media box is the bleed box grown by <paramref name="slug"/> on every side, room outside
    /// the bleed for printer's marks, colour bars and job information. The crop box is removed, so the whole media box (marks included)
    /// shows and prints; a crop box equal to the bleed box would hide the slug from every reader that clips to the crop box
    /// (ISO 32000-2 14.11.2.1, 14.11.2.2). Art boxes stored on the page are clipped to the new media box. With a zero slug this is the
    /// same as <see cref="SetPrintGeometry(PdfRectangle, double)"/>.
    /// </summary>
    /// <param name="trim">The finished page, in default user space; finite and with an area.</param>
    /// <param name="bleed">The bleed margin in points; finite and zero or more.</param>
    /// <param name="slug">The slug margin outside the bleed, in points; finite and zero or more.</param>
    /// <exception cref="ArgumentException">The trim rectangle is not valid, or the bleed or slug is negative or not finite.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetPrintGeometry(PdfRectangle trim, double bleed, double slug) => SetPrintGeometry(trim, new PdfPrintMargins(bleed), new PdfPrintMargins(slug), null);

    /// <summary>
    /// Sets print geometry with a slug and printer's marks: as <see cref="SetPrintGeometry(PdfRectangle, double, double)"/>, then draws
    /// <paramref name="marks"/> over the page in the slug area.
    /// </summary>
    /// <param name="trim">The finished page, in default user space; finite and with an area.</param>
    /// <param name="bleed">The bleed margin in points; finite and zero or more.</param>
    /// <param name="slug">The slug margin outside the bleed, in points; at least <see cref="PdfPrinterMarks.MinimumSlug"/> for the marks.</param>
    /// <param name="marks">The marks to draw; they are artifacts of type Page and lie outside the bleed box.</param>
    /// <exception cref="ArgumentException">The trim rectangle, bleed or slug is not valid, or the slug is too small for the marks. Nothing is changed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="marks"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetPrintGeometry(PdfRectangle trim, double bleed, double slug, PdfPrinterMarks marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        SetPrintGeometry(trim, new PdfPrintMargins(bleed), new PdfPrintMargins(slug), marks);
    }

    /// <summary>
    /// Sets print geometry with a different bleed and slug on each side: the bleed box is the trim box grown by
    /// <paramref name="bleed"/> (left, bottom, right, top), the media box is the bleed box grown by <paramref name="slug"/>, and the crop
    /// box is removed. Content keeps its coordinates, so the trim box stays where it was drawn. Otherwise as
    /// <see cref="SetPrintGeometry(PdfRectangle, double, double)"/>, which is this with equal sides.
    /// </summary>
    /// <param name="trim">The finished page, in default user space; finite and with an area.</param>
    /// <param name="bleed">The bleed on each side, in points; finite and zero or more.</param>
    /// <param name="slug">The slug on each side outside the bleed, in points; finite and zero or more.</param>
    /// <param name="marks">Printer's marks to draw in the slug, or null for none. A side's slug smaller than <see cref="PdfPrinterMarks.MinimumSlug"/> is refused.</param>
    /// <exception cref="ArgumentException">A rectangle or margin is not valid, or the slug is too small for the marks. Nothing is changed.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetPrintGeometry(PdfRectangle trim, PdfPrintMargins bleed, PdfPrintMargins slug, PdfPrinterMarks? marks = null)
    {
        PdfRectangle t = CheckBox(trim, nameof(trim));
        PdfRectangle bleedBox = PageBoxes.WithBleed(t, bleed);
        PdfRectangle media = PageBoxes.WithSlug(bleedBox, slug);
        marks?.Check(slug);
        Document.EnsureEditable(0);
        SetMediaBox(media);
        ClearCropBox();
        Dictionary.SetEntry("BleedBox", ToArray(bleedBox));
        Dictionary.SetEntry("TrimBox", ToArray(t));
        Refresh();
        if (marks is not null)
        {
            using PdfPageContentBuilder content = BeginContent();
            marks.Draw(content, t, bleedBox);
            content.Commit();
        }
    }

    // ---- BoxColorInfo ----

    /// <summary>
    /// The page's <c>/BoxColorInfo</c> entry for a box: the colour, width and style a viewer should use for that box's guides
    /// (ISO 32000-2 14.11.2.2). Missing keys take the specification's defaults. Null when the page has no entry for the box.
    /// </summary>
    /// <param name="box">The crop, bleed, trim or art box (the media box has no entry).</param>
    /// <exception cref="ArgumentException"><paramref name="box"/> is the media box.</exception>
    public PdfBoxColorInfo? GetBoxColorInfo(PdfPageBox box)
    {
        string key = ColorInfoKey(box);
        if (Dictionary.Get("BoxColorInfo") is not PdfDictionary all || all.Get(key) is not PdfDictionary d)
        {
            return null;
        }

        double c0 = 0, c1 = 0, c2 = 0;
        if (d.Get("C") is PdfArray c && c.Count >= 3)
        {
            c0 = Clamp01(c.GetDouble(0)); c1 = Clamp01(c.GetDouble(1)); c2 = Clamp01(c.GetDouble(2));
        }

        double w = d.GetDouble("W", 1);
        var dash = new List<double>();
        if (d.Get("D") is PdfArray da)
        {
            int n = Math.Min(da.Count, 32);
            for (int i = 0; i < n; i++)
            {
                double v = da.GetDouble(i);
                if (double.IsFinite(v) && v >= 0)
                {
                    dash.Add(v);
                }
            }
        }

        return new PdfBoxColorInfo
        {
            Red = c0,
            Green = c1,
            Blue = c2,
            LineWidth = double.IsFinite(w) && w >= 0 ? w : 1,
            Style = d.Get("S") is PdfName { Value: "D" } ? PdfBoxLineStyle.Dashed : PdfBoxLineStyle.Solid,
            Dash = dash.Count > 0 ? dash : [3],
        };
    }

    /// <summary>
    /// Sets (or, with null, removes) the page's <c>/BoxColorInfo</c> entry for a box. Entries of the other boxes are kept.
    /// </summary>
    /// <param name="box">The crop, bleed, trim or art box.</param>
    /// <param name="info">What to write; null to remove the entry (and the whole dictionary when it becomes empty).</param>
    /// <exception cref="ArgumentException"><paramref name="box"/> is the media box, a colour component is outside 0 to 1, the width is negative or not finite, or a dash is negative, not finite, or the pattern is empty or all zero.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    public void SetBoxColorInfo(PdfPageBox box, PdfBoxColorInfo? info)
    {
        string key = ColorInfoKey(box);
        if (info is not null)
        {
            CheckColorInfo(info);
        }

        Document.EnsureEditable(0);
        PdfDictionary? all = Dictionary.Get("BoxColorInfo") as PdfDictionary;
        if (info is null)
        {
            if (all is not null)
            {
                all.RemoveEntry(key);
                if (all.Count == 0)
                {
                    Dictionary.RemoveEntry("BoxColorInfo");
                }
            }

            return;
        }

        PdfDictionary d = PageBoxes.BuildColorInfo(info);

        if (all is null)
        {
            all = new PdfDictionary();
            all.SetEntry(key, d);
            Dictionary.SetEntry("BoxColorInfo", all);
        }
        else
        {
            all.SetEntry(key, d);
        }
    }

    // ---- helpers ----

    private static readonly string[] OwnBoxKeys = ["CropBox", "BleedBox", "TrimBox", "ArtBox"];

    private static double Clamp01(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;

    private static string ColorInfoKey(PdfPageBox box) => PageBoxes.ColorInfoKey(box);

    private static void CheckColorInfo(PdfBoxColorInfo i) => PageBoxes.CheckColorInfo(i);

    private static PdfRectangle CheckBox(PdfRectangle r, string name) => PageBoxes.Normalize(r, name);

    private static PdfArray ToArray(PdfRectangle r) => PageBoxes.ToArray(r);

    private PdfRectangle MediaBoxStored() => PdfRectangle.From(Dictionary.Get("MediaBox")) ?? PdfRectangle.From(Inherited("MediaBox")) ?? MediaBox;

    private void SetOwnBox(string key, PdfRectangle rectangle)
    {
        PdfRectangle r = CheckBox(rectangle, nameof(rectangle));
        Document.EnsureEditable(0);
        Dictionary.SetEntry(key, ToArray(r));
        Refresh();
    }

    private void ClearOwnBox(string key)
    {
        Document.EnsureEditable(0);
        Dictionary.RemoveEntry(key);
        Refresh();
    }

    // The value of an inheritable attribute found on an ancestor of the page (not on the page itself).
    private PdfObject? Inherited(string key)
    {
        PdfDictionary? node = Dictionary.Get("Parent") as PdfDictionary;
        for (int depth = 0; node is not null && depth < MaxParentDepth; depth++)
        {
            if (node.Get(key) is { } v)
            {
                return v;
            }

            node = node.Get("Parent") as PdfDictionary;
        }

        return null;
    }

    // Recomputes what the page reports after an edit, and tells the page collection so later lookups agree.
    private void Refresh()
    {
        PdfPageNode n = _node with
        {
            MediaBox = Dictionary.Get("MediaBox") ?? Inherited("MediaBox"),
            CropBox = Dictionary.Get("CropBox") ?? Inherited("CropBox"),
            Rotate = Dictionary.Get("Rotate") ?? Inherited("Rotate"),
        };
        _node = n;
        ReadGeometry(n, false);
        PdfPageCollection pages = Document.Pages;
        if (Index < pages.Count && ReferenceEquals(pages.NodeAt(Index).Dictionary, Dictionary))
        {
            pages.ReplaceNode(Index, n);
        }
    }
}
