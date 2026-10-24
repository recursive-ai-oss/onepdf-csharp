using OnePdf.Internal;
using OnePdf.Writing;

namespace OnePdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Writes the document as a new, complete file. Only objects reachable from the catalog and the Info dictionary are
    /// written, numbered compactly from 1, so unreachable objects, earlier revisions, linearization data and the damage of a
    /// file that had to be repaired are gone. Content that was changed through the mutable view is included; an unchanged
    /// document can be saved too. Encryption is kept, removed or replaced as <see cref="PdfSaveOptions.Encryption"/> says.
    /// </summary>
    /// <remarks>
    /// Do not save to the file the document was read from while it is open. The result depends only on the document, not on
    /// earlier saves. Saving never changes the document.
    /// </remarks>
    /// <param name="output">A writable stream; it does not have to seek and is left open.</param>
    /// <param name="options">Null means defaults (<see cref="PdfSaveOptions"/>).</param>
    /// <param name="cancellationToken">Checked between objects.</param>
    /// <exception cref="PdfFormatException">The document has no catalog.</exception>
    /// <exception cref="PdfLimitException">A bound in <see cref="PdfLimits"/> was exceeded, or the file is too large for a classic table.</exception>
    /// <exception cref="PdfEncryptionException">New encryption cannot be made on this runtime.</exception>
    /// <exception cref="ArgumentException">The options are inconsistent (<see cref="PdfEncryptionAction.Set"/> without settings, or a stream that cannot be written).</exception>
    public void Save(Stream output, PdfSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ThrowIfDisposed();
        if (!output.CanWrite)
        {
            throw new ArgumentException("The stream is not writable.", nameof(output));
        }

        options ??= new PdfSaveOptions();
        if (options.PdfUA is not null)
        {
            options = PdfUAProcessor.PrepareForSave(this, options, cancellationToken);
        }
        else if (options.PdfA is not null)
        {
            options = PdfAProcessor.PrepareForSave(this, options, cancellationToken);
        }

        CreationState.FinishFonts(this);
        using var saver = new FullSaver(this, options, cancellationToken);
        saver.Run(output);
    }

    /// <summary>Like <see cref="Save(Stream, PdfSaveOptions?, CancellationToken)"/>, to a file that is created or replaced.</summary>
    public void Save(string path, PdfSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);
        Save(file, options, cancellationToken);
    }

    /// <summary>
    /// Writes the original file unchanged, followed by an incremental update (ISO 32000-2 7.5.6) holding only the objects that were
    /// changed, added or removed through the mutable view, a cross-reference section for them and a trailer chained to the previous
    /// one with <c>/Prev</c>. The original bytes are an exact prefix of the output, earlier revisions stay readable through
    /// <see cref="Revisions"/>, and the update is encrypted like the document when it is. The cross-reference form follows the newest
    /// revision; the header is not touched.
    /// </summary>
    /// <remarks>
    /// Each call writes the changes made since the document was opened, on top of the original file, so saving twice gives the
    /// same update twice. A file that had to be rebuilt by scanning has no cross-reference data to chain to: use
    /// <see cref="Save(Stream, PdfSaveOptions?, CancellationToken)"/> for it.
    /// </remarks>
    /// <param name="output">A writable stream; it does not have to seek and is left open.</param>
    /// <param name="options">Only <see cref="PdfSaveOptions.CompressStreams"/> applies; encryption must stay <see cref="PdfEncryptionAction.Keep"/>.</param>
    /// <param name="cancellationToken">Checked between objects.</param>
    /// <exception cref="PdfFormatException">The file was rebuilt by scanning on open.</exception>
    /// <exception cref="ArgumentException">The options ask to change the encryption, or the stream is not writable.</exception>
    /// <exception cref="PdfSourceChangedException">The source changed since the document was opened.</exception>
    public void SaveIncremental(Stream output, PdfSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ThrowIfDisposed();
        if (_redacted)
        {
            throw new InvalidOperationException("The document has been redacted. An incremental save would keep the removed content in the file; use Save.");
        }

        options ??= new PdfSaveOptions();
        if (!output.CanWrite)
        {
            throw new ArgumentException("The stream is not writable.", nameof(output));
        }

        if (options.PdfUA is not null)
        {
            throw new ArgumentException("An incremental save cannot make a PDF/UA file: it appends to the original bytes. Use Save.", nameof(options));
        }

        if (options.PdfA is not null)
        {
            throw new ArgumentException("An incremental save cannot make a PDF/A file: it appends to the original bytes. Use Save.", nameof(options));
        }

        if (options.Encryption != PdfEncryptionAction.Keep)
        {
            throw new ArgumentException("An incremental save keeps the encryption of the file; use Save to change it.", nameof(options));
        }

        if (Revisions.Count == 0 || Revisions[^1].XrefOffset < 0)
        {
            throw new PdfFormatException("The file was rebuilt by scanning, so there is no cross-reference section to chain an update to; use Save instead.");
        }

        CreationState.FinishFonts(this);
        byte[] last = new byte[1];
        if (_source.Length > 0)
        {
            _source.EnsureUnchanged();
            _source.Read(_source.Length - 1, last);
        }

        var context = new IncrementalContext
        {
            Get = n => _pinned is not null && _pinned.TryGetValue(n, out PdfObject? o) ? o : PdfNull.Instance,
            GenerationOf = n => _merged!.TryGetValue(n, out XrefEntry e) && e.Type != 2 ? e.B : 0,
            FreeHead = _merged!.TryGetValue(0, out XrefEntry head) && head.Type == 0 ? head.A : 0,
            Size = Math.Max(ObjectCount, _nextObjectNumber),
            LastByte = last[0],
        };
        new IncrementalSaver(this, options, cancellationToken).Run(output, _source, _bias, DirtyNumbers(), context);
    }
}
