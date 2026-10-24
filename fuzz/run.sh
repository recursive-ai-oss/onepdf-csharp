#!/usr/bin/env bash
# Build, instrument and run one fuzz target under libFuzzer.
#
#   fuzz/run.sh <target> <seconds> [corpus dir] [extra libFuzzer flags...]
#   fuzz/run.sh replay <target> <file>...      # run saved inputs once, without libFuzzer (reproduce a finding)
#
# Targets: parser document model pdffont filters security fontprogram cmap type4 ccitt content appearance extraction shading png save redact fontsubset signature pdfa layout   (see fuzz/README.md)
# `layout` is a separate executable (fuzz/OnePdf.Layout.Fuzz) because the layout package is not part of OnePdf.slnx; it needs the OneFont checkout beside the repository, like the layout package itself.
#
# Environment (nothing is baked into the script):
#   ONEPDF_FUZZ_WORK    required: where builds, corpora and findings go. Keep it outside the checkout.
#   ONEPDF_FUZZ_TOOLS   directory holding `fuzz/libfuzzer-dotnet` (the libFuzzer host) and `sharpfuzz/sharpfuzz`
#                       (the instrumenter). LIBFUZZER_DOTNET and SHARPFUZZ override the two paths individually.
#   ONEPDF_ARTIFACTS    build output root, as for every other project in the repo
#   DOTNET              the dotnet executable (default: dotnet from PATH)
#   FUZZ_FONTS          directory with .ttf/.otf files to seed the fontprogram target (optional)
#   FUZZ_TIMEOUT        per-input timeout in seconds (default 10)
#   FUZZ_RSS_MB         memory limit in MB (default 1024)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_bin="${DOTNET:-dotnet}"
work="${ONEPDF_FUZZ_WORK:?set ONEPDF_FUZZ_WORK to a scratch directory outside the checkout}"
case "$(readlink -f "$work")/" in
  "$root"/*) echo "ONEPDF_FUZZ_WORK must be outside the checkout" >&2; exit 2 ;;
esac
tools="${ONEPDF_FUZZ_TOOLS:-}"
host="${LIBFUZZER_DOTNET:-$tools/fuzz/libfuzzer-dotnet}"
instrument="${SHARPFUZZ:-$tools/sharpfuzz/sharpfuzz}"

mode=fuzz
if [[ "${1:-}" == replay ]]; then
  mode=replay
  shift
fi
target="${1:?usage: run.sh <target> <seconds> [corpus dir] | run.sh replay <target> <file>...}"
shift

# What each target reads: pdf = seeded with the PDFs under testdata/, none = hand-built seeds from `seeds`. Maximum input length.
case "$target" in
  parser|document|model|content|pdffont|save|redact|signature|pdfa) maxlen=131072 ;;
  filters|security|cmap|type4|ccitt|shading|png) maxlen=65536 ;;
  fontprogram|fontsubset) maxlen=262144 ;;
  extraction|appearance) maxlen=4096 ;;
  layout) maxlen=262144 ;;
  *) echo "unknown target '$target'" >&2; exit 2 ;;
esac

# The executable, its project and the assemblies to instrument.
proj=OnePdf.Fuzz
libs=(OnePdf.dll)
if [[ "$target" == layout ]]; then
  proj=OnePdf.Layout.Fuzz
  libs=(OnePdf.dll OnePdf.Layout.dll OneFont.dll)
fi

bin="$work/bin"
rm -rf "$bin"
mkdir -p "$bin"

# Build once per run: release, then instrument only the library assembly (the harness itself needs no coverage).
"$dotnet_bin" build "$root/fuzz/$proj/$proj.csproj" -c Release -v quiet -clp:NoSummary -o "$bin"

if [[ "$mode" == replay ]]; then
  ONEPDF_FUZZ_WORK="$work" "$dotnet_bin" "$bin/$proj.dll" replay "$target" "$@"
  exit $?
fi

seconds="${1:?usage: run.sh <target> <seconds> [corpus dir]}"
shift
extra=""
if [[ $# -gt 0 && "${1:0:1}" != "-" ]]; then
  extra="$1"
  shift
fi
[[ -x "$host" ]] || { echo "libFuzzer host not found at '$host' (set ONEPDF_FUZZ_TOOLS)" >&2; exit 2; }
[[ -x "$instrument" ]] || { echo "sharpfuzz not found at '$instrument' (set ONEPDF_FUZZ_TOOLS)" >&2; exit 2; }

export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v "$dotnet_bin")")")}"
corpus="$work/corpus/$target"
findings="$work/findings/$target"
mkdir -p "$corpus" "$findings"

# Seeds come from the uninstrumented build (instrumented code needs libFuzzer's shared memory).
if [[ "$target" == layout ]]; then
  "$dotnet_bin" "$bin/$proj.dll" seeds "$corpus" "$root/testdata/fonts" >/dev/null
else
  "$dotnet_bin" "$bin/$proj.dll" seeds "$target" "$corpus" "$root/testdata" "${FUZZ_FONTS:-}" >/dev/null
fi
[[ -n "$(ls -A "$corpus")" ]] || printf '\x00' >"$corpus/seed-empty"

for lib in "${libs[@]}"; do
  "$instrument" "$bin/$lib" >/dev/null
done

# An extra seed directory (for example a sample of a local corpus) is read-only for libFuzzer: new finds go to $corpus.
dirs=("$corpus")
[[ -z "$extra" ]] || dirs+=("$extra")

# Run. -max_total_time bounds the run; crashes, hangs and out-of-memory inputs are written to the findings folder.
ONEPDF_FUZZ_TARGET="$target" ONEPDF_FUZZ_WORK="$work" "$host" \
  --target_path="$(command -v "$dotnet_bin")" --target_arg="$bin/$proj.dll" \
  -max_total_time="$seconds" -timeout="${FUZZ_TIMEOUT:-10}" -rss_limit_mb="${FUZZ_RSS_MB:-1024}" \
  -max_len="$maxlen" -artifact_prefix="$findings/" -print_final_stats=1 "$@" "${dirs[@]}"
