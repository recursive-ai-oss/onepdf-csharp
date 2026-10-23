#!/usr/bin/env bash
# Peak resident memory per gate row: one fresh process per (engine, row, input, degree) under /usr/bin/time -v.
#
#   bench/rss.sh <out.csv>
#
# Needs the benchmark project built in Release (BENCH_DLL, or under ONEPDF_ARTIFACTS), ONEPDF_BENCH_DATA (see bench/README.md)
# and ONEPDF_TESTDATA. Rows that are not available yet (OnePDF text extraction before it is wired in) print "pending" and are skipped.
# The figure includes the .NET runtime itself, the same for every engine; each row is one cold run, so compare engines on the same row.
set -uo pipefail
out="${1:?usage: rss.sh <out.csv>}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dll="${BENCH_DLL:-${ONEPDF_ARTIFACTS:?set ONEPDF_ARTIFACTS or BENCH_DLL}/bin/OnePdf.Benchmarks/release/OnePdf.Benchmarks.dll}"
: "${ONEPDF_BENCH_DATA:?set ONEPDF_BENCH_DATA}"
export ONEPDF_TESTDATA="${ONEPDF_TESTDATA:-$root/testdata}"
tmpdir="${TMPDIR:-${ONEPDF_ARTIFACTS:-}}"
[ -n "$tmpdir" ] && [ -d "$tmpdir" ] || { echo "rss.sh: set TMPDIR (or ONEPDF_ARTIFACTS) to an existing scratch directory" >&2; exit 1; }
tmpf="$(mktemp "$tmpdir/rss.XXXXXX")" || exit 1
trap 'rm -f "$tmpf"' EXIT

echo "engine,row,input,degree,peak_rss_kb,wall_ms" >"$out"
skipped=0
run() { # engine row input degree
  local line kb
  line=$(/usr/bin/time -v -o "$tmpf" dotnet "$dll" rss "$1" "$2" "$3" "$4" 2>/dev/null | tail -1)
  case "$line" in
    ok*) kb=$(awk -F: '/Maximum resident set size/ {gsub(/ /,"",$2); print $2}' "$tmpf")
         echo "$1,$2,$3,$4,$kb,${line##*wall_ms=}" | tee -a "$out" ;;
    *) skipped=$((skipped + 1)); echo "skipped: $1 $2 $3 $4 (${line:-no output})" ;;
  esac
}

for e in onepdf pdfpig; do
  for f in small pages-1000 broken; do run $e open $f 1; done
  for f in small-metadata born-300; do run $e metadata $f 1; done
  run $e encrypted-open aes256 1
  for d in 1 default; do
    for f in born-300 scan-ocr cjk; do run $e text $f $d; done
    for f in born-300 text-page; do run $e words $f $d; done
    run $e encrypted-text aes256 $d
  done
done
for f in vector-page text-page; do run onepdf interpret $f 1; done
for d in 1 default; do run onepdf text-glyphs born-300 $d; done   # information: glyphs kept in the result
for e in pdfium mutool; do for f in born-300 scan-ocr cjk; do run $e text $f 1; done; done

# Rows that all fail mean a broken setup (missing build, data or time), not a result.
if [ "$(wc -l <"$out")" -le 1 ]; then echo "rss.sh: no row produced a result ($skipped skipped); check BENCH_DLL, ONEPDF_BENCH_DATA and /usr/bin/time" >&2; exit 1; fi
