using System.Text;

namespace OnePdf.Tests.Content;

/// <summary>The soft mask <c>/TR</c> transfer function arrives as an evaluated 256-entry table (ISO 32000-2 11.6.5.2).</summary>
public sealed class SoftMaskTransferTests
{
    private sealed class MaskDevice : PdfDevice
    {
        public readonly List<PdfSoftMaskInfo> Masks = [];

        public override void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state) => Masks.Add(mask);
    }

    private static PdfSoftMaskInfo Run(string tr, Action<PdfBuilder>? extra, out PdfDocument doc, out string text)
    {
        string res = "/Resources << /ExtGState << /M << /SMask << /Type /Mask /S /Luminosity /G 5 0 R" + (tr.Length > 0 ? " /TR " + tr : "") + " >> >> >> >>";
        byte[] data = TestPdf.Build("q /M gs 0 0 80 80 re f Q", res, b =>
        {
            b.Stream(5, "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency /CS /DeviceGray >>", Encoding.Latin1.GetBytes("1 g 0 0 50 50 re f"));
            extra?.Invoke(b);
        }, objects: 7);
        doc = TestPdf.Open(data);
        var dev = new MaskDevice();
        TestPdf.Page(doc).Interpret(dev);
        text = TestPdf.Text(data);
        return Assert.Single(dev.Masks);
    }

    [Fact]
    public void ExponentialFunctionIsEvaluatedIntoATable()
    {
        PdfSoftMaskInfo m = Run("<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 2 >>", null, out PdfDocument doc, out string text);
        using (doc)
        {
            Assert.NotNull(m.TransferTable);
            Assert.Equal(256, m.TransferTable!.Length);
            Assert.Equal(0, m.TransferTable[0]);
            Assert.Equal(64, m.TransferTable[128]); // (128/255)^2 * 255 = 64.26
            Assert.Equal(255, m.TransferTable[255]);
            Assert.Contains(" trtable=" + Convert.ToHexString(m.TransferTable).ToLowerInvariant(), text);
        }
    }

    [Fact]
    public void PostScriptFunctionInvertsTheMask()
    {
        PdfSoftMaskInfo m = Run("6 0 R", b => b.Stream(6, "/FunctionType 4 /Domain [0 1] /Range [0 1]", Encoding.Latin1.GetBytes("{ 1 exch sub }")), out PdfDocument doc, out _);
        using (doc)
        {
            Assert.Equal(255, m.TransferTable![0]);
            Assert.Equal(155, m.TransferTable[100]);
            Assert.Equal(0, m.TransferTable[255]);
        }
    }

    [Fact]
    public void IdentityAndAbsentGiveNoTable()
    {
        PdfSoftMaskInfo a = Run("/Identity", null, out PdfDocument d1, out string text);
        PdfSoftMaskInfo b = Run("", null, out PdfDocument d2, out _);
        using (d1)
        using (d2)
        {
            Assert.Null(a.TransferTable);
            Assert.Null(b.TransferTable);
            Assert.Contains(" trtable=none", text);
            Assert.DoesNotContain(d1.Diagnostics, d => d.Message.Contains("/TR"));
        }
    }

    [Fact]
    public void BrokenFunctionFallsBackToIdentityWithADiagnostic()
    {
        PdfSoftMaskInfo a = Run("6 0 R", b => b.Stream(6, "/FunctionType 4 /Domain [0 1] /Range [0 1]", Encoding.Latin1.GetBytes("{ 1 exch bogus")), out PdfDocument d1, out _);
        PdfSoftMaskInfo c = Run("<< /FunctionType 9 >>", null, out PdfDocument d2, out _);
        using (d1)
        using (d2)
        {
            Assert.Null(a.TransferTable);
            Assert.Contains(d1.Diagnostics, d => d.Message.Contains("/TR"));
            Assert.Null(c.TransferTable);
            Assert.Contains(d2.Diagnostics, d => d.Message.Contains("/TR"));
        }
    }
}
