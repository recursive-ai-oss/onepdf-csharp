#!/usr/bin/env python3
"""Fetch the local-only part of the real-world extraction-quality set and verify the committed part.

Usage: ONEPDF_QUALITY_CACHE=/some/dir fetch_realworld.py [--check]

Committed files (tools/corpus/realworld-selection.json) are verified against their SHA-256 under testdata/realworld/pdf.
Local-only files (tools/corpus/realworld-local.lock.json) are downloaded from their pinned URLs into
$ONEPDF_QUALITY_CACHE/pdf/<name>.pdf and verified against the pinned SHA-256. Files already present with the
right hash are left alone, so the script is safe to run again. With --check nothing is downloaded: the exit
code says whether everything is in place.

The reference text of local-only files lives next to them, in $ONEPDF_QUALITY_CACHE/truth/<name>.json; it is
derived from material we may not redistribute and is not recreated by this script. The lock file records its
SHA-256 so a restored copy can be checked.
"""
import hashlib, json, os, sys, time, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def download(url, dest):
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 (corpus fetch)"})
    for attempt in range(3):
        try:
            data = urllib.request.urlopen(req, timeout=120).read()
            tmp = dest + ".part"
            with open(tmp, "wb") as f:
                f.write(data)
            os.replace(tmp, dest)
            return True
        except Exception as e:  # network errors are retried, then reported
            print(f"  attempt {attempt + 1} failed: {e}")
            time.sleep(3 * (attempt + 1))
    return False


def main():
    check = "--check" in sys.argv
    bad = 0
    committed = json.load(open(os.path.join(HERE, "realworld-selection.json")))
    for r in committed:
        p = os.path.join(ROOT, r["path"])
        if not os.path.exists(p) or sha256(p) != r["sha256"]:
            print("committed file missing or changed:", r["path"])
            bad += 1
    print(f"{len(committed)} committed files checked, {bad} problem(s)")

    cache = os.environ.get("ONEPDF_QUALITY_CACHE")
    if not cache:
        print("ONEPDF_QUALITY_CACHE is not set; skipping the local-only files")
        sys.exit(1 if bad else 0)
    lock = json.load(open(os.path.join(HERE, "realworld-local.lock.json")))
    os.makedirs(os.path.join(cache, "pdf"), exist_ok=True)
    os.makedirs(os.path.join(cache, "truth"), exist_ok=True)
    fetched = 0
    for r in lock:
        dest = os.path.join(cache, "pdf", r["name"] + ".pdf")
        ok = os.path.exists(dest) and sha256(dest) == r["sha256"]
        if not ok and not check:
            print("fetching", r["name"])
            if download(r["url"], dest):
                fetched += 1
                ok = sha256(dest) == r["sha256"]
                if not ok:
                    print("  hash differs from the lock file; the source has changed, removing", dest)
                    os.remove(dest)
        if not ok:
            print("local-only file missing or wrong:", r["name"])
            bad += 1
        truth = os.path.join(cache, "truth", r["name"] + ".json")
        if not os.path.exists(truth):
            print("reference text not present (it is kept locally, not fetched):", r["name"])
        elif sha256(truth) != r["truth_sha256"]:
            print("reference text differs from the lock file's hash (edited since?):", r["name"])
    print(f"{len(lock)} local-only files, {fetched} fetched, {bad} problem(s) in total")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
