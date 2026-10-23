#!/usr/bin/env python3
"""Writes the hand-assembled PDFs that exercise appearance generation into testdata/generated/pdf.

Four files, all byte-for-byte reproducible:

  raw-appearance-widgets.pdf         every widget kind with no /AP at all
  raw-appearance-widgets-needap.pdf  the same fields with stale stored /AP and /NeedAppearances true
  raw-appearance-markup.pdf          every simple markup subtype with no /AP
  raw-appearance-markup-needap.pdf   the same annotations in a file whose form sets /NeedAppearances true

Run from the repository root:  python3 tools/corpus/make_appearance_files.py
"""
import os
import sys

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "testdata", "generated", "pdf")


class Pdf:
    def __init__(self):
        self.objs = []

    def add(self, body):
        self.objs.append(body if isinstance(body, bytes) else body.encode("latin-1"))
        return len(self.objs)

    def reserve(self):
        self.objs.append(b"")
        return len(self.objs)

    def set(self, num, body):
        self.objs[num - 1] = body if isinstance(body, bytes) else body.encode("latin-1")

    def stream(self, entries, data):
        if isinstance(data, str):
            data = data.encode("latin-1")
        return self.add(b"<< " + entries.encode("latin-1") + b" /Length %d >>\nstream\n" % len(data) + data + b"\nendstream")

    def build(self, root):
        out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
        offs = []
        for i, o in enumerate(self.objs, 1):
            offs.append(len(out))
            out += b"%d 0 obj\n" % i + o + b"\nendobj\n"
        x = len(out)
        out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(self.objs) + 1)
        for o in offs:
            out += b"%010d 00000 n \n" % o
        out += b"trailer\n<< /Size %d /Root %d 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(self.objs) + 1, root, x)
        return bytes(out)


def s(text):
    return "(" + text.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)") + ")"


def u16(text):
    return "<FEFF" + text.encode("utf-16-be").hex().upper() + ">"


def widgets_file(stale):
    p = Pdf()
    catalog, pages, page = p.reserve(), p.reserve(), p.reserve()
    helv = p.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    cour = p.add("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>")
    zadb = p.add("<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>")
    fields = []
    annots = []

    def stale_ap(w, h):
        body = "1 0.8 0.8 rg 0 0 %g %g re f 0 g BT /Helv 9 Tf 2 2 Td (STALE) Tj ET" % (w, h)
        return p.stream("/Type /XObject /Subtype /Form /BBox [0 0 %g %g] /Resources << /Font << /Helv %d 0 R >> >>" % (w, h, helv), body)

    def widget(name, rect, entries, stale_state=None):
        n = p.reserve()
        x0, y0, x1, y1 = rect
        ap = ""
        if stale:
            w, h = x1 - x0, y1 - y0
            if stale_state is None:
                ap = "/AP << /N %d 0 R >>" % stale_ap(w, h)
            else:
                on = p.stream("/Type /XObject /Subtype /Form /BBox [0 0 %g %g]" % (w, h), "0 0 1 rg 2 2 %g %g re f" % (w - 4, h - 4))
                off = p.stream("/Type /XObject /Subtype /Form /BBox [0 0 %g %g]" % (w, h), "")
                ap = "/AP << /N << /%s %d 0 R /Off %d 0 R >> >>" % (stale_state, on, off)
        p.set(n, "<< /Type /Annot /Subtype /Widget /F 4 /T %s /Rect [%g %g %g %g] %s %s /P %d 0 R >>" % (s(name), x0, y0, x1, y1, entries, ap, page))
        fields.append(n)
        annots.append(n)
        return n

    frame = "/MK << /BG [0.95 0.95 0.85] /BC [0 0 0.5] >> /BS << /W 1 /S /S >>"
    y = 740

    def row(h=24):
        nonlocal y
        y -= h + 12
        return y

    # text fields
    r = row(); widget("text.plain", (40, r, 240, r + 24), "/FT /Tx /V %s /DA (/Helv 12 Tf 0 g) %s" % (s("Hello World"), frame))
    widget("text.right", (260, r, 460, r + 24), "/FT /Tx /Q 2 /V %s /DA (/Helv 12 Tf 0 0 1 rg) %s" % (s("Right aligned"), frame))
    r = row(); widget("text.center", (40, r, 240, r + 24), "/FT /Tx /Q 1 /V %s /DA (/Cour 10 Tf 0 g) %s" % (s("Centered"), frame))
    widget("text.auto", (260, r, 460, r + 30), "/FT /Tx /V %s /DA (/Helv 0 Tf 0 g) %s" % (s("Auto size text that is long enough to shrink"), frame))
    r = row(60); widget("text.multi", (40, r, 240, r + 60), "/FT /Tx /Ff 4096 /V %s /DA (/Helv 11 Tf 0 g) %s" % (s("The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs.\nSecond paragraph."), frame))
    widget("text.comb", (260, r + 36, 460, r + 60), "/FT /Tx /Ff 16777216 /MaxLen 8 /V %s /DA (/Helv 12 Tf 0 g) %s" % (s("12345"), frame))
    widget("text.password", (260, r, 460, r + 24), "/FT /Tx /Ff 8192 /V %s /DA (/Helv 12 Tf 0 g) %s" % (s("secret"), frame))
    r = row(); widget("text.dashed", (40, r, 140, r + 24), "/FT /Tx /V %s /DA (/Helv 12 Tf 0 g) /MK << /BC [1 0 0] >> /BS << /W 2 /S /D /D [3 2] >>" % s("Dashed"))
    widget("text.beveled", (150, r, 250, r + 24), "/FT /Tx /V %s /DA (/Helv 12 Tf 0 g) /MK << /BG [0.8 0.8 0.8] /BC [0.4 0.4 0.4] >> /BS << /W 2 /S /B >>" % s("Beveled"))
    widget("text.inset", (260, r, 360, r + 24), "/FT /Tx /V %s /DA (/Helv 12 Tf 0 g) /MK << /BG [0.8 0.8 0.8] /BC [0.4 0.4 0.4] >> /BS << /W 2 /S /I >>" % s("Inset"))
    widget("text.underline", (370, r, 470, r + 24), "/FT /Tx /V %s /DA (/Helv 12 Tf 0 g) /MK << /BC [0 0 0] >> /BS << /W 1 /S /U >>" % s("Underline"))
    r = row(); widget("text.unicode", (40, r, 340, r + 24), "/FT /Tx /V %s /DA (/Helv 12 Tf 0 g) %s" % (u16("Caf\u00e9 \u03a9mega \u2713 \u6f22"), frame))
    widget("text.rotated", (360, r - 40, 384, r + 24), "/FT /Tx /V %s /DA (/Helv 11 Tf 0 g) /MK << /R 90 /BG [1 1 0.8] /BC [0 0 0] >>" % s("Rotated"))
    # choice fields
    r = row(); widget("choice.combo", (40, r, 240, r + 24), "/FT /Ch /Ff 131072 /V (b) /Opt [[(a) (Alpha)] [(b) (Bravo)] [(c) (Charlie)]] /DA (/Helv 12 Tf 0 g) %s" % frame)
    r = row(60); widget("choice.list", (40, r, 240, r + 60), "/FT /Ch /V (Delta) /TI 1 /Opt [(Alpha) (Bravo) (Charlie) (Delta) (Echo) (Foxtrot)] /DA (/Helv 11 Tf 0 g) %s" % frame)
    # buttons
    r = row(); widget("check.on", (40, r, 60, r + 20), "/FT /Btn /V /Yes /AS /Yes /DA (/ZaDb 0 Tf 0 g) /MK << /CA (4) /BC [0 0 0] /BG [1 1 1] >>", "Yes")
    widget("check.off", (80, r, 100, r + 20), "/FT /Btn /V /Off /AS /Off /DA (/ZaDb 0 Tf 0 g) /MK << /CA (4) /BC [0 0 0] >>", "Yes")
    widget("check.cross", (120, r, 140, r + 20), "/FT /Btn /V /Yes /AS /Yes /DA (/ZaDb 0 Tf 0 0 1 rg) /MK << /CA (8) /BC [0 0 0] >>", "Yes")
    widget("check.square", (160, r, 180, r + 20), "/FT /Btn /V /Yes /AS /Yes /DA (/ZaDb 0 Tf 0 g) /MK << /CA (n) /BC [0 0 0] >>", "Yes")
    # radio group: a parent with two kids
    parent = p.reserve()
    k1, k2 = p.reserve(), p.reserve()
    for k, (x, state) in ((k1, (220, "1")), (k2, (260, "2"))):
        ap = ""
        if stale:
            on = p.stream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0 0 1 rg 2 2 16 16 re f")
            off = p.stream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "")
            ap = "/AP << /N << /%s %d 0 R /Off %d 0 R >> >>" % (state, on, off)
        p.set(k, "<< /Type /Annot /Subtype /Widget /F 4 /Parent %d 0 R /Rect [%g %g %g %g] /AS /%s %s /MK << /CA (l) /BC [0 0 0] /BG [1 1 1] >> /P %d 0 R >>" % (parent, x, r, x + 20, r + 20, "1" if state == "1" else "Off", ap, page))
        annots.append(k)
    p.set(parent, "<< /FT /Btn /Ff 32768 /T (radio) /V /1 /DA (/ZaDb 0 Tf 0 g) /Kids [%d 0 R %d 0 R] >>" % (k1, k2))
    fields.append(parent)
    widget("push.submit", (310, r - 2, 410, r + 22), "/FT /Btn /Ff 65536 /DA (/Helv 12 Tf 0 g) /MK << /CA (Submit) /BG [0.75 0.85 1] /BC [0 0 0.5] >> /BS << /W 2 /S /B >>")
    widget("sig", (40, row(), 240, y + 30), "/FT /Sig")

    p.set(catalog, "<< /Type /Catalog /Pages %d 0 R /AcroForm << /Fields [%s] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv %d 0 R /Cour %d 0 R /ZaDb %d 0 R >> >> %s >> >>" % (
        pages, " ".join("%d 0 R" % f for f in fields), helv, cour, zadb, "/NeedAppearances true" if stale else ""))
    p.set(pages, "<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    p.set(page, "<< /Type /Page /Parent %d 0 R /MediaBox [0 0 500 780] /Annots [%s] >>" % (pages, " ".join("%d 0 R" % a for a in annots)))
    return p.build(catalog)


def markup_file(need):
    p = Pdf()
    catalog, pages, page = p.reserve(), p.reserve(), p.reserve()
    helv = p.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    annots = []

    def annot(entries):
        n = p.add("<< /Type /Annot /F 4 %s /P %d 0 R >>" % (entries, page))
        annots.append(n)

    # Some text under the text markup.
    content = p.stream("", "BT /F1 14 Tf 40 700 Td (Marked up text line one) Tj 0 -40 Td (Marked up text line two) Tj 0 -40 Td (Marked up text line three) Tj 0 -40 Td (Marked up text line four) Tj ET")
    annot("/Subtype /Square /Rect [40 560 140 620] /C [1 0 0] /IC [1 1 0.6] /BS << /W 3 /S /S >> /CA 0.6")
    annot("/Subtype /Circle /Rect [160 560 260 620] /C [0 0.5 0] /BS << /W 2 /S /D /D [4 2] >>")
    annot("/Subtype /Line /Rect [40 500 300 540] /L [50 510 290 530] /LE [/OpenArrow /ClosedArrow] /C [0 0 1] /IC [0 0 1] /BS << /W 2 >>")
    annot("/Subtype /Line /Rect [320 480 440 560] /L [330 490 430 490] /LE [/Circle /Square] /LL 20 /LLE 5 /C [0.5 0 0.5] /IC [1 0.8 1]")
    annot("/Subtype /Polygon /Rect [40 400 140 470] /Vertices [50 410 130 410 90 460] /C [0 0 0] /IC [0.8 1 0.8] /BS << /W 1 >>")
    annot("/Subtype /PolyLine /Rect [160 400 300 470] /Vertices [170 410 220 460 260 410 290 450] /LE [/None /OpenArrow] /C [0.8 0 0] /BS << /W 2 >>")
    annot("/Subtype /Ink /Rect [320 400 440 470] /InkList [[330 410 350 460 370 410 390 460 410 410] [330 440 430 440]] /C [0 0.4 0.8] /BS << /W 2 >>")
    annot("/Subtype /Highlight /Rect [40 695 200 715] /QuadPoints [40 715 200 715 40 695 200 695] /C [1 1 0]")
    annot("/Subtype /Underline /Rect [40 655 200 675] /QuadPoints [40 675 200 675 40 655 200 655] /C [0 0.6 0]")
    annot("/Subtype /StrikeOut /Rect [40 615 200 635] /QuadPoints [40 635 200 635 40 615 200 615] /C [1 0 0]")
    annot("/Subtype /Squiggly /Rect [40 575 200 595] /QuadPoints [40 595 200 595 40 575 200 575] /C [0 0 1]")
    annot("/Subtype /FreeText /Rect [40 300 240 370] /DA (/Helv 12 Tf 0 0 0.6 rg) /Q 1 /Contents %s /C [1 1 0.8] /BS << /W 2 >> /CL [240 335 300 300] /LE /OpenArrow" % s("Free text with a callout line pointing away from the box"))
    annot("/Subtype /Text /Rect [320 340 340 360] /Name /Note /C [1 0.9 0.2] /Contents (A note)")
    annot("/Subtype /Text /Rect [360 340 380 360] /Name /Comment /C [0.6 0.9 1] /Contents (A comment)")
    annot("/Subtype /Text /Rect [400 340 420 360] /Name /Help /C [0.7 1 0.7] /Contents (Help)")
    p.set(catalog, "<< /Type /Catalog /Pages %d 0 R /AcroForm << /Fields [] %s >> >>" % (pages, "/NeedAppearances true" if need else ""))
    p.set(pages, "<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
    p.set(page, "<< /Type /Page /Parent %d 0 R /MediaBox [0 0 500 780] /Contents %d 0 R /Resources << /Font << /F1 %d 0 R >> >> /Annots [%s] >>" % (pages, content, helv, " ".join("%d 0 R" % a for a in annots)))
    return p.build(catalog)


def main():
    os.makedirs(OUT, exist_ok=True)
    files = {
        "raw-appearance-widgets.pdf": widgets_file(False),
        "raw-appearance-widgets-needap.pdf": widgets_file(True),
        "raw-appearance-markup.pdf": markup_file(False),
        "raw-appearance-markup-needap.pdf": markup_file(True),
    }
    for name, data in files.items():
        with open(os.path.join(OUT, name), "wb") as f:
            f.write(data)
        print(name, len(data))


if __name__ == "__main__":
    sys.exit(main())
