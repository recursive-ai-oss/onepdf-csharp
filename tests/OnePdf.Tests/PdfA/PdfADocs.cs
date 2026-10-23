using OnePdf.Tests.Creation;

namespace OnePdf.Tests.PdfA;

/// <summary>
/// Builds the documents the PDF/A tests save and validate. Everything is made through the public creation API; fonts come from the
/// OFL set in ONEPDF_FONT_CACHE (a test skips when one is missing) and the standard fonts through the Fonts package.
/// </summary>
internal static class PdfADocs
{
    public const string Serif = "LiberationSerif-Regular.ttf";
    public const string Sans = "LiberationSans-Regular.ttf";
    public const string Cjk = "NotoSansJP-Regular.otf";

    public static string[] Names { get; } = ["truetype", "cff", "std14", "images", "form-filled", "form-flat", "annotations", "pages"];

    public static PdfDocument New()
    {
        PdfDocument d = PdfDocument.CreateEmpty();
        d.FontProvider = OnePdf.Fonts.FontsPackage.Provider;
        d.SetInfo("Title", "PDF/A test document");
        d.SetInfo("Author", "A. Writer & Co <test>");
        d.SetInfo("Subject", "Generated for conformance checks");
        d.SetInfo("Keywords", "pdfa, test");
        d.SetInfo("Creator", "OnePdf tests");
        d.SetInfo("Producer", "OnePdf");
        d.SetInfo("CreationDate", "D:20240102030405+01'00'");
        d.SetInfo("ModDate", "D:20240203040506Z");
        return d;
    }

    public static PdfDocument Build(string name) => name switch
    {
        "truetype" => TrueType(),
        "cff" => Cff(),
        "std14" => Standard14(),
        "images" => Images(),
        "form-filled" => Form(flatten: false),
        "form-flat" => Form(flatten: true),
        "annotations" => Annotations(),
        "pages" => Pages(),
        _ => throw new ArgumentException(name),
    };

    public static PdfDocument TrueType()
    {
        PdfDocument d = New();
        PdfPage page = d.InsertBlankPage(0, 595, 842);
        PdfCreatedFont serif = d.AddFont(CreationFont(Serif));
        PdfCreatedFont sans = d.AddFont(CreationFont(Sans), new PdfEmbedFontOptions { Subset = false });
        using PdfPageContentBuilder b = page.BeginContent();
        b.SetFont(serif, 14);
        b.DrawTextBox("The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. \u00C6sop \u2014 na\u00EFve caf\u00E9 \u03A9\u03BC\u03AD\u03B3\u03B1.", 72, 700, 450, 100);
        b.SetFont(sans, 10);
        b.DrawText("Whole font embedded, second face.", 72, 650);
        b.Commit();
        return d;
    }

    public static PdfDocument Cff()
    {
        PdfDocument d = New();
        PdfPage page = d.InsertBlankPage(0, 400, 300);
        PdfCreatedFont f = d.AddFont(CreationFont(Cjk));
        using PdfPageContentBuilder b = page.BeginContent();
        b.SetFont(f, 18);
        b.DrawText("Hello \u65E5\u672C\u8A9E\u306E\u30C6\u30AD\u30B9\u30C8", 40, 200);
        b.Commit();
        return d;
    }

    public static PdfDocument Standard14()
    {
        PdfDocument d = New();
        PdfPage page = d.InsertBlankPage(0, 400, 300);
        using PdfPageContentBuilder b = page.BeginContent();
        b.SetFont(d.AddStandardFont("Helvetica"), 14);
        b.DrawText("Helvetica text (caf\u00E9)", 20, 250);
        b.SetFont(d.AddStandardFont("Times-Italic"), 14);
        b.DrawText("Times italic text", 20, 220);
        b.SetFont(d.AddStandardFont("Courier-Bold"), 12);
        b.DrawText("Courier bold text", 20, 190);
        b.SetFont(d.AddStandardFont("Helvetica-BoldOblique"), 12);
        b.DrawText("Bold oblique", 20, 160);
        b.Commit();
        return d;
    }

    public static PdfDocument Images()
    {
        PdfDocument d = New();
        PdfPage page = d.InsertBlankPage(0, 400, 400);
        string dir = Path.Combine(CreationSupport.Root(), "testdata", "generated", "images");
        PdfCreatedImage jpeg = d.AddJpegImage(File.ReadAllBytes(Path.Combine(dir, "rgb16.jpg")));
        PdfCreatedImage gray = d.AddJpegImage(File.ReadAllBytes(Path.Combine(dir, "gray16.jpg")));
        byte[] samples = new byte[8 * 8 * 3];
        byte[] alpha = new byte[8 * 8];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int i = (y * 8) + x;
                samples[3 * i] = (byte)(x * 30);
                samples[(3 * i) + 1] = (byte)(y * 30);
                samples[(3 * i) + 2] = 128;
                alpha[i] = (byte)((x + y) * 16);
            }
        }

        PdfCreatedImage rgba = d.AddRawImage(8, 8, PdfImageColor.Rgb, samples, alpha);
        PdfCreatedImage g = d.AddRawImage(4, 4, PdfImageColor.Gray, new byte[16]);
        using PdfPageContentBuilder b = page.BeginContent();
        b.DrawImage(jpeg, 20, 300, 80, 80);
        b.DrawImage(gray, 120, 300, 80, 80);
        b.DrawImage(rgba, 220, 300, 80, 80);
        b.DrawImage(g, 320, 300, 40, 40);
        b.SetFillRgb(0.8, 0.2, 0.1);
        b.SetStrokeGray(0.3);
        b.SetLineWidth(2);
        b.SetDash([4, 2], 0);
        b.MoveTo(20, 20);
        b.CurveTo(100, 200, 200, 0, 300, 150);
        b.LineTo(300, 20);
        b.ClosePath();
        b.FillAndStroke();
        b.Save();
        b.SetOpacity(0.5);
        b.SetBlendMode(PdfBlendMode.Multiply);
        b.SetFillRgb(0.1, 0.4, 0.9);
        b.Ellipse(150, 100, 200, 100);
        b.Fill();
        b.Restore();
        b.Commit();
        return d;
    }

    public static PdfDocument Form(bool flatten)
    {
        PdfDocument d = New();
        PdfPage page = d.InsertBlankPage(0, 300, 200);
        PdfCreatedFont helv = d.AddStandardFont("Helvetica");
        PdfReference text = d.AddObject(Widget("Tx", "name", "20 150 180 175"));
        PdfDictionary combo = Widget("Ch", "fruit", "20 110 180 135");
        combo.SetEntry("Ff", new PdfInteger(1 << 17));
        var opt = new PdfArray();
        opt.Append(PdfString.FromText("Apple"));
        opt.Append(PdfString.FromText("Banana"));
        combo.SetEntry("Opt", opt);
        PdfReference comboRef = d.AddObject(combo);
        var annots = new PdfArray();
        annots.Append(text);
        annots.Append(comboRef);
        page.Dictionary.SetEntry("Annots", annots);
        foreach (PdfReference r in new[] { text, comboRef })
        {
            ((PdfDictionary)d.Resolve(r)!).SetEntry("P", page.Dictionary.Reference);
        }

        var form = new PdfDictionary();
        var fields = new PdfArray();
        fields.Append(text);
        fields.Append(comboRef);
        form.SetEntry("Fields", fields);
        form.SetEntry("DA", PdfString.FromText("/Helv 0 Tf 0 g"));
        var dr = new PdfDictionary();
        var fonts = new PdfDictionary();
        fonts.SetEntry("Helv", helv.Reference);
        dr.SetEntry("Font", fonts);
        form.SetEntry("DR", dr);
        form.SetEntry("NeedAppearances", new PdfBoolean(true));
        d.Catalog.SetEntry("AcroForm", form);

        PdfAcroForm f = d.Form!;
        f.SetValue("name", "Ada Lovelace");
        f.SetValue("fruit", "Banana");
        if (flatten)
        {
            d.Flatten();
        }

        return d;
    }

    private static PdfDictionary Widget(string ft, string name, string rect)
    {
        var w = new PdfDictionary();
        w.SetEntry("Type", new PdfName("Annot"));
        w.SetEntry("Subtype", new PdfName("Widget"));
        w.SetEntry("FT", new PdfName(ft));
        w.SetEntry("T", PdfString.FromText(name));
        w.SetEntry("F", new PdfInteger(4));
        string[] n = rect.Split(' ');
        var r = new PdfArray();
        foreach (string s in n)
        {
            r.Append(new PdfInteger(int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)));
        }

        w.SetEntry("Rect", r);
        w.SetEntry("DA", PdfString.FromText("/Helv 12 Tf 0 g"));
        var mk = new PdfDictionary();
        var bc = new PdfArray();
        bc.Append(new PdfInteger(0));
        mk.SetEntry("BC", bc);
        w.SetEntry("MK", mk);
        return w;
    }

    public static PdfDocument Annotations()
    {
        PdfDocument d = New();
        PdfPage page = d.InsertBlankPage(0, 300, 200);
        var square = new PdfDictionary();
        square.SetEntry("Type", new PdfName("Annot"));
        square.SetEntry("Subtype", new PdfName("Square"));
        square.SetEntry("Rect", Array(20, 20, 120, 80));
        square.SetEntry("C", Array(1, 0, 0));
        square.SetEntry("IC", Array(1, 1, 0.5));
        square.SetEntry("Contents", PdfString.FromText("A square"));
        PdfReference sq = d.AddObject(square);

        var free = new PdfDictionary();
        free.SetEntry("Type", new PdfName("Annot"));
        free.SetEntry("Subtype", new PdfName("FreeText"));
        free.SetEntry("Rect", Array(140, 20, 280, 60));
        free.SetEntry("Contents", PdfString.FromText("Free text note"));
        free.SetEntry("DA", PdfString.FromText("/Helv 11 Tf 0 g"));
        PdfReference ft = d.AddObject(free);

        var link = new PdfDictionary();
        link.SetEntry("Type", new PdfName("Annot"));
        link.SetEntry("Subtype", new PdfName("Link"));
        link.SetEntry("Rect", Array(20, 120, 120, 150));
        var action = new PdfDictionary();
        action.SetEntry("S", new PdfName("URI"));
        action.SetEntry("URI", PdfString.FromText("https://example.com/"));
        link.SetEntry("A", action);
        PdfReference lk = d.AddObject(link);

        var annots = new PdfArray();
        annots.Append(sq);
        annots.Append(ft);
        annots.Append(lk);
        page.Dictionary.SetEntry("Annots", annots);
        using PdfPageContentBuilder b = page.BeginContent();
        b.SetFont(d.AddStandardFont("Times-Roman"), 12);
        b.DrawText("Annotated page", 20, 170);
        b.Commit();
        return d;
    }

    public static PdfDocument Pages()
    {
        PdfDocument d = New();
        PdfCreatedFont serif = d.AddFont(CreationFont(Serif));
        for (int i = 0; i < 3; i++)
        {
            PdfPage p = d.InsertBlankPage(i, 300, 200);
            using PdfPageContentBuilder b = p.BeginContent();
            b.SetFont(serif, 16);
            b.DrawText("Page number " + (i + 1), 20, 100);
            b.Commit();
        }

        d.DuplicatePage(0);
        d.MovePage(3, 1);

        using PdfDocument other = New();
        PdfPage op = other.InsertBlankPage(0, 300, 200);
        PdfCreatedFont sans = other.AddFont(CreationFont(Sans));
        using (PdfPageContentBuilder ob = op.BeginContent())
        {
            ob.SetFont(sans, 12);
            ob.DrawText("Imported page", 20, 100);
            ob.Commit();
        }

        d.ImportPages(other);
        d.RotatePage(2, 90);
        d.AddTextStamp("DRAFT", d.AddStandardFont("Helvetica-Bold"), 48, new PdfStampOptions { Opacity = 0.25, RotationDegrees = 45, Color = (0.8, 0, 0) });
        return d;
    }

    public static byte[] CreationFont(string name) => CreationSupport.RequireFont(name);

    private static PdfArray Array(params double[] v)
    {
        var a = new PdfArray();
        foreach (double x in v)
        {
            a.Append(new PdfReal(x));
        }

        return a;
    }
}
