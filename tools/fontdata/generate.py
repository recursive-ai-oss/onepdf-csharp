#!/usr/bin/env python3
"""Generates the compact font-data resources embedded in the core assembly.

Usage: generate.py UPSTREAM_DIR SPEC_TEXT OUT_DIR

UPSTREAM_DIR holds the unpacked upstream sources (see fetch.sh, which pins URLs, commits and hashes):
  afm/           Adobe Core 14 AFM files and MustRead.html
  agl/           glyphlist.txt, zapfdingbats.txt (adobe-type-tools/agl-aglfn)
  cmr/           adobe-type-tools/cmap-resources
SPEC_TEXT is `pdftotext -layout` output of ISO 32000-1 (PDF32000_2008.pdf); Annex D is the source of the
Standard, MacRoman, WinAnsi, PDFDoc and MacExpert encodings.

Outputs (all deflate-compressed, raw deflate streams, no zlib header):
  afm-<FontName>.bin       AFM text without kerning/composite sections, with a conversion note
  glyphlist.bin            AGL "name;XXXX[ XXXX...]" lines
  zapfdingbats.bin         ZapfDingbats names, same format
  encodings.bin            "Encoding<TAB>code<TAB>glyphname" lines
  cmaps.bin                directory + one deflated binary blob per predefined CMap (format in CMapBinary below)
  cid2uni-<Ordering>.bin   CID -> code point, three byte planes (high, middle, low), 0 = unmapped
The output is deterministic.
"""
import os
import re
import struct
import sys
import zlib

import cffdata

CORE14 = ["Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
          "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
          "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic", "Symbol", "ZapfDingbats"]

ORDERINGS = {
    # ordering -> (directory, CMaps used to invert CID -> Unicode, in priority order)
    "Japan1": ("Adobe-Japan1-7", ["UniJIS-UTF32-H", "UniJIS2004-UTF32-H", "UniJISX0213-UTF32-H"]),
    "GB1": ("Adobe-GB1-6", ["UniGB-UTF32-H"]),
    "CNS1": ("Adobe-CNS1-7", ["UniCNS-UTF32-H"]),
    "Korea1": ("Adobe-Korea1-2", ["UniKS-UTF32-H"]),
    "KR": ("Adobe-KR-9", ["UniAKR-UTF32-H"]),
}
CMAP_DIRS = ["Adobe-Japan1-7", "Adobe-GB1-6", "Adobe-CNS1-7", "Adobe-Korea1-2", "Adobe-KR-9", "Adobe-Identity-0"]

NOTE = ("Comment Converted for use in OnePDF: the KernData and Composites sections were removed and the file is stored "
        "compressed. This file is derived from Adobe's Core 14 AFM file; see THIRD-PARTY-NOTICES.md for Adobe's terms.\n")


def deflate(data: bytes) -> bytes:
    c = zlib.compressobj(9, zlib.DEFLATED, -15)
    return c.compress(data) + c.flush()


def write(out, name, data):
    with open(os.path.join(out, name), "wb") as f:
        f.write(data)
    print(f"{name}: {len(data)} bytes")


def gen_afm(up, out):
    for name in CORE14:
        text = open(os.path.join(up, "afm", name + ".afm"), encoding="latin-1").read().replace("\r\n", "\n").replace("\r", "\n")
        # Drop the sections the library does not use (kerning pairs and composite characters).
        text = re.sub(r"StartKernData.*?EndKernData\n", "", text, flags=re.S)
        text = re.sub(r"StartComposites.*?EndComposites\n", "", text, flags=re.S)
        lines = text.split("\n")
        lines.insert(1, NOTE.rstrip("\n"))
        write(out, f"afm-{name}.bin", deflate(("\n".join(lines)).encode("latin-1")))


def gen_agl(up, out):
    for src, dst in (("glyphlist.txt", "glyphlist.bin"), ("zapfdingbats.txt", "zapfdingbats.bin")):
        keep = [l for l in open(os.path.join(up, "agl", src), encoding="ascii").read().split("\n") if l and not l.startswith("#")]
        write(out, dst, deflate(("\n".join(keep) + "\n").encode("ascii")))


def parse_afm_encoding(up, font):
    enc = {}
    for line in open(os.path.join(up, "afm", font + ".afm"), encoding="latin-1"):
        m = re.match(r"C\s+(-?\d+)\s*;.*?N\s+(\S+)\s*;", line)
        if m and int(m.group(1)) >= 0:
            enc[int(m.group(1))] = m.group(2)
    return enc


def gen_encodings(up, spec, out):
    text = open(spec, encoding="utf-8").read()
    start = text.index("D.2 Latin Character Set and Encodings")
    mid = text.index("D.3 PDFDocEncoding Character Set", start) if "D.3 PDFDocEncoding Character Set" in text[start:] else None
    end4 = text.index("D.4 Expert Set and MacExpertEncoding", start)
    code = r"(—|[0-7]{3})"
    row = re.compile(r"(?<![\w])([A-Za-z][A-Za-z0-9]*)(?: [1-9])?[ \t]+" + r"[ \t]+".join([code] * 4) + r"(?![\w])")
    std, mac, win, pdf = {}, {}, {}, {}
    # The Latin table ends where D.3 begins; find D.3 after the table's last page.
    d3 = text.find("D.3 PDFDocEncoding Character Set", start)
    latin = text[start:d3 if d3 > 0 else end4]
    for m in row.finditer(latin):
        name = m.group(1)
        for table, v in zip((std, mac, win, pdf), m.groups()[1:]):
            if v != "—":
                table[int(v, 8)] = name
    # Footnotes of Table D.2: bullet fills unused WinAnsi codes above 040; space and hyphen duplicates.
    for c in range(0x21, 0x100):
        win.setdefault(c, "bullet")
    win[0xA0] = "space"
    win[0xAD] = "hyphen"
    mac[0xCA] = "space"
    win[0x20] = "space"
    # Expert set (Table D.4): rows are "name code" pairs, two per line.
    exp = {}
    erow = re.compile(r"(?<![\w])([A-Za-z][A-Za-z0-9]*)[ \t]{2,}([0-7]{3})(?![\w0-7])")
    end5 = text.index("D.5 Symbol Set and Encoding", end4)
    for m in erow.finditer(text[end4:end5]):
        code = int(m.group(2), 8)
        if code <= 255 and m.group(1) != "reserved":
            exp[code] = m.group(1)
    exp[0x20] = "space"
    encs = [("StandardEncoding", std), ("MacRomanEncoding", mac), ("WinAnsiEncoding", win), ("PDFDocEncoding", pdf),
            ("MacExpertEncoding", exp), ("SymbolEncoding", parse_afm_encoding(up, "Symbol")),
            ("ZapfDingbatsEncoding", parse_afm_encoding(up, "ZapfDingbats"))]
    # StandardEncoding must agree with Adobe's AFM for Helvetica.
    afm_std = parse_afm_encoding(up, "Helvetica")
    if afm_std != std:
        diff = {k: (afm_std.get(k), std.get(k)) for k in set(afm_std) | set(std) if afm_std.get(k) != std.get(k)}
        raise SystemExit("StandardEncoding mismatch with AFM: %r" % diff)
    lines = []
    for name, table in encs:
        print(name, len(table))
        for c in sorted(table):
            lines.append(f"{name}\t{c}\t{table[c]}")
    write(out, "encodings.bin", deflate(("\n".join(lines) + "\n").encode("ascii")))


# --- CMaps -----------------------------------------------------------------------------------------------------
#
# Binary form of one CMap (before deflate); integers are unsigned LEB128 varints unless noted:
#   'C' 'M' version(1)  wmode(1 byte)  nameLen(1) useCMapName  nCodespaces
#   per codespace: byteLength(1 byte), lo, hi                                    (code values as integers)
#   nRanges, then per range, grouped by byteLength and sorted by lo:
#       byteLength(1 byte), gap = lo - previous lo (0 on a new byteLength), span = hi - lo, zigzag(cid - (prevCid + prevSpan + 1))

TOKEN = re.compile(r"<([0-9A-Fa-f\s]*)>|/(\S+)|(\[|\])|([^\s<>/\[\]]+)")


def varint(n):
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def zigzag(n):
    return (n << 1) if n >= 0 else ((-n << 1) - 1)


def parse_cmap(path):
    text = open(path, encoding="latin-1").read()
    text = re.sub(r"%[^\n]*", "", text)
    wmode, use = 0, None
    spaces, ranges = [], []  # (len, lo, hi), (len, lo, hi, cid)
    toks = [m for m in TOKEN.findall(text)]
    i = 0
    stack = []
    mode = None
    while i < len(toks):
        hexs, name, br, word = toks[i]
        i += 1
        if word in ("begincodespacerange", "begincidrange", "begincidchar", "beginnotdefrange", "beginnotdefchar"):
            mode = word
            stack = []
            continue
        if word and word.startswith("end") and word != "endcmap":
            mode = None
            continue
        if word == "usecmap":
            use = stack_name
            continue
        if word == "def" and stack_name == "WMode":
            wmode = last_int
        if name:
            stack_name = name
        if mode is None:
            if word and re.fullmatch(r"-?\d+", word):
                last_int = int(word)
            continue
        if mode == "begincodespacerange":
            if hexs:
                stack.append(hexs)
                if len(stack) == 2:
                    a, b = stack
                    a, b = re.sub(r"\s", "", a), re.sub(r"\s", "", b)
                    spaces.append((len(a) // 2, int(a, 16), int(b, 16)))
                    stack = []
        elif mode == "begincidrange":
            if hexs:
                stack.append(re.sub(r"\s", "", hexs))
            elif word and re.fullmatch(r"\d+", word):
                a, b = stack
                ranges.append((len(a) // 2, int(a, 16), int(b, 16), int(word)))
                stack = []
        elif mode == "begincidchar":
            if hexs:
                stack.append(re.sub(r"\s", "", hexs))
            elif word and re.fullmatch(r"\d+", word):
                (a,) = stack
                ranges.append((len(a) // 2, int(a, 16), int(a, 16), int(word)))
                stack = []
    return wmode, use, spaces, ranges


last_int = 0
stack_name = ""


def encode_cmap(wmode, use, spaces, ranges):
    out = bytearray(b"CM\x01")
    out.append(wmode)
    u = (use or "").encode("ascii")
    out.append(len(u))
    out += u
    out += varint(len(spaces))
    for ln, lo, hi in spaces:
        out.append(ln)
        out += varint(lo) + varint(hi)
    ranges = sorted(ranges, key=lambda r: (r[0], r[1]))
    out += varint(len(ranges))
    prev_len, prev_lo, prev_cid_end = -1, 0, 0
    for ln, lo, hi, cid in ranges:
        if ln != prev_len:
            prev_len, prev_lo = ln, 0
        out.append(ln)
        out += varint(lo - prev_lo) + varint(hi - lo) + varint(zigzag(cid - prev_cid_end))
        prev_lo = lo
        prev_cid_end = cid + (hi - lo) + 1
    return bytes(out)


def collect_cmaps(up):
    found = {}
    for d in CMAP_DIRS:
        base = os.path.join(up, "cmr", d, "CMap")
        for n in sorted(os.listdir(base)):
            if n in found:
                raise SystemExit("duplicate CMap name " + n)
            found[n] = os.path.join(base, n)
    return found


def gen_cmaps(up, out):
    found = collect_cmaps(up)
    blobs = []
    for n in sorted(found):
        wmode, use, spaces, ranges = parse_cmap(found[n])
        raw = encode_cmap(wmode, use, spaces, ranges)
        blobs.append((n, len(raw), deflate(raw), len(ranges)))
    # Directory: u32 count; per entry nameLen(1) name, u32 offset, u32 compressed length, u32 raw length (big endian).
    # Offsets are relative to the end of the directory.
    d = bytearray(struct.pack(">I", len(blobs)))
    off = 0
    for n, rawlen, comp, _ in blobs:
        d += bytes([len(n)]) + n.encode("ascii") + struct.pack(">III", off, len(comp), rawlen)
        off += len(comp)
    write(out, "cmaps.bin", bytes(d) + b"".join(b[2] for b in blobs))
    print("cmaps:", len(blobs), "total ranges", sum(b[3] for b in blobs))


def cp_rank(cp):
    """Orders candidate code points for one CID: standard characters before radicals, compatibility forms and private use."""
    odd = (0x2E80 <= cp <= 0x2FDF) or (0xF900 <= cp <= 0xFAFF) or (0x2F800 <= cp <= 0x2FA1F) or (0xE000 <= cp <= 0xF8FF) or (0x3200 <= cp <= 0x33FF)
    return (1 if odd else 0, cp)


def gen_cid2uni(up, out):
    for ordering, (d, names) in ORDERINGS.items():
        table = {}
        for n in names:
            _, _, _, ranges = parse_cmap(os.path.join(up, "cmr", d, "CMap", n))
            local = {}
            for ln, lo, hi, cid in ranges:
                for k in range(hi - lo + 1):
                    c, cp = cid + k, lo + k
                    if c not in local or cp_rank(cp) < cp_rank(local[c]):
                        local[c] = cp
            for c, cp in local.items():
                table.setdefault(c, cp)
        count = max(table) + 1
        planes = [bytearray(count), bytearray(count), bytearray(count)]
        for c, cp in table.items():
            planes[0][c] = (cp >> 16) & 0xFF
            planes[1][c] = (cp >> 8) & 0xFF
            planes[2][c] = cp & 0xFF
        write(out, f"cid2uni-{ordering}.bin", deflate(struct.pack(">I", count) + bytes(planes[0] + planes[1] + planes[2])))


def main():
    up, spec, out = sys.argv[1:4]
    os.makedirs(out, exist_ok=True)
    for f in os.listdir(out):
        if f.endswith(".bin"):
            os.remove(os.path.join(out, f))
    gen_afm(up, out)
    gen_agl(up, out)
    gen_encodings(up, spec, out)
    gen_cmaps(up, out)
    gen_cid2uni(up, out)
    cffdata.generate(up, out, write, deflate)
    cffdata.generate_mac_glyphs(up, out, write, deflate)


if __name__ == "__main__":
    main()
