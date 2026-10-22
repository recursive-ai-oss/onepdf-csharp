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
  (warning). `/N` and `/First` are validated against the decoded size before anything is allocated.
- **References.** A reference to a missing, free or null object resolves to null. A chain of references to references
  longer than `PdfLimits.MaxRecursionDepth` (or a loop) reads as null (warning). A load that needs itself is a
  `PdfFormatException`.
- **Page tree.** `PageCount` walks `/Pages` iteratively. A node reached twice is skipped (warning); a kid that is not a
  dictionary is skipped (warning); a node without `/Kids` is a page unless `/Type` is `/Pages`.
- **External streams.** A stream with `/F` reads as empty (warning). Nothing outside the document is ever opened.
- **Encryption.** Not decrypted yet. `IsEncrypted` is true and an Info note is recorded.

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
