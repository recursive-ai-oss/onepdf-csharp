# Reading and repair rules

How OnePDF opens a file, and what it does when the file is damaged. Every repair adds a `PdfDiagnostic` (severity
Warning, with object number and offset where known) to `PdfDocument.Diagnostics`. With `PdfOpenOptions.Strict` each of
them throws `PdfFormatException` instead. Informational notes (Info) never throw.

## The fallback chain

1. **Trust the cross-reference data.** Find the last `startxref` in the final 8 KiB, read the section it names, then
   follow `/Prev` back to the first section. Sections are tables (`xref` ... `trailer`) or cross-reference streams
   (ISO 32000-2 7.5.8). In a hybrid file the table's `/XRefStm` supplies entries for objects the table lists as free
   (7.5.8.4). Each section is one revision (`PdfDocument.Revisions`, oldest first).
2. **Fix individual objects by scanning.** When an entry's offset does not lead to `n g obj` with the expected number,
   the file is scanned once (shared by all later lookups) for object headers and the object is read from where it is
   found. Diagnostic: "the cross-reference offset ... does not point at the object". An object that cannot be found
   reads as null (severity Error).
3. **Rebuild.** If the xref data cannot be read at all (no `startxref`, no section at the offset, a damaged or truncated
   table or trailer, a `/Prev` loop, a subsection claiming more entries than the file can hold) or the trailer's `/Root`
   does not lead to a dictionary, the whole file is scanned and the cross-reference data is replaced by what the scan
   finds. Diagnostic: "the cross-reference data is unusable (...); rebuilt by scanning the file for objects".

After the chain has run, the document needs a catalog. If the trailer's `/Root` is missing, free, or not a dictionary (even after a
rebuild, which is what a truncated or damaged file often looks like), OnePDF uses the **last `/Type /Catalog` object** the file
has and records a warning: "The trailer's /Root does not lead to a dictionary; the last /Type /Catalog object was used." If the file
has no such object either, there is no document structure to read, and opening **throws `PdfFormatException`** (the document has no
catalog). `PdfDocument.TryOpen` returns `false` for such a file and returns the exception in `error`, so a file that `TryOpen` accepts always has a
catalog. In `PdfOpenOptions.Strict` mode the fallback itself is an error.

Limit violations (`PdfLimitException`) are never repaired: they propagate.

## Rules in detail

- **Header.** `%PDF-` is looked for in the first 1024 bytes. Bytes before it are ignored (Info). A missing header is a
  warning. Offsets in `startxref` and the xref data are tried as absolute file positions, then relative to the header;
  the first that lands on a section is used for the whole file (Info).
- **Table entries.** Entries are read as tokens, not fixed 20-byte lines, so 19-byte and CR-only lines work. An entry
  of type `n` with offset 0 is free. A first subsection that starts at 1 although its first entry is the free head
  (`0000000000 65535 f`) is renumbered from 0 (warning).
- **Trailer.** The newest trailer is used; `Root`, `Info`, `ID` and `Encrypt` it lacks are taken from older trailers.
  Each revision keeps its own trailer.
- **Stream length** (7.3.8.2). `/Length` (direct, or indirect) is trusted only if `endstream` follows the data after at
  most 16 white-space bytes. Otherwise the data ends at the next `endstream`, else `endobj`, else the end of the file,
  less one end-of-line (CRLF, LF or CR). Diagnostic: "the stream's /Length ... is wrong". A `/Length` that is indirect
  and cannot be resolved takes the same path. The length of an object stream or xref stream is never read from inside an
  object stream.
- **Stream keyword.** `stream` may be followed by spaces before its end-of-line; CR alone is accepted.
- **Missing `endobj`.** Any token other than `endobj` after an object's value is a warning; reading continues.
- **Unterminated containers.** An array or dictionary cut off by `endobj`, `stream`, `obj`, `xref`, `trailer`,
  `startxref` or the end of data is closed there (warning). A stray `]` or `>>` is ignored. A dictionary key that is
  not a name skips that token; a key without a value is dropped. Null values are dropped (a missing key and a null
  value are the same, 7.3.7).
- **Numbers.** Leading signs, a second decimal point, and a minus after digits are tolerated (`--5` is -5, `1.2.3` is
  1.2, `5-3` is 5). Integers beyond 64 bits become reals.
- **Strings and names.** Unterminated strings run to the end of data. Bad `#xx` escapes in names stay literal. Name
  bytes that are not valid UTF-8 are read as Latin-1.
- **Object streams.** The entry's index is checked against the stream's header; a wrong index is found by number
  (warning). `/N` and `/First` are validated against the decoded size before anything is allocated. When `/N` or `/First`
  is missing, negative, too large, or `/First` lies inside the header, the header is salvaged (warning "invalid /N or
  /First"): the leading run of `number offset` integer pairs is the header (at most `/N` pairs when `/N` is sane, else
  as many as the data allows), and `/First` is taken to be where that run ends. A header that stops early keeps the
  pairs read so far and the rest of the stream's objects read as null (warning). An object stream that is missing or not
  a stream makes its objects read as null (warning). Strict mode throws for each of these.
- **References.** A reference to a missing, free or null object resolves to null. A chain of references to references
  longer than `PdfLimits.MaxRecursionDepth` (or a loop) reads as null (warning). A load that needs itself is a
  `PdfFormatException`.
- **Page tree.** `Pages` (and `PageCount`) walk `/Pages` iteratively once; the inheritable attributes `Resources`,
  `MediaBox`, `CropBox` and `Rotate` come from the nearest ancestor that has them. A page without a usable `MediaBox`
  is US Letter (warning); a `CropBox` is clipped to the `MediaBox`; a `Rotate` that is not a multiple of 90 is 0 (warning).
  A page tree entry that does not lead to a page is dropped (warning) and reported as a `PdfLostPage` (below). A node
  without `/Kids` is a page when its `/Type` is `/Page`, or when it has no `/Type` and has `/Contents`, `/MediaBox`,
  `/Resources` or `/Parent`; any other type (a font, an annotation) is not a page, and a `/Type /Pages` node without
  `/Kids` is an empty node that holds no page. Nesting beyond `MaxNestingDepth` is dropped too.

### Lost pages and the saved page tree

A repaired file can declare pages it no longer holds: a damaged region takes some page objects, and the page tree
still lists them. The tree entries that do not lead to a page are:

| `PdfLostPageReason` | Entry |
|---|---|
| `Null` | a direct null in `/Kids` |
| `MissingObject` | a reference to an object the file does not contain, or that could not be recovered |
| `NotADictionary` | a number, name, array or other non-dictionary |
| `NotAPage` | a dictionary that is not a page or a page-tree node |
| `Repeated` | a node already visited: a page listed twice, or a cycle (the first occurrence is kept) |
| `TooDeep` | beyond `MaxNestingDepth` |

`PdfDocument.LostPages` lists them in declared order after open, without saving. Each `PdfLostPage` has the `Index` the
entry would have had if every entry were a page (the pages after a lost one keep their `Pages` index shifted down by the
number of lost entries before them), the `Reason`, the `ObjectNumber` it pointed at and the `ParentObjectNumber` of the
node that listed it. `PageCount` and `Pages` already exclude them, so `Pages.Count` is the number of pages a save writes.

**Full save** (`Save`, including the PDF/A and PDF/UA paths) writes the tree with only the pages that exist (ISO 32000-2
7.7.3.2): lost entries are left out, a page-tree node left with no page is left out, and every node's `/Count` is the number
of pages below it. Pages keep their own dictionaries and their `/Parent`, so inherited attributes (7.7.3.4) still come from
the same ancestors. A sound tree is written as it is. The document itself is not changed. Reopening the saved file gives the
same pages and no page-tree diagnostics.

**Incremental save** (`SaveIncremental`) throws `PdfFormatException` when `LostPages` is not empty. It appends to the original
bytes, which hold the inconsistent tree, and it only writes objects the caller changed; it would either pass the damage on
or rewrite the tree behind the caller's back. Use `Save`. A page operation that rewrites the tree (insert, delete, reorder, move, import, merge; not rotate) builds a new,
consistent tree, after which `LostPages` is empty and an incremental save is allowed again (a file rebuilt by scanning is
refused for its own reason, see above). A wrong `/Count` on its own, with no lost entry, does not stop an incremental save.
- **External streams.** A stream with `/F` reads as empty (warning). Nothing outside the document is ever opened.
- **Encryption.** `/Encrypt` is read as soon as the cross-reference data is, before the catalog or any object stream is
  trusted (a file rebuilt by scanning authenticates before its object streams are opened). `PdfOpenOptions.Password`
  is tried as user then owner password; the empty password is always tried. A wrong or missing password is
  `PdfPasswordException`; an unsupported or malformed handler is `PdfEncryptionException`. Everything parsed before
  authentication is dropped from the cache. Strings are decrypted when their object is parsed, stream data when it is
  read (before filters), under the object number and generation of the containing indirect object. Exempt: the
  `/Encrypt` dictionary, cross-reference streams, strings inside object streams (the stream is encrypted as a whole) and
  the trailer `/ID`. A `Crypt` filter names the crypt filter to use (`Identity` leaves the data alone) and is not part of
  the decode chain. A metadata stream is left in the clear when `/EncryptMetadata` is false. Damaged ciphertext (bad
  padding, partial block) decrypts as far as it goes with a warning.

## The rebuild scan

One pass over the file, byte by byte, no whole-file buffer:

- `obj` preceded by `<number> <generation>` and not by `end`, followed by a delimiter or white space, starts an
  object. The object is parsed; for a stream the body is skipped using a trusted `/Length` or the next `endstream`, so
  bytes inside stream data (an embedded PDF, say) are not taken for objects.
- The last definition of a number wins. `trailer` followed by a dictionary is a trailer candidate; so is the
  dictionary of any `/Type /XRef` stream.
- The trailer used is the last candidate with a `/Root`; `Root`, `Info`, `ID` and `Encrypt` are each taken from the
  latest candidate that has them. With no candidate, the last `/Type /Catalog` object is used (warning).
- Objects inside object streams are added after the scan. An object stream counts as defined where it sits in the
  file, so a later direct definition of the same number wins over its contents and a later object stream wins over an
  earlier definition. An object stream that cannot be read is skipped (warning).
- The result is a single revision. More than `PdfLimits.MaxObjects` objects raises `PdfLimitException`.

## Limits checked while reading

| Limit | Checked |
|---|---|
| `MaxObjects` | declared subsection and `/Index` counts before reading entries; headers during the rebuild scan; page-tree nodes |
| `MaxNestingDepth` | on entering each array or dictionary level (explicit stack, no recursion) |
| `MaxDecodedStreamBytes`, `MaxExpansionRatio` | by the filters while they run; raw stored size before reading it |
| `MaxRecursionDepth` | reference-to-reference chains |

## The typed layer

Name trees, number trees, outlines, form field trees and action chains are walked iteratively with a visited set, so a
node reached twice (a cycle) is skipped with one warning, and with `MaxObjects` / `MaxNestingDepth` bounds. A malformed
tree key, a non-dictionary page or annotation entry, a destination that leads nowhere, an unparseable date or an XMP
packet that is not well-formed XML never throws: the value reads as null or empty (XMP keeps its raw bytes). DTDs in XMP
are refused, never expanded. Stream-backed text (embedded files, XFA, XMP) is size-checked against the limits before it is
read; over the limit the item is ignored with a warning, except `PdfEmbeddedFile.ReadContent`, which throws
`PdfLimitException` because the caller asked for it.
