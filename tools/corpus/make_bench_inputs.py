#!/usr/bin/env python3
"""Generate the large inputs the benchmark suite needs.

    make_bench_inputs.py <out dir> [--only NAME]

Nothing here is committed: the files are large, so they are rebuilt into a scratch directory (about 100 MB) and
the benchmarks find them through the ONEPDF_BENCH_DATA environment variable. Files built by this script itself
(raw-*) are byte-for-byte deterministic; TeX and Chrome output carries dates and is only equivalent between runs.

Files written:
  pages-1000.pdf     1,000 pages of Helvetica text, hand-assembled (open + page count at scale)
  born-300.pdf       300-page born-digital TeX article with embedded fonts, outline and Info (tectonic)
  born-300-broken.pdf  born-300 with a damaged cross-reference table and startxref, so the objects have to be found by scanning
  born-300-aes256.pdf  born-300 encrypted with AES-256 (user password "bench"), by qpdf
  scan-ocr-40.pdf    40 scanned-looking pages: a full-page grey image plus an invisible text layer
  cjk-120.pdf        120 pages of Japanese, Chinese and Korean text (Chrome, system CJK fonts)
  vector-page.pdf    one page of about 60,000 stroked and filled path segments
  text-page.pdf      one page of about 6,000 words

Tools (PATH or environment): TECTONIC, QPDF, CHROME. A missing tool skips the files that need it.
"""
import os, random, shutil, subprocess, sys, tempfile, zlib, hashlib

OUT = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else sys.exit(__doc__)
ONLY = sys.argv[sys.argv.index("--only") + 1] if "--only" in sys.argv else ""
os.makedirs(OUT, exist_ok=True)


def tool(env, *names):
    if os.environ.get(env):
        return os.environ[env]
    for n in names:
        p = shutil.which(n)
        if p:
            return p
    return None


TECTONIC = tool("TECTONIC", "tectonic")
QPDF = tool("QPDF", "qpdf")
CHROME = tool("CHROME", "google-chrome", "chromium")


def want(name):
    return name.startswith(ONLY)


def run(cmd, timeout=900, env=None):
    e = dict(os.environ)
    e.update(env or {})
    p = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout, env=e, start_new_session=True)
    return p.returncode, p.stdout.decode("utf-8", "replace")


class Raw:
    """Minimal PDF assembler: objects added in order, classic xref table."""

    def __init__(self):
        self.objs = []

    def add(self, body):
        self.objs.append(body if isinstance(body, bytes) else body.encode("latin-1"))
        return len(self.objs)

    def stream(self, dict_extra, data, compress=True):
        if compress:
            data = zlib.compress(data, 6)
            dict_extra += " /Filter /FlateDecode"
        return self.add(f"<< {dict_extra} /Length {len(data)} >>\nstream\n".encode() + data + b"\nendstream")

    def reserve(self):
        self.objs.append(None)
        return len(self.objs)

    def set(self, num, body):
        self.objs[num - 1] = body if isinstance(body, bytes) else body.encode("latin-1")

    def save(self, path, root, info=None):
        buf = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
        offs = []
        for i, o in enumerate(self.objs, 1):
            offs.append(len(buf))
            buf += f"{i} 0 obj\n".encode() + o + b"\nendobj\n"
        xref = len(buf)
        buf += f"xref\n0 {len(self.objs) + 1}\n0000000000 65535 f \n".encode()
        for o in offs:
            buf += f"{o:010d} 00000 n \n".encode()
        trailer = f"trailer\n<< /Size {len(self.objs) + 1} /Root {root} 0 R" + (f" /Info {info} 0 R" if info else "") + " >>"
        buf += trailer.encode() + f"\nstartxref\n{xref}\n%%EOF\n".encode()
        with open(path, "wb") as f:
            f.write(buf)


WORDS = ("the quick brown fox jumps over a lazy dog while seven bright engines hum quietly beneath open skies and measured "
         "records of every season are kept in careful ledgers by patient clerks near the river").split()


def lines_of_text(rnd, n, per_line=12):
    return [" ".join(rnd.choice(WORDS) for _ in range(per_line)).capitalize() + "." for _ in range(n)]


def gen_pages(name, count, line_count=46, font_size=11):
    rnd = random.Random(1000)
    r = Raw()
    font = r.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    pages_num = r.reserve()
    kids = []
    for i in range(count):
        ops = [f"BT /F1 {font_size} Tf {font_size + 3} TL 56 790 Td"]
        for line in lines_of_text(rnd, line_count):
            ops.append(f"({line}) Tj T*")
        ops.append("ET")
        content = r.stream("", "\n".join(ops).encode("latin-1"))
        kids.append(r.add(f"<< /Type /Page /Parent {pages_num} 0 R /MediaBox [0 0 612 842] /Contents {content} 0 R "
                          f"/Resources << /Font << /F1 {font} 0 R >> >> >>"))
    r.set(pages_num, f"<< /Type /Pages /Count {count} /Kids [{' '.join(f'{k} 0 R' for k in kids)}] >>")
    info = r.add("<< /Title (Benchmark pages) /Author (OnePDF benchmarks) /Producer (make_bench_inputs.py) >>")
    root = r.add(f"<< /Type /Catalog /Pages {pages_num} 0 R >>")
    r.save(os.path.join(OUT, name), root, info)


def gen_scan(name, count=40, w=850, h=1100):
    rnd = random.Random(7)
    r = Raw()
    font = r.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    pages_num = r.reserve()
    kids = []
    for i in range(count):
        # a smooth gradient with noise stands in for scanned paper
        rows = bytearray()
        for y in range(h):
            base = 215 + (y * 30 // h)
            noise = rnd.randbytes(w)
            rows += bytes(min(255, base + (n & 15) - 8 + (40 if (y % 60) in (10, 11, 12) and (x // 20) % 3 else 0)) for x, n in enumerate(noise))
        img = r.stream(f"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 8", bytes(rows))
        ops = ["q 612 0 0 792 0 0 cm /Im0 Do Q", "BT 3 Tr /F1 11 Tf 12 TL 56 740 Td"]
        for line in lines_of_text(rnd, 50):
            ops.append(f"({line}) Tj T*")
        ops.append("ET")
        content = r.stream("", "\n".join(ops).encode("latin-1"))
        kids.append(r.add(f"<< /Type /Page /Parent {pages_num} 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R "
                          f"/Resources << /Font << /F1 {font} 0 R >> /XObject << /Im0 {img} 0 R >> >> >>"))
    r.set(pages_num, f"<< /Type /Pages /Count {count} /Kids [{' '.join(f'{k} 0 R' for k in kids)}] >>")
    root = r.add(f"<< /Type /Catalog /Pages {pages_num} 0 R >>")
    r.save(os.path.join(OUT, name), root)


def gen_vector(name, polylines=3000, points=20):
    rnd = random.Random(42)
    ops = ["0.5 w 1 j 1 J"]
    for i in range(polylines):
        x, y = rnd.uniform(20, 590), rnd.uniform(20, 770)
        ops.append(f"{rnd.random():.2f} {rnd.random():.2f} {rnd.random():.2f} RG {x:.2f} {y:.2f} m")
        for _ in range(points - 1):
            x = min(592, max(20, x + rnd.uniform(-12, 12)))
            y = min(772, max(20, y + rnd.uniform(-12, 12)))
            ops.append(f"{x:.2f} {y:.2f} l")
        ops.append("S")
    for i in range(300):
        x, y = rnd.uniform(20, 560), rnd.uniform(20, 740)
        ops.append(f"{rnd.random():.2f} {rnd.random():.2f} {rnd.random():.2f} rg {x:.2f} {y:.2f} m {x + 20:.2f} {y:.2f} l "
                   f"{x + 20:.2f} {y + 15:.2f} {x + 5:.2f} {y + 25:.2f} {x:.2f} {y + 15:.2f} c f")
    r = Raw()
    pages = r.reserve()
    content = r.stream("", "\n".join(ops).encode())
    page = r.add(f"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R /Resources << >> >>")
    r.set(pages, f"<< /Type /Pages /Count 1 /Kids [{page} 0 R] >>")
    root = r.add(f"<< /Type /Catalog /Pages {pages} 0 R >>")
    r.save(os.path.join(OUT, name), root)


def gen_text_page(name):
    rnd = random.Random(5)
    ops = ["BT /F1 5 Tf 5.5 TL 20 780 Td"]
    for _ in range(135):
        words = [rnd.choice(WORDS) for _ in range(45)]
        ops.append("[" + " ".join(f"({w}) -250" for w in words) + "] TJ T*")
    ops.append("ET")
    r = Raw()
    font = r.add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>")
    pages = r.reserve()
    content = r.stream("", "\n".join(ops).encode())
    page = r.add(f"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R /Resources << /Font << /F1 {font} 0 R >> >> >>")
    r.set(pages, f"<< /Type /Pages /Count 1 /Kids [{page} 0 R] >>")
    root = r.add(f"<< /Type /Catalog /Pages {pages} 0 R >>")
    r.save(os.path.join(OUT, name), root)


def gen_born(name):
    if not TECTONIC:
        print("skip", name, "(no tectonic)")
        return False
    rnd = random.Random(300)
    body = []
    for s in range(1, 246):
        body.append(f"\\section{{Section {s}: {' '.join(rnd.choice(WORDS) for _ in range(4))}}}")
        for _ in range(7):
            body.append(" ".join(rnd.choice(WORDS) for _ in range(rnd.randint(90, 150))) +
                        f" The ratio $\\frac{{{rnd.randint(1, 9)}}}{{{rnd.randint(2, 9)}}}$ and the sum $\\sum_{{i=1}}^{{n}} x_i^2$ appear here.\n")
    tex = ("\\documentclass[11pt]{article}\n\\usepackage[a4paper,margin=2.5cm]{geometry}\n\\usepackage{hyperref}\n"
           "\\hypersetup{pdftitle={Born-digital benchmark},pdfauthor={OnePDF benchmarks}}\n\\begin{document}\n\\tableofcontents\n\\newpage\n"
           + "\n".join(body) + "\n\\end{document}\n")
    wd = tempfile.mkdtemp(prefix="bench-tex-", dir=os.environ.get("TMPDIR"))
    with open(os.path.join(wd, "born.tex"), "w") as f:
        f.write(tex)
    rc, log = run([TECTONIC, "--outdir", wd, os.path.join(wd, "born.tex")], env={"SOURCE_DATE_EPOCH": "1700000000"})
    if not os.path.exists(os.path.join(wd, "born.pdf")):
        raise RuntimeError("tectonic failed: " + log[-400:])
    shutil.move(os.path.join(wd, "born.pdf"), os.path.join(OUT, name))
    shutil.rmtree(wd, ignore_errors=True)
    return True


def gen_cjk(name, count=120):
    if not CHROME:
        print("skip", name, "(no chrome)")
        return
    ja = "すべての人間は、生まれながらにして自由であり、かつ、尊厳と権利とについて平等である。人間は、理性と良心とを授けられており、互いに同胞の精神をもって行動しなければならない。"
    zh = "人人生而自由，在尊严和权利上一律平等。他们赋有理性和良心，并应以兄弟关系的精神相对待。"
    ko = "모든 인간은 태어날 때부터 자유로우며 그 존엄과 권리에 있어 평등하다. 인간은 천부적으로 이성과 양심을 부여받았으며 서로 형제의 정신으로 대하여야 한다."
    paras = []
    for i in range(count):
        paras.append(f"<h2>{i + 1}</h2>" + "".join(f"<p lang='{l}' style=\"font-family:'Noto Sans CJK {f}'\">{t * 4}</p>" for l, f, t in
                                                  (("ja", "JP", ja), ("zh", "SC", zh), ("ko", "KR", ko))) )
    doc = ("<!DOCTYPE html><html><head><meta charset='utf-8'><title>CJK benchmark</title><style>@page{size:A4;margin:20mm}"
           "body{font-size:11pt;line-height:1.5}h2{break-before:page}</style></head><body>" + "\n".join(paras) + "</body></html>")
    wd = tempfile.mkdtemp(prefix="bench-cjk-", dir=os.environ.get("TMPDIR"))
    src = os.path.join(wd, "cjk.html")
    with open(src, "w", encoding="utf-8") as f:
        f.write(doc)
    out = os.path.join(OUT, name)
    run([CHROME, "--headless=new", "--no-sandbox", "--disable-gpu", "--no-pdf-header-footer",
         f"--user-data-dir={os.path.join(wd, 'profile')}", f"--print-to-pdf={out}", "file://" + src], timeout=300)
    shutil.rmtree(wd, ignore_errors=True)
    if not os.path.exists(out):
        raise RuntimeError("chrome failed for " + name)


if want("pages-1000"):
    gen_pages("pages-1000.pdf", 1000)
if want("vector-page"):
    gen_vector("vector-page.pdf")
if want("text-page"):
    gen_text_page("text-page.pdf")
if want("scan-ocr"):
    gen_scan("scan-ocr-40.pdf")
if want("cjk"):
    gen_cjk("cjk-120.pdf")
if want("born"):
    born = os.path.join(OUT, "born-300.pdf")
    if gen_born("born-300.pdf") or os.path.exists(born):
        if QPDF:
            # Damage the cross-reference data of an otherwise intact classic-table file: startxref points at nothing and the
            # table keyword is gone, so a reader has to find the objects by scanning.
            plain = os.path.join(OUT, "born-300-plain.tmp")
            rc, log = run([QPDF, "--object-streams=disable", "--stream-data=preserve", born, plain])
            if rc not in (0, 3):
                raise RuntimeError("qpdf failed: " + log[-300:])
            data = open(plain, "rb").read()
            os.remove(plain)
            at = data.rindex(b"startxref")
            xr = data.rindex(b"\nxref\n", 0, at) + 1
            data = data[:xr] + b"xxxx" + data[xr + 4:at] + b"startxref\n12345\n%%EOF\n"
            with open(os.path.join(OUT, "born-300-broken.pdf"), "wb") as f:
                f.write(data)
            rc, log = run([QPDF, "--encrypt", "bench", "owner", "256", "--", born, os.path.join(OUT, "born-300-aes256.pdf")])
            if rc not in (0, 3):
                raise RuntimeError("qpdf failed: " + log[-300:])
        else:
            print("skip born-300-broken.pdf and born-300-aes256.pdf (no qpdf)")

for fn in sorted(os.listdir(OUT)):
    p = os.path.join(OUT, fn)
    h = hashlib.sha256(open(p, "rb").read()).hexdigest()[:12]
    print(f"{fn:24s} {os.path.getsize(p):>11,d} bytes  sha256 {h}")
