#!/usr/bin/env python3
"""Select a feature-diverse subset of the veraPDF corpus and copy it, unmodified, into testdata/verapdf/.

Usage: select_verapdf.py CORPUS_DIR [--dry]
CORPUS_DIR is a checkout of https://github.com/veraPDF/veraPDF-corpus (see local-corpus.lock.json
for the commit this was built from). Selection is deterministic: all ISO 32000-1 and -2 files, then a
greedy cover of the byte-level feature markers below (smallest file first among ties), then a fill
from each remaining folder so every folder is represented. Files over MAX_BYTES are never taken.
"""
import hashlib, os, re, shutil, sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "testdata", "verapdf")
MAX_BYTES = 300_000
TARGET = 110

FEATURES = {
    "objstm": rb"/Type\s*/ObjStm", "xrefstm": rb"/Type\s*/XRef", "encrypt": rb"/Encrypt\b", "linearized": rb"/Linearized",
    "prev": rb"/Prev\s+\d", "hybrid": rb"/XRefStm", "type3": rb"/Subtype\s*/Type3", "type1": rb"/Subtype\s*/Type1\b",
    "truetype": rb"/Subtype\s*/TrueType", "cidtype0": rb"/CIDFontType0", "cidtype2": rb"/CIDFontType2", "type0": rb"/Subtype\s*/Type0",
    "fontfile": rb"/FontFile\b", "fontfile2": rb"/FontFile2", "fontfile3": rb"/FontFile3", "tounicode": rb"/ToUnicode",
    "cmapname": rb"/CMapName|/UseCMap", "lzw": rb"/LZWDecode|/LZW\b", "ascii85": rb"/ASCII85Decode|/A85\b", "asciihex": rb"/ASCIIHexDecode",
    "runlength": rb"/RunLengthDecode", "ccitt": rb"/CCITTFaxDecode", "jbig2": rb"/JBIG2Decode", "jpx": rb"/JPXDecode", "dct": rb"/DCTDecode",
    "predictor": rb"/Predictor\s+\d", "smask": rb"/SMask\b", "imagemask": rb"/ImageMask\s+true", "decode": rb"/Decode\s*\[",
    "indexed": rb"/Indexed", "separation": rb"/Separation", "devicen": rb"/DeviceN", "icc": rb"/ICCBased", "lab": rb"/Lab\b", "calrgb": rb"/CalRGB|/CalGray",
    "shading": rb"/ShadingType", "pattern": rb"/PatternType", "tiling": rb"/PatternType\s+1", "function": rb"/FunctionType\s+\d",
    "blend": rb"/BM\s*/(?!Normal)", "group": rb"/S\s*/Transparency", "extgstate": rb"/ExtGState", "oc": rb"/OCProperties", "annots": rb"/Annots",
    "widget": rb"/Subtype\s*/Widget", "acroform": rb"/AcroForm", "xfa": rb"/XFA", "link": rb"/Subtype\s*/Link", "outlines": rb"/Outlines",
    "pagelabels": rb"/PageLabels", "struct": rb"/StructTreeRoot", "markinfo": rb"/MarkInfo", "xmp": rb"/Type\s*/Metadata", "embeddedfile": rb"/EmbeddedFile",
    "af": rb"/AFRelationship", "outputintent": rb"/OutputIntents", "js": rb"/JavaScript|/JS\b", "launch": rb"/Launch", "uri": rb"/URI\b",
    "names": rb"/Names\b", "dests": rb"/Dests\b", "inlineimage": rb"\bBI\b.*?\bID\b", "form_xobj": rb"/Subtype\s*/Form", "rotate": rb"/Rotate\s+(90|180|270)",
    "userunit": rb"/UserUnit", "cropbox": rb"/CropBox", "differences": rb"/Differences", "winansi": rb"/WinAnsiEncoding", "macroman": rb"/MacRomanEncoding",
    "identityh": rb"/Identity-H", "vertical": rb"/Identity-V|-V\b", "w2": rb"/W2\b|/DW2\b", "cidtogid": rb"/CIDToGIDMap", "symbolic": rb"/Flags\s+(4|6\d|12\d)\b",
    "stdfont": rb"/BaseFont\s*/(Helvetica|Times|Courier|Symbol|ZapfDingbats)", "trapped": rb"/Trapped", "tabs": rb"/Tabs\b", "lang": rb"/Lang\b",
    "textrender": rb"\b[1-7] Tr\b", "clip": rb"\bW\*? n\b", "dash": rb"\bd\b", "marked": rb"\bBDC\b|\bBMC\b", "sigfield": rb"/Sig\b|/ByteRange",
    "crlf": rb"\r\n", "cr_only": rb"[^\n]\r[^\n]", "version2": rb"%PDF-2\.0", "version10": rb"%PDF-1\.[0-3]",
}
FEATURES = {k: re.compile(v, re.S) for k, v in FEATURES.items()}


def clean(out):
    """Remove earlier selected PDFs (keeps NOTICE.md)."""
    for d, _, fs in os.walk(out, topdown=False):
        for f in fs:
            if f.lower().endswith(".pdf"):
                os.remove(os.path.join(d, f))
        if d != out and not os.listdir(d):
            os.rmdir(d)


def features(path):
    data = open(path, "rb").read()
    return {k for k, rx in FEATURES.items() if rx.search(data)}


def main():
    corpus = sys.argv[1]
    dry = "--dry" in sys.argv
    files = []
    for d, _, fs in os.walk(corpus):
        if ".git" in d.split(os.sep):
            continue
        for f in fs:
            if f.lower().endswith(".pdf"):
                p = os.path.join(d, f)
                files.append((os.path.relpath(p, corpus), os.path.getsize(p)))
    files.sort()
    feat = {rel: features(os.path.join(corpus, rel)) for rel, sz in files if sz <= MAX_BYTES}
    size = dict(files)
    chosen = []
    covered = set()
    for rel in feat:
        if rel.startswith(("ISO 32000-1/", "ISO 32000-2/")):
            chosen.append(rel); covered |= feat[rel]
    # greedy: most new features per byte class, ties by path
    while len(chosen) < TARGET * 0.8:
        best = max((r for r in feat if r not in chosen), key=lambda r: (len(feat[r] - covered), -size[r], r), default=None)
        if best is None or not (feat[best] - covered):
            break
        chosen.append(best); covered |= feat[best]
    # representation: one small and one mid-size file from each folder not yet present, then fill by rarity
    rarity = {k: sum(k in f for f in feat.values()) for k in FEATURES}
    folders = sorted({r.split("/")[0] for r in feat})
    for fo in folders:
        have = [r for r in chosen if r.startswith(fo + "/")]
        pool = sorted((r for r in feat if r.startswith(fo + "/") and r not in chosen),
                      key=lambda r: (-sum(1 / rarity[k] for k in feat[r]), r))
        for r in pool[: max(0, 3 - len(have))]:
            chosen.append(r)
    pool = sorted((r for r in feat if r not in chosen), key=lambda r: (-sum(1 / max(1, rarity[k]) for k in feat[r]), r))
    for r in pool:
        if len(chosen) >= TARGET:
            break
        chosen.append(r)
    chosen.sort()
    total = sum(size[r] for r in chosen)
    missing = sorted(set(FEATURES) - covered)
    print(f"{len(chosen)} files, {total/1e6:.2f} MB; features covered {len(covered)}/{len(FEATURES)}; uncovered in corpus: {missing}")
    if dry:
        return
    clean(OUT)
    for r in chosen:
        dst = os.path.join(OUT, r)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copyfile(os.path.join(corpus, r), dst)


if __name__ == "__main__":
    main()
