namespace OnePdf.Tests.Images;

/// <summary>Opens a PDF and runs every image object through the image pipeline, counting outcomes by kind.</summary>
internal sealed class ImageSweep
{
    public int Files, Images, Decoded, Stencils, EncodedOnly, Skipped, Limit, Unusable, Bad;
    public readonly Dictionary<string, int> Codecs = [];
    public readonly List<string> Failures = [];

    private static readonly PdfImageOptions Options = ImageKit.With("DCTDecode", new ImageKit.OneImageDctDecoder());

    public void Add(string path)
    {
        Files++;
        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(path, new PdfOpenOptions { Limits = PdfLimits.Default with { MaxImagePixels = 1 << 26 } });
        }
        catch (PdfException)
        {
            return;
        }

        using (doc)
        {
            PdfDictionary? resources = null;
            try
            {
                resources = doc.PageCount > 0 ? doc.Pages[0].Resources : null;
            }
            catch (PdfException)
            {
            }

            int size = (int)Math.Min(doc.Trailer.GetInt("Size", doc.ObjectCount), 200_000);
            for (int n = 1; n < size; n++)
            {
                PdfObject? o;
                try
                {
                    o = doc.GetObject(n);
                }
                catch (PdfException)
                {
                    continue;
                }

                if (o is not PdfStream s || s.Dictionary.GetName("Subtype") != "Image")
                {
                    continue;
                }

                Images++;
                int diagBefore = doc.Diagnostics.Count;
                try
                {
                    PdfImageData? img = PdfImageData.FromXObject(doc, s, resources, Options);
                    string codec = s.UndecodedFilter ?? "none";
                    Codecs[codec] = Codecs.GetValueOrDefault(codec) + 1;
                    bool diag = doc.Diagnostics.Count > diagBefore;
                    if (img is null)
                    {
                        Unusable++;
                        if (!diag)
                        {
                            Bad++;
                            Failures.Add($"{path} obj {n}: null without diagnostic");
                        }
                    }
                    else if (img.IsDecoded)
                    {
                        if (img.IsStencil)
                        {
                            Stencils++;
                        }
                        else
                        {
                            Decoded++;
                            long want = (long)img.Width * img.Height * img.ColorSpace!.ComponentCount;
                            if (img.Samples.Length != want)
                            {
                                Bad++;
                                Failures.Add($"{path} obj {n}: sample length {img.Samples.Length} != {want}");
                            }
                        }
                    }
                    else
                    {
                        EncodedOnly++;
                        if (!diag)
                        {
                            Bad++;
                            Failures.Add($"{path} obj {n}: undecoded without diagnostic");
                        }
                    }
                }
                catch (PdfLimitException)
                {
                    Limit++;
                }
                catch (PdfException ex)
                {
                    Skipped++;
                    Failures.Add($"{path} obj {n}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    public override string ToString() =>
        $"files={Files} images={Images} decoded={Decoded} stencils={Stencils} encodedOnly={EncodedOnly} unusable={Unusable} limit={Limit} pdfException={Skipped} bad={Bad} codecs=" +
        string.Join(",", Codecs.OrderBy(k => k.Key).Select(k => k.Key + ":" + k.Value));
}
