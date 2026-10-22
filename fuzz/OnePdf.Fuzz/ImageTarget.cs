using System;
using System.Text;
using System.Threading;

namespace OnePdf.Fuzz;

/// <summary>
/// Image stream target. The first bytes choose the image dictionary (size, bit depth, colour space, /Decode, colour-key
/// mask, stencil, codec filter); the rest is the stream data. A fake decoder turns codec data into samples whose shape
/// also comes from the input, so every conversion path is reachable. Only the library's typed exceptions may escape.
/// </summary>
internal static class ImageTarget
{
    private static readonly string[] Spaces =
    [
        "/DeviceGray", "/DeviceRGB", "/DeviceCMYK", "[/Indexed /DeviceRGB 7 <00112233445566778899aabbccddeeff00112233>]",
        "[/Indexed /DeviceCMYK 300 <00ff>]", "[/Lab << /WhitePoint [0.95 1 1.09] /Range [-90 90 -90 90] >>]",
        "[/Separation /Spot /DeviceCMYK 9 0 R]", "[/DeviceN [/A /B] /DeviceRGB 9 0 R]", "[/CalRGB << /WhitePoint [0.95 1 1.09] >>]",
        "/Missing", "[/Indexed /Pattern 1 <00>]",
    ];

    private static readonly string[] Filters = ["", "/Filter /DCTDecode", "/Filter /JPXDecode", "/Filter /JBIG2Decode", "/Filter /CCITTFaxDecode"];

    private static readonly int[] Depths = [1, 2, 4, 8, 16, 3, 0];

    private sealed class FakeDecoder(byte[] shape) : IPdfImageDecoder
    {
        public PdfDecodedImage? Decode(in PdfImageDecodeRequest request, CancellationToken cancellationToken)
        {
            int comps = 1 + shape[0] % 4;
            int bpc = Depths[shape[1] % 5];
            int w = 1 + shape[2] % 40;
            int h = 1 + shape[3] % 40;
            bool alpha = (shape[0] & 0x80) != 0;
            if (shape[1] == 255)
            {
                return null;
            }

            long stride = ((long)w * (comps + (alpha ? 1 : 0)) * bpc + 7) / 8;
            int len = (int)Math.Min(stride * h, request.Data.Length + 8);
            var samples = new byte[len];
            request.Data.Span[..Math.Min(len, request.Data.Length)].CopyTo(samples);
            return new PdfDecodedImage(w, h, comps, bpc, samples) { HasAlpha = alpha };
        }
    }

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
        {
            return;
        }

        int w = 1 + data[0] % 48;
        int h = 1 + data[1] % 48;
        if ((data[2] & 0x40) != 0)
        {
            w = data[0] * 257; // sometimes huge, to reach the limits
            h = data[1] * 257;
        }

        int bpc = Depths[data[3] % Depths.Length];
        string cs = Spaces[data[4] % Spaces.Length];
        string filter = Filters[data[5] % Filters.Length];
        var sb = new StringBuilder();
        sb.Append($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /BitsPerComponent {bpc} {filter} ");
        if ((data[2] & 1) != 0)
        {
            sb.Append("/ImageMask true ");
        }
        else
        {
            sb.Append("/ColorSpace ").Append(cs).Append(' ');
        }

        if ((data[2] & 2) != 0)
        {
            sb.Append($"/Decode [{data[6] % 3} {data[7] % 3} {data[8] % 3 - 1} {data[9] % 3} {data[6] % 2} {data[7] % 2} 1 0] ");
        }

        if ((data[2] & 4) != 0)
        {
            sb.Append($"/Mask [{data[10] % 4} {data[11] % 300} {data[12] % 4} {data[13] % 300} 0 255 0 255] ");
        }

        if ((data[2] & 8) != 0)
        {
            sb.Append("/SMask 6 0 R /SMaskInData 1 ");
        }

        if ((data[2] & 16) != 0)
        {
            sb.Append("/Mask 6 0 R ");
        }

        ReadOnlySpan<byte> body = data[14..];
        var ms = new System.IO.MemoryStream();
        void Raw(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        Raw("%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Raw("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>\nendobj\n");
        Raw("9 0 obj\n<< /FunctionType 2 /Domain [0 1] /N 1 >>\nendobj\n");
        Raw("6 0 obj\n<< /Type /XObject /Subtype /Image /Width 3 /Height 3 /ImageMask true /BitsPerComponent 1 /Length 3 >>\nstream\n");
        ms.Write(body[..Math.Min(3, body.Length)]);
        Raw("\nendstream\nendobj\n");
        Raw("4 0 obj\n" + sb + $"/Length {body.Length} >>\nstream\n");
        ms.Write(body);
        Raw("\nendstream\nendobj\ntrailer\n<< /Size 10 /Root 1 0 R >>\n");
        try
        {
            var limits = PdfLimits.Default with { MaxImagePixels = 1 << 20, MaxDecodedStreamBytes = 4 << 20 };
            using PdfDocument doc = PdfDocument.Open(ms.ToArray(), new PdfOpenOptions { Limits = limits });
            if (doc.GetObject(4) is not PdfStream s)
            {
                return;
            }

            var reg = new PdfImageDecoderRegistry();
            byte[] shape = data[..4].ToArray();
            reg.Register("DCTDecode", new FakeDecoder(shape));
            reg.Register("JPXDecode", new FakeDecoder(shape));
            reg.Register("JBIG2Decode", new FakeDecoder(shape));
            PdfImage? img = PdfImage.FromXObject(doc, s, null, new PdfImageOptions { Decoders = reg, PreferEncoded = (data[2] & 0x20) != 0 });
            if (img is { IsDecoded: true, IsStencil: false })
            {
                if (img.Samples.Length != (long)img.Width * img.Height * img.ColorSpace!.ComponentCount)
                {
                    throw new InvalidOperationException("Sample buffer size does not match the image size.");
                }
            }

            if (img?.Alpha is { } a && a.Values.Length != (long)a.Width * a.Height)
            {
                throw new InvalidOperationException("Alpha plane size does not match its size.");
            }
        }
        catch (PdfException)
        {
        }
    }
}
