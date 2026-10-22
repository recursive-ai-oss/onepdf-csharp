"""Extracts the CFF predefined data from Adobe Technical Note #5176 (appendices A-C).

Needs UPSTREAM_DIR/cff.pdf and UPSTREAM_DIR/cff.txt (`pdftotext -layout`). Writes cffdata.bin: deflated text with
  S <sid> <name>      the 391 standard strings
  E <code> <sid>      Expert encoding
  X <sid> ...         Expert charset, glyph order after .notdef
  Y <sid> ...         Expert Subset charset
"""
import os
import re
import subprocess

NAME = r"(\.?[A-Za-z][A-Za-z0-9._]*|\d{3}\.\d{3})"
WORD = re.compile(r'<word xMin="([\d.]+)" yMin="([\d.]+)" xMax="[\d.]+" yMax="[\d.]+">([^<]*)</word>')


def generate(up, out, write, deflate):
    layout = open(os.path.join(up, "cff.txt"), encoding="utf-8").read()
    a = layout.index("Standard Strings\n\n\n(SID / name)")
    b = layout.index("Predefined Encodings", a)
    strings = {}
    for line in layout[a:b].split("\n"):
        if "Appendix" in line:
            continue
        for m in re.finditer(r"(?<![\w.])(\d{1,3})[ \t]+" + NAME, line):
            strings[int(m.group(1))] = m.group(2)
    if sorted(strings) != list(range(391)):
        raise SystemExit("standard strings incomplete: %d" % len(strings))

    c = layout.index("ExpertEncoding (code / SID / name", b)
    d = layout.index("Predefined Charsets", c)
    expert_enc = {}
    for line in layout[c:d].split("\n"):
        if "Appendix" in line:
            continue
        for m in re.finditer(r"(?<![\w.])(\d{1,3})[ \t]+(\d{1,3})[ \t]+" + NAME, line):
            code, sid, name = int(m.group(1)), int(m.group(2)), m.group(3)
            if sid and strings.get(sid) == name:
                expert_enc[code] = sid

    # The charsets are listed in glyph order, column by column; recover the columns from word positions.
    xml = subprocess.run(["pdftotext", "-bbox", os.path.join(up, "cff.pdf"), "-"], capture_output=True, text=True, check=True).stdout
    sets = {"expert": [], "subset": [], "iso": []}
    section = None
    started = False
    for page in xml.split("<page ")[1:]:
        words = [(float(m.group(1)), float(m.group(2)), m.group(3)) for m in WORD.finditer(page)]
        texts = [w[2] for w in words]
        if "ISOAdobe" in texts and "(SID" in texts:
            started = True
        if not started:
            continue
        if "Binary" in texts and "dump" in texts:
            break
        # Headings on this page: (y, section). Entries above the first heading continue the previous section.
        heads = []
        for i, t in enumerate(texts):
            if t == "ISOAdobe" and texts[i + 1:i + 2] == ["(SID"]:
                heads.append((words[i][1], "iso"))
            if t == "Expert" and texts[i + 1:i + 2] == ["(SID"]:
                heads.append((words[i][1], "expert"))
            if t == "Expert" and texts[i + 1:i + 3] == ["Subset", "(SID"]:
                heads.append((words[i][1], "subset"))
        entries = []
        for i in range(len(words) - 1):
            x, y, t = words[i]
            n = words[i + 1]
            if re.fullmatch(r"\d{1,3}", t) and abs(n[1] - y) < 2 and n[0] > x and re.fullmatch(r"[A-Za-z][A-Za-z0-9.]*", n[2]) and n[2] != "Appendix":
                sec = section
                for hy, hs in sorted(heads):
                    if hy <= y:
                        sec = hs
                entries.append((x, y, int(t), n[2], sec))
        if heads:
            section = sorted(heads)[-1][1]
        for sec in ("iso", "expert", "subset"):
            part = [e for e in entries if e[4] == sec]
            xs = sorted({e[0] for e in part})
            cols = []
            for x in xs:
                if cols and x - cols[-1][-1] <= 40:
                    cols[-1].append(x)
                else:
                    cols.append([x])

            def colof(x):
                for k, grp in enumerate(cols):
                    if x in grp:
                        return k
                return 0

            for x, y, sid, name, _ in sorted(part, key=lambda e: (colof(e[0]), e[1])):
                if strings.get(sid) != name:
                    raise SystemExit("charset entry mismatch %d %s" % (sid, name))
                sets[sec].append(sid)
    if sets["iso"] != list(range(1, 229)):
        raise SystemExit("ISOAdobe charset is not SIDs 1..228")
    print("expert charset", len(sets["expert"]), "subset", len(sets["subset"]), "expert encoding", len(expert_enc))
    if len(sets["expert"]) != 165 or len(sets["subset"]) != 86:
        raise SystemExit("unexpected charset sizes")
    lines = ["S %d %s" % (k, v) for k, v in sorted(strings.items())]
    lines += ["E %d %d" % (k, v) for k, v in sorted(expert_enc.items())]
    lines.append("X " + " ".join(map(str, sets["expert"])))
    lines.append("Y " + " ".join(map(str, sets["subset"])))
    write(out, "cffdata.bin", deflate(("\n".join(lines) + "\n").encode("ascii")))


def generate_mac_glyphs(up, out, write, deflate):
    """The 258 Macintosh standard glyph names used by 'post' formats 1 and 2 (Apple TrueType Reference Manual, 'post' table)."""
    t = open(os.path.join(up, "applepost.html"), encoding="utf-8", errors="replace").read()
    rows = re.findall(r"<td>(\d+)</td>\s*<td class=\"description\">([^<]+)</td>", t)
    names = [r[1].strip() for r in rows]
    if [int(r[0]) for r in rows] != list(range(258)):
        raise SystemExit("post table: expected 258 consecutive rows, got %d" % len(rows))
    write(out, "macglyphs.bin", deflate(("\n".join(names) + "\n").encode("ascii")))
