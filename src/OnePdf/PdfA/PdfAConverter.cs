namespace OnePdf;

/// <summary>
/// Converts existing PDF files to PDF/A-2 or PDF/A-3 (level B or U). The happy path is one call; everything it does is also available in
/// pieces through <see cref="PdfDocument.PreparePdfA"/> and <see cref="PdfDocument.CheckPdfA"/>.
/// </summary>
/// <remarks>
/// <para>
/// A conversion repairs what can be repaired without guessing: the output intent and XMP packet are added, fonts that are not embedded get
/// metric-compatible substitutes, scripts, forbidden actions, hidden annotations and other forbidden features are removed, LZW streams are
/// stored with Flate, encryption is removed, device colours are brought under an output intent. Anything that remains wrong is listed in
/// a <see cref="PdfAException"/> and <b>nothing is written</b>: a file that claims PDF/A when it does not conform is never produced.
/// Page content is not redrawn. The result is checked by OnePDF's own checks, which are not a validator (see <c>docs/pdfa.md</c>).
/// </para>
/// <code>
/// OnePdf.Fonts.FontsPackage.Register();                       // once: Liberation substitutes for unembedded fonts
/// PdfAConversionResult r = PdfAConverter.Convert("in.pdf", "out.pdf");
/// foreach (PdfAChange c in r.Changes) Console.WriteLine(c);
/// </code>
/// </remarks>
public static class PdfAConverter
{
    /// <summary>Converts the file at <paramref name="inputPath"/> and writes PDF/A to <paramref name="outputPath"/>.</summary>
    /// <param name="inputPath">The source file. It is read, never modified.</param>
    /// <param name="outputPath">Where to write the result. It is written to a temporary file next to it and moved into place only when the conversion succeeded; a failed conversion leaves an existing file at this path untouched.</param>
    /// <param name="options">Target and tuning; null means PDF/A-2b with every repair on.</param>
    /// <param name="cancellationToken">Checked between objects and pages.</param>
    /// <returns>What was changed.</returns>
    /// <exception cref="PdfAException">The file cannot be made to conform; <see cref="PdfAException.Violations"/> lists every reason. Nothing is written.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and <see cref="PdfAConversionOptions.Password"/> does not open it.</exception>
    /// <exception cref="PdfFormatException">The file cannot be read.</exception>
    /// <exception cref="ArgumentException">Level A was asked for.</exception>
    public static PdfAConversionResult Convert(string inputPath, string outputPath, PdfAConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        options ??= new PdfAConversionOptions();
        using PdfDocument doc = PdfDocument.Open(inputPath, OpenOptions(options));
        return doc.ConvertToPdfA(outputPath, options, cancellationToken);
    }

    /// <summary>Converts the PDF in <paramref name="input"/> and writes PDF/A to <paramref name="output"/>. Neither stream is closed.</summary>
    /// <param name="input">A readable stream; a seekable one is read in place, any other is copied to memory.</param>
    /// <param name="output">A writable stream. Nothing is written to it when the conversion is refused.</param>
    /// <param name="options">Target and tuning; null means PDF/A-2b with every repair on.</param>
    /// <param name="cancellationToken">Checked between objects and pages.</param>
    /// <returns>What was changed.</returns>
    /// <exception cref="PdfAException">The file cannot be made to conform; nothing is written.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password does not open it.</exception>
    /// <exception cref="PdfFormatException">The file cannot be read.</exception>
    /// <exception cref="ArgumentException">Level A was asked for.</exception>
    public static PdfAConversionResult Convert(Stream input, Stream output, PdfAConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new PdfAConversionOptions();
        using PdfDocument doc = PdfDocument.Open(input, OpenOptions(options) with { LeaveOpen = true });
        return doc.ConvertToPdfA(output, options, cancellationToken);
    }

    private static PdfOpenOptions OpenOptions(PdfAConversionOptions o) => new() { AllowEditing = true, Password = o.Password };
}

public sealed partial class PdfDocument
{
    /// <summary>
    /// Converts this document to PDF/A and writes the result. The document is changed in memory (repairs are applied to it) and must have been opened with
    /// <see cref="PdfOpenOptions.AllowEditing"/>. See <see cref="PdfAConverter"/> for what a conversion does and refuses.
    /// </summary>
    /// <param name="output">A writable stream. Nothing is written to it when the conversion is refused.</param>
    /// <param name="options">Target and tuning; null means PDF/A-2b with every repair on.</param>
    /// <param name="cancellationToken">Checked between objects and pages.</param>
    /// <returns>What was changed.</returns>
    /// <exception cref="PdfAException">The file cannot be made to conform; <see cref="PdfAException.Violations"/> lists every reason. Nothing is written.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="ArgumentException">Level A was asked for.</exception>
    public PdfAConversionResult ConvertToPdfA(Stream output, PdfAConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ThrowIfDisposed();
        options ??= new PdfAConversionOptions();
        PdfA.PdfAConversion.Run(this, options, cancellationToken, out PdfSaveOptions save, out PdfAConversionResult result);
        Save(output, save, cancellationToken);
        return result;
    }

    /// <summary>
    /// Converts this document to PDF/A and writes the result to <paramref name="path"/>, through a temporary file that replaces the target only on success.
    /// </summary>
    /// <param name="path">The output file.</param>
    /// <param name="options">Target and tuning; null means PDF/A-2b with every repair on.</param>
    /// <param name="cancellationToken">Checked between objects and pages.</param>
    /// <returns>What was changed.</returns>
    /// <exception cref="PdfAException">The file cannot be made to conform; nothing is written and an existing file at <paramref name="path"/> is untouched.</exception>
    /// <exception cref="InvalidOperationException">The document was not opened with <see cref="PdfOpenOptions.AllowEditing"/>.</exception>
    /// <exception cref="ArgumentException">Level A was asked for.</exception>
    public PdfAConversionResult ConvertToPdfA(string path, PdfAConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ThrowIfDisposed();
        options ??= new PdfAConversionOptions();
        PdfA.PdfAConversion.Run(this, options, cancellationToken, out PdfSaveOptions save, out PdfAConversionResult result);
        string temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Save(fs, save, cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // the original failure is the one to report
            }

            throw;
        }

        return result;
    }
}
