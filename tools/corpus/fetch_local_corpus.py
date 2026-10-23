#!/usr/bin/env python3
"""Fetch the local-only crash/hang corpus. Nothing it downloads is ever committed.

Usage: fetch_local_corpus.py CACHE_DIR [--max-gb N]    (or set ONEPDF_CORPUS_DIR)

Layout under CACHE_DIR:
  govdocs1/*.pdf        PDFs extracted from pinned GovDocs1 zips (SHA-1 from the published list)
  safedocs/*.pdf        PDFs extracted from pinned SafeDocs CC-MAIN-2021-31 zips (SHA-256 pinned in the lock file)
  verapdf-corpus/       the veraPDF corpus at a pinned commit
  .done/<name>          marker per finished zip, so a re-run downloads nothing
  COUNTS.txt            file counts and size

Pins live in local-corpus.lock.json next to this script. A SafeDocs entry with a null
sha256 is trusted on first download: the hash is computed and written back to the lock,
and every later fetch must match it. The zips are deleted after extraction.
Requires: python3, curl, and git (or tar, which takes the pinned veraPDF commit as a tarball).
"""
import hashlib, json, os, shutil, subprocess, sys, time, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
LOCK = os.path.join(HERE, "local-corpus.lock.json")


def run(*a):
    subprocess.run(a, check=True)


def digest(path, algo):
    h = hashlib.new(algo)
    with open(path, "rb") as f:
        while chunk := f.read(1 << 20):
            h.update(chunk)
    return h.hexdigest()


def tree_size(root):
    return sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(root) for f in fs)


def fetch_zip(entry, kind, root, lock, cap):
    done = os.path.join(root, ".done", f"{kind}-{entry['name']}")
    if os.path.exists(done):
        return
    if tree_size(root) + entry["size"] * 2 > cap:
        sys.exit(f"size cap would be exceeded by {entry['name']}; raise --max-gb")
    zpath = os.path.join(root, "tmp", f"{kind}-{entry['name']}")
    os.makedirs(os.path.dirname(zpath), exist_ok=True)
    print("fetch", entry["url"], flush=True)
    for attempt in range(8):  # resume after connection resets
        if subprocess.run(["curl", "-fsSL", "--retry", "5", "--retry-delay", "10", "-C", "-", "-o", zpath, entry["url"]]).returncode == 0:
            break
        time.sleep(15)
    else:
        sys.exit(f"download failed: {entry['url']}")
    if os.path.getsize(zpath) != entry["size"]:
        sys.exit(f"{entry['name']}: size {os.path.getsize(zpath)} != pinned {entry['size']}")
    if "sha1" in entry:
        if digest(zpath, "sha1") != entry["sha1"]:
            sys.exit(f"{entry['name']}: SHA-1 mismatch")
    else:
        got = digest(zpath, "sha256")
        if entry.get("sha256") is None:
            entry["sha256"] = got  # trust on first use, then pinned
            with open(LOCK, "w") as f:
                json.dump(lock, f, indent=1)
                f.write("\n")
        elif entry["sha256"] != got:
            sys.exit(f"{entry['name']}: SHA-256 mismatch")
    out = os.path.join(root, kind)
    os.makedirs(out, exist_ok=True)
    n = 0
    with zipfile.ZipFile(zpath) as z:
        for info in z.infolist():
            base = os.path.basename(info.filename)
            if info.is_dir() or not base.lower().endswith(".pdf"):
                continue
            # flat extraction by basename; names are unique inside each corpus
            with z.open(info) as src, open(os.path.join(out, base), "wb") as dst:
                shutil.copyfileobj(src, dst)
            n += 1
    os.remove(zpath)
    os.makedirs(os.path.dirname(done), exist_ok=True)
    with open(done, "w") as f:
        f.write(f"{n}\n")
    print(f"  {n} pdfs from {entry['name']}", flush=True)


def main():
    args = sys.argv[1:]
    cap_gb = 60.0
    if "--max-gb" in args:
        i = args.index("--max-gb")
        cap_gb = float(args[i + 1])
        del args[i:i + 2]
    root = args[0] if args else os.environ.get("ONEPDF_CORPUS_DIR")
    if not root:
        sys.exit(__doc__)
    os.makedirs(root, exist_ok=True)
    lock = json.load(open(LOCK))
    cap = int(cap_gb * 1e9)

    vp = lock["verapdf"]
    vdir = os.path.join(root, "verapdf-corpus")
    if shutil.which("git"):
        if not os.path.isdir(os.path.join(vdir, ".git")):
            run("git", "clone", "--quiet", vp["url"], vdir)
        head = subprocess.run(["git", "-C", vdir, "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
        if head != vp["commit"]:
            run("git", "-C", vdir, "fetch", "--quiet", "origin", vp["commit"])
            run("git", "-C", vdir, "checkout", "--quiet", vp["commit"])
    elif not os.path.exists(os.path.join(vdir, ".commit")):
        # No git on this machine: take the pinned commit as a tarball instead.
        os.makedirs(vdir, exist_ok=True)
        tgz = os.path.join(root, "verapdf.tar.gz")
        run("curl", "-fsSL", "--retry", "5", "-o", tgz, vp["url"].removesuffix(".git") + "/archive/" + vp["commit"] + ".tar.gz")
        run("tar", "-xzf", tgz, "-C", vdir, "--strip-components=1")
        os.remove(tgz)
        with open(os.path.join(vdir, ".commit"), "w") as f:
            f.write(vp["commit"] + "\n")

    for e in lock["govdocs1"]:
        fetch_zip(e, "govdocs1", root, lock, cap)
    for e in lock["safedocs"]:
        fetch_zip(e, "safedocs", root, lock, cap)

    shutil.rmtree(os.path.join(root, "tmp"), ignore_errors=True)
    counts = {}
    for k in ("govdocs1", "safedocs"):
        d = os.path.join(root, k)
        counts[k] = len(os.listdir(d)) if os.path.isdir(d) else 0
    counts["verapdf"] = sum(f.lower().endswith(".pdf") for _, _, fs in os.walk(vdir) for f in fs)
    total = tree_size(root)
    with open(os.path.join(root, "COUNTS.txt"), "w") as f:
        for k, v in counts.items():
            f.write(f"{k}: {v} pdfs\n")
        f.write(f"total bytes: {total}\n")
    print(open(os.path.join(root, "COUNTS.txt")).read())


if __name__ == "__main__":
    main()
