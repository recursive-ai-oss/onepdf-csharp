using System.Diagnostics;

namespace OnePdf.Tests.Forms;

/// <summary>A black-box renderer (pdftoppm, when installed) shows the values of a filled and of a flattened file.</summary>
public class FormRenderTests
{
    private static string? Pdftoppm()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string c = Path.Combine(dir, "pdftoppm");
            if (File.Exists(c))
            {
                return c;
            }
        }

        return null;
    }

    // Renders page 1 at 72 dpi as a binary PPM and counts the dark pixels inside the PDF-space box (x0,y0,x1,y1; page height 200).
    private static int DarkPixels(string tool, byte[] pdf, int x0, int y0, int x1, int y1)
    {
        string dir = Path.Combine(Path.GetTempPath(), "onepdf-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "in.pdf");
            File.WriteAllBytes(file, pdf);
            var psi = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in new[] { "-r", "72", "-f", "1", "-l", "1", file, Path.Combine(dir, "out") })
            {
                psi.ArgumentList.Add(a);
            }

            using (Process p = Process.Start(psi)!)
            {
                p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }

            byte[] ppm = File.ReadAllBytes(Directory.GetFiles(dir, "out*.ppm").Single());
            int pos = 0;
            string Token()
            {
                while (char.IsWhiteSpace((char)ppm[pos]))
                {
                    pos++;
                }

                int s = pos;
                while (!char.IsWhiteSpace((char)ppm[pos]))
                {
                    pos++;
                }

                return System.Text.Encoding.ASCII.GetString(ppm, s, pos - s);
            }

            Assert.Equal("P6", Token());
            int w = int.Parse(Token(), System.Globalization.CultureInfo.InvariantCulture), h = int.Parse(Token(), System.Globalization.CultureInfo.InvariantCulture);
            Token();
            pos++;
            int count = 0;
            for (int py = h - y1; py < h - y0; py++)
            {
                for (int px = x0; px < x1; px++)
                {
                    int o = pos + (((py * w) + px) * 3);
                    if (ppm[o] + ppm[o + 1] + ppm[o + 2] < 300)
                    {
                        count++;
                    }
                }
            }

            return count;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FilledAndFlattenedFilesShowTheValuesInARenderer()
    {
        string? tool = Pdftoppm();
        Assert.SkipWhen(tool is null, "pdftoppm is not installed");
        byte[] plain = FormTestSupport.Synthetic(needAppearances: false, markup: false);
        // The multi-line field [10 10 190 45]; with no appearance it is blank.
        int blank = DarkPixels(tool!, plain, 10, 10, 190, 45);

        using PdfDocument doc = PdfDocument.Open(plain, FormTestSupport.Editable);
        doc.Form!.SetValue("notes", "Rendered words\nsecond line");
        byte[] filled = Writing.WriterSupport.SaveIncremental(doc);
        int withText = DarkPixels(tool!, filled, 10, 10, 190, 45);
        Assert.True(withText > blank + 60, $"blank {blank}, filled {withText}");

        doc.Flatten();
        byte[] flat = Writing.WriterSupport.Save(doc);
        int flattened = DarkPixels(tool!, flat, 10, 10, 190, 45);
        Assert.True(Math.Abs(flattened - withText) <= withText / 20 + 2, $"filled {withText}, flattened {flattened}");
    }
}
