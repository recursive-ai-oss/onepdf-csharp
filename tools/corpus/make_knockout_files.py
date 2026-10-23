#!/usr/bin/env python3
"""Writes the hand-assembled PDFs that exercise knockout data into testdata/generated/pdf.

  raw-knockout-tk.pdf     /TK true and false through q/Q and gs, inside a Form XObject and a transparency group
  raw-knockout-paint.pdf  B, B*, b and b* with a translucent stroke over a fill, a clipped B, and f then S as two calls

Both are byte-for-byte reproducible. Run from the repository root:  python3 tools/corpus/make_knockout_files.py
"""
import os
import sys

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "testdata", "generated", "pdf")


class Pdf:
    def __init__(self):
        self.objs = []

    def add(self, body):
        self.objs.append(body.encode("latin-1"))
        return len(self.objs)

    def stream(self, entries, data):
        d = data.encode("latin-1")
        return self.add("<< %s /Length %d >>\nstream\n%s\nendstream" % (entries, len(d), data))

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


def finish(p, content, extra_resources, first_ids):
    cat, pages, page = first_ids
    p.objs[cat - 1] = ("<< /Type /Catalog /Pages %d 0 R >>" % pages).encode()
    p.objs[pages - 1] = ("<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page).encode()
    p.objs[page - 1] = ("<< /Type /Page /Parent %d 0 R /MediaBox [0 0 200 200] /Contents %d 0 R /Resources << %s >> >>" % (pages, content, extra_resources)).encode()
    return p.build(cat)


def tk_file():
    p = Pdf()
    ids = (p.add(""), p.add(""), p.add(""))
    font = p.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    off = p.add("<< /Type /ExtGState /TK false >>")
    on = p.add("<< /Type /ExtGState /TK true >>")
    res = "/Font << /F1 %d 0 R >> /ExtGState << /Off %d 0 R /On %d 0 R >>" % (font, off, on)
    # A form that inherits the caller's state, one that turns knockout on, and a transparency group that turns it off.
    inherit = p.stream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << %s >>" % res, "BT /F1 10 Tf 100 20 Td (inherit) Tj ET")
    turn_on = p.stream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << %s >>" % res, "/On gs BT /F1 10 Tf 100 40 Td (formon) Tj ET")
    group = p.stream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /I true >> /Resources << %s >>" % res, "/Off gs BT /F1 10 Tf 100 60 Td (groupoff) Tj ET")
    content = "\n".join([
        "BT /F1 10 Tf 10 190 Td (default) Tj ET",
        "q /Off gs BT /F1 10 Tf 10 170 Td (off in q) Tj ET Q",
        "BT /F1 10 Tf 10 150 Td (after Q) Tj ET",
        "/Off gs BT /F1 10 Tf 10 130 Td (off by gs) Tj ET",
        "q /On gs BT /F1 10 Tf 10 110 Td (on in q) Tj ET Q",
        "BT /F1 10 Tf 10 90 Td (off restored) Tj ET",
        "/Fm1 Do /Fm2 Do",
        "/On gs",
        "/Fm3 Do /Fm1 Do",
        "BT /F1 10 Tf 10 70 Td (on again) Tj ET",
    ])
    res_page = res + " /XObject << /Fm1 %d 0 R /Fm2 %d 0 R /Fm3 %d 0 R >>" % (inherit, turn_on, group)
    c = p.stream("", content)
    return finish(p, c, res_page, ids)


def paint_file():
    p = Pdf()
    ids = (p.add(""), p.add(""), p.add(""))
    gs = p.add("<< /Type /ExtGState /CA 0.5 /ca 0.5 >>")
    res = "/ExtGState << /GA %d 0 R >>" % gs
    style = "/GA gs 1 0 0 rg 0 0 1 RG 8 w"
    content = "\n".join([
        "q " + style + " 10 150 40 40 re B Q",
        "q " + style + " 60 150 40 40 re B* Q",
        "q " + style + " 110 150 m 150 150 l 150 190 l b Q",
        "q " + style + " 160 150 m 190 150 l 190 190 l b* Q",
        "q " + style + " 10 100 40 40 re f 10 100 40 40 re S Q",
        "q " + style + " 60 100 40 40 re W B Q",
        "q " + style + " 110 100 40 40 re W n 100 90 60 60 re B Q",
        "q " + style + " 160 100 m 190 100 l 190 140 l h f* Q",
    ])
    c = p.stream("", content)
    return finish(p, c, res, ids)


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, data in {"raw-knockout-tk.pdf": tk_file(), "raw-knockout-paint.pdf": paint_file()}.items():
        with open(os.path.join(OUT, name), "wb") as f:
            f.write(data)
        print(name, len(data))


if __name__ == "__main__":
    sys.exit(main())
