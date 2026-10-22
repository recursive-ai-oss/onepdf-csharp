#!/usr/bin/env python3
"""Select ~30 GovDocs1 PDFs whose original URL is on a US federal host and copy them into testdata/govdocs/.

Usage: select_govdocs.py CORPUS_DIR
Needs CORPUS_DIR/govdocs1/*.pdf (from fetch_local_corpus.py) and CORPUS_DIR/govdocs-meta/dump.sql
(the GovDocs1 database dump published next to the corpus; it maps document ids to the URLs they were crawled from).
Only files whose URL host is under a listed federal domain are eligible, and the file's SHA-1 must match
the dump. Files whose URL cannot be established are skipped. Writes tools/corpus/govdocs-selection.json.
"""
import hashlib, json, os, re, shutil, sys
from urllib.parse import urlparse

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from select_verapdf import features, clean  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "testdata", "govdocs")
COUNT = 30
MAX_BYTES = 250_000
MIN_BYTES = 3_000
PER_DOMAIN = 3

# Federal agencies (US government works, 17 U.S.C. 105). State and local .gov sites are not listed on purpose.
FEDERAL = """nasa.gov noaa.gov nist.gov usgs.gov epa.gov fda.gov nih.gov cdc.gov census.gov gao.gov energy.gov doe.gov usda.gov nps.gov
fws.gov fcc.gov ftc.gov sec.gov loc.gov nsf.gov nrc.gov dot.gov faa.gov va.gov treasury.gov irs.gov ed.gov hhs.gov osha.gov dol.gov doi.gov
blm.gov usace.army.mil army.mil navy.mil af.mil whitehouse.gov state.gov justice.gov usdoj.gov fbi.gov nws.noaa.gov fs.fed.us fed.us ars.usda.gov
nrcs.usda.gov fsa.usda.gov cbo.gov gpo.gov archives.gov si.edu fema.gov dhs.gov uscg.mil osti.gov ornl.gov lanl.gov llnl.gov sandia.gov anl.gov
bls.gov bea.gov eia.gov ahrq.gov cms.gov samhsa.gov hud.gov sba.gov ssa.gov opm.gov gsa.gov nara.gov nlm.nih.gov usps.gov ntsb.gov nasa.gov""".split()


def federal(host):
    host = host.lower().split(":")[0]
    return any(host == d or host.endswith("." + d) for d in FEDERAL)


def main():
    root = sys.argv[1]
    dump = os.path.join(root, "govdocs-meta", "dump.sql")
    rx = re.compile(r"\((\d+),'((?:[^'\\]|\\.)*)','pdf',(?:'(?:[^'\\]|\\.)*'|NULL),(?:'(?:[^'\\]|\\.)*'|NULL),'[^']*','[^']*','[^']*','([0-9a-f]{40})'")
    meta = {}
    with open(dump, encoding="utf-8", errors="replace") as f:
        for line in f:
            if not line.startswith("INSERT INTO `govdocs`"):
                continue
            for m in rx.finditer(line):
                meta["%06d" % int(m.group(1))] = (m.group(2).replace("\\'", "'").replace("\\\\", "\\"), m.group(3))
    print(len(meta), "pdf rows in the dump")
    pdfdir = os.path.join(root, "govdocs1")
    cands = []
    for fn in sorted(os.listdir(pdfdir)):
        did = fn[:-4]
        if did not in meta:
            continue
        url, sha1 = meta[did]
        host = urlparse(url).hostname or ""
        if not federal(host):
            continue
        p = os.path.join(pdfdir, fn)
        sz = os.path.getsize(p)
        if not MIN_BYTES <= sz <= MAX_BYTES:
            continue
        if hashlib.sha1(open(p, "rb").read()).hexdigest() != sha1:
            continue
        cands.append((did, url, sha1, host, sz, features(p)))
    print(len(cands), "eligible federal files")
    chosen, covered, per = [], set(), {}
    while len(chosen) < COUNT and cands:
        def dom(h):
            return ".".join(h.split(".")[-2:])
        pool = [c for c in cands if per.get(dom(c[3]), 0) < PER_DOMAIN]
        if not pool:
            break
        best = max(pool, key=lambda c: (len(c[5] - covered), -c[4], c[0]))
        chosen.append(best); covered |= best[5]; per[dom(best[3])] = per.get(dom(best[3]), 0) + 1
        cands.remove(best)
    chosen.sort()
    clean(OUT)
    os.makedirs(OUT, exist_ok=True)
    rows = []
    for did, url, sha1, host, sz, feats in chosen:
        shutil.copyfile(os.path.join(pdfdir, did + ".pdf"), os.path.join(OUT, did + ".pdf"))
        rows.append({"path": f"testdata/govdocs/{did}.pdf", "url": url, "sha1": sha1})
    json.dump(rows, open(os.path.join(HERE, "govdocs-selection.json"), "w"), indent=1)
    print(len(rows), "selected;", len(covered), "features;", sum(c[4] for c in chosen) / 1e6, "MB")


if __name__ == "__main__":
    main()
