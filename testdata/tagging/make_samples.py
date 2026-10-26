#!/usr/bin/env python3
"""Builds the untagged sample PDFs of testdata/tagging (our own files).

  python3 testdata/tagging/make_samples.py [name-prefix]

* src/*.html -> pdf/*.pdf with headless Chrome, asking for an untagged document (`generateTaggedPDF: false`, which only the
  DevTools protocol exposes; the command-line print switch always tags). Needs google-chrome (or set CHROME).
* src/figure-raster.png is written here first, from a seeded generator, so the raster figure is reproducible.
* pdf/synthetic-forms.pdf is assembled byte by byte: Form XObjects (shared and nested), an inline image, a vector drawing and a
  link, with nothing compressed so the file can be read in a text editor.

Chrome stamps dates and ids, so regenerating gives equivalent, not byte-identical, Chrome files; the committed files are the
reference. The synthetic file is byte-for-byte reproducible.
"""
import base64
import fcntl
import json
import os
import random
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "src")
PDF = os.path.join(HERE, "pdf")
CHROME = os.environ.get("CHROME") or shutil.which("google-chrome") or shutil.which("chromium")


def png(width, height, pixel):
    raw = b"".join(b"\0" + b"".join(bytes(pixel(x, y)) for x in range(width)) for y in range(height))

    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def make_raster():
    rng = random.Random(7)

    def pixel(x, y):
        # Sky over a stone wall over water, with noise, so the image looks like a photograph and not like a flat fill.
        n = rng.randint(-12, 12)
        if y < 50:
            base = (150 + y, 190 + y // 2, 235)
        elif y < 105:
            stone = 110 + 25 * (((x // 20) + (y // 12)) % 2)
            base = (stone, stone - 8, stone - 20)
        else:
            base = (40, 90 + (y - 105) // 2, 130)
        return tuple(max(0, min(255, c + n)) for c in base)

    with open(os.path.join(SRC, "figure-raster.png"), "wb") as f:
        f.write(png(240, 150, pixel))


def print_untagged(html_path, out_path, profile):
    """Chrome print-to-PDF through the DevTools pipe, with the tagged-PDF option off."""
    # The pipes must reach the child as descriptors 3 (commands in) and 4 (replies out), so move our ends out of that range first.
    a, b = [[fcntl.fcntl(fd, fcntl.F_DUPFD, 10) for fd in os.pipe()] for _ in range(2)]
    parent_w, parent_r = os.dup(a[1]), os.dup(b[0])

    def child():
        os.dup2(a[0], 3)
        os.dup2(b[1], 4)

    proc = subprocess.Popen([CHROME, "--headless=new", "--no-sandbox", "--disable-gpu", "--remote-debugging-pipe",
                             f"--user-data-dir={profile}", "about:blank"],
                            pass_fds=(3, 4), preexec_fn=child, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for fd in (a[0], a[1], b[0], b[1]):
        os.close(fd)
    w, r = os.fdopen(parent_w, "wb"), os.fdopen(parent_r, "rb")
    counter = [0]

    def call(method, params=None, session=None):
        counter[0] += 1
        message = {"id": counter[0], "method": method, "params": params or {}}
        if session:
            message["sessionId"] = session
        w.write(json.dumps(message).encode() + b"\0")
        w.flush()
        while True:
            buf = b""
            while True:
                c = r.read(1)
                if c in (b"\0", b""):
                    break
                buf += c
            if not buf:
                raise RuntimeError("chrome closed the pipe")
            m = json.loads(buf)
            if m.get("id") == counter[0]:
                if "error" in m:
                    raise RuntimeError(m["error"])
                return m["result"]

    try:
        target = call("Target.createTarget", {"url": "about:blank"})["targetId"]
        session = call("Target.attachToTarget", {"targetId": target, "flatten": True})["sessionId"]
        call("Page.enable", {}, session)
        call("Page.navigate", {"url": "file://" + html_path}, session)
        time.sleep(1.5)
        result = call("Page.printToPDF", {"generateTaggedPDF": False, "preferCSSPageSize": True, "printBackground": True,
                                          "displayHeaderFooter": False}, session)
        with open(out_path, "wb") as f:
            f.write(base64.b64decode(result["data"]))
    finally:
        proc.kill()
        proc.wait()


def make_synthetic():
    """Two Letter pages of Form XObjects: a running header and a watermark shared by both pages, a footer, a body paragraph that
    lives inside a form, a sidebar form that nests a drawing form, an inline image, and a link."""
    objs = {}

    def add(num, body):
        objs[num] = body

    def stream(dict_body, data):
        return f"<< {dict_body} /Length {len(data)} >>\nstream\n{data}\nendstream"

    add(1, "<< /Type /Catalog /Pages 2 0 R >>")
    add(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>")
    fonts = "/Font << /F1 5 0 R /F2 6 0 R >>"
    add(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << " + fonts +
        " /XObject << /Hdr 7 0 R /Ftr 8 0 R /Mark 9 0 R /Body1 10 0 R /Card 11 0 R >> >> /Contents 13 0 R >>")
    add(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << " + fonts +
        " /XObject << /Hdr 7 0 R /Ftr 8 0 R /Mark 9 0 R >> >> /Contents 14 0 R /Annots [15 0 R] >>")
    add(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    add(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>")
    form = "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << " + fonts + " >>"
    add(7, stream(form, "0.4 g BT /F1 9 Tf 72 752 Td (Tide Tables 2026 - Harbor Office) Tj ET 0.5 G 0.5 w 72 746 m 540 746 l S"))
    add(8, stream(form, "0.4 g BT /F1 9 Tf 72 40 Td (Issued by the harbor office. Not for navigation.) Tj ET 0.5 G 0.5 w 72 56 m 540 56 l S"))
    add(9, stream(form + " /Group << /S /Transparency >>", "0.85 g BT /F2 60 Tf 0.7071 -0.7071 0.7071 0.7071 150 250 Tm (DRAFT) Tj ET"))
    add(10, stream(form, "0 g BT /F1 11 Tf 14 TL 72 640 Td (The harbor opens at first light and closes at dusk. Vessels arriving) Tj T* "
                   "(after dusk must wait outside the breakwater until morning.) Tj T* "
                   "(Pilots are available on request from the harbor office.) Tj ET"))
    add(11, stream("/Type /XObject /Subtype /Form /BBox [0 0 220 150] /Matrix [1 0 0 1 340 420] /Resources << " + fonts +
                   " /XObject << /Wave 12 0 R >> >>",
                   "0.93 g 0 0 220 150 re f 0.4 G 1 w 0.5 0.5 219 149 re S /Wave Do "
                   "0 g BT /F2 11 Tf 12 126 Td (Sidebar) Tj /F1 10 Tf 0 -16 Td (Spring tides are the highest) Tj 0 -13 Td (of the year. Check the table) Tj 0 -13 Td (before planning a passage.) Tj ET"))
    add(12, stream("/Type /XObject /Subtype /Form /BBox [0 0 220 60]",
                   "0.2 0.4 0.7 rg 12 8 m 40 30 70 30 98 18 c 126 6 150 6 208 26 c 208 8 l 12 8 l f 0.2 0.4 0.7 RG 1.5 w 12 40 m 60 52 110 28 208 48 l S"))
    # 8x8 inline image, RGB, ASCIIHex: a blue/white checkerboard with a red border row.
    rows = []
    for y in range(8):
        row = ""
        for x in range(8):
            row += "FF0000" if y == 0 else ("2060C0" if (x + y) % 2 else "FFFFFF")
        rows.append(row)
    inline = "q 120 0 0 120 72 440 cm\nBI /W 8 /H 8 /CS /RGB /BPC 8 /F /AHx\nID\n" + "\n".join(rows) + ">\nEI\nQ"
    add(13, stream("", "/Hdr Do /Mark Do /Ftr Do\n"
                   "0 g BT /F2 20 Tf 72 700 Td (Harbor Notices) Tj ET\n"
                   "BT /F1 11 Tf 72 680 Td (Opening hours and pilotage for the season.) Tj ET\n"
                   "/Body1 Do\n" + inline + "\n"
                   "BT /F1 9 Tf 72 424 Td (Figure 1. Notice board at the quay.) Tj ET\n"
                   "/Card Do"))
    add(14, stream("", "/Hdr Do /Mark Do /Ftr Do\n"
                   "0 g BT /F2 14 Tf 72 700 Td (Contacts) Tj ET\n"
                   "BT /F1 11 Tf 14 TL 72 676 Td (For berths and pilots call the harbor office.) Tj T* "
                   "(Visit the harbor office online for the full tables.) Tj ET\n"
                   "0 0 0.8 RG 0.5 w 213 674 m 277 674 l S"))
    add(15, "<< /Type /Annot /Subtype /Link /Rect [212 672 278 688] /Border [0 0 0] /A << /S /URI /URI (https://example.org/harbor) >> >>")

    out = "%PDF-1.7\n%\u00e2\u00e3\u00cf\u00d3\n"
    offsets = {}
    for n in sorted(objs):
        offsets[n] = len(out.encode("latin-1"))
        out += f"{n} 0 obj\n{objs[n]}\nendobj\n"
    xref = len(out.encode("latin-1"))
    out += f"xref\n0 {len(objs) + 1}\n0000000000 65535 f \n"
    for n in sorted(objs):
        out += f"{offsets[n]:010d} 00000 n \n"
    out += f"trailer\n<< /Size {len(objs) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"
    with open(os.path.join(PDF, "synthetic-forms.pdf"), "wb") as f:
        f.write(out.encode("latin-1"))


def main():
    only = sys.argv[1] if len(sys.argv) > 1 else ""
    os.makedirs(PDF, exist_ok=True)
    make_raster()
    if "synthetic-forms".startswith(only) or only == "":
        make_synthetic()
    if CHROME is None:
        print("no Chrome found: Chrome samples skipped")
        return
    profile = tempfile.mkdtemp(prefix="onepdf-tagging-chrome-")
    try:
        for name in sorted(os.listdir(SRC)):
            if name.endswith(".html") and name.startswith(only):
                out = os.path.join(PDF, name[:-5] + ".pdf")
                print_untagged(os.path.join(SRC, name), out, os.path.join(profile, name))
                print("wrote", out)
    finally:
        shutil.rmtree(profile, ignore_errors=True)


if __name__ == "__main__":
    main()
