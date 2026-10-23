using System.Linq;
using OnePdf.Forms;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OnePdf.Content;

namespace OnePdf.Redaction;

/// <summary>
/// One call of <see cref="PdfDocument.Redact"/>. The work is in two phases: first every page is read and its new content built
/// (this only adds unreferenced objects to the document, so a page that cannot be redacted safely stops the call with nothing
/// changed); then the new content, annotation changes and the document-level clean-up are put in place.
/// </summary>
internal sealed class RedactionPass
{
    private static readonly string[] AnnotationSecrets = ["V", "DV", "RV", "AP", "Contents", "RC", "A", "AA", "Dest", "Subj", "DS"];

    private readonly PdfDocument _doc;
    private readonly PdfRedactionOptions _opt;
    private readonly PdfRedactionResult _res = new();
    private readonly CancellationToken _ct;

    private sealed class PagePlan
    {
        public PdfPage Page = null!;
        public List<PdfRedactionRegion> Regions = [];
        public ContentRedactor Redactor = null!;
        public HashSet<PdfDictionary> Remove = new(ReferenceEqualityComparer.Instance);
        public List<PdfAnnotation> Keep = [];
    }

    private RedactionPass(PdfDocument doc, PdfRedactionOptions options, CancellationToken ct)
    {
        _doc = doc;
        _opt = options;
        _ct = ct;
    }

    public static PdfRedactionResult Run(PdfDocument doc, IEnumerable<PdfRedactionRegion> regions, PdfRedactionOptions options, IReadOnlyList<(int Page, PdfDictionary Annotation)>? applied, CancellationToken ct)
    {
        doc.EnsureEditable(0);
        return new RedactionPass(doc, options, ct).Execute(regions, applied);
    }

    private PdfRedactionResult Execute(IEnumerable<PdfRedactionRegion> input, IReadOnlyList<(int Page, PdfDictionary Annotation)>? applied)
    {
        List<PdfRedactionRegion> regions = [.. input];
        int pageCount = _doc.Pages.Count;
        foreach (PdfRedactionRegion r in regions)
        {
            if (r.PageIndex < 0 || r.PageIndex >= pageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(input), $"Page index {r.PageIndex} is outside the document's {pageCount} pages.");
            }

            PdfRectangle b = r.Rectangle;
            if (!double.IsFinite(b.Left) || !double.IsFinite(b.Bottom) || !double.IsFinite(b.Right) || !double.IsFinite(b.Top) || b.IsEmpty)
            {
                throw new ArgumentException("A redaction rectangle is empty or not finite.", nameof(input));
            }

            CheckColor(r.FillColor);
        }

        CheckColor(_opt.Overlay?.Color);
        CheckColor(_opt.Overlay?.TextColor);

        var byPage = new SortedDictionary<int, PagePlan>();
        foreach (PdfRedactionRegion r in regions)
        {
            if (!byPage.TryGetValue(r.PageIndex, out PagePlan? plan))
            {
                byPage[r.PageIndex] = plan = new PagePlan { Page = _doc.Pages[r.PageIndex] };
            }

            plan.Regions.Add(r);
        }

        // ---- phase 1: read and rewrite, changing nothing the document keeps ----
        foreach ((int index, PagePlan plan) in byPage)
        {
            _ct.ThrowIfCancellationRequested();
            PlanPage(index, plan, applied);
        }

        // ---- phase 2: put it in place ----
        if (_doc.GetSignatures().Count > 0)
        {
            _res.SignaturesInvalidated = true;
            _res._warnings.Add("The document has signatures. Redaction changes the signed bytes, so they are no longer valid once the document is saved.");
        }

        var redactedMcids = new Dictionary<int, HashSet<int>>();
        foreach ((int index, PagePlan plan) in byPage)
        {
            ApplyPage(index, plan);
            _res.PagesProcessed++;
            if (plan.Redactor.RedactedMcids.Count > 0)
            {
                redactedMcids[plan.Page.Dictionary.Reference?.Number ?? -1] = plan.Redactor.RedactedMcids;
            }
        }

        _doc.ResetPageViews();
        foreach ((int index, PagePlan plan) in byPage)
        {
            DrawOverlays(index, plan);
        }

        if (redactedMcids.Count > 0)
        {
            StripStructure(redactedMcids);
        }

        if (_opt.MetadataTerms.Count > 0)
        {
            ClearMetadata();
        }

        if (_opt.RemoveJavaScript)
        {
            RemoveJavaScript();
        }

        if (_opt.RemoveEmbeddedFiles)
        {
            RemoveEmbeddedFiles();
        }

        if (_opt.RemoveThumbnails)
        {
            for (int i = 0; i < _doc.Pages.Count; i++)
            {
                _doc.Pages[i].Dictionary.RemoveEntry("Thumb");
            }
        }

        _doc.ResetPageViews();
        _doc.MarkRedacted();
        return _res;
    }

    private static void CheckColor(IReadOnlyList<double>? c)
    {
        if (c is null)
        {
            return;
        }

        if (c.Count is not (1 or 3 or 4))
        {
            throw new ArgumentException("A colour has 1 (gray), 3 (RGB) or 4 (CMYK) components.");
        }

        foreach (double v in c)
        {
            if (!double.IsFinite(v) || v < 0 || v > 1)
            {
                throw new ArgumentException("Colour components are numbers from 0 to 1.");
            }
        }
    }

    // ---- phase 1 ----

    private void PlanPage(int index, PagePlan plan, IReadOnlyList<(int Page, PdfDictionary Annotation)>? applied)
    {
        PdfPage page = plan.Page;
        var rects = new List<PdfRectangle>();
        foreach (PdfRedactionRegion r in plan.Regions)
        {
            rects.Add(r.Rectangle);
        }

        foreach (PdfAnnotation a in page.Annotations)
        {
            bool isApplied = false;
            if (applied is not null)
            {
                foreach ((int p, PdfDictionary d) in applied)
                {
                    if (p == index && ReferenceEquals(d, a.Dictionary))
                    {
                        isApplied = true;
                    }
                }
            }

            if (isApplied || (a.Subtype != "Popup" && Meets(a.Rect, rects)))
            {
                if (isApplied || _opt.Annotations == PdfRedactionAnnotationMode.Remove)
                {
                    plan.Remove.Add(a.Dictionary);
                }
                else
                {
                    plan.Keep.Add(a);
                }
            }
        }

        var redactor = new ContentRedactor(_doc, page, _opt, rects, _ct);
        plan.Redactor = redactor;
        var io = new PdfInterpretOptions
        {
            Box = PdfPageBox.MediaBox,
            Scale = 1,
            ApplyRotation = false,
            ApplyUserUnit = false,
            Annotations = plan.Keep.Count > 0 ? PdfAnnotationMode.View : PdfAnnotationMode.None,
            GenerateAppearances = false,
            OptionalContentOverride = static _ => true,
            GlyphOutlines = true,
        };
        try
        {
            ContentInterpreter.Run(page, redactor, io, redactor, _ct);
        }
        catch (PdfException e) when (e is not PdfRedactionException)
        {
            throw new PdfRedactionException($"Page {index + 1} could not be redacted safely: {e.Message}", index, e);
        }

        if (redactor.FailReason is { } why)
        {
            throw new PdfRedactionException($"Page {index + 1} could not be redacted safely: {why}.", index);
        }
    }

    private static bool Meets(PdfRectangle a, List<PdfRectangle> rects)
    {
        if (!(a.Width > 0 || a.Height > 0) && a.Left == 0 && a.Right == 0)
        {
            return false;
        }

        foreach (PdfRectangle r in rects)
        {
            if (a.Left <= r.Right && a.Right >= r.Left && a.Bottom <= r.Top && a.Top >= r.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    // ---- phase 2 ----

    private void ApplyPage(int index, PagePlan plan)
    {
        PdfPage page = plan.Page;
        PdfDictionary pd = page.Dictionary;
        ContentRedactor r = plan.Redactor;
        _res.GlyphsRemoved += r.GlyphsRemoved;
        _res.PathsRemoved += r.PathsRemoved;
        _res.ImagesBlanked += r.ImagesBlanked;
        _res.ImagesRemoved += r.ImagesRemoved;
        _res.AlternateTextsRemoved += r.AlternateTextsRemoved;
        if (r.ImagesRemoved > 0)
        {
            _res._warnings.Add($"Page {index + 1}: {r.ImagesRemoved} image(s) were removed whole (inline images, or images that could not be decoded).");
        }

        r.PageResources.Prune();
        if (r.PageStreams is { } streams)
        {
            if (streams.Count == 1)
            {
                pd.SetEntry("Contents", _doc.AddObject(_doc.CreateStream(streams[0], compress: true)));
            }
            else
            {
                var arr = new PdfArray();
                foreach (byte[] s in streams)
                {
                    arr.Append(_doc.AddObject(_doc.CreateStream(s, compress: true)));
                }

                pd.SetEntry("Contents", arr);
            }
        }

        if (r.PageResources.Owned is { } owned)
        {
            pd.SetEntry("Resources", owned);
        }

        pd.RemoveEntry("Thumb");

        // Kept annotations: rewritten appearance, no free text.
        foreach (PdfAnnotation a in plan.Keep)
        {
            PdfDictionary ad = a.Dictionary;
            if (r.AnnotationAppearances.TryGetValue(ad, out PdfReference? ap))
            {
                var apd = new PdfDictionary();
                apd.SetEntry("N", ap);
                ad.SetEntry("AP", apd);
            }

            foreach (string k in (string[])["Contents", "RC", "Subj"])
            {
                ad.RemoveEntry(k);
            }

            if (a.Subtype == "Link")
            {
                ad.RemoveEntry("A");
                ad.RemoveEntry("Dest");
                ad.RemoveEntry("AA");
            }

            ClearFieldValue(ad);
        }

        if (plan.Remove.Count > 0)
        {
            RemoveAnnotations(pd, plan.Remove);
        }
    }

    private void RemoveAnnotations(PdfDictionary pd, HashSet<PdfDictionary> remove)
    {
        PdfArray? annots = pd.GetArray("Annots");
        if (annots is null)
        {
            return;
        }

        // Pop-ups and replies go with the annotation they belong to.
        bool grew = true;
        for (int pass = 0; grew && pass <= annots.Count; pass++)
        {
            grew = false;
            for (int i = 0; i < annots.Count; i++)
            {
                if (annots[i] is not PdfDictionary a || remove.Contains(a))
                {
                    continue;
                }

                if ((a.GetDictionary("Parent") is { } parent && remove.Contains(parent)) || (a.GetDictionary("IRT") is { } irt && remove.Contains(irt)))
                {
                    remove.Add(a);
                    grew = true;
                }
            }
        }

        var keep = new PdfArray();
        var gone = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < annots.Count; i++)
        {
            PdfObject raw = annots.GetRaw(i);
            if (annots[i] is PdfDictionary a && remove.Contains(a))
            {
                gone.Add(a);
                continue;
            }

            keep.Append(raw is PdfReference ? raw : FormFiller.Clone(_doc, raw));
        }

        pd.SetEntry("Annots", keep);
        foreach (PdfDictionary a in gone)
        {
            ClearFieldValue(a);
            foreach (string k in AnnotationSecrets)
            {
                a.RemoveEntry(k);
            }
        }

        _res.AnnotationsRemoved += gone.Count;
        DetachFromFields(gone);
    }

    private void ClearFieldValue(PdfDictionary widget)
    {
        PdfDictionary? d = widget;
        bool cleared = false;
        for (int depth = 0; d is not null && depth < 32; depth++)
        {
            cleared |= d.RemoveEntry("V");
            cleared |= d.RemoveEntry("DV");
            cleared |= d.RemoveEntry("RV");
            d = d.GetDictionary("Parent");
        }

        if (cleared)
        {
            _res.FieldsCleared++;
        }
    }

    // A widget that is gone from its page must not stay reachable through the field tree.
    private void DetachFromFields(HashSet<PdfDictionary> gone)
    {
        PdfDictionary? form = _doc.Catalog.GetDictionary("AcroForm");
        if (form?.GetArray("Fields") is not { } fields)
        {
            return;
        }

        var arrays = new List<PdfArray>();
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfArray>();
        stack.Push(fields);
        int budget = _doc.Limits.MaxObjects;
        while (stack.Count > 0 && budget-- > 0)
        {
            PdfArray arr = stack.Pop();
            if (!seen.Add(arr))
            {
                continue;
            }

            arrays.Add(arr);
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i] is not PdfDictionary d)
                {
                    continue;
                }

                if (gone.Contains(d))
                {
                    arr.RemoveAt(i);
                }
                else if (d.GetArray("Kids") is { } kids)
                {
                    stack.Push(kids);
                }
            }
        }

        // Fields left with no kids at all go too.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (PdfArray arr in arrays)
            {
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    if (arr[i] is PdfDictionary d && d.GetArray("Kids") is { Count: 0 } && d.GetName("Subtype") != "Widget")
                    {
                        arr.RemoveAt(i);
                        changed = true;
                    }
                }
            }
        }
    }

    // ---- overlays ----

    private void DrawOverlays(int index, PagePlan plan)
    {
        PdfPage page = _doc.Pages[index];
        bool any = false;
        foreach (PdfRedactionRegion r in plan.Regions)
        {
            any |= r.FillColor is not null || _opt.Overlay is not null;
        }

        if (!any)
        {
            return;
        }

        PdfCreatedFont? font = null;
        using PdfPageContentBuilder b = page.BeginContent();
        foreach (PdfRedactionRegion r in plan.Regions)
        {
            IReadOnlyList<double>? fill = r.FillColor ?? _opt.Overlay?.Color;
            if (fill is null)
            {
                continue;
            }

            SetColor(b, fill, fill: true);
            PdfRectangle rc = r.Rectangle;
            b.Rectangle(rc.Left, rc.Bottom, rc.Width, rc.Height);
            b.Fill();
            if (!string.IsNullOrEmpty(r.OverlayText))
            {
                try
                {
                    font ??= _doc.AddStandardFont("Helvetica");
                    double size = _opt.Overlay?.TextSize > 0 ? _opt.Overlay.TextSize : Math.Min(12, rc.Height * 0.8);
                    b.SetFont(font, size);
                    double width = b.MeasureText(r.OverlayText);
                    if (width > rc.Width - 2 && width > 0)
                    {
                        size = Math.Max(1, size * (rc.Width - 2) / width);
                        b.SetFont(font, size);
                    }

                    SetColor(b, _opt.Overlay?.TextColor ?? [1.0], fill: true);
                    b.DrawText(r.OverlayText, rc.Left + 1, rc.Bottom + ((rc.Height - size) / 2) + (size * 0.2));
                }
                catch (ArgumentException)
                {
                    _res._warnings.Add($"Page {index + 1}: overlay text could not be drawn with the standard font.");
                }
            }
        }

        b.Commit();
    }

    private static void SetColor(PdfContentBuilder b, IReadOnlyList<double> c, bool fill)
    {
        _ = fill;
        switch (c.Count)
        {
            case 1:
                b.SetFillGray(c[0]);
                break;
            case 3:
                b.SetFillRgb(c[0], c[1], c[2]);
                break;
            default:
                b.SetFillCmyk(c[0], c[1], c[2], c[3]);
                break;
        }
    }

    // ---- structure ----

    private void StripStructure(Dictionary<int, HashSet<int>> mcidsByPage)
    {
        PdfDictionary? root = _doc.Catalog.GetDictionary("StructTreeRoot");
        if (root is null)
        {
            return;
        }

        var nodes = new List<(PdfDictionary Dict, int Parent)>();
        var dirty = new HashSet<int>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfDictionary Dict, int Parent)>();
        stack.Push((root, -1));
        int budget = _doc.Limits.MaxObjects;
        while (stack.Count > 0 && budget-- > 0)
        {
            (PdfDictionary d, int parent) = stack.Pop();
            if (!seen.Add(d))
            {
                continue;
            }

            int self = nodes.Count;
            nodes.Add((d, parent));
            int pg = d.GetRaw("Pg") is PdfReference pr ? pr.Number : -1;
            bool hit = false;
            PdfObject? k = d.Get("K");
            var kids = new List<PdfObject?>();
            if (k is PdfArray ka)
            {
                for (int i = 0; i < ka.Count; i++)
                {
                    kids.Add(ka[i]);
                }
            }
            else
            {
                kids.Add(k);
            }

            foreach (PdfObject? kid in kids)
            {
                switch (kid)
                {
                    case PdfInteger mi:
                        hit |= McidHit(mcidsByPage, pg, mi.Value);
                        break;
                    case PdfDictionary kd when kd.GetName("Type") == "MCR":
                        int kpg = kd.GetRaw("Pg") is PdfReference kr ? kr.Number : pg;
                        hit |= McidHit(mcidsByPage, kpg, kd.GetInt("MCID", -1));
                        break;
                    case PdfDictionary kd when kd.GetName("Type") != "OBJR":
                        stack.Push((kd, self));
                        break;
                }
            }

            if (hit)
            {
                for (int n = self; n >= 0; n = nodes[n].Parent)
                {
                    dirty.Add(n);
                }
            }
        }

        foreach (int n in dirty)
        {
            PdfDictionary d = nodes[n].Dict;
            bool removed = false;
            foreach (string key in (string[])["ActualText", "Alt", "E"])
            {
                removed |= d.RemoveEntry(key);
            }

            if (removed)
            {
                _res.AlternateTextsRemoved++;
            }
        }
    }

    private static bool McidHit(Dictionary<int, HashSet<int>> byPage, int pageObject, long mcid) =>
        pageObject >= 0 && mcid >= 0 && byPage.TryGetValue(pageObject, out HashSet<int>? set) && set.Contains((int)Math.Min(mcid, int.MaxValue));

    // ---- metadata ----

    private void ClearMetadata()
    {
        string[] terms = [.. _opt.MetadataTerms.Where(static t => !string.IsNullOrEmpty(t))];
        if (terms.Length == 0)
        {
            return;
        }

        bool Matches(string text) => terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (_doc.Trailer.GetDictionary("Info") is { } info)
        {
            foreach (string key in (string[])[.. info.Keys])
            {
                if (info.Get(key) is PdfString s && Matches(s.GetText()))
                {
                    _doc.SetInfo(key, null);
                    _res.MetadataCleared++;
                }
            }
        }

        ClearOutlineTitles(Matches);

        PdfStream? xmp = _doc.Catalog.GetStream("Metadata");
        if (xmp is null)
        {
            return;
        }

        ReadOnlyMemory<byte> bytes = xmp.GetDecodedBytes(_ct);
        if (bytes.Length > _doc.Limits.MaxMetadataBytes)
        {
            throw new PdfLimitException("The XMP packet is larger than the metadata limit.");
        }

        string text = Encoding.UTF8.GetString(bytes.Span);
        if (!Matches(text))
        {
            return;
        }

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = _doc.Limits.MaxMetadataBytes };
            XDocument doc;
            using (var reader = XmlReader.Create(new StringReader(text), settings))
            {
                doc = XDocument.Load(reader);
            }

            int cleared = 0;
            foreach (XElement e in doc.Descendants().ToList())
            {
                foreach (XAttribute a in e.Attributes().ToList())
                {
                    if (!a.IsNamespaceDeclaration && Matches(a.Value))
                    {
                        a.Value = string.Empty;
                        cleared++;
                    }
                }

                if (!e.HasElements && Matches(e.Value))
                {
                    e.Value = string.Empty;
                    cleared++;
                }
            }

            if (cleared == 0)
            {
                throw new XmlException("nothing to clear");
            }

            _res.MetadataCleared += cleared;
            _doc.SetXmp(Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting)));
        }
        catch (XmlException)
        {
            // A packet that cannot be edited safely and mentions a term is dropped whole.
            _doc.Catalog.RemoveEntry("Metadata");
            _res.MetadataCleared++;
            _res._warnings.Add("The XMP packet mentions a redacted term and could not be edited, so it was removed.");
        }
    }

    // Bookmark titles are text from the pages; a title that holds a term is emptied (the bookmark and its destination stay).
    private void ClearOutlineTitles(Func<string, bool> matches)
    {
        PdfDictionary? root = _doc.Catalog.GetDictionary("Outlines");
        if (root is null)
        {
            return;
        }

        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfDictionary>();
        stack.Push(root);
        int budget = _doc.Limits.MaxObjects;
        while (stack.Count > 0 && budget-- > 0)
        {
            PdfDictionary item = stack.Pop();
            if (!seen.Add(item))
            {
                continue;
            }

            if (item.Get("Title") is PdfString t && matches(t.GetText()))
            {
                item.SetEntry("Title", PdfString.FromText(string.Empty));
                _res.MetadataCleared++;
            }

            if (item.GetDictionary("First") is { } first)
            {
                stack.Push(first);
            }

            if (item.GetDictionary("Next") is { } next)
            {
                stack.Push(next);
            }
        }
    }

    // ---- active content, attachments ----

    private static bool IsScript(PdfDictionary? action) => action is not null && action.GetName("S") == "JavaScript";

    private void RemoveJavaScript()
    {
        PdfDictionary cat = _doc.Catalog;
        cat.GetDictionary("Names")?.RemoveEntry("JavaScript");
        cat.RemoveEntry("AA");
        if (IsScript(cat.GetDictionary("OpenAction")))
        {
            cat.RemoveEntry("OpenAction");
        }

        for (int i = 0; i < _doc.Pages.Count; i++)
        {
            PdfDictionary pd = _doc.Pages[i].Dictionary;
            pd.RemoveEntry("AA");
            if (pd.GetArray("Annots") is { } annots)
            {
                for (int j = 0; j < annots.Count; j++)
                {
                    if (annots[j] is PdfDictionary a)
                    {
                        StripScripts(a);
                    }
                }
            }
        }

        if (cat.GetDictionary("AcroForm")?.GetArray("Fields") is { } fields)
        {
            var stack = new Stack<PdfArray>();
            var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
            stack.Push(fields);
            int budget = _doc.Limits.MaxObjects;
            while (stack.Count > 0 && budget-- > 0)
            {
                PdfArray arr = stack.Pop();
                if (!seen.Add(arr))
                {
                    continue;
                }

                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is PdfDictionary f)
                    {
                        StripScripts(f);
                        if (f.GetArray("Kids") is { } kids)
                        {
                            stack.Push(kids);
                        }
                    }
                }
            }
        }
    }

    private static void StripScripts(PdfDictionary d)
    {
        d.RemoveEntry("AA");
        if (IsScript(d.GetDictionary("A")))
        {
            d.RemoveEntry("A");
        }
    }

    private void RemoveEmbeddedFiles()
    {
        PdfDictionary cat = _doc.Catalog;
        cat.GetDictionary("Names")?.RemoveEntry("EmbeddedFiles");
        cat.RemoveEntry("AF");
        for (int i = 0; i < _doc.Pages.Count; i++)
        {
            PdfDictionary pd = _doc.Pages[i].Dictionary;
            if (pd.GetArray("Annots") is not { } annots)
            {
                continue;
            }

            var remove = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            for (int j = 0; j < annots.Count; j++)
            {
                if (annots[j] is PdfDictionary a && a.GetName("Subtype") == "FileAttachment")
                {
                    remove.Add(a);
                }
            }

            if (remove.Count > 0)
            {
                RemoveAnnotations(pd, remove);
            }
        }
    }

    internal static string Format(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
