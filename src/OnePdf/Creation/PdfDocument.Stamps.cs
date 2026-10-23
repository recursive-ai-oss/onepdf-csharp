namespace OnePdf;

/// <summary>Options of the stamp and watermark methods of <see cref="PdfDocument"/>.</summary>
public sealed class PdfStampOptions
{
    /// <summary>Opacity 0..1 of the whole stamp. Default 1.</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>
    /// Rotation in degrees counter-clockwise as the reader sees the page: the page's own <c>/Rotate</c> is compensated, so 0 reads
    /// upright on a rotated page too. Default 0.
    /// </summary>
    public double RotationDegrees { get; init; }

    /// <summary>Over (default) or under the page content.</summary>
    public PdfContentPlacement Placement { get; init; } = PdfContentPlacement.Overlay;

    /// <summary>X of the stamp's centre in default user space. Null means the centre of the page's CropBox.</summary>
    public double? CenterX { get; init; }

    /// <summary>Y of the stamp's centre in default user space. Null means the centre of the page's CropBox.</summary>
    public double? CenterY { get; init; }

    /// <summary>Text colour red, green, blue in 0..1 (text stamps only). Default black.</summary>
    public (double Red, double Green, double Blue) Color { get; init; }

    /// <summary>Blend mode of the stamp. Default normal.</summary>
    public PdfBlendMode BlendMode { get; init; }
}

public sealed partial class PdfDocument
{
    /// <summary>
    /// Draws <paramref name="text"/> on each chosen page as a watermark or stamp. The text is drawn once into a shared Form XObject,
    /// which every page places with its own matrix, so the cost per page is a few dozen bytes.
    /// </summary>
    /// <param name="text">One line of text.</param>
    /// <param name="font">A font of this document from <see cref="AddStandardFont"/> or <see cref="AddFont(ReadOnlyMemory{byte}, PdfEmbedFontOptions?)"/>.</param>
    /// <param name="fontSize">Size in points.</param>
    /// <param name="options">Null means defaults (centred, upright, opaque black, over the content).</param>
    /// <param name="pageIndexes">Zero-based pages; null means every page.</param>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="ArgumentException">The font is from another document, or the text has no drawable character.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page index, the size or the opacity is out of range.</exception>
    public void AddTextStamp(string text, PdfCreatedFont font, double fontSize, PdfStampOptions? options = null, IEnumerable<int>? pageIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        EnsureEditable(0);
        options ??= new PdfStampOptions();
        if (font.Document != this)
        {
            throw new ArgumentException("The font belongs to another document.", nameof(font));
        }

        double width = font.MeasureText(text, fontSize);
        double height = (font.Ascent - font.Descent) * fontSize;
        if (!(width > 0) || !(height > 0))
        {
            throw new ArgumentException("The text has nothing to draw.", nameof(text));
        }

        PdfFormContentBuilder fb = BeginForm(new PdfRectangle(0, 0, width, height));
        PdfFormXObject form;
        try
        {
            ApplyStampState(fb, options);
            fb.SetFont(font, fontSize);
            fb.SetFillRgb(options.Color.Red, options.Color.Green, options.Color.Blue);
            fb.DrawText(text, 0, -font.Descent * fontSize);
            form = fb.Commit();
        }
        finally
        {
            fb.Dispose();
        }

        PlaceStamp(form, width, height, options, pageIndexes);
    }

    /// <summary>Draws an image on each chosen page as a watermark or stamp, <paramref name="width"/> by <paramref name="height"/> points. The image is placed through one shared Form XObject.</summary>
    /// <param name="image">An image of this document from <see cref="AddJpegImage"/> or <see cref="AddRawImage"/>.</param>
    /// <param name="width">Width on the page in points.</param>
    /// <param name="height">Height on the page in points.</param>
    /// <param name="options">Null means defaults. <see cref="PdfStampOptions.Color"/> does not apply.</param>
    /// <param name="pageIndexes">Zero-based pages; null means every page.</param>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    /// <exception cref="ArgumentException">The image is from another document.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size, page index or the opacity is out of range.</exception>
    public void AddImageStamp(PdfCreatedImage image, double width, double height, PdfStampOptions? options = null, IEnumerable<int>? pageIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        EnsureEditable(0);
        options ??= new PdfStampOptions();
        if (image.Document != this)
        {
            throw new ArgumentException("The image belongs to another document.", nameof(image));
        }

        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (!double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        PdfFormContentBuilder fb = BeginForm(new PdfRectangle(0, 0, width, height));
        PdfFormXObject form;
        try
        {
            ApplyStampState(fb, options);
            fb.DrawImage(image, 0, 0, width, height);
            form = fb.Commit();
        }
        finally
        {
            fb.Dispose();
        }

        PlaceStamp(form, width, height, options, pageIndexes);
    }

    private static void ApplyStampState(PdfContentBuilder b, PdfStampOptions options)
    {
        if (!double.IsFinite(options.Opacity) || options.Opacity < 0 || options.Opacity > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Opacity must be between 0 and 1.");
        }

        if (options.Opacity < 1)
        {
            b.SetOpacity(options.Opacity);
        }

        if (options.BlendMode != PdfBlendMode.Normal)
        {
            b.SetBlendMode(options.BlendMode);
        }
    }

    private void PlaceStamp(PdfFormXObject form, double width, double height, PdfStampOptions options, IEnumerable<int>? pageIndexes)
    {
        int count = PageCount;
        List<int> indexes = [];
        if (pageIndexes is null)
        {
            for (int i = 0; i < count; i++)
            {
                indexes.Add(i);
            }
        }
        else
        {
            foreach (int i in pageIndexes)
            {
                if ((uint)i >= (uint)count)
                {
                    throw new ArgumentOutOfRangeException(nameof(pageIndexes), $"Page {i} does not exist.");
                }

                if (!indexes.Contains(i))
                {
                    indexes.Add(i);
                }
            }
        }

        foreach (int i in indexes)
        {
            PdfPage page = Pages[i];
            PdfRectangle crop = page.CropBox;
            double cx = options.CenterX ?? ((crop.Left + crop.Right) / 2);
            double cy = options.CenterY ?? ((crop.Bottom + crop.Top) / 2);
            using PdfPageContentBuilder b = page.BeginContent(options.Placement);
            b.Translate(cx, cy);
            b.Rotate(options.RotationDegrees + page.Rotate);
            b.Translate(-width / 2, -height / 2);
            b.DrawForm(form);
            b.Commit();
        }
    }
}
