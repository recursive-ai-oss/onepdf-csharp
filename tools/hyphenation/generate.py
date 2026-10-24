#!/usr/bin/env python3
"""Fetches the hyphenation pattern files listed below from the hyph-utf8 collection at a pinned commit,
checks their SHA-256, and writes the compact data embedded in RecursiveAI.OnePdf.Hyphenation plus the
package's notice file.

    python3 tools/hyphenation/generate.py [--cache DIR]

The sources are TeX pattern files (data, each under its own licence, quoted in the notices). Nothing of the
programs that use them is read or copied. Output, relative to the repository root:

    src/OnePdf.Hyphenation/Patterns/<id>.bin   raw-deflated text: "<left> <right>", the patterns one per
                                                line, an empty line, then the exceptions one per line
    src/OnePdf.Hyphenation/THIRD-PARTY-NOTICES.md

Run again to check: the script fails when a downloaded file does not match the pinned hash.
"""
import argparse, hashlib, os, re, sys, urllib.request, zlib

COMMIT = "5684c0f51c0b81133db2efbe60a408b4155a3ff5"
BASE = ("https://raw.githubusercontent.com/hyphenation/tex-hyphen/" + COMMIT +
        "/hyph-utf8/tex/generic/hyph-utf8/patterns/tex/")

# id -> (display name, source files in order, licence key, licence used)
LANGS = [
    ("en-us", "English (American)", ["hyph-en-us.tex"], "copy"),
    ("en-gb", "English (British)", ["hyph-en-gb.tex"], "mit"),
    ("de-1996", "German (reformed spelling)", ["hyph-de-1996.tex"], "mit"),
    ("de-1901", "German (traditional spelling)", ["hyph-de-1901.tex"], "mit"),
    ("de-ch-1901", "German (Swiss, traditional spelling)", ["hyph-de-ch-1901.tex"], "mit"),
    ("fr", "French", ["hyph-fr.tex"], "mit"),
    ("es", "Spanish", ["hyph-es.tex"], "mit"),
    ("it", "Italian", ["hyph-it.tex"], "mit"),
    ("nl", "Dutch", ["hyph-nl.tex"], "mit"),
    ("pt", "Portuguese", ["hyph-pt.tex"], "bsd3"),
    ("pl", "Polish", ["hyph-pl.tex"], "mit"),
    ("da", "Danish", ["hyph-da.tex"], "mit"),
    ("no", "Norwegian", ["hyph-no.tex"], "copy"),
    ("nb", "Norwegian Bokmal", ["hyph-no.tex", "hyph-nb.tex"], "copy"),
    ("nn", "Norwegian Nynorsk", ["hyph-no.tex", "hyph-nn.tex"], "copy"),
    ("bg", "Bulgarian", ["hyph-bg.tex"], "bg"),
    ("sk", "Slovak", ["hyph-sk.tex"], "mit"),
    ("sl", "Slovenian", ["hyph-sl.tex"], "mit"),
    ("lt", "Lithuanian", ["hyph-lt.tex"], "mit"),
    ("et", "Estonian", ["hyph-et.tex"], "mit"),
    ("uk", "Ukrainian", ["hyph-uk.tex"], "mit"),
    ("gl", "Galician", ["hyph-gl.tex"], "unlicense"),
    ("el-monoton", "Greek (monotonic)", ["hyph-el-monoton.tex"], "mit"),
    ("la", "Latin", ["hyph-la.tex"], "mit"),
    ("cy", "Welsh", ["hyph-cy.tex"], "mit"),
    ("ga", "Irish", ["hyph-ga.tex"], "mit"),
]

HASHES_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sources.sha256")

MIT_TEXT = """Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE."""

UNLICENSE_TEXT = """This is free and unencumbered software released into the public domain. Anyone is free to copy, modify, publish, use, compile, sell, or distribute this software, either in source code form or as a compiled binary, for any purpose, commercial or non-commercial, and by any means. (The full text is at https://unlicense.org/.)"""


def fetch(name, cache):
    path = os.path.join(cache, name)
    if not os.path.exists(path):
        with urllib.request.urlopen(BASE + name) as r:
            data = r.read()
        with open(path, "wb") as f:
            f.write(data)
    with open(path, "rb") as f:
        return f.read()


def header_value(text, key):
    """The value of a '% key:' header entry, including its indented continuation lines."""
    lines = text.splitlines()
    for i, ln in enumerate(lines):
        m = re.match(r"%\s{0,2}" + key + r":\s*(.*)$", ln)
        if m and not ln.startswith("%    "):
            parts = [m.group(1).strip()]
            for nxt in lines[i + 1:]:
                if re.match(r"%\s{3,}\S", nxt) and not re.match(r"%\s{0,2}\w+:", nxt):
                    parts.append(nxt[1:].strip())
                else:
                    break
            return " ".join(p for p in parts if p)
    return ""


def licence_text(text):
    lines = text.splitlines()
    out, on, indent = [], False, None
    in_lic = False
    for ln in lines:
        if re.match(r"%\s*licence:", ln):
            in_lic = True
            continue
        if in_lic and re.match(r"%\s*(hyphenmins|changes|texlive|language|authors|version):", ln):
            break
        if not in_lic:
            continue
        if not on:
            if re.search(r"text:\s*>", ln):
                on = True
            continue
        body = ln[1:]
        if body.strip() == "":
            out.append("")
            continue
        cur = len(body) - len(body.lstrip())
        if indent is None:
            indent = cur
        if cur < indent:
            break
        out.append(body[indent:].rstrip())
    para, cur = [], []
    for ln in out:
        if ln == "":
            if cur:
                para.append(" ".join(cur))
                cur = []
        else:
            cur.append(ln)
    if cur:
        para.append(" ".join(cur))
    return "\n\n".join(para)


def parse(text):
    t = re.sub(r"(?m)(?<!\\)%.*$", "", text)
    pats, exc = [], []
    for m in re.finditer(r"\\patterns\s*\{(.*?)\}", t, re.S):
        pats += m.group(1).split()
    for m in re.finditer(r"\\hyphenation\s*\{(.*?)\}", t, re.S):
        exc += m.group(1).split()
    return pats, exc


def hyphenmins(text):
    m = re.search(r"typesetting:\s*%\s*left:\s*(\d+)\s*%\s*right:\s*(\d+)", text)
    return (int(m.group(1)), int(m.group(2))) if m else (2, 2)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=os.path.join(os.environ.get("TMPDIR", "."), "hyph-src"))
    args = ap.parse_args()
    os.makedirs(args.cache, exist_ok=True)
    root = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    outdir = os.path.join(root, "src", "OnePdf.Hyphenation", "Patterns")
    os.makedirs(outdir, exist_ok=True)

    pinned = {}
    if os.path.exists(HASHES_FILE):
        for ln in open(HASHES_FILE, encoding="utf-8"):
            if ln.strip():
                h, n = ln.split()
                pinned[n] = h
    seen = {}
    texts = {}
    for _, _, files, _ in LANGS:
        for f in files:
            if f not in texts:
                raw = fetch(f, args.cache)
                h = hashlib.sha256(raw).hexdigest()
                if f in pinned and pinned[f] != h:
                    sys.exit("hash mismatch for " + f)
                seen[f] = h
                texts[f] = raw.decode("utf-8")
    if not pinned:
        with open(HASHES_FILE, "w", encoding="utf-8", newline="\n") as w:
            for f in sorted(seen):
                w.write(seen[f] + "  " + f + "\n")

    sizes = {}
    for lid, _, files, _ in LANGS:
        pats, exc = [], []
        for f in files:
            p, e = parse(texts[f])
            pats += p
            exc += e
        left, right = hyphenmins(texts[files[0]])
        pats = sorted({p.lower() for p in pats})
        # A later entry for the same word replaces an earlier one (a Norwegian variant overrides the shared list).
        exc = [v for _, v in sorted({e.lower().replace("-", ""): e.lower() for e in exc}.items())]
        body = "%d %d\n" % (left, right) + "\n".join(pats) + "\n\n" + "\n".join(exc) + "\n"
        c = zlib.compressobj(9, zlib.DEFLATED, -15)
        data = c.compress(body.encode("utf-8")) + c.flush()
        with open(os.path.join(outdir, lid + ".bin"), "wb") as w:
            w.write(data)
        sizes[lid] = (len(pats), len(exc), len(data))

    # Notices.
    o = []
    o.append("# Third-party notices: RecursiveAI.OnePdf.Hyphenation\n")
    o.append("The code of this package is `MIT OR Apache-2.0`. The embedded pattern data is derived from the hyphenation pattern files of the "
             "hyph-utf8 collection (https://github.com/hyphenation/tex-hyphen, commit `" + COMMIT + "`), converted to a compact "
             "binary form (patterns lower-cased, de-duplicated and sorted; comments dropped; nothing else changed). Each file is used under "
             "the terms below; where a file offers several licences, the permissive one named here is the one used. "
             "The package license expression is therefore `(MIT OR Apache-2.0) AND MIT AND BSD-3-Clause AND Unlicense` plus the notices "
             "for the terms that have no SPDX identifier (the two short permissions quoted under \"Copying permitted\" and \"Bulgarian\").\n")
    groups = {"mit": [], "copy": [], "bsd3": [], "bg": [], "unlicense": []}
    for lid, name, files, key in LANGS:
        groups[key].append((lid, name, files))

    def cr(f):
        return header_value(texts[f], "copyright")

    o.append("## MIT licence\n")
    o.append("Languages and copyright holders:\n")
    for lid, name, files in groups["mit"]:
        o.append("- %s (`%s`, `%s`): %s" % (name, lid, files[0], cr(files[0])))
    o.append("\n" + MIT_TEXT + "\n")
    o.append("## Copying permitted (en-us, no, nb, nn)\n")
    for lid, name, files in groups["copy"]:
        o.append("- %s (`%s`; sources %s): %s" % (name, lid, ", ".join("`" + f + "`" for f in files), "; ".join(dict.fromkeys(cr(f) for f in files))))
    o.append("\nThe files carry this permission (quoted from `hyph-en-us.tex`; the Norwegian files carry the same sentence):\n")
    o.append("> " + licence_text(texts["hyph-en-us.tex"]).replace("\n", "\n> ") + "\n")
    o.append("## BSD 3-clause (pt)\n")
    for lid, name, files in groups["bsd3"]:
        o.append("- %s (`%s`, `%s`): %s\n" % (name, lid, files[0], cr(files[0])))
    o.append("> " + licence_text(texts["hyph-pt.tex"]).replace("\n", "\n> ") + "\n")
    o.append("## Bulgarian (bg)\n")
    for lid, name, files in groups["bg"]:
        o.append("- %s (`%s`, `%s`): %s\n" % (name, lid, files[0], cr(files[0])))
    o.append("The file's own terms, which its header describes as equivalent to a 3-clause BSD licence:\n")
    o.append("> " + licence_text(texts["hyph-bg.tex"]).replace("\n", "\n> ") + "\n")
    o.append("## Unlicense (gl)\n")
    for lid, name, files in groups["unlicense"]:
        o.append("- %s (`%s`, `%s`): %s\n" % (name, lid, files[0], cr(files[0])))
    o.append(UNLICENSE_TEXT + "\n")
    with open(os.path.join(root, "src", "OnePdf.Hyphenation", "THIRD-PARTY-NOTICES.md"), "w", encoding="utf-8", newline="\n") as w:
        w.write("\n".join(o))

    tot = 0
    for lid, (p, e, c) in sizes.items():
        print("%-11s patterns %5d exceptions %4d bytes %6d" % (lid, p, e, c))
        tot += c
    print("total", tot)


if __name__ == "__main__":
    main()
