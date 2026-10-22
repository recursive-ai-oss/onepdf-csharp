namespace OnePdf.Content;

internal sealed partial class ContentInterpreter
{
    // ---- shadings ----

    /// <summary>Builds (once per object) the shading for <c>sh</c> or a shading pattern; null with a diagnostic when unusable.</summary>
    private PdfShading? LoadShading(PdfObject obj)
    {
        if (_shadings.TryGetValue(obj, out PdfShading? cached))
        {
            return cached;
        }

        PdfShading? shading = null;
        try
        {
            shading = PdfShading.Load(_doc, obj, _res, _spaces, _ct, out string? problem);
            if (shading is null)
            {
                Report(PdfSeverity.Warning, problem ?? "A shading is unusable and was skipped.");
            }
            else if (problem is not null)
            {
                Report(PdfSeverity.Warning, problem);
            }
        }
        catch (PdfException e)
        {
            Report(PdfSeverity.Error, "A shading could not be read and was skipped: " + e.Message);
        }

        _shadings[obj] = shading;
        return shading;
    }

    // ---- patterns ----

    /// <summary>
    /// The pattern named by <c>scn</c>, resolved once per name and parent space. Null (with a diagnostic) when the pattern
    /// is unusable, in which case the colour operator does nothing.
    /// </summary>
    private PdfPattern? GetPattern(string name, PdfObject obj)
    {
        int number = obj.Reference?.Number ?? -1;
        var key = (number, name, _patternBase);
        if (number >= 0 && _patterns.TryGetValue(key, out PdfPattern? known))
        {
            return known;
        }

        PdfPattern? made = MakePattern(name, obj);
        if (number >= 0)
        {
            _patterns[key] = made;
        }

        return made;
    }

    private PdfPattern? MakePattern(string name, PdfObject obj)
    {
        PdfDictionary d = obj is PdfStream st ? st.Dictionary : (PdfDictionary)obj;
        PdfMatrix matrix = ReadMatrix(d.GetArray("Matrix")).Then(_patternBase);
        if (!matrix.IsFinite)
        {
            Report(PdfSeverity.Warning, "Pattern /" + name + " has a matrix that overflows; it was skipped.");
            return null;
        }

        long type = d.GetInt("PatternType", 0);
        if (type == 2)
        {
            if (d.Get("Shading") is not ({ } shadingObj and (PdfDictionary or PdfStream)))
            {
                Report(PdfSeverity.Warning, "Shading pattern /" + name + " has no /Shading; it was skipped.");
                return null;
            }

            PdfShading? shading = LoadShading(shadingObj);
            return shading is null ? null : new PdfShadingPattern(obj, name, matrix, shading);
        }

        if (type != 1 || obj is not PdfStream)
        {
            Report(PdfSeverity.Warning, "Pattern /" + name + " has an unknown /PatternType or is not a stream; it was skipped.");
            return null;
        }

        PdfBounds? bbox = ReadRect(d.GetArray("BBox"));
        if (bbox is not { } box || !(box.Width > 0) || !(box.Height > 0))
        {
            Report(PdfSeverity.Warning, "Tiling pattern /" + name + " has a missing or empty /BBox; it was skipped.");
            return null;
        }

        long paint = d.GetInt("PaintType", 1);
        if (paint is not (1 or 2))
        {
            Report(PdfSeverity.Warning, "Tiling pattern /" + name + " has an unknown /PaintType; coloured is assumed.");
            paint = 1;
        }

        long tiling = d.GetInt("TilingType", 1);
        if (tiling is < 1 or > 3)
        {
            tiling = 1;
        }

        double xs = d.GetDouble("XStep", box.Width), ys = d.GetDouble("YStep", box.Height);
        if (!double.IsFinite(xs) || xs == 0)
        {
            Report(PdfSeverity.Warning, "Tiling pattern /" + name + " has no usable /XStep; the box width is used.");
            xs = box.Width;
        }

        if (!double.IsFinite(ys) || ys == 0)
        {
            Report(PdfSeverity.Warning, "Tiling pattern /" + name + " has no usable /YStep; the box height is used.");
            ys = box.Height;
        }

        return new PdfTilingPattern(obj, name, matrix, paint == 1, (int)tiling, box, xs, ys);
    }

    /// <summary>
    /// Interprets the cell of a tiling pattern once into its own display list. Patterns nest inside the form and pattern
    /// recursion limit, and a pattern that is already being interpreted (it paints with itself) gets no cell.
    /// </summary>
    private void BuildCell(PdfTilingPattern pattern)
    {
        pattern.CellBuilt = true;
        if (_stopped)
        {
            return;
        }

        if (_formDepth >= _limits.MaxRecursionDepth)
        {
            Report(PdfSeverity.Error, "Patterns are nested deeper than the recursion limit; one was skipped.");
            return;
        }

        var stream = (PdfStream)pattern.Source;
        int number = stream.Reference?.Number ?? -1;
        if (number >= 0 && !_activeForms.Add(number))
        {
            Report(PdfSeverity.Warning, "A tiling pattern paints with itself (directly or through others); the inner use was skipped.");
            return;
        }

        try
        {
            ReadOnlyMemory<byte> data;
            try
            {
                data = stream.GetDecodedBytes(_ct);
            }
            catch (PdfException e)
            {
                Report(PdfSeverity.Error, "A tiling pattern could not be decoded and was skipped: " + e.Message);
                return;
            }

            var cell = new PdfDisplayList();
            var child = new ContentInterpreter(this, cell);
            try
            {
                child.RunCell(pattern, data);
            }
            finally
            {
                _opCount = child._opCount;
                if (child._stopped)
                {
                    _stopped = true;
                }
            }

            pattern.Cell = cell;
        }
        finally
        {
            if (number >= 0)
            {
                _activeForms.Remove(number);
            }
        }
    }

    private void RunCell(PdfTilingPattern pattern, ReadOnlyMemory<byte> data)
    {
        _res = pattern.Dictionary.GetDictionary("Resources");
        _noColor = !pattern.IsColored;
        _patternBase = PdfMatrix.Identity;
        ResetState(PdfMatrix.Identity);
        try
        {
            PdfBounds b = pattern.BBox;
            double[] pts = [b.Left, b.Bottom, b.Right, b.Bottom, b.Right, b.Top, b.Left, b.Top];
            var rect = new PdfPath(RectVerbs, pts);
            PdfGraphicsState s = SnapshotCore();
            _device.PushClip(in rect, false, in s);
            _g.ClipCount++;
            Execute(new ContentLexer(data, _limits.MaxNestingDepth));
            UnwindTo(0, 0, "a pattern cell");
        }
        finally
        {
            UnwindTo(0, 0, null);
        }
    }
}
