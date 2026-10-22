using System.Text;
using OneImage;

namespace OnePdf.Tests.Images;

/// <summary>Builds one-image PDF files in memory and runs the image pipeline on them.</summary>
internal static class ImageKit
{
    /// <summary>An extra indirect object: a plain body, or a stream when <see cref="Data"/> is set.</summary>
    public sealed record Obj(int Number, string Body, byte[]? Data = null);

    public sealed class Result(PdfDocument doc, PdfImageData? image) : IDisposable
    {
        public PdfDocument Doc { get; } = doc;
        public PdfImageData? Image { get; } = image;
        public PdfImageData Img => Image ?? throw new InvalidOperationException("no image");
        public string[] Warnings => [.. Doc.Diagnostics.Select(d => d.Message)];
        public void Dispose() => Doc.Dispose();
    }

    /// <summary>Image XObject is object 10; object 20, when given as a dictionary, is the resource dictionary.</summary>
    public static Result Make(string entries, byte[] data, PdfImageOptions? options = null, PdfLimits? limits = null, params Obj[] extra)
    {
        var b = new PdfBuilder();
        b.BasicPages();
        b.Stream(10, "/Type /XObject /Subtype /Image " + entries, data);
        foreach (Obj o in extra)
        {
            if (o.Data is null)
            {
                b.Obj(o.Number, o.Body);
            }
            else
            {
                b.Stream(o.Number, o.Body, o.Data);
            }
        }

        long x = b.XrefTable(31);
        b.Trailer("/Size 31 /Root 1 0 R", x);
        PdfDocument doc = PdfDocument.Open(b.ToArray(), limits is null ? null : new PdfOpenOptions { Limits = limits });
        var s = (PdfStream)doc.GetObject(10)!;
        PdfDictionary? res = doc.GetObject(20) as PdfDictionary;
        return new Result(doc, PdfImageData.FromXObject(doc, s, res, options));
    }

    public static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    public static byte[] Samples(PdfImageData img) => img.Samples.ToArray();

    public static byte[] Alpha(PdfImageData img) => img.Alpha!.Values.ToArray();

    /// <summary>An <see cref="IPdfImageDecoder"/> that returns what a delegate says and records its requests.</summary>
    public sealed class FakeDecoder(Func<PdfImageDecodeRequest, PdfDecodedImage?> decode) : IPdfImageDecoder
    {
        public int Calls { get; private set; }
        public PdfImageDecodeRequest Last { get; private set; }

        public PdfDecodedImage? Decode(in PdfImageDecodeRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Last = request;
            return decode(request);
        }
    }

    public static PdfImageOptions With(string filter, IPdfImageDecoder decoder, bool preferEncoded = false)
    {
        var reg = new PdfImageDecoderRegistry();
        reg.Register(filter, decoder);
        return new PdfImageOptions { Decoders = reg, PreferEncoded = preferEncoded };
    }

    /// <summary>
    /// JPEG decoder for tests, backed by OneImage. Honours the registry contract: components as stored (OneImage's
    /// Adobe CMYK inversion is undone), APP14 reported.
    /// </summary>
    public sealed class OneImageDctDecoder : IPdfImageDecoder
    {
        public PdfDecodedImage? Decode(in PdfImageDecodeRequest request, CancellationToken cancellationToken)
        {
            try
            {
                return DecodeCore(request.Data, cancellationToken);
            }
            catch (ImageFormatException ex)
            {
                // OneImage decodes lazily, so damaged scan data surfaces while pixels are read.
                throw new PdfFormatException("JPEG: " + ex.Message, ex);
            }
        }

        private static PdfDecodedImage DecodeCore(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            int? adobe = AdobeTransform(data.Span);
            Image img = Image.Load(data, new LoadOptions { AutoOrient = false }, cancellationToken);

            PixelFormat f = img.Format;
            if (f != PixelFormat.Gray8 && f != PixelFormat.Rgb8 && f != PixelFormat.Cmyk8)
            {
                img = img.ConvertTo(PixelFormat.Rgb8);
                f = img.Format;
            }

            int comps = f.ColorChannels;
            byte[] px = new byte[img.Width * img.Height * comps];
            if (comps == 1)
            {
                Gray8[] a = img.ToArray<Gray8>(0, cancellationToken);
                for (int i = 0; i < a.Length; i++)
                {
                    px[i] = a[i].V;
                }
            }
            else if (comps == 3)
            {
                Rgb24[] a = img.ToArray<Rgb24>(0, cancellationToken);
                for (int i = 0; i < a.Length; i++)
                {
                    (px[i * 3], px[i * 3 + 1], px[i * 3 + 2]) = (a[i].R, a[i].G, a[i].B);
                }
            }
            else
            {
                Cmyk32[] a = img.ToArray<Cmyk32>(0, cancellationToken);
                for (int i = 0; i < a.Length; i++)
                {
                    (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = (a[i].C, a[i].M, a[i].Y, a[i].K);
                }
            }

            if (comps == 4 && adobe is not null)
            {
                for (int i = 0; i < px.Length; i++)
                {
                    px[i] = (byte)(255 - px[i]);
                }
            }

            return new PdfDecodedImage(img.Width, img.Height, comps, 8, px) { AdobeTransform = adobe };
        }

        public static int? AdobeTransform(ReadOnlySpan<byte> jpeg)
        {
            int p = 2;
            while (p + 4 <= jpeg.Length && jpeg[p] == 0xFF)
            {
                int marker = jpeg[p + 1];
                int len = (jpeg[p + 2] << 8) | jpeg[p + 3];
                if (marker == 0xEE && len >= 14 && p + 4 + 12 <= jpeg.Length && jpeg.Slice(p + 4, 5).SequenceEqual("Adobe"u8))
                {
                    return jpeg[p + 4 + 11];
                }

                if (marker == 0xDA)
                {
                    break;
                }

                p += 2 + len;
            }

            return null;
        }
    }
}

/// <summary>Four-byte CMYK pixel laid out as OneImage's Cmyk8 format.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly record struct Cmyk32(byte C, byte M, byte Y, byte K) : IPixel<Cmyk32>
{
    public static PixelFormat Format => PixelFormat.Cmyk8;
}
