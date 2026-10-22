# Third-party notices

OnePDF's own code is licensed `MIT OR Apache-2.0`. Material from other projects that ships with the packages is listed here.

## Core library (`RecursiveAI.OnePdf`)

The core package embeds data tables generated from the sources below by `tools/fontdata`. Each is reproduced here with its terms.

### Adobe Core 14 font metrics

The metrics of the 14 standard PDF fonts (advance widths, glyph bounding boxes, ascent, descent, cap height and so on) are
converted from Adobe's Core 14 AFM files, <http://download.macromedia.com/pub/developer/opentype/tech-notes/Core14_AFMs.tar>.
**The data was converted:** the AFM text was stored compressed, without its kerning and composite-character sections, and each
resource carries a comment saying so. The AFM files themselves are not distributed. Copyright lines of the AFM files:

- Copyright (c) 1985, 1987, 1988, 1989, 1997 Adobe Systems Incorporated. All Rights Reserved.
- Copyright (c) 1985, 1987, 1989, 1990, 1993, 1997 Adobe Systems Incorporated. All Rights Reserved.
- Copyright (c) 1985, 1987, 1989, 1990, 1997 Adobe Systems Incorporated. All Rights Reserved.
- Copyright (c) 1989, 1990, 1991, 1992, 1993, 1997 Adobe Systems Incorporated. All Rights Reserved.
- Copyright (c) 1989, 1990, 1991, 1993, 1997 Adobe Systems Incorporated. All Rights Reserved.

Adobe's terms, verbatim from the ReadMe (`MustRead.html`) that accompanies the files:

> This file and the 14 PostScript(R) AFM files it accompanies may be used, copied, and distributed for any purpose and without
> charge, with or without modification, provided that all copyright notices are retained; that the AFM files are not distributed
> without this file; that all modifications to this file or any of the AFM files are prominently noted in the modified file(s);
> and that this paragraph is not modified. Adobe Systems has no responsibility or obligation to support the use of the AFM files.

### Adobe Glyph List

Glyph names to Unicode (`glyphlist.txt`) and the ZapfDingbats names (`zapfdingbats.txt`) from
<https://github.com/adobe-type-tools/agl-aglfn> at commit 4036a9ca80a62f64f9de4f7321a9a045ad0ecfd6.

Copyright 2002-2019 Adobe (http://www.adobe.com/).

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

Redistributions of source code must retain the above copyright notice,
this list of conditions and the following disclaimer.

Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

Neither the name of Adobe nor the names of its contributors may be
used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

### Adobe CMap resources

The predefined CMaps (Adobe-Japan1, Adobe-GB1, Adobe-CNS1, Adobe-Korea1, Adobe-KR and Identity) and the CID to Unicode tables
derived from their UTF-32 CMaps, from <https://github.com/adobe-type-tools/cmap-resources> at commit
f5cf3bca7fdfeaceb77aa82847e974f2306c20b4. The CMap data is stored in a compact binary form.

Copyright 1990-2023 Adobe. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

Redistributions of source code must retain the above copyright notice,
this list of conditions and the following disclaimer.

Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

Neither the name of Adobe nor the names of its contributors may be
used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

### Encoding tables and standard strings

The predefined encodings (StandardEncoding, WinAnsiEncoding, MacRomanEncoding, MacExpertEncoding, PDFDocEncoding), the CFF
standard strings, predefined charsets and Expert encoding, and the Macintosh standard glyph names are factual tables transcribed
from the published specifications (ISO 32000-1 Annex D, Adobe Technical Note #5176 appendices A to C, and the Apple TrueType
Reference Manual, 'post' table). They are listed here for provenance; no license terms attach to them.

### Unicode bidirectional class data

`src/OnePdf/Text/Extraction/BidiData.g.cs` is generated (`tools/bidi/generate.py`) from the Unicode Character Database 16.0.0 files
`DerivedBidiClass.txt` and `BidiBrackets.txt`. Copyright (c) 1991-2024 Unicode, Inc., under the Unicode License v3
(https://www.unicode.org/license.txt):

> Permission is hereby granted, free of charge, to any person obtaining a copy of data files and any associated documentation (the "Data Files") or
> software and any associated documentation (the "Software") to deal in the Data Files or Software without restriction, including without limitation
> the rights to use, copy, modify, merge, publish, distribute, and/or sell copies of the Data Files or Software, and to permit persons to whom the
> Data Files or Software are furnished to do so, provided that either (a) this copyright and permission notice appear with all copies of the Data
> Files or Software, or (b) this copyright and permission notice appear in associated Documentation.
>
> THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
> MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF THIRD PARTY RIGHTS.
>
> IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES, OR
> ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
> OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA FILES OR SOFTWARE.
>
> Except as contained in this notice, the name of a copyright holder shall not be used in advertising or otherwise to promote the sale, use or other
> dealings in these Data Files or Software without prior written authorization of the copyright holder.

## Fonts package (`RecursiveAI.OnePdf.Fonts`)

The package license expression is `(MIT OR Apache-2.0) AND OFL-1.1`: the code is `MIT OR Apache-2.0`, the font files are under the SIL Open Font License, Version 1.1 (https://openfontlicense.org). The fonts are the upstream release files, embedded byte for byte as resources: nothing is subset, converted or rebuilt (the Liberation fonts carry Reserved Font Names). Each font's full license text, with its copyright lines, is in the package under `licenses/` and in the source tree under `src/OnePdf.Fonts/Fonts/`; `src/OnePdf.Fonts/Fonts/README.md` gives the release URLs, and `tools/fonts/fetch.sh` verifies the files against them by SHA-256. The fonts are not offered on their own.

- **Liberation Fonts 2.1.5** (Sans, Serif, Mono; Regular, Bold, Italic, Bold Italic) — "Digitized data copyright (c) 2010 Google Corporation with Reserved Font Arimo, Tinos and Cousine. Copyright (c) 2012 Red Hat, Inc. with Reserved Font Name Liberation." Used for the Helvetica, Times and Courier families. License text: `Liberation.OFL.txt`.
- **Noto Sans Math 3.000** — "Copyright 2022 The Noto Project Authors (https://github.com/notofonts/math)". Used for Symbol. License text: `NotoSansMath.OFL.txt`.
- **Noto Sans Symbols 2 2.008** — "Copyright 2022 The Noto Project Authors (https://github.com/notofonts/symbols)". Used for ZapfDingbats. License text: `NotoSansSymbols2.OFL.txt`.
- **Noto Sans Symbols 2.003** — "Copyright 2022 The Noto Project Authors (https://github.com/notofonts/symbols)". Fills the ZapfDingbats glyphs that Noto Sans Symbols 2 lacks. License text: `NotoSansSymbols.OFL.txt`.

OFL 1.1 conditions that apply: the copyright notice and license accompany every copy (they do, in the package and in the assembly's resources); the fonts are not sold by themselves; no Modified Version is made.

## Test data (not shipped)

None of this is in any package (`tools/corpus/check_nupkg.sh` checks the packed files). Every redistributed file is listed with source URL, upstream commit, license and SHA-256 in `testdata/MANIFEST.csv`.

- `testdata/pdf20examples/` — PDF Association, *pdf20examples* (https://github.com/pdf-association/pdf20examples). "The PDF Association provides these example PDF 2.0 files under the Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0) license." Files are unmodified and kept apart from the library code; the library does not incorporate or derive from them. Their `LICENSE.md` sits in that folder.
- `testdata/verapdf/` — veraPDF Consortium, *veraPDF corpus* (https://github.com/veraPDF/veraPDF-corpus), Creative Commons Attribution 4.0 International (CC BY 4.0). Selected files, unmodified.
- `testdata/govdocs/` — selected files from the GovDocs1 corpus whose original URL is a United States federal site; works of the US government (17 U.S.C. 105).
- `testdata/generated/` — produced by this project's `tools/corpus/generate.py`; our own work. Chrome, LibreOffice, TeX and qpdf were used as tools; fonts embedded in the output PDFs come from fonts installed on the generating machine (Liberation, DejaVu, Noto, all under the SIL Open Font License or the Bitstream Vera license, which permit embedding in documents).
