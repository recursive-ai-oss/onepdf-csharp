#!/usr/bin/env python3
"""Write testdata/MANIFEST.csv for the redistributed (third-party) files and verify the testdata tree.

Usage: build_manifest.py [--check]
Covers testdata/pdf20examples, testdata/verapdf and testdata/govdocs. testdata/generated and testdata/repros hold our own
files and is described by its own README and truth sidecars. With --check nothing is written; the exit code
is non-zero when a file under testdata/ is neither in the manifest, nor generated, nor a documentation file,
or when a hash or license value is wrong.
"""
import csv, hashlib, json, os, sys
from urllib.parse import quote

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
TD = os.path.join(ROOT, "testdata")

VERA_COMMIT = json.load(open(os.path.join(HERE, "local-corpus.lock.json")))["verapdf"]["commit"]
PDF20_COMMIT = "c20f2c17bfcc4baab7cfe62e70fae64caf14d5fa"
PDF20_DATE = "2025-01-13"
ALLOWED = {"CC-BY-SA-4.0": "pdf20examples", "CC-BY-4.0": "verapdf", "US-Government-Work": "govdocs"}
HEADER = ["path", "source_url", "upstream_ref", "license", "sha256", "sha1", "attribution"]


def digest(p, a):
    h = hashlib.new(a)
    h.update(open(p, "rb").read())
    return h.hexdigest()


def rows():
    out = []
    d = os.path.join(TD, "pdf20examples")
    for f in sorted(os.listdir(d)):
        if f.lower().endswith(".pdf"):
            rel = f"testdata/pdf20examples/{f}"
            out.append([rel, "https://github.com/pdf-association/pdf20examples/blob/%s/%s" % (PDF20_COMMIT, quote(f)),
                        f"commit {PDF20_COMMIT} ({PDF20_DATE})", "CC-BY-SA-4.0", digest(os.path.join(TD, "pdf20examples", f), "sha256"), "",
                        "PDF Association, pdf20examples, CC BY-SA 4.0 (https://creativecommons.org/licenses/by-sa/4.0/); unmodified"])
    base = os.path.join(TD, "verapdf")
    for dp, _, fs in os.walk(base):
        for f in fs:
            if f.lower().endswith(".pdf"):
                p = os.path.join(dp, f)
                sub = os.path.relpath(p, base).replace(os.sep, "/")
                out.append([f"testdata/verapdf/{sub}", "https://github.com/veraPDF/veraPDF-corpus/blob/%s/%s" % (VERA_COMMIT, quote(sub)),
                            f"commit {VERA_COMMIT} (2026-09-16)", "CC-BY-4.0", digest(p, "sha256"), "",
                            "veraPDF Consortium, veraPDF corpus, CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/); unmodified"])
    sel = json.load(open(os.path.join(HERE, "govdocs-selection.json")))
    for r in sel:
        p = os.path.join(ROOT, r["path"])
        out.append([r["path"], r["url"], "GovDocs1 (digitalcorpora.org), crawled 2009", "US-Government-Work", digest(p, "sha256"), digest(p, "sha1"),
                    "Work of the United States government (17 U.S.C. 105); original URL in source_url; unmodified"])
    out.sort()
    return out


def main():
    check = "--check" in sys.argv
    r = rows()
    manifest = os.path.join(TD, "MANIFEST.csv")
    bad = 0
    if check:
        have = list(csv.reader(open(manifest, newline="", encoding="utf-8")))
        if have[0] != HEADER or have[1:] != r:
            print("MANIFEST.csv is stale; run build_manifest.py"); bad = 1
        for row in r:
            if row[3] not in ALLOWED or ALLOWED[row[3]] not in row[0]:
                print("license/path mismatch:", row[0], row[3]); bad = 1
            if row[3] == "US-Government-Work":
                if not row[1].startswith("http") or len(row[5]) != 40:
                    print("govdocs row without url/sha1:", row[0]); bad = 1
        listed = {x[0] for x in r}
        tracked = [os.path.relpath(os.path.join(dp, f), ROOT).replace(os.sep, "/") for dp, _, fs in os.walk(TD) for f in fs]
        for t in tracked:
            if t in listed or t.startswith(("testdata/generated/", "testdata/repros/")) or t.endswith(("MANIFEST.csv", "README.md", "LICENSE.md", "NOTICE.md")):
                continue
            print("not in manifest:", t); bad = 1
        print(f"{len(r)} manifest rows; problems: {bad}")
        sys.exit(bad)
    with open(manifest, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(HEADER)
        w.writerows(r)
    print(len(r), "rows written")


if __name__ == "__main__":
    main()
