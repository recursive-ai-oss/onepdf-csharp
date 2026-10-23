# Writing

Documents open read-only by default. A document opened with `PdfOpenOptions.AllowEditing = true` can be changed and saved, in full or as an incremental update.

```csharp
using var doc = PdfDocument.Open("in.pdf", new PdfOpenOptions { AllowEditing = true });
doc.SetInfo("Title", "Quarterly report");
doc.Pages[0].Dictionary.SetEntry("Rotate", new PdfInteger(90));
doc.SaveIncremental(File.Create("out-update.pdf"));                       // original bytes + an update
doc.Save("out-clean.pdf", new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream, CompressStreams = true });
```

## The mutable view

- **Objects.** `PdfDictionary.SetEntry`/`RemoveEntry` (null removes), `PdfArray.Append`/`Insert`/`SetItem`/`RemoveAt`/`Clear`, `PdfStream.SetData` (unfiltered or Flate) and `SetStoredBytes` (bytes exactly as given; you keep `/Filter` in step). The simple types have public constructors (`new PdfName("X")`, `new PdfInteger(1)`, `PdfString.FromText("é")`, ...); `new PdfDictionary()` and `new PdfArray()` make containers that belong to no document until they are stored in one.
- **Indirect objects.** `doc.AddObject(value)` gives a new object the next number and returns its `PdfReference`; `SetObject(number, value)` replaces an object's content; `RemoveObject(number)` frees it; `doc.CreateStream(data, compress)` makes a stream for the document. An object that already is indirect is stored *by reference* when you put it into a container, and a loose stream is added automatically.
- **Metadata.** `SetInfo(key, text)` and `SetXmp(bytes)` create the Info dictionary or the metadata stream when missing and refresh `Info` and `Xmp`.
- **Rules.** Values of another document are refused (`ArgumentException`: copy them first); objects of an earlier revision (`doc.Revisions[i].GetObject`) and objects of a read-only document are refused (`InvalidOperationException`). The null object is not stored: it removes.
- **What is pinned.** An editable document keeps every object it has read, so a change cannot be lost when the object cache drops an entry; `ObjectCacheBytes` no longer bounds memory for parsed objects (decoded streams stay cached). A change marks the *indirect object it lives in* as changed, however deeply nested it is.
- **Concurrency.** Any number of threads may read an editable document, as with a read-only one. Changing it is for one thread at a time and not concurrent with reading or saving. Views built from objects earlier (`Pages`, outlines, fonts) are not refreshed by later changes; take them again. `Info` and `Xmp` are.

## Full save

`doc.Save(Stream | path, PdfSaveOptions?, CancellationToken)` writes a new file from the catalog and the Info dictionary:

- Objects are walked once, numbered 1, 2, 3 … in the order they are met, and written when met. Unreachable objects, earlier revisions, linearization and hint data, free entries and the damage of a repaired file are gone. `/Length` is always written directly. Nothing but the queue of references still to write is kept in memory. Nesting is walked with an explicit stack and refused beyond `PdfLimits.MaxNestingDepth`.
- **Cross-reference form.** `Table` (a classic `xref` table, no object streams), `Stream` (a cross-reference stream with the "up" PNG predictor and, unless `UseObjectStreams` is false, object streams of up to 100 objects or 1 MiB), or `Auto` (the newest revision's form). The catalog is always a plain object, and so are streams and the `/Encrypt` dictionary: readers that need the catalog before they have the key must not have to open an object stream first.
- **Version.** The highest of the document's, `MinimumVersion` and what is needed: 1.5 for cross-reference streams, 1.6 for AES-128, 2.0 for AES-256. A header is rewritten with the binary comment line.
- **Streams** keep their filters and bytes. `CompressStreams` Flate-compresses streams with no filter (not metadata, not when it would not shrink them). A `/Crypt` filter, which only named the decryption just undone, is dropped.
- **`/ID`.** Kept as it is; a document without one gets two random strings.
- **Repaired files** save as clean files; dangling references become `null`.
- **Limits.** `PdfLimits.MaxObjects` bounds the output; a classic table beyond 10 digits of offset throws `PdfLimitException` (use `Stream`). A stream larger than `MaxDecodedStreamBytes` throws `PdfLimitException` as when reading it.

## Incremental save

`doc.SaveIncremental(Stream, PdfSaveOptions?, CancellationToken)` copies the original file byte for byte, then appends the objects changed, added or removed since the document was opened (changed objects keep number and generation; removed ones become free entries chained onto the existing free list), a cross-reference section for them only, and a trailer with `/Prev` to the previous section (ISO 32000-2 7.5.6). It continues the newest revision's form (table or stream; a hybrid file gets a table). The update is encrypted like the document. `/ID`'s first string stays and the second changes. Offsets are written relative to the header when the file has junk in front of it, as the file itself does. `doc.Revisions` shows the old and new revisions. Each call writes the changes since *open*, on top of the original file.

An update goes on top of the file as it is. A file that needed repair on open may read differently with an update behind it (a stream whose end was found by scanning can run on into the update), so use `Save` for such files. A file that was rebuilt by scanning has no section to chain to: `SaveIncremental` throws `PdfFormatException`; use `Save`.

## Encryption on write

`PdfSaveOptions.Encryption`: `Keep` (default; the same key, passwords and dictionary, everything re-encrypted under the new numbers; a plain document stays plain), `Remove` (the document is already open, so access is already granted) or `Set` with `NewEncryption` (`PdfEncryptionOptions`: user and owner password, permission flags, `Aes128` = revision 4 or `Aes256` = revision 6, `EncryptMetadata`). An empty owner password means the user password. Reserved bits of `/P` are set as ISO 32000-2 Table 22 requires. RC4 is only ever written by keeping an RC4 file (a full save, or an incremental one). OnePDF records permission flags and does not enforce them. Output with AES is not byte-identical between runs (random IVs and salts).

## Not covered

Page operations and copying pages between documents are in [page-operations.md](page-operations.md); content creation is in [content-creation.md](content-creation.md) and signing in [signatures.md](signatures.md). Linearized output and public-key encryption are later work.
