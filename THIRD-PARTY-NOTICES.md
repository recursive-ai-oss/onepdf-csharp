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

## Fonts package (`RecursiveAI.OnePdf.Fonts`)

None yet.

## Test data (not shipped)

None yet.
