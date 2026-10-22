#!/bin/sh
# Downloads the pinned upstream sources for the font-data generator into a cache directory.
# Usage: fetch.sh CACHE_DIR      (creates CACHE_DIR/afm, CACHE_DIR/agl, CACHE_DIR/cmr, CACHE_DIR/PDF32000_2008.txt)
# Then:  python3 generate.py CACHE_DIR CACHE_DIR/PDF32000_2008.txt ../../src/OnePdf/FontData/Resources
set -eu
D=${1:?cache directory}
mkdir -p "$D/afm" "$D/agl" "$D/cmr"
cd "$D"

check() { echo "$2  $1" | sha256sum -c - >/dev/null || { echo "hash mismatch: $1" >&2; exit 1; }; }

# Adobe Core 14 AFM files (the ReadMe, MustRead.html, carries the license paragraph).
curl -sSL -o Core14_AFMs.tar http://download.macromedia.com/pub/developer/opentype/tech-notes/Core14_AFMs.tar
check Core14_AFMs.tar fb0736bab04daf130998f5e5b09c61307fd0e48c778bff1a24bf23591a56467a
tar -xf Core14_AFMs.tar -C afm 2>/dev/null || true

# Adobe Glyph List (adobe-type-tools/agl-aglfn, BSD-3-Clause).
AGL=4036a9ca80a62f64f9de4f7321a9a045ad0ecfd6
for f in glyphlist.txt zapfdingbats.txt aglfn.txt LICENSE.md; do
  curl -sSL -o "agl/$f" "https://raw.githubusercontent.com/adobe-type-tools/agl-aglfn/$AGL/$f"
done
check agl/glyphlist.txt a3b2f61ced9f3644cc0d4ecde5c59df34ca286c689d9484a43a710a81c466789
check agl/zapfdingbats.txt f6394e3cb8a447e84a1dad75d4baaf2aa7f45dc104faf369f4720e1a774ef2dc

# Adobe CMap resources (adobe-type-tools/cmap-resources, BSD-3-Clause).
CMR=f5cf3bca7fdfeaceb77aa82847e974f2306c20b4
curl -sSL -o cmap-resources.tgz "https://codeload.github.com/adobe-type-tools/cmap-resources/tar.gz/$CMR"
check cmap-resources.tgz 664dd496e7eea4f41d401c2fc296b639ace93ef14484f53f62abbaaf3c2b39cf
tar -xzf cmap-resources.tgz -C cmr --strip-components=1

# ISO 32000-1 (PDF 1.7) text; Annex D lists the predefined simple-font encodings.
curl -sSL -o PDF32000_2008.pdf https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/PDF32000_2008.pdf
pdftotext -layout PDF32000_2008.pdf PDF32000_2008.txt
# Adobe Technical Note #5176 (CFF): standard strings, Expert encoding and charsets.
curl -sSL -o cff.pdf https://adobe-type-tools.github.io/font-tech-notes/pdfs/5176.CFF.pdf
pdftotext -layout cff.pdf cff.txt

# Apple TrueType Reference Manual, 'post' table: the Macintosh standard glyph ordering.
curl -sSL -o applepost.html https://developer.apple.com/fonts/TrueType-Reference-Manual/RM06/Chap6post.html
echo done
