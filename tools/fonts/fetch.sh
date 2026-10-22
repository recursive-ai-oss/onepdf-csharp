#!/bin/sh
# Downloads the pinned upstream font releases, verifies their SHA-256, and (with --verify-repo) checks that the font files
# committed under src/OnePdf.Fonts/Fonts are byte-identical to the release files. Nothing is converted or rebuilt: the
# fonts' Reserved Font Names forbid modified copies.
# Usage: fetch.sh CACHE_DIR [--verify-repo]
set -eu
D=${1:?cache directory}
HERE=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$D"
cd "$D"

get() { # file url sha256
  [ -f "$1" ] || curl -sSL -o "$1" "$2"
  echo "$3  $1" | sha256sum -c - >/dev/null || { echo "hash mismatch: $1" >&2; exit 1; }
}

get liberation-fonts-ttf-2.1.5.tar.gz \
  https://github.com/liberationfonts/liberation-fonts/files/7261482/liberation-fonts-ttf-2.1.5.tar.gz \
  7191c669bf38899f73a2094ed00f7b800553364f90e2637010a69c0e268f25d0
get NotoSansMath-v3.000.zip \
  https://github.com/notofonts/math/releases/download/NotoSansMath-v3.000/NotoSansMath-v3.000.zip \
  ac351837b41f8a897f020b97fb0f075ad574c1e9669fb5839ada1f92fd748356
get NotoSansSymbols2-v2.008.zip \
  https://github.com/notofonts/symbols/releases/download/NotoSansSymbols2-v2.008/NotoSansSymbols2-v2.008.zip \
  346c930bbe8eb946701a05c54e9c11a2094dee1d93c387bf1771c0a3e335688f
get NotoSansSymbols-v2.003.zip \
  https://github.com/notofonts/symbols/releases/download/NotoSansSymbols-v2.003/NotoSansSymbols-v2.003.zip \
  0c113cdcf6c31d050b80dac39fba2d804a6985281012e76e9220c0a00da007f3

rm -rf x fonts-out && mkdir x && cd x
tar -xzf ../liberation-fonts-ttf-2.1.5.tar.gz
mkdir NotoSansMath-v3.000 && (cd NotoSansMath-v3.000 && unzip -q ../../NotoSansMath-v3.000.zip)
mkdir NotoSansSymbols2-v2.008 && (cd NotoSansSymbols2-v2.008 && unzip -q ../../NotoSansSymbols2-v2.008.zip)
mkdir NotoSansSymbols-v2.003 && (cd NotoSansSymbols-v2.003 && unzip -q ../../NotoSansSymbols-v2.003.zip)
# The unhinted release builds: OnePDF never hints, and they are the smallest official files.
mkdir ../fonts-out
cp liberation-fonts-ttf-2.1.5/*.ttf NotoSansMath-v3.000/NotoSansMath/unhinted/ttf/NotoSansMath-Regular.ttf \
   NotoSansSymbols2-v2.008/NotoSansSymbols2/unhinted/ttf/NotoSansSymbols2-Regular.ttf NotoSansSymbols-v2.003/NotoSansSymbols/unhinted/ttf/NotoSansSymbols-Regular.ttf ../fonts-out/
cd ..
echo "extracted to $D/fonts-out"

if [ "${2:-}" = "--verify-repo" ]; then
  R="$HERE/../../src/OnePdf.Fonts/Fonts"
  for f in fonts-out/*.ttf; do
    cmp "$f" "$R/$(basename "$f")" || { echo "differs from release: $f" >&2; exit 1; }
  done
  (cd "$R" && sha256sum -c SHA256SUMS >/dev/null) && echo "repo fonts identical to the pinned releases"
fi
