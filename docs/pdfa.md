# PDF/A

OnePDF writes PDF/A-2b, 2u, 2a, 3b, 3u and 3a (ISO 19005-2 and -3, on ISO 32000-1) from a full save, and checks documents against the requirements it can verify from the file's structure.

```csharp
using var doc = PdfDocument.CreateEmpty();
doc.FontProvider = OnePdf.Fonts.FontsPackage.Provider;       // Liberation faces for the standard fonts
doc.SetInfo("Title", "Quarterly report");
PdfPage page = doc.InsertBlankPage(0, 595, 842);
PdfCreatedFont body = doc.AddFont(File.ReadAllBytes("LiberationSerif-Regular.ttf"));
using (PdfPageContentBuilder b = page.BeginContent())
{
    b.SetFont(body, 12);
    b.DrawText("Hello, archive.", 72, 700);
    b.Commit();
}

doc.AttachFile("data.xml", xmlBytes, new PdfAttachmentOptions { MimeType = "text/xml", Relationship = PdfAttachmentRelationship.Data });
doc.Save("out.pdf", new PdfSaveOptions
{
    PdfA = new PdfAOptions { Part = PdfAPart.Part3, Conformance = PdfAConformance.U },
    DocumentId = id16,                                        // optional: the same input then gives the same bytes
});
```

## What it is, and is not

- **It writes PDF/A for documents made or edited with OnePDF**, and for existing files that already meet the requirements. Level B (visual reproducibility) and level U (B plus a Unicode mapping for all text) of parts 2 and 3. Level A (tagged structure) is offered for documents that are already tagged, including existing files tagged through [the tagging API](tagging.md): see [pdfua.md](pdfua.md).
- **It is not a validator.** `CheckPdfA` verifies what is listed below and nothing else; a clean result does not prove conformance. The tests use the veraPDF command-line validator as a black box (see "Verification").
- **Saving does not convert; the converter does.** `PdfSaveOptions.PdfA` refuses a file that breaks a requirement and lists the violations. To turn an arbitrary existing file into PDF/A, use the converter below, which repairs what it can without changing how the pages look and refuses the rest.

## Converting existing files

```csharp
OnePdf.Fonts.FontsPackage.Register();                       // substitute fonts for non-embedded text
PdfAConversionResult result = PdfAConverter.Convert("in.pdf", "out.pdf");   // PDF/A-2b
foreach (PdfAChange change in result.Changes) Console.WriteLine(change);
```

`PdfAConverter.Convert(path|Stream, path|Stream, PdfAConversionOptions?)` and `PdfDocument.ConvertToPdfA(path|Stream, options)` read an arbitrary file, repair it, and write PDF/A-2b by default. Options: `Part` (`Part2` default, `Part3`), `Conformance` (`B` default, `U`; level A is an `ArgumentException`, because it needs a logical structure the converter does not invent: tag the file first), `Password` for encrypted input, `FontProvider`, `EmbedSubstituteFonts` and `SubstituteUnknownFonts` (default true), `RemoveForbiddenFeatures` (default true), `DocumentId` and `Language`. The result lists what changed (`Changes`, by kind with counts) and `Warnings` for the changes a reader may notice.

A file that cannot be converted is refused with `PdfAException`, listing every reason by clause and object, and **nothing is written**: the output stream is untouched and an existing output file is not replaced (the path form writes a temporary file and moves it into place). A converted file is also run through the same check as a save, so the converter never writes a file that claims PDF/A and fails OnePDF's own check. The same caveat as everywhere on this page applies: OnePDF is not a validator. The tests run veraPDF over the output.

### What is repaired

| Problem | What the converter does |
|---|---|
| Encryption | Removes it (needs `Password` when the user password is not empty). |
| JavaScript, Launch, Hide, ResetForm and other forbidden actions; additional actions (`/AA`) | Removed; widgets keep their appearance. Remote go-to actions keep their target as a plain file name. |
| Forbidden or hidden annotations (Sound, Movie, 3D, hidden, no usable appearance), part 2 file attachments | Removed, with their form-field entries. |
| Embedded files | Part 2: removed. Part 3: completed with `/AFRelationship`, `/UF`, a MIME type, dates and a listing in `/AF`. |
| External stream references (`/F`, `/FFilter`, `/FDecodeParms`) | Dropped; the stream keeps its own data. |
| LZW | Decoded and stored again with Flate (also in front of an image codec). Inline-image LZW is refused. |
| Fonts that are not embedded | A Liberation (or Noto) subset from the Fonts package, or a caller face, with the PDF's `/Widths`, so text does not move. Unknown non-symbolic fonts get a sans, serif or monospace stand-in by their flags. |
| Embedded fonts that disagree with the file | Small width differences (up to 12 units of 1/1000 em) are corrected in `/Widths` or `/W`; CFF widths are matched to the file. Wrong `/CIDSet`, `/CharSet`, `/CIDToGIDMap` defaults, `FontFile3` subtypes, non-ASCII font names and TrueType symbolic flags or encodings are repaired. |
| Level U | Unicode values OnePDF can derive are written to `/ToUnicode`. A code whose glyph names no character is mapped to a private-use code point, which the clause allows, and a warning says so. Padding after the end of a `/ToUnicode` program is removed. |
| Device colour | Output intent chosen by the colours used: the generated sRGB profile, or a generated CMYK profile for CMYK files; an intent already in the file is kept. Where RGB and CMYK are mixed, `DefaultRGB`/`DefaultCMYK` are written into every resource dictionary, including the ones created for generated field appearances. |
| Graphics states and images | `/TR`, `/TR2`, `/HTP`, halftones, non-standard blend modes and rendering intents, image `/Alternates`, `/OPI`, `/Interpolate true`, and an overprint mode that PDF/A forbids are removed or reset. |
| Optional content | `/Order` completed, duplicate names made unique, `/AS` removed from configurations. |
| Signatures | The signature value is removed (a rewritten file cannot carry it); the field stays, empty, and a warning says so. |
| Metadata | XMP written from the Info dictionary; metadata streams on other objects removed; pages get the `/Resources` they inherit. |

### What is refused

The converter does not change how a page looks beyond what is listed, so it refuses:

- text that draws `.notdef` (6.2.11.8): a code that selects glyph 0, as when a layout engine shows a character its font lacks and the face is drawn as its missing-glyph box. Removing it would change what the page shows, so the converter names the font and refuses. An embedded font program that lacks any other glyph the pages show (6.2.11.4.1), or whose advance widths differ from the file's by more than 12 units (correcting them would move text), or a TrueType font whose glyph names are outside the Adobe Glyph List with no symbolic cmap to fall back on;
- a font with no program, no usable substitute for the codes shown, or text in a symbolic simple TrueType font that uses code 0 (validators read it as `.notdef`);
- spot colours that cannot be made consistent: Separation colours of one name with different alternates, DeviceN spot colorants with no `/Colorants`;
- content the interpreter cannot read or PDF/A forbids in a place the converter does not rewrite: unknown operators, more than 28 nested `q`, an image or stream whose data cannot be decoded (it would have to be dropped), inline-image filters other than the six PDF/A allows (LZW among them), malformed hexadecimal strings, numbers and names beyond the limits of ISO 19005-2 6.1.13, a non-standard rendering intent in a content stream, a damaged ICC profile;
- fonts and images whose structure PDF/A forbids and the converter cannot change without altering the page: Type 3 widths that differ from the glyph procedures by more than 12 units, a symbolic TrueType program with several cmap subtables and no (3,0) one, a CMap with a different `WMode` or character ids above 65535, one that refers to a CMap outside the predefined list, a JPEG 2000 image in the CIEJab colour space or with an inconsistent header, and (level U) a `/ToUnicode` map that gives a shown code the value U+0000, U+FEFF or U+FFFE;
- files that cannot be opened at all.

A clean conversion is not proof of conformance; run a validator where it matters.

### Results on real files

Every converted file was validated with veraPDF (2b, 2u and 3b), and none failed. Of 200 sampled govdocs files 181 converted to 2b, of 200 web-crawl files 182, of 92 quality-set files 82, of 30 committed government files 28, and all 7 PDF 2.0 examples; the rest were refused with reasons. Against veraPDF's own published corpus (2,906 files, many built to break exactly the rules the converter repairs), 2,694 converted to 2b and all passed. The commonest reasons for a refusal are an embedded font program that lacks a shown glyph, advance widths that disagree with the program by more than 12 units, and a font with no program and no usable substitute.

A packet of XMP already in the file is never trusted to make the claim: the converter always writes its own packet from the Info dictionary, after copying what only the old packet said (title, author, subject, keywords, creator tool, producer) into Info.

## API

| | |
|---|---|
| `PdfSaveOptions.PdfA` (`PdfAOptions`) | Target part (`Part2` default, `Part3`), level (`B` default, `U`, `A`: tagged, see [pdfua.md](pdfua.md)), `Language` (fallback `/Lang` for level A), and `EmbedLibraryFonts` (default true), `EmbedSubstituteFonts` (default false: embeds Liberation subsets into existing non-embedded fonts such as Arial, see [fonts.md](fonts.md)). A full save then prepares, checks, and writes, or throws `PdfAException` and writes nothing. Needs an editable document (`AllowEditing`, or `CreateEmpty`). Not available for `SaveIncremental`. |
| `PdfDocument.PreparePdfA(options)` | Makes the repairs below in memory and returns what is still wrong. Calling it again changes nothing more. |
| `PdfDocument.CheckPdfA(options)` | Reports every violation of the document as it stands, without changing it. Works on read-only documents. |
| `PdfAException.Violations` | `PdfAViolation`: `Clause` (ISO 19005-2 clause number), `Message`, `ObjectNumber` (-1 for the file as a whole), `PageIndex` (-1 when not on a page). Large numbers of one kind (a hundred bad operators) are cut after twenty; at most 2000 violations are listed. |
| `PdfDocument.AttachFile(name, data, options)` | Adds an embedded file (Flate-compressed) with `/F`, `/UF`, `/AFRelationship`, MIME type and dates, entered in the `EmbeddedFiles` name tree and the catalog's `/AF`. |

A PDF/A save changes the document: it adds the output intent, the XMP packet and the other repairs to the in-memory document, as `SetInfo` or `AddObject` would. The file on disk is not touched. A failed save leaves those repairs in place.

The header is `%PDF-1.7` with the binary comment line, whatever the source said. A source that was PDF 2.0 only because of AES-256 encryption is therefore written as 1.7 when the encryption is removed. Features that exist only in PDF 2.0 are not detected, and a validator will report them.

Encrypted documents are refused unless the save also sets `Encryption = PdfEncryptionAction.Remove` (then the output is plain); `Encryption = Set` with PDF/A throws `ArgumentException`.

## What is written

| Item | Clause | What OnePDF does |
|---|---|---|
| Header, binary comment | 6.1.2 | `%PDF-1.7`, then `%` and four bytes above 127 (every full save writes the comment). |
| File identifiers | 6.1.3 | `/ID` written in the trailer (`DocumentId` makes it fixed, otherwise random). |
| Cross-reference formats, object and stream spacing, no data after `%%EOF` | 6.1.4, 6.1.7.1, 6.1.9, 6.1.3 | The writer's own layout; classic table or cross-reference stream with object streams (both allowed in part 2). Checked with veraPDF. |
| Output intent | 6.2.3 | A `GTS_PDFA1` intent with `/DestOutputProfile`, an sRGB profile generated by OnePDF (below), added once when the document has no PDF/A intent. |
| Metadata stream | 6.6.2.1 | `/Metadata` with `/Type /Metadata /Subtype /XML`, unfiltered, UTF-8, no `bytes` or `encoding` attribute. |
| XMP identification and properties | 6.6.2.3, 6.6.4 | `pdfaid:part` and `pdfaid:conformance`; `dc:title`, `dc:creator`, `dc:description`, `pdf:Keywords`, `pdf:Producer`, `xmp:CreatorTool`, `xmp:CreateDate`, `xmp:ModifyDate` taken from the Info dictionary, so the two agree. Only properties the XMP specification itself defines are used, so no extension schema block is needed. No clock is read. An existing packet that already claims the same part and level is kept as it is; any other packet is replaced. |
| Standard fonts added by OnePDF | 6.2.11.4.1 | With the Fonts package registered (or a `PdfDocument.FontProvider` that supplies faces): the font becomes an embedded non-symbolic TrueType font (Liberation, a subset with a Windows Unicode `cmap` holding the glyphs the font's encoding reaches, Flate-compressed, named with a six-letter subset tag as ISO 32000-2 9.6.4 requires), `/Widths` from the Adobe font metrics (the font program's own widths where they differ by more than one unit), `/FontDescriptor`, WinAnsi encoding (a `Differences` array when the original encoding was Standard). This applies to fonts from `AddStandardFont` and to the fonts of appearances OnePDF generated; fonts that came with the file are never replaced. Symbol and ZapfDingbats have no substitute and are reported. |
| Fonts from `AddFont` | 6.2.11 | Already embedded subsets (TrueType and CFF), Type 0 with Identity-H, `/CIDToGIDMap Identity` for TrueType, `/W`, `/ToUnicode`. |
| Annotation flags | 6.3.2 | `/F` created as Print (4), or Print added. Other bits are left, and a hidden or invisible annotation stays a violation. |
| Annotation appearances | 6.3.3 | Widgets: `GenerateMissingAppearances` (which also clears `/NeedAppearances`, 6.4.1). Other annotations without `/AP`: the generated appearance of the 1.1 generator, stored as `/AP /N`. Appearance entries other than `/N` (`/R`, `/D`) are removed. |
| Attachments (part 3) | 6.8 | `/F`, `/UF`, `/AFRelationship`, `/EF` stream with MIME `/Subtype` and `/Params /ModDate`, listed in `/AF`. A file with no date from the caller gets the Info modification (else creation) date. |

### The sRGB profile

The generated sRGB profile is an ICC.1:2001-12 (version 2.1) display profile of 2,612 bytes from the published parameters: the Rec. 709 primaries and D65 white point (IEC 61966-2-1 Table 1), adapted to the D50 profile connection space with the Bradford matrix, and the sRGB decoding curve sampled at 1024 points. Class `mntr`, colour space `RGB `, PCS `XYZ `, tags `desc`, `cprt`, `wtpt`, `rXYZ`, `gXYZ`, `bXYZ`, `rTRC`, `gTRC`, `bTRC` (one curve shared by the three). The creation date is fixed so the bytes never change. It is embedded once, Flate-compressed, as the intent's `/DestOutputProfile` with `/N 3`. The code is original; no vendor profile is copied.

### Device colour

ISO 19005-2 6.2.4.3 ties the device spaces to the output intent:

- DeviceRGB needs an RGB intent. OnePDF's intent is RGB, so DeviceRGB is fine.
- DeviceGray needs any intent (an RGB intent is fine).
- DeviceCMYK needs a CMYK intent. A plain save has no CMYK profile, so CMYK content is a violation there. An existing document that already carries a CMYK `GTS_PDFA1` intent keeps it, and then DeviceRGB is the violation. The converter generates a CMYK profile (below).
- ICCBased, CalRGB, Lab, Separation, DeviceN and Indexed spaces are not device spaces; ICC profile headers are checked (class, colour space, version below 5), spot colourant consistency is not.
- A `DefaultRGB`, `DefaultGray` or `DefaultCMYK` entry (an ICC-based, Cal or Lab space) stands in for the intent only when every resource dictionary of the file carries it and no content stream that paints in the space lacks resources; one dictionary without it, or DeviceCMYK as the process space of a DeviceN space, brings the violation back. The converter writes the defaults into every resource dictionary where the intent does not cover a space the file uses (also into dictionaries it creates while preparing the file).

## A conformance save never drops content

A PDF/A save (and a PDF/UA save, see [pdfua.md](pdfua.md)) writes every part of the document it reaches, or it fails. If a part cannot be read, the check reports it and the save throws `PdfAException` listing it, instead of writing a file that quietly lacks the part and then claiming conformance. The finding names the object (number and generation) and what it is: an embedded font program, a font descriptor or other font part, an ICC profile, an image, a page or form content stream, an XMP stream, an embedded file, an annotation or a structure node. Three situations count:

- **A stream that cannot be read or decoded:** its bytes cannot be read, it cannot be decrypted cleanly (bad padding, a partial block, a missing IV, an unknown crypt filter), or its filter data is cut short or invalid (truncated or corrupt Flate, LZW, ASCII85 and the like). Repairs that lose nothing are not findings: a wrong `/Length` found by scanning for `endstream`, a missing or wrong Flate checksum, a missing end marker, trailing bytes after the data. Streams that need an image codec (DCT, JPX, JBIG2) are read up to that filter.
- **A string damaged by decryption:** a string in any object the scan reaches whose ciphertext had bad padding, a partial last block or a missing IV. It reads as far as it decrypted, and a normal save writes those bytes again; a conformance save would claim to preserve text it does not have. The finding names the key (`the string under /Title`) and the object.
- **A reference that leads nowhere** where the part is needed: `FontFile`, `FontFile2`, `FontFile3`, `FontDescriptor`, `DescendantFonts`, `ToUnicode`, `CIDToGIDMap`, `Widths`, `W`, `Encoding`, `DestOutputProfile`, `ICCBased` profiles, page `Contents`, `SMask`, `Mask`, `EF`, `AF`, `Annots`, `Kids`, `Pages`, `StructTreeRoot`, structure `K`, and every entry of the page resource categories (`Font`, `XObject`, `ColorSpace`, `Pattern`, `Shading`, `ExtGState`, `Properties`) and of `CharProcs`. A normal save writes `null` for such a reference; a conformance save refuses. A missing appearance stream, output intent or XMP packet is not a finding: the preparers write those. An XMP packet that exists but cannot be read is a finding and is never replaced by one built from the Info dictionary.

The check and the save run the same scan, so a document the check passes is saved without loss and one whose save would fail is reported by the check. Clauses: 6.2.11.4.1 (font programs and font parts), 6.2.4.2 (ICC profiles), 6.2.8 (images), 6.2.2 (content streams), 6.6.2.1 (XMP), 6.8 (embedded files), 6.1.7.1 (anything else); the standard has no clause for "cannot be read" itself. A save without `PdfSaveOptions.PdfA` or `PdfUA` is unchanged: it copies streams as stored and writes `null` for a missing object, as before.

## What is checked (and refused)

Checked by `CheckPdfA` and again, after the repairs, by every PDF/A save. Clause numbers are those of ISO 19005-2; part 3 uses the same numbers.

| Clause | Checked |
|---|---|
| 6.1.2, 6.1.3 | Header version 1.0 to 1.7 (when reading a file); `/ID` present (when reading); no `/Encrypt`. |
| 6.1.7 | No `/F`, `/FFilter`, `/FDecodeParms` in a stream; filters are the standard ones, not LZW, `Crypt` only with `Identity`; content streams that cannot be decoded. |
| 6.1.10 | Inline images: no LZW or Crypt. |
| 6.1.12 | Permissions dictionary only `UR3` and `DocMDP`. |
| 6.1.13 | Integers beyond 32 bits, reals out of range, strings over 32767 bytes, names over 127 bytes, more than 8,388,607 objects, `q` nesting deeper than 28, page boxes smaller than 3 or larger than 14400 units. |
| 6.2.2 | Operators not defined in ISO 32000-1; form XObjects and patterns that use resources without a `/Resources` of their own. |
| 6.2.3, 6.2.4.2 | Output intent profile header (class prtr or mntr, RGB, CMYK or GRAY, version below 5), several intents naming different profiles; ICCBased profile headers. |
| 6.2.4.3 | Device colour spaces against the output intent (above), in content operators, resources, images, shadings and inline images. |
| 6.2.5, 6.2.6, 6.2.10 | `/TR`, `/TR2` other than Default, `/HTP`, halftone types and names, soft-mask `/TR`; rendering intents (`/Intent`, `/RI`); blend modes; transparency on a page without output intent and without a page group `/CS`. |
| 6.2.8, 6.2.9 | Image `/Alternates`, `/OPI`, `/Interpolate true`, `/BitsPerComponent`; form `/OPI`, `/PS`, `/Ref`; PostScript XObjects. |
| 6.2.11 | Fonts must be embedded (not Type 3); `/Subtype`, `/BaseFont`; `FirstChar`, `LastChar` and `Widths` of embedded simple fonts; `FontFile3` subtype; Type 0 `CIDToGIDMap`, predefined or embedded CMap, one descendant; TrueType encodings (WinAnsi or MacRoman, none when symbolic); **glyph 0 (.notdef) referenced by text drawn with a font from `AddFont`** (6.2.11.8). Type 3 glyph procedures are scanned like other content. |
| 6.2.11.7 (level U) | Every font has a `/ToUnicode` or is of an exempt kind: WinAnsi, MacRoman or MacExpert encoding; Type 1 and Type 3 whose `Differences` names are in the Adobe Glyph List; Adobe-GB1, CNS1, Japan1 or Korea1 CID fonts; non-symbolic TrueType. |
| 6.3 | Annotation types (no 3D, Sound, Screen, Movie or unknown types); `/F` with Print and without Invisible, Hidden, NoView and ToggleNoView; an appearance dictionary with only `/N`, a stream (a dictionary of states for button widgets); an appearance for every annotation except Popup, Link and zero-sized rectangles. |
| 6.4, 6.5 | Widget and field `/A` and `/AA`; `/NeedAppearances`; `/XFA`; `/NeedsRendering`; catalog and page `/AA`; action types (only GoTo, GoToR, GoToE, Thread, URI, Named, SubmitForm; Named only NextPage, PrevPage, FirstPage, LastPage); a JavaScript name tree. |
| 6.6 | Metadata stream present, unfiltered, well-formed, claiming the requested part and level. |
| 6.8 | Part 2: **any embedded file** or file attachment annotation is refused (the standard allows PDF/A attachments in part 2; OnePDF does not write or verify them). Part 3: `/F` and `/UF`, a valid `/AFRelationship`, an `/EF` stream with a MIME `/Subtype` and `/Params /ModDate`, and a listing in an `/AF` array. |
| 6.9 | Optional content configurations: a unique `/Name`, no `/AS`. |

## Not checked

These need the font programs, colour management or XMP schema validation that OnePDF leaves to a validator, or concern features OnePDF does not write:

- 6.1.6 hexadecimal string syntax, 6.1.8 name encoding (the writer produces valid ones; foreign files are not rechecked).
- 6.1.13: DeviceN with more than 32 colourants, CIDs above 65535.
- 6.2.4.2 overprint mode with ICCBased CMYK; 6.2.4.4 spot colour `Colorants` and Separation consistency.
- 6.2.8.3 JPEG 2000 details.
- 6.2.11: whether every referenced glyph exists in a foreign font program (6.2.11.4.1-2), width consistency between `/Widths` and a foreign font program (6.2.11.5), `CIDSet` and `CharSet` accuracy, TrueType `cmap` contents (6.2.11.6), CIDSystemInfo compatibility, `ToUnicode` values (not 0, FEFF, FFFE), `ActualText` (level A). Fonts written by `AddFont` and the substituted standard fonts are accepted by the validator on the test set.
- 6.4.3 signatures: a PDF/A save rewrites the file, which invalidates existing signatures; signing a PDF/A file afterwards is an incremental update and is not checked against 6.4.3.
- 6.6.2.3 XMP property types and extension schemas in an existing packet that already claims the requested part and level.
- 6.9 `/Order` completeness.
- Unused (unreachable) objects: a full save drops them, so they cannot make the output fail.
- Fonts used only in text render mode 3 are not exempted from embedding; embed them, or keep the file out of PDF/A.

## Verification

`tests/OnePdf.Tests/PdfA` generates documents with embedded TrueType and CFF fonts, standard fonts through the Fonts package, JPEG and raw images with alpha, transparency and blend modes, vector paths, filled and flattened forms, annotations, multiple pages with page operations and a stamp, and attachments; saves each as 2b, 2u, 3b and 3u and runs the veraPDF command-line validator (`ONEPDF_VERAPDF` names its launcher script; the tests skip without it). The refusals, the sRGB profile, the XMP round trip and determinism have tests of their own. The `pdfa` fuzz target checks, prepares and saves arbitrary input and requires that any file it writes passes `CheckPdfA`.

## Limits and known gaps

- Standard fonts are subset to every glyph their encoding can reach (about 220 per face; a test file with four faces went from 834 KB to 112 KB), not to the glyphs the pages show, because finding those would mean reading all content. Liberation is licensed under the SIL Open Font License, whose FAQ (entry 1.12) allows embedding OFL fonts in a document "either in full or a subset"; the subset keeps the face name behind its subset tag.
- A font from a provider other than the Fonts package must be a TrueType-flavoured font with a Unicode `cmap`, and its `fsType` must allow embedding; otherwise the font stays unembedded and is reported.
- `PreparePdfA` and PDF/A saves read the whole document (every reachable object and content stream). Time and memory grow with the file; `PdfLimits` apply.
