#!/usr/bin/env bash
# Peak resident memory and wall time per (engine, row): one fresh process per cell under /usr/bin/time -v. Each cell is one cold
# run (the wall time is for the first document, including JIT for the managed programs, measured inside the process), so compare
# engines on the same row.
#
#   bench/OnePdf.Layout.Benchmarks/rss.sh <out.csv>
#
# Needs the benchmark project built in Release (BENCH_DLL, or under ONEPDF_ARTIFACTS) and, for the NativeAOT rows, the published
# bench/OnePdf.Layout.Benchmarks.Aot program (AOT_EXE). Repeats each cell REPS times (default 3); the CSV has every repetition.
set -uo pipefail
out="${1:?usage: rss.sh <out.csv>}"
dll="${BENCH_DLL:-${ONEPDF_ARTIFACTS:?set ONEPDF_ARTIFACTS or BENCH_DLL}/bin/OnePdf.Layout.Benchmarks/release/OnePdf.Layout.Benchmarks.dll}"
reps="${REPS:-3}"
tmpdir="${TMPDIR:-${ONEPDF_ARTIFACTS:-}}"
[ -n "$tmpdir" ] && [ -d "$tmpdir" ] || { echo "rss.sh: set TMPDIR (or ONEPDF_ARTIFACTS) to an existing scratch directory" >&2; exit 1; }
tmpf="$(mktemp "$tmpdir/rss.XXXXXX")" || exit 1
trap 'rm -f "$tmpf"' EXIT

echo "engine,row,rep,bytes,peak_rss_kb,wall_ms" >"$out"
cell() { # label, command...
  local label="$1" rep="$2" row="$3"; shift 3
  local line kb
  line=$(/usr/bin/time -v -o "$tmpf" "$@" 2>/dev/null | tail -1)
  case "$line" in
    ok*) kb=$(awk -F: '/Maximum resident set size/ {gsub(/ /,"",$2); print $2}' "$tmpf")
         bytes=${line##*bytes=}; bytes=${bytes%% *}
         echo "$label,$row,$rep,$bytes,$kb,${line##*wall_ms=}" | tee -a "$out" ;;
    *) echo "skipped: $label $row (${line:-no output})" ;;
  esac
}

for rep in $(seq 1 "$reps"); do
  for row in invoice report50 table10k book500 mixed20 throughput32; do
    for e in onepdf migradoc itext; do cell "$e" "$rep" "$row" dotnet "$dll" rss "$e" "$row"; done
    if [ -n "${AOT_EXE:-}" ]; then cell onepdf-aot "$rep" "$row" "$AOT_EXE" "$row"; fi
  done
done
