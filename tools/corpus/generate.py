#!/usr/bin/env python3
"""Build the generated test set with exact truth.

Output (relative to the repo root):
  testdata/generated/src/    sources (HTML, flat ODF, LaTeX)
  testdata/generated/pdf/    resulting PDFs
  testdata/generated/truth/  one <name>.json sidecar per PDF (see testdata/generated/README.md)

Tools (found on PATH, or named by environment variables):
  CHROME (google-chrome), SOFFICE (soffice), QPDF, TECTONIC. A missing tool skips the
  cases that need it and prints a notice. QPDF is used only as a black-box binary to
  encrypt, rotate and repack files.

Usage: generate.py [--only PREFIX] [--work DIR]
Generated PDFs from Chrome, LibreOffice and TeX carry creation dates, so a re-run yields
equivalent but not byte-identical files; the committed outputs are the reference.
The hand-assembled "raw-*" files are byte-for-byte deterministic.
"""
import json, os, re, shutil, subprocess, sys, tempfile, zlib, html

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
GEN = os.path.join(ROOT, "testdata", "generated")
SRC, PDF, TRUTH = (os.path.join(GEN, d) for d in ("src", "pdf", "truth"))
for d in (SRC, PDF, TRUTH):
    os.makedirs(d, exist_ok=True)

ONLY = ""
WORK = None
args = sys.argv[1:]
if "--only" in args:
    ONLY = args[args.index("--only") + 1]
if "--work" in args:
    WORK = args[args.index("--work") + 1]
WORK = WORK or tempfile.mkdtemp(prefix="onepdf-gen-")
os.makedirs(WORK, exist_ok=True)


def tool(env, *names):
    if os.environ.get(env):
        return os.environ[env]
    for n in names:
        p = shutil.which(n)
        if p:
            return p
    return None


CHROME = tool("CHROME", "google-chrome", "chromium")
SOFFICE = tool("SOFFICE", "soffice")
QPDF = tool("QPDF", "qpdf")
TECTONIC = tool("TECTONIC", "tectonic")


def want(name):
    return name.startswith(ONLY)


def run(cmd, timeout=180, env=None):
    # own session so a stuck browser or office process can be stopped as a group
    import signal
    e = dict(os.environ)
    e.update(env or {})
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=True, env=e)
    try:
        out, _ = p.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        os.killpg(p.pid, signal.SIGKILL)
        p.communicate()
        raise
    return p.returncode, out.decode("utf-8", "replace")


def write_truth(name, pages, generator, passwords=None, **extra):
    d = {"schema": 1, "name": name, "generator": generator, "pageCount": len(pages),
         "pages": [{"text": t} for t in pages]}
    if passwords:
        d["passwords"] = passwords
    d.update(extra)
    with open(os.path.join(TRUTH, name + ".json"), "w", encoding="utf-8") as f:
        json.dump(d, f, ensure_ascii=False, indent=1, sort_keys=True)
        f.write("\n")


def write_src(fname, text):
    with open(os.path.join(SRC, fname), "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def page_count(pdf):
    # trust the producer-independent answer from qpdf when available, else count /Type /Page
    if QPDF:
        rc, out = run([QPDF, "--show-npages", pdf])
        if rc == 0:
            return int(out.strip())
    return len(re.findall(rb"/Type\s*/Page[^s]", open(pdf, "rb").read()))


# ---------------------------------------------------------------- texts
# Sentences are written for this set. Non-Latin samples are Article 1 of the Universal
# Declaration of Human Rights in each language (translations published by the United Nations).
EN = [
    "The harbor town woke slowly. Fishing boats rocked against their moorings while gulls argued over the morning catch.",
    "By nine the market stalls were open, and the smell of fresh bread drifted along the quay toward the old customs house.",
    "Visitors who arrived early could watch the ferry load: crates of lemons, bicycles, a piano wrapped in blankets, and one very patient goat.",
    "At noon the bell on the clock tower rang twelve times, although the clock itself had been running four minutes slow for as long as anyone remembered.",
    "In the afternoon the wind turned, and the sails of the small regatta filled together like a flock of white birds.",
    "Evening came with long shadows and the clatter of dishes. Lamps were lit along the pier, one after another, until the whole shore glowed.",
]
EN2 = [
    "Section two begins on a new page. It repeats nothing from the first page and uses entirely different words so that extraction errors are easy to spot.",
    "Quartz, jasper, and obsidian were sorted into wooden trays by an apprentice who hummed while he worked.",
    "Beyond the workshop door a narrow stair led to the roof, where the cats kept their own strict timetable.",
]
AR = "يولد جميع الناس أحرارا متساوين في الكرامة والحقوق. وقد وهبوا عقلا وضميرا وعليهم أن يعامل بعضهم بعضا بروح الإخاء."
HE = "כל בני האדם נולדו בני חורין ושווים בערכם ובזכויותיהם. כולם חוננו בתבונה ובמצפון, ועליהם לנהוג איש ברעהו ברוח של אחווה."
ZH = "人人生而自由，在尊严和权利上一律平等。他们赋有理性和良心，并应以兄弟关系的精神相对待。"
JA = "すべての人間は、生れながらにして自由であり、かつ、尊厳と権利とについて平等である。人間は、理性と良心とを授けられており、互いに同胞の精神をもって行動しなければならない。"
KO = "모든 인간은 태어날 때부터 자유로우며 그 존엄과 권리에 있어 평등하다. 인간은 천부적으로 이성과 양심을 부여받았으며 서로 형제의 정신으로 대하여야 한다."

CHROME_VER = None


def chrome_gen():
    global CHROME_VER
    if CHROME_VER is None:
        rc, out = run([CHROME, "--version"])
        CHROME_VER = out.strip()
    return CHROME_VER


def chrome_pdf(name, body, css="", lang="en", dir_="ltr", paper="@page{size:A4;margin:20mm}", extra_head=""):
    """Render an HTML page with headless Chrome. Returns False when Chrome is missing."""
    if not CHROME:
        print("skip", name, "(no chrome)")
        return False
    doc = (f'<!DOCTYPE html>\n<html lang="{lang}" dir="{dir_}">\n<head>\n<meta charset="utf-8">\n<title>{name}</title>\n'
           f"{extra_head}<style>\n{paper}\nbody{{font-family:'Liberation Serif',serif;font-size:12pt;line-height:1.4}}\n"
           f"p{{margin:0 0 10pt 0}}\n.pb{{break-after:page}}\n{css}\n</style>\n</head>\n<body>\n{body}\n</body>\n</html>\n")
    write_src(name + ".html", doc)
    out = os.path.join(PDF, name + ".pdf")
    prof = os.path.join(WORK, "chrome-profile")
    rc, log = run([CHROME, "--headless=new", "--no-sandbox", "--disable-gpu", "--no-pdf-header-footer",
                   "--generate-pdf-document-outline", f"--user-data-dir={prof}",
                   f"--print-to-pdf={out}", "file://" + os.path.join(SRC, name + ".html")], timeout=120)
    if not os.path.exists(out):
        raise RuntimeError(f"chrome failed for {name}: {log[-400:]}")
    return True


def paras(items):
    return "\n".join(f"<p>{html.escape(t)}</p>" for t in items)


def gen_chrome():
    g = lambda: "Chrome " + chrome_gen()
    n = "chrome-single-column-en"
    if want(n) and chrome_pdf(n, paras(EN) + '<div class="pb"></div>\n' + paras(EN2)):
        write_truth(n, ["\n\n".join(EN), "\n\n".join(EN2)], g(), category="single-column")

    n = "chrome-two-column-en"
    if want(n):
        c1, c2 = EN[:3], EN[3:]
        css = ".cols{display:flex;gap:12mm}.cols>div{flex:1}"
        if chrome_pdf(n, f'<h1>Two columns</h1><div class="cols"><div>{paras(c1)}</div><div>{paras(c2)}</div></div>', css):
            write_truth(n, ["Two columns\n\n" + "\n\n".join(c1) + "\n\n" + "\n\n".join(c2)], g(), category="multi-column")

    n = "chrome-three-column-en"
    if want(n):
        cols = [EN[:2], EN[2:4], EN[4:] + EN2[:1]]
        css = ".cols{display:flex;gap:8mm;font-size:10pt}.cols>div{flex:1}"
        if chrome_pdf(n, '<div class="cols">' + "".join(f"<div>{paras(c)}</div>" for c in cols) + "</div>", css):
            write_truth(n, ["\n\n".join("\n\n".join(c) for c in cols)], g(), category="multi-column")

    n = "chrome-table-en"
    if want(n):
        rows = [["Item", "Quantity", "Unit", "Notes"], ["Lemons", "120", "crate", "unwaxed"], ["Bicycles", "14", "each", "assorted"],
                ["Blankets", "40", "bale", "wool"], ["Lamp oil", "9", "drum", "sealed"]]
        t = "<table>" + "".join("<tr>" + "".join(f"<td>{c}</td>" for c in r) + "</tr>" for r in rows) + "</table>"
        css = "table{border-collapse:collapse}td{border:1px solid #444;padding:4pt 10pt}"
        if chrome_pdf(n, "<p>Cargo manifest</p>" + t + "<p>End of manifest.</p>", css):
            write_truth(n, ["Cargo manifest\n\n" + "\n".join(" ".join(r) for r in rows) + "\n\nEnd of manifest."], g(),
                        category="table", notes="table cells in row-major order, one row per line")

    for n, txt, lang in (("chrome-rtl-arabic", AR, "ar"), ("chrome-rtl-hebrew", HE, "he")):
        if want(n):
            title = "Article 1"
            if chrome_pdf(n, f"<p>{title}</p>\n<p>{txt}</p>\n<p>{txt}</p>", "p:nth-of-type(n+2){font-size:16pt}", lang=lang, dir_="rtl"):
                write_truth(n, [f"{title}\n\n{txt}\n\n{txt}"], g(), category="rtl", notes="truth is in logical (reading) order")

    n = "chrome-bidi-mixed"
    if want(n):
        l1 = f"The Hebrew sentence {HE.split('.')[0]} appears inside English text and then the line continues in English."
        l2 = f"Arabic works too: {AR.split('.')[0]}. Numbers like 2026 stay in place."
        if chrome_pdf(n, paras([l1, l2])):
            write_truth(n, ["\n\n".join([l1, l2])], g(), category="rtl", notes="embedded RTL runs inside LTR paragraphs; logical order")

    for n, txt, lang, font in (("chrome-cjk-zh", ZH, "zh", "Noto Sans CJK SC"), ("chrome-cjk-ja", JA, "ja", "Noto Sans CJK JP"),
                               ("chrome-cjk-ko", KO, "ko", "Noto Sans CJK KR")):
        if want(n):
            if chrome_pdf(n, paras([txt, txt[: len(txt) // 2]]), f"body{{font-family:'{font}'}}", lang=lang):
                write_truth(n, [txt + "\n\n" + txt[: len(txt) // 2]], g(), category="cjk")

    n = "chrome-cjk-ja-vertical"
    if want(n):
        lines = [JA, "吾輩は猫である。名前はまだ無い。どこで生れたかとんと見当がつかぬ。"]
        css = "body{font-family:'Noto Sans CJK JP';writing-mode:vertical-rl;height:240mm;font-size:14pt}"
        if chrome_pdf(n, paras(lines), css, lang="ja"):
            write_truth(n, ["\n\n".join(lines)], g(), category="cjk-vertical",
                        notes="vertical-rl: the first paragraph is the rightmost column; truth is in logical order")

    n = "chrome-rotated-text"
    if want(n):
        a, b = EN[0], EN[1]
        css = ".r{position:absolute;left:150mm;top:25mm;width:200mm;transform-origin:0 0;transform:rotate(90deg)}"
        if chrome_pdf(n, f'<p>{a}</p><p class="r">{b}</p>', css):
            write_truth(n, [a + "\n\n" + b], g(), category="rotated-text",
                        notes="a horizontal paragraph and a paragraph rotated 90 degrees clockwise; the rotated one is read last")

    n = "chrome-ligatures"
    if want(n):
        lines = ["The office staff offered affluent fluffy waffles to the final shift before the first flight.",
                 "A difficult fjord, a flagship, an effigy: efficient workflows suffice."]
        css = "p{font-family:'DejaVu Serif';font-variant-ligatures:common-ligatures;font-size:13pt}"
        if chrome_pdf(n, paras(lines), css):
            write_truth(n, ["\n\n".join(lines)], g(), category="ligatures",
                        notes="the font may emit fi/fl/ff/ffi ligature glyphs; truth is the decomposed text")

    n = "chrome-links-outline"
    if want(n):
        body = ('<h1>Handbook</h1><p>Start with <a href="https://example.com/start">the start page</a> or jump to '
                '<a href="#details">the details</a>.</p><h2>Overview</h2><p>This part gives an overview of the handbook.</p>'
                '<div class="pb"></div><h1 id="details">Details</h1><h2>First detail</h2><p>The first detail is short.</p>'
                '<h2>Second detail</h2><p>The second detail is longer, but not by much.</p>')
        if chrome_pdf(n, body, "a{color:#036}"):
            write_truth(n, ["Handbook\n\nStart with the start page or jump to the details.\n\nOverview\n\nThis part gives an overview of the handbook.",
                            "Details\n\nFirst detail\n\nThe first detail is short.\n\nSecond detail\n\nThe second detail is longer, but not by much."],
                        g(), category="links-outline",
                        links=[{"page": 0, "uri": "https://example.com/start"}, {"page": 0, "internalTarget": "Details"}],
                        outline=[["Handbook", 0, [["Overview", 0, []]]], ["Details", 1, [["First detail", 1, []], ["Second detail", 1, []]]]])

    n = "chrome-styles"
    if want(n):
        body = ('<p>Plain, <b>bold</b>, <i>italic</i>, <b><i>bold italic</i></b>, <span style="font-family:\'Liberation Mono\'">monospace</span>'
                ' and <span style="font-size:7pt">tiny</span> and <span style="font-size:24pt">large</span> text share one line.</p>'
                '<p style="font-family:\'DejaVu Sans\'">A second paragraph in a sans-serif face with superscript x<sup>2</sup> and subscript H<sub>2</sub>O.</p>')
        if chrome_pdf(n, body):
            write_truth(n, ["Plain, bold, italic, bold italic, monospace and tiny and large text share one line.\n\n"
                            "A second paragraph in a sans-serif face with superscript x2 and subscript H2O."], g(), category="fonts-styles",
                        notes="sup/sub digits appear inline; expect no spaces around them")

    n = "chrome-emoji"
    if want(n):
        lines = ["Weather report: sunny \u2600 with a chance of rain \u2614 and a rainbow."]
        if chrome_pdf(n, paras(lines), "p{font-family:'Noto Color Emoji','DejaVu Sans'}"):
            write_truth(n, ["\n\n".join(lines)], g(), category="symbols",
                        notes="symbol glyphs may be embedded as bitmap/Type 3 fonts depending on the available fonts")


# ---------------------------------------------------------------- LibreOffice
NS = ('xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" '
      'xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" '
      'xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" xmlns:xlink="http://www.w3.org/1999/xlink" '
      'xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0"')


def fodt(body, auto_styles="", master="", layout_props='fo:page-width="21cm" fo:page-height="29.7cm" fo:margin="2cm"', header=None, footer=None):
    hd = f'<style:header><text:p>{header}</text:p></style:header>' if header else ""
    ft = f'<style:footer><text:p>{footer}</text:p></style:footer>' if footer else ""
    return (f'<?xml version="1.0" encoding="UTF-8"?>\n<office:document {NS} office:version="1.3" '
            f'office:mimetype="application/vnd.oasis.opendocument.text">\n<office:font-face-decls/>\n'
            f'<office:styles><style:style style:name="Standard" style:family="paragraph"><style:text-properties style:font-name="Liberation Serif" fo:font-family="\'Liberation Serif\'" fo:font-size="12pt"/></style:style>'
            f'<style:style style:name="Heading" style:family="paragraph"><style:text-properties fo:font-family="\'Liberation Sans\'" fo:font-size="16pt" fo:font-weight="bold"/></style:style></office:styles>\n'
            f'<office:automatic-styles><style:page-layout style:name="PL1"><style:page-layout-properties {layout_props}/>'
            f'<style:header-style/><style:footer-style/></style:page-layout>\n'
            f'<style:style style:name="PB" style:family="paragraph" style:parent-style-name="Standard"><style:paragraph-properties fo:break-before="page"/></style:style>\n'
            f'<style:style style:name="CB" style:family="paragraph" style:parent-style-name="Standard"><style:paragraph-properties fo:break-before="column"/></style:style>\n'
            f'<style:style style:name="Sect2" style:family="section"><style:section-properties><style:columns fo:column-count="2" fo:column-gap="1cm"/></style:section-properties></style:style>\n'
            f'<style:style style:name="Tbl" style:family="table"><style:table-properties style:width="12cm" table:align="left"/></style:style>\n'
            f'<style:style style:name="Cell" style:family="table-cell"><style:table-cell-properties fo:padding="0.1cm" fo:border="0.5pt solid #000000"/></style:style>\n'
            f'{auto_styles}</office:automatic-styles>\n<office:master-styles><style:master-page style:name="Standard" style:page-layout-name="PL1">{hd}{ft}</style:master-page></office:master-styles>\n'
            f'<office:body><office:text>\n{body}\n</office:text></office:body></office:document>\n')


def lo_pdf(name, doc, filter_opts=None, tagged=False):
    if not SOFFICE:
        print("skip", name, "(no soffice)")
        return False
    write_src(name + ".fodt", doc)
    prof = os.path.join(WORK, "lo-profile")
    opts = dict(filter_opts or {})
    if tagged:
        opts["UseTaggedPDF"] = True
    flt = "pdf:writer_pdf_Export"
    if opts:
        flt += ":" + json.dumps({k: {"type": "boolean" if isinstance(v, bool) else "string", "value": str(v).lower() if isinstance(v, bool) else v}
                                 for k, v in opts.items()}, separators=(",", ":"))
    outdir = os.path.join(WORK, "lo-out")
    os.makedirs(outdir, exist_ok=True)
    rc, log = run([SOFFICE, f"-env:UserInstallation=file://{prof}", "--headless", "--norestore", "--convert-to", flt,
                   "--outdir", outdir, os.path.join(SRC, name + ".fodt")], timeout=240)
    produced = os.path.join(outdir, name + ".pdf")
    if not os.path.exists(produced):
        raise RuntimeError(f"soffice failed for {name}: {log[-400:]}")
    shutil.move(produced, os.path.join(PDF, name + ".pdf"))
    return True


def lo_version():
    rc, out = run([SOFFICE, "--version"])
    return out.strip()


def gen_lo():
    if not SOFFICE:
        return
    g = "LibreOffice " + (lo_version().split()[1] if lo_version() else "")
    head = "Quarterly Harbor Report"
    foot = "Prepared for internal circulation"
    items = ["Boats serviced", "Cargo weighed", "Lamps replaced"]
    rows = [["Month", "Boats", "Tons"], ["April", "31", "120"], ["May", "44", "175"], ["June", "52", "210"]]
    tbl = ('<table:table table:name="T1" table:style-name="Tbl"><table:table-column table:number-columns-repeated="3"/>' +
           "".join("<table:table-row>" + "".join(f'<table:table-cell table:style-name="Cell" office:value-type="string"><text:p>{c}</text:p></table:table-cell>' for c in r) + "</table:table-row>" for r in rows) +
           "</table:table>")
    body = (f'<text:h text:style-name="Heading" text:outline-level="1">Summary</text:h>\n<text:p text:style-name="Standard">{EN[0]}</text:p>\n'
            '<text:p text:style-name="Standard">Read the <text:a xlink:href="https://example.org/report" xlink:type="simple">full report online</text:a> for more.</text:p>\n'
            '<text:list>' + "".join(f"<text:list-item><text:p>{i}</text:p></text:list-item>" for i in items) + "</text:list>\n" + tbl +
            f'\n<text:h text:style-name="Heading" text:outline-level="1" ><text:bookmark-start text:name="second"/>Second page<text:bookmark-end text:name="second"/></text:h>\n'
            f'<text:p text:style-name="PB">{EN2[0]}</text:p>\n<text:p text:style-name="Standard">{EN2[1]}</text:p>')
    page1 = f"{head}\n\nSummary\n\n{EN[0]}\n\nRead the full report online for more.\n\n" + "\n".join(items) + "\n\n" + "\n".join(" ".join(r) for r in rows) + f"\n\n{foot}"
    # the second heading sits on page 1 or 2 depending on flow; keep the break explicit instead
    body = body.replace('<text:h text:style-name="Heading" text:outline-level="1" ><text:bookmark-start text:name="second"/>Second page<text:bookmark-end text:name="second"/></text:h>\n<text:p text:style-name="PB">',
                        '<text:p text:style-name="PB"><text:bookmark-start text:name="second"/>Second page<text:bookmark-end text:name="second"/></text:p>\n<text:p text:style-name="Standard">')
    page1 = page1
    pages = [page1, f"{head}\n\nSecond page\n\n{EN2[0]}\n\n{EN2[1]}\n\n{foot}"]
    for n, tagged in (("lo-report", False), ("lo-report-tagged", True)):
        if want(n) and lo_pdf(n, fodt(body, header=head, footer=foot), {"ExportBookmarks": True, "ExportNotes": True}, tagged):
            write_truth(n, pages, g, category="report", furniture=[head, foot], tagged=tagged,
                        notes="page header first, footer last on every page; list items and table rows one per line",
                        links=[{"page": 0, "uri": "https://example.org/report"}])

    n = "lo-two-columns"
    if want(n):
        c1, c2 = EN[:3], EN[3:]
        b = ('<text:section text:style-name="Sect2" text:name="S1">' + "".join(f'<text:p text:style-name="Standard">{t}</text:p>' for t in c1) +
             f'<text:p text:style-name="CB">{c2[0]}</text:p>' + "".join(f'<text:p text:style-name="Standard">{t}</text:p>' for t in c2[1:]) + "</text:section>")
        if lo_pdf(n, fodt(b)):
            write_truth(n, ["\n\n".join(c1 + c2)], g, category="multi-column", notes="explicit column break between the two groups")

    n = "lo-vertical-ja"
    if want(n):
        b = f'<text:p text:style-name="Standard">{JA}</text:p>'
        lp = 'fo:page-width="21cm" fo:page-height="29.7cm" fo:margin="2cm" style:writing-mode="tb-rl"'
        if lo_pdf(n, fodt(b, layout_props=lp).replace("Liberation Serif", "Noto Sans CJK JP")):
            write_truth(n, [JA], g, category="cjk-vertical", notes="page writing mode tb-rl; truth is logical order")

    n = "lo-encrypted-rc4-128"
    if want(n):
        b = f'<text:p text:style-name="Standard">{EN[0]}</text:p><text:p text:style-name="Standard">{EN[1]}</text:p>'
        opts = {"EncryptFile": True, "DocumentOpenPassword": "user-pw", "RestrictPermissions": True, "PermissionPassword": "owner-pw",
                "Printing": "0", "Changes": "0"}
        if lo_pdf(n, fodt(b), opts):
            write_truth(n, ["\n\n".join(EN[:2])], g, passwords={"user": "user-pw", "owner": "owner-pw"}, category="encrypted",
                        securityRevision="R3", notes="exported by LibreOffice's own encryption; this version writes RC4-128 (R3) with print and change restrictions")


# ---------------------------------------------------------------- TeX
TEX_ARTICLE = r"""\documentclass[11pt]{article}
\usepackage[a4paper,margin=25mm]{geometry}
\usepackage{multicol}
\begin{document}
\section*{Ligatures and columns}
The office staff offered affluent, fluffy waffles. A difficult fjord, a flagship, an effigy:
efficient workflows suffice. Final first fly floor.

\begin{multicols}{2}
Alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu.
\columnbreak
Nu xi omicron pi rho sigma tau upsilon phi chi psi omega.
\end{multicols}

\newpage
\section*{Second page}
Quartz, jasper, and obsidian were sorted into wooden trays.
\end{document}
"""


def gen_tex():
    n = "tex-article"
    if not want(n):
        return
    if not TECTONIC:
        print("skip", n, "(no tectonic)")
        return
    write_src(n + ".tex", TEX_ARTICLE)
    wd = os.path.join(WORK, "tex")
    os.makedirs(wd, exist_ok=True)
    rc, log = run([TECTONIC, "--outdir", wd, os.path.join(SRC, n + ".tex")], timeout=600,
                  env={"SOURCE_DATE_EPOCH": "1700000000"})
    if not os.path.exists(os.path.join(wd, n + ".pdf")):
        raise RuntimeError("tectonic failed: " + log[-400:])
    shutil.move(os.path.join(wd, n + ".pdf"), os.path.join(PDF, n + ".pdf"))
    rc, v = run([TECTONIC, "--version"])
    write_truth(n, ["Ligatures and columns\n\nThe office staff offered affluent, fluffy waffles. A difficult fjord, a flagship, an effigy: "
                    "efficient workflows suffice. Final first fly floor.\n\nAlpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu.\n\n"
                    "Nu xi omicron pi rho sigma tau upsilon phi chi psi omega.", "Second page\n\nQuartz, jasper, and obsidian were sorted into wooden trays."],
                v.strip(), category="ligatures", notes="TeX ligatures (ff, fi, fl, ffi); the second group reads after the first column")


# ---------------------------------------------------------------- hand-assembled PDFs
class Raw:
    """Tiny PDF assembler: objects are written in order, uncompressed unless given."""

    def __init__(self, version="1.7"):
        self.objs = []  # list of bytes bodies (index+1 = number)
        self.version = version

    def add(self, body):
        if isinstance(body, str):
            body = body.encode("latin-1")
        self.objs.append(body)
        return len(self.objs)

    def reserve(self):
        self.objs.append(None)
        return len(self.objs)

    def set(self, num, body):
        self.objs[num - 1] = body.encode("latin-1") if isinstance(body, str) else body

    def stream(self, dict_body, data, compress=False):
        if compress:
            data = zlib.compress(data, 9)
            dict_body += " /Filter /FlateDecode"
        return self.add(f"<< {dict_body} /Length {len(data)} >>\nstream\n".encode("latin-1") + data + b"\nendstream")

    def build(self, root, info=None, extra_trailer="", ids=True):
        out = bytearray(f"%PDF-{self.version}\n%\xe2\xe3\xcf\xd3\n".encode("latin-1"))
        offs = []
        for i, b in enumerate(self.objs, 1):
            offs.append(len(out))
            out += f"{i} 0 obj\n".encode() + b + b"\nendobj\n"
        xref = len(out)
        out += f"xref\n0 {len(self.objs) + 1}\n".encode() + b"0000000000 65535 f \n"
        for o in offs:
            out += f"{o:010d} 00000 n \n".encode()
        tr = f"<< /Size {len(self.objs) + 1} /Root {root} 0 R"
        if info:
            tr += f" /Info {info} 0 R"
        if ids:
            tr += " /ID [<4f6e65506466546573744f6e6550646620> <4f6e65506466546573744f6e6550646620>]"
        tr += f" {extra_trailer} >>"
        out += f"trailer\n{tr}\nstartxref\n{xref}\n%%EOF\n".encode()
        return bytes(out)


def pdfstr(s):
    return "(" + s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)") + ")"


def text_page_content(lines, size=14, x=72, y=760, lead=20, font="F1"):
    out = [f"BT /{font} {size} Tf {lead} TL {x} {y} Td"]
    for l in lines:
        out.append(f"{pdfstr(l)} Tj T*")
    out.append("ET")
    return "\n".join(out).encode("latin-1")


def simple_doc(pages_lines, with_info=True, version="1.7", fonts_extra=""):
    """Pages of Helvetica text. Returns (Raw, root, info, page_nums)."""
    r = Raw(version)
    cat = r.reserve()
    pages = r.reserve()
    font = r.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    info = r.add("<< /Title (OnePdf test document) /Author (OnePdf) /Producer (hand-assembled) /CreationDate (D:20260101000000Z) >>") if with_info else None
    nums = []
    for lines in pages_lines:
        c = r.stream("", text_page_content(lines))
        p = r.add(f"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R {fonts_extra} >> >> /Contents {c} 0 R >>")
        nums.append(p)
    r.set(pages, f"<< /Type /Pages /Kids [{' '.join(f'{p} 0 R' for p in nums)}] /Count {len(nums)} >>")
    r.set(cat, f"<< /Type /Catalog /Pages {pages} 0 R >>")
    return r, cat, info, nums, font, pages


def save_raw(name, data, pages_text, category, **extra):
    with open(os.path.join(PDF, name + ".pdf"), "wb") as f:
        f.write(data)
    write_truth(name, pages_text, "hand-assembled by tools/corpus/generate.py", category=category, **extra)


BASE_PAGES = [
    ["The first page of the base document.", "It has two lines of plain Helvetica text."],
    ["A second page follows with its own words.", "Nothing here repeats the first page."],
    ["The third and last page ends the document."],
]


def tx(lines):
    return "\n".join(lines)


def gen_raw():
    # ---- base documents
    n = "raw-base"
    if want(n):
        r, cat, info, *_ = simple_doc(BASE_PAGES)
        save_raw(n, r.build(cat, info), [tx(p) for p in BASE_PAGES], "structure", notes="uncompressed three-page file with a classic xref table; base for the repair and encryption variants")

    # ---- repair variants of the base
    base = open(os.path.join(PDF, "raw-base.pdf"), "rb").read() if os.path.exists(os.path.join(PDF, "raw-base.pdf")) else None
    if base and want("raw-broken"):
        pages_text = [tx(p) for p in BASE_PAGES]
        sx = base.rindex(b"startxref")
        variants = {
            "raw-broken-truncated-tail": (base[: base.index(b"xref\n0 ")], "xref table, trailer and startxref cut off: objects and the catalog must be found by scanning (some viewers cannot recover this)"),
            "raw-broken-no-xref-table": (base[: base.index(b"xref\n0 ")] + base[base.index(b"trailer"):], "xref table removed but trailer and startxref kept"),
            "raw-broken-bad-startxref": (base[: sx] + b"startxref\n7\n%%EOF\n", "startxref points into the middle of the header"),
            "raw-broken-junk-before-header": (b"GARBAGE LINE BEFORE THE HEADER\r\n" * 3 + base, "junk before %PDF-; all offsets are shifted"),
            "raw-broken-bad-xref-offsets": (re.sub(rb"(\d{10}) 00000 n", lambda m: b"%010d 00000 n" % (int(m.group(1)) + 3), base), "every xref offset is wrong by three bytes"),
            "raw-broken-wrong-length": (re.sub(rb"/Length (\d+)", lambda m: b"/Length %d" % (int(m.group(1)) + 40), base, count=1), "the first content stream declares /Length too large"),
            "raw-broken-short-length": (re.sub(rb"/Length (\d+)", lambda m: b"/Length %d" % max(1, int(m.group(1)) - 15), base, count=1), "the first content stream declares /Length too small"),
            "raw-broken-no-endobj": (base.replace(b"\nendobj\n", b"\n"), "no object is closed with endobj"),
            "raw-broken-truncated-stream": (base[: base.index(b"3 0 obj") + 200], "file ends in the middle of the first objects: the document cannot be recovered; expect an error or an empty result", ),
        }
        for k, (data, note) in variants.items():
            recoverable = k != "raw-broken-truncated-stream"
            save_raw(k, data, pages_text if recoverable else [], "repair", notes=note, expect="recovers-all-text" if recoverable else "unrecoverable",
                     **({"blackBoxCheck": "may-fail"} if k == "raw-broken-truncated-tail" else {}))
    # a page tree that points back at itself
    n = "raw-broken-cyclic-page-tree"
    if want(n):
        r = Raw()
        cat = r.reserve(); pages = r.reserve(); sub = r.reserve()
        font = r.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
        c = r.stream("", text_page_content(["Only page behind a cyclic tree."]))
        page = r.add(f"<< /Type /Page /Parent {sub} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {c} 0 R >>")
        r.set(pages, f"<< /Type /Pages /Kids [{sub} 0 R] /Count 2 >>")
        r.set(sub, f"<< /Type /Pages /Parent {pages} 0 R /Kids [{page} 0 R {pages} 0 R] /Count 2 >>")
        r.set(cat, f"<< /Type /Catalog /Pages {pages} 0 R >>")
        save_raw(n, r.build(cat), ["Only page behind a cyclic tree."], "repair", notes="/Kids contains an ancestor; expect one page and a diagnostic", expect="one-page-with-diagnostic")

    # ---- annotations
    n = "raw-annotations"
    if want(n):
        r, cat, info, nums, font, pages = simple_doc([["Annotated page.", "Review the highlighted sentence before the link."]])
        dest = f"[{nums[0]} 0 R /XYZ 0 792 null]"
        a1 = r.add("<< /Type /Annot /Subtype /Link /Rect [72 700 200 715] /Border [0 0 0] /A << /S /URI /URI (https://example.com/ref) >> >>")
        a2 = r.add(f"<< /Type /Annot /Subtype /Link /Rect [72 680 200 695] /Border [0 0 0] /Dest {dest} >>")
        a3 = r.add("<< /Type /Annot /Subtype /Text /Rect [400 740 420 760] /Contents (Check this reference) /T (Reviewer) /Name /Comment /F 28 /C [1 1 0] >>")
        a4 = r.add("<< /Type /Annot /Subtype /Highlight /Rect [72 735 300 752] /QuadPoints [72 752 300 752 72 735 300 735] /Contents (Highlighted sentence) /C [1 1 0] /F 4 >>")
        a5 = r.add("<< /Type /Annot /Subtype /FreeText /Rect [320 600 540 640] /Contents (Draft stamp text) /DA (/Helv 12 Tf 0 g) /F 4 >>")
        a6 = r.add("<< /Type /Annot /Subtype /Square /Rect [72 500 200 560] /Contents (A square) /C [1 0 0] /IC [0.9 0.9 1] /F 4 >>")
        r.set(nums[0], r.objs[nums[0] - 1].decode("latin-1").rstrip(">").rstrip() + f" /Annots [{a1} 0 R {a2} 0 R {a3} 0 R {a4} 0 R {a5} 0 R {a6} 0 R] >>")
        save_raw(n, r.build(cat, info), ["Annotated page.\nReview the highlighted sentence before the link."], "annotations",
                 annotations=[{"subtype": "Link", "uri": "https://example.com/ref"}, {"subtype": "Link", "dest": "page 0 XYZ"},
                              {"subtype": "Text", "contents": "Check this reference", "title": "Reviewer"},
                              {"subtype": "Highlight", "contents": "Highlighted sentence"},
                              {"subtype": "FreeText", "contents": "Draft stamp text"}, {"subtype": "Square", "contents": "A square"}],
                 notes="annotations have no appearance streams; annotation text is not page text")

    # ---- AcroForm
    n = "raw-acroform"
    if want(n):
        r, cat, info, nums, font, pages = simple_doc([["Registration form.", "Fill in every field."]])
        helv = font
        def ap(text, w, h):
            c = f"/Tx BMC q BT /Helv 12 Tf 0 g 2 {h/2-4:.1f} Td {pdfstr(text)} Tj ET Q EMC".encode()
            return r.stream(f"/Type /XObject /Subtype /Form /BBox [0 0 {w} {h}] /Resources << /Font << /Helv {helv} 0 R >> >>", c)
        f1 = r.add(f"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Ada Lovelace) /DA (/Helv 12 Tf 0 g) /Rect [72 600 272 620] /F 4 /AP << /N {ap('Ada Lovelace', 200, 20)} 0 R >> >>")
        f2 = r.add(f"<< /Type /Annot /Subtype /Widget /FT /Tx /T (city) /V (London) /DA (/Helv 12 Tf 0 g) /Rect [72 570 272 590] /F 4 /AP << /N {ap('London', 200, 20)} 0 R >> >>")
        on = r.stream("/Type /XObject /Subtype /Form /BBox [0 0 14 14]", b"q 0 g 2 2 10 10 re f Q")
        off = r.stream("/Type /XObject /Subtype /Form /BBox [0 0 14 14]", b"q 0 G 0.5 0.5 13 13 re S Q")
        f3 = r.add(f"<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /Yes /AS /Yes /Rect [72 540 86 554] /F 4 /AP << /N << /Yes {on} 0 R /Off {off} 0 R >> >> >>")
        radio = r.reserve()
        rk = []
        for i, st in enumerate(("Small", "Medium", "Large")):
            rk.append(r.add(f"<< /Type /Annot /Subtype /Widget /Parent {radio} 0 R /Rect [{72 + 40 * i} 510 {86 + 40 * i} 524] /F 4 /AS /{'Off' if st != 'Medium' else 'Medium'} /AP << /N << /{st} {on} 0 R /Off {off} 0 R >> >> >>"))
        r.set(radio, f"<< /FT /Btn /Ff 49152 /T (size) /V /Medium /Kids [{' '.join(f'{k} 0 R' for k in rk)}] >>")
        f5 = r.add("<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (color) /V (Green) /Opt [(Red) (Green) (Blue)] /DA (/Helv 12 Tf 0 g) /Rect [72 470 200 490] /F 4 >>")
        f6 = r.add("<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 2097152 /T (tags) /V [(alpha) (gamma)] /Opt [(alpha) (beta) (gamma)] /DA (/Helv 12 Tf 0 g) /Rect [72 400 200 460] /F 4 >>")
        allf = [f1, f2, f3, radio, f5, f6]
        for k in rk:
            pass
        r.set(nums[0], r.objs[nums[0] - 1].decode("latin-1").rstrip(">").rstrip() +
              f" /Annots [{' '.join(f'{x} 0 R' for x in [f1, f2, f3] + rk + [f5, f6])}] >>")
        r.set(cat, f"<< /Type /Catalog /Pages {pages} 0 R /AcroForm << /Fields [{' '.join(f'{x} 0 R' for x in allf)}] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv {helv} 0 R >> >> >> >>")
        save_raw(n, r.build(cat, info), ["Registration form.\nFill in every field.", ][0:1], "forms",
                 fields=[{"name": "name", "type": "Tx", "value": "Ada Lovelace"}, {"name": "city", "type": "Tx", "value": "London"},
                         {"name": "agree", "type": "Btn/checkbox", "value": "Yes"}, {"name": "size", "type": "Btn/radio", "value": "Medium"},
                         {"name": "color", "type": "Ch/combo", "value": "Green", "options": ["Red", "Green", "Blue"]},
                         {"name": "tags", "type": "Ch/list", "value": ["alpha", "gamma"], "options": ["alpha", "beta", "gamma"]}],
                 notes="text-field appearance streams contain the field values; the two Tx values are visible content only when widgets are drawn")

    # ---- embedded files
    n = "raw-embedded-files"
    if want(n):
        r, cat, info, nums, font, pages = simple_doc([["This file carries two attachments."]])
        t1 = b"Hello from an embedded file.\n"
        t2 = b"id,name\n1,alpha\n2,beta\n"
        e1 = r.stream("/Type /EmbeddedFile /Subtype /text#2Fplain /Params << /Size %d >>" % len(t1), t1)
        e2 = r.stream("/Type /EmbeddedFile /Subtype /text#2Fcsv /Params << /Size %d >>" % len(t2), t2, compress=True)
        fs1 = r.add(f"<< /Type /Filespec /F (notes.txt) /UF (notes.txt) /Desc (Plain notes) /EF << /F {e1} 0 R /UF {e1} 0 R >> >>")
        fs2 = r.add(f"<< /Type /Filespec /F (data.csv) /UF (data.csv) /EF << /F {e2} 0 R >> >>")
        r.set(cat, f"<< /Type /Catalog /Pages {pages} 0 R /Names << /EmbeddedFiles << /Names [(data.csv) {fs2} 0 R (notes.txt) {fs1} 0 R] >> >> >>")
        save_raw(n, r.build(cat, info), ["This file carries two attachments."], "embedded-files",
                 embeddedFiles=[{"name": "data.csv", "content": t2.decode()}, {"name": "notes.txt", "content": t1.decode(), "description": "Plain notes"}])

    # ---- outlines + page labels
    n = "raw-outlines-labels"
    if want(n):
        pl = [["Preface text on a roman-numbered page."], ["Contents text on a second roman page."], ["Chapter one opens here."], ["Chapter two opens here."]]
        r, cat, info, nums, font, pages = simple_doc(pl)
        o = [r.reserve() for _ in range(5)]  # root, ch1, s11, s12, ch2
        dst = lambda i: f"[{nums[i]} 0 R /Fit]"
        r.set(o[0], f"<< /Type /Outlines /First {o[1]} 0 R /Last {o[4]} 0 R /Count 4 >>")
        r.set(o[1], f"<< /Title (Chapter 1) /Parent {o[0]} 0 R /Next {o[4]} 0 R /First {o[2]} 0 R /Last {o[3]} 0 R /Count 2 /Dest {dst(2)} >>")
        r.set(o[2], f"<< /Title (Section 1.1) /Parent {o[1]} 0 R /Next {o[3]} 0 R /Dest {dst(2)} >>")
        r.set(o[3], f"<< /Title (Section 1.2) /Parent {o[1]} 0 R /Prev {o[2]} 0 R /Dest {dst(2)} /C [1 0 0] /F 2 >>")
        r.set(o[4], f"<< /Title (Chapter 2) /Parent {o[0]} 0 R /Prev {o[1]} 0 R /Dest {dst(3)} >>")
        r.set(cat, f"<< /Type /Catalog /Pages {pages} 0 R /Outlines {o[0]} 0 R /PageLabels << /Nums [0 << /S /r >> 2 << /S /D /P (A-) /St 1 >>] >> /PageMode /UseOutlines >>")
        save_raw(n, r.build(cat, info), [tx(p) for p in pl], "outlines-labels",
                 outline=[["Chapter 1", 2, [["Section 1.1", 2, []], ["Section 1.2", 2, []]]], ["Chapter 2", 3, []]],
                 pageLabels=["i", "ii", "A-1", "A-2"])

    # ---- Type 3
    n = "raw-type3"
    if want(n):
        r = Raw()
        cat = r.reserve(); pages = r.reserve()
        g1 = r.stream("", b"1000 0 0 0 750 750 d1 0 0 750 750 re f")
        g2 = r.stream("", b"1000 0 0 0 750 750 d1 0 0 m 750 0 l 375 750 l f")
        g3 = r.stream("", b"1000 0 0 0 750 750 d1 375 375 375 0 360 arc 0 0 m 750 0 l 750 750 l 0 750 l f")
        g3 = r.stream("", b"1000 0 0 0 750 750 d1 0 0 m 750 0 l 750 750 l 0 750 l h 150 150 m 150 600 l 600 600 l 600 150 l h f*")
        tu = (b"/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Adobe-Identity-UCS def /CMapType 2 def "
              b"1 begincodespacerange <00> <FF> endcodespacerange 3 beginbfchar <41> <0041> <42> <0042> <43> <0043> endbfchar endcmap end end")
        tuo = r.stream("", tu)
        f = r.add(f"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /sq {g1} 0 R /tri {g2} 0 R /frame {g3} 0 R >> "
                  f"/Encoding << /Type /Encoding /Differences [65 /sq /tri /frame] >> /FirstChar 65 /LastChar 67 /Widths [1000 1000 1000] /ToUnicode {tuo} 0 R >>")
        c = r.stream("", b"BT /T3 24 Tf 72 700 Td (ABCCBA) Tj 0 -40 Td (AABBCC) Tj ET")
        p = r.add(f"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /T3 {f} 0 R >> >> /Contents {c} 0 R >>")
        r.set(pages, f"<< /Type /Pages /Kids [{p} 0 R] /Count 1 >>")
        r.set(cat, f"<< /Type /Catalog /Pages {pages} 0 R >>")
        save_raw(n, r.build(cat), ["ABCCBA\nAABBCC"], "type3", notes="Type 3 font: glyphs are a square, a triangle and a frame; codes map to A, B, C through /ToUnicode")

    # ---- standard fonts, encodings, ligature via /Differences
    n = "raw-std14-encodings"
    if want(n):
        r = Raw()
        cat = r.reserve(); pages = r.reserve()
        f1 = r.add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>")
        f2 = r.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [1 /fi /fl /ffi] >> >>")
        f3 = r.add("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>")
        f4 = r.add("<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>")
        f5 = r.add("<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>")
        c = ("BT /F1 14 Tf 72 740 Td (caf\\351 na\\357ve \\226 \\223quoted\\224 \\200 5) Tj ET\n"
             "BT /F2 14 Tf 72 710 Td (\\001nal a\\002oat e\\003gy) Tj ET\n"
             "BT /F3 12 Tf 72 680 Td (monospaced courier text) Tj ET\n"
             "BT /F4 14 Tf 72 650 Td (abg) Tj ET\n"
             "BT /F5 14 Tf 72 620 Td (4 3) Tj ET\n").encode("latin-1")
        cs = r.stream("", c)
        p = r.add(f"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {f1} 0 R /F2 {f2} 0 R /F3 {f3} 0 R /F4 {f4} 0 R /F5 {f5} 0 R >> >> /Contents {cs} 0 R >>")
        r.set(pages, f"<< /Type /Pages /Kids [{p} 0 R] /Count 1 >>")
        r.set(cat, f"<< /Type /Catalog /Pages {pages} 0 R >>")
        save_raw(n, r.build(cat), ["café naïve – “quoted” € 5\nfinal afloat effigy\nmonospaced courier text\nαβγ\n✔ ✓"], "fonts-std14",
                 notes="Standard-14 fonts without embedding. Line 2: codes 1, 2 and 3 are the fi, fl and ffi ligature glyphs through /Differences; truth is decomposed. "
                       "Line 4 is Symbol (alpha beta gamma); line 5 is ZapfDingbats codes 4 and 3 (U+2714, U+2713).")

    # ---- rotation, boxes
    n = "raw-rotated-pages"
    if want(n):
        r = Raw()
        cat = r.reserve(); pages = r.reserve()
        font = r.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
        kids = []
        texts = []
        for i, rot in enumerate((0, 90, 180, 270)):
            t = f"This page is rotated by {rot} degrees."
            c = r.stream("", text_page_content([t, "Second line of the page."]))
            kids.append(r.add(f"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Rotate {rot} /Resources << /Font << /F1 {font} 0 R >> >> /Contents {c} 0 R >>"))
            texts.append(t + "\nSecond line of the page.")
        # a page whose text matrix itself is rotated, with an offset CropBox and a user unit
        t = "Text on a rotated baseline."
        c = r.stream("", f"BT /F1 14 Tf 0 1 -1 0 300 300 Tm ({t}) Tj ET".encode())
        kids.append(r.add(f"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /CropBox [100 100 500 700] /UserUnit 2 /Resources << /Font << /F1 {font} 0 R >> >> /Contents {c} 0 R >>"))
        texts.append(t)
        r.set(pages, f"<< /Type /Pages /Kids [{' '.join(f'{k} 0 R' for k in kids)}] /Count {len(kids)} >>")
        r.set(cat, f"<< /Type /Catalog /Pages {pages} 0 R >>")
        save_raw(n, r.build(cat), texts, "rotation", rotations=[0, 90, 180, 270, 0], notes="last page: rotated text matrix, CropBox [100 100 500 700], UserUnit 2")

    # ---- incremental update
    n = "raw-incremental-update"
    if want(n):
        r, cat, info, nums, font, pages = simple_doc([["Original text of the only page."]])
        first = r.build(cat, info)
        sx_old = int(re.search(rb"startxref\n(\d+)", first).group(1))
        cobj = nums[0] + 0  # page object holds /Contents; find stream object number
        cnum = int(re.search(rb"/Contents (\d+) 0 R", r.objs[nums[0] - 1]).group(1))
        new = text_page_content(["Updated text of the only page."])
        body = f"{cnum} 0 obj\n<< /Length {len(new)} >>\nstream\n".encode() + new + b"\nendstream\nendobj\n"
        off = len(first)
        total = len(r.objs) + 1
        xr = len(first) + len(body)
        upd = (first + body + f"xref\n{cnum} 1\n{off:010d} 00000 n \ntrailer\n<< /Size {total} /Root {cat} 0 R /Info {info} 0 R /Prev {sx_old} "
               "/ID [<4f6e65506466546573744f6e6550646620> <4f6e65506466546573744f6e6550646620>] >>\n".encode() + f"startxref\n{xr}\n%%EOF\n".encode())
        save_raw(n, upd, ["Updated text of the only page."], "structure", revisions=["Original text of the only page.", "Updated text of the only page."],
                 notes="two revisions; the second replaces the content stream through an incremental update")

    # ---- Info + XMP
    n = "raw-metadata"
    if want(n):
        r = Raw("1.7")
        cat = r.reserve(); pages = r.reserve()
        font = r.add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
        c = r.stream("", text_page_content(["Metadata carrier."]))
        p = r.add(f"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {c} 0 R >>")
        r.set(pages, f"<< /Type /Pages /Kids [{p} 0 R] /Count 1 >>")
        xmp = ('<?xpacket begin="\ufeff" id="W5M0MpCehiHzreSzNTczkc9d"?><x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
               '<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:pdf="http://ns.adobe.com/pdf/1.3/">'
               '<dc:title><rdf:Alt><rdf:li xml:lang="x-default">XMP Title \u00e9</rdf:li></rdf:Alt></dc:title><dc:creator><rdf:Seq><rdf:li>Ada</rdf:li><rdf:li>Grace</rdf:li></rdf:Seq></dc:creator>'
               '<xmp:CreatorTool>onepdf-generator</xmp:CreatorTool><pdf:Keywords>alpha, beta</pdf:Keywords></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end="w"?>').encode("utf-8")
        mx = r.stream("/Type /Metadata /Subtype /XML", xmp)
        info = r.add("<< /Title <FEFF005400690074006C0065002000E9> /Author (Ada) /Subject (Metadata test) /Keywords (alpha, beta) /Creator (onepdf-generator) /Producer (hand-assembled) "
                     "/CreationDate (D:20260102030405+02'00') /ModDate (D:20260102030405Z) /Custom (custom value) >>")
        r.set(cat, f"<< /Type /Catalog /Pages {pages} 0 R /Metadata {mx} 0 R /Lang (en-US) >>")
        save_raw(n, r.build(cat, info), ["Metadata carrier."], "metadata",
                 info={"Title": "Title \u00e9", "Author": "Ada", "Subject": "Metadata test", "Keywords": "alpha, beta", "Creator": "onepdf-generator",
                       "CreationDate": "2026-01-02T03:04:05+02:00", "ModDate": "2026-01-02T03:04:05Z", "Custom": "custom value"},
                 xmp={"title": "XMP Title \u00e9", "creators": ["Ada", "Grace"], "creatorTool": "onepdf-generator", "keywords": "alpha, beta"})


# ---------------------------------------------------------------- qpdf-derived variants
ENC = [
    # name, qpdf args after the passwords, revision note
    ("rc4-40", ["40"], "R2", True),
    ("rc4-128", ["128", "--use-aes=n"], "R3", True),
    ("aes-128", ["128", "--use-aes=y"], "R4", False),
    ("aes-256-r5", ["256", "--force-R5"], "R5", True),
    ("aes-256", ["256"], "R6", False),
]


def qpdf_run(args):
    rc, out = run([QPDF] + args, timeout=120)
    if rc not in (0, 3):
        raise RuntimeError("qpdf failed: " + out[-300:])


def gen_qpdf():
    if not QPDF:
        print("skip qpdf variants (no qpdf)")
        return
    rc, v = run([QPDF, "--version"])
    gen = "qpdf (black-box) " + v.splitlines()[0].strip()
    bases = {"raw-base": "raw-base", "chrome-single-column-en": "chrome-single-column-en"}
    for b in list(bases):
        if not os.path.exists(os.path.join(PDF, b + ".pdf")):
            return
    def truth_of(name):
        return json.load(open(os.path.join(TRUTH, name + ".json"), encoding="utf-8"))
    for base in bases:
        t = truth_of(base)
        pages = [p["text"] for p in t["pages"]]
        src = os.path.join(PDF, base + ".pdf")
        tag = "raw" if base.startswith("raw") else "chrome"
        for ename, eargs, rev, weak in ENC:
            if tag == "chrome" and ename not in ("aes-128", "aes-256", "rc4-128"):
                continue
            name = f"enc-{tag}-{ename}"
            if not want(name):
                continue
            out = os.path.join(PDF, name + ".pdf")
            extra = ["--allow-weak-crypto"] if weak else []
            qpdf_run(extra + ["--encrypt", "user-pw", "owner-pw"] + eargs + ["--", src, out])
            write_truth(name, pages, gen + f" from {base}", passwords={"user": "user-pw", "owner": "owner-pw"}, category="encrypted",
                        securityRevision=rev, source=base)
    # empty user password (owner password only), and cleartext metadata
    for name, args, rev, weak in (("enc-raw-rc4-128-empty-user", ["128", "--use-aes=n"], "R3", True), ("enc-raw-aes-256-empty-user", ["256"], "R6", False),
                                  ("enc-raw-aes-128-empty-user", ["128", "--use-aes=y"], "R4", False)):
        if not want(name):
            continue
        t = truth_of("raw-base")
        qpdf_run((["--allow-weak-crypto"] if weak else []) + ["--encrypt", "", "owner-pw"] + args + ["--", os.path.join(PDF, "raw-base.pdf"), os.path.join(PDF, name + ".pdf")])
        write_truth(name, [p["text"] for p in t["pages"]], gen + " from raw-base", passwords={"user": "", "owner": "owner-pw"}, category="encrypted", securityRevision=rev,
                    notes="opens with the empty password; the owner password also works")
    name = "enc-raw-aes-128-cleartext-metadata"
    if want(name):
        t = truth_of("raw-metadata")
        qpdf_run(["--encrypt", "user-pw", "owner-pw", "128", "--use-aes=y", "--cleartext-metadata", "--", os.path.join(PDF, "raw-metadata.pdf"), os.path.join(PDF, name + ".pdf")])
        write_truth(name, [p["text"] for p in t["pages"]], gen + " from raw-metadata", passwords={"user": "user-pw", "owner": "owner-pw"}, category="encrypted",
                    securityRevision="R4", notes="the XMP stream stays in the clear (/EncryptMetadata false); /Info strings are encrypted", source="raw-metadata")

    # structure variants: object streams + xref stream, linearized, rotated
    for base, variant, flags, note in (
            ("raw-base", "objstm", ["--object-streams=generate", "--compress-streams=y"], "objects live in object streams; the cross-reference is a stream"),
            ("raw-base", "linearized", ["--linearize"], "linearized layout (read as an ordinary file)"),
            ("raw-annotations", "objstm", ["--object-streams=generate"], "annotation objects inside an object stream"),
            ("chrome-links-outline", "objstm", ["--object-streams=generate"], "outline, links and fonts inside object streams"),
            ("raw-base", "rotated-90", ["--rotate=+90:1-z"], "pages rotated by /Rotate 90 after the fact"),
            ("chrome-two-column-en", "rotated-270", ["--rotate=+270:1-z"], "two-column page with /Rotate 270; reading order follows the rotated page")):
        name = f"{base}-{variant}"
        if not want(name) or not os.path.exists(os.path.join(PDF, base + ".pdf")):
            continue
        qpdf_run(["--deterministic-id"] + flags + [os.path.join(PDF, base + ".pdf"), os.path.join(PDF, name + ".pdf")])
        t = truth_of(base)
        extra = {k: v for k, v in t.items() if k in ("annotations", "outline", "links")}
        write_truth(name, [p["text"] for p in t["pages"]], gen + f" from {base}", category="structure" if "rotated" not in variant else "rotation", source=base, notes=note, **extra)


def main():
    gen_raw()
    gen_chrome()
    gen_lo()
    gen_tex()
    gen_qpdf()
    # drop sidecars without a PDF and report PDFs without sidecars
    pdfs = {f[:-4] for f in os.listdir(PDF) if f.endswith(".pdf")}
    truth = {f[:-5] for f in os.listdir(TRUTH) if f.endswith(".json")}
    for x in sorted(truth - pdfs):
        print("sidecar without pdf:", x)
    for x in sorted(pdfs - truth):
        print("pdf without sidecar:", x)
    print(len(pdfs), "pdfs,", len(truth), "sidecars")


if __name__ == "__main__":
    main()
