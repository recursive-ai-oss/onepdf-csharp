using OnePdf.PdfA;
using OnePdf.PdfUA;
using OnePdf.Writing;

namespace OnePdf;

public sealed partial class PdfDocument
{
    /// <summary>
    /// Checks the document against the requirements of ISO 14289-1 (PDF/UA-1) that OnePDF can verify from the file structure and
    /// returns every violation found (an empty list means none of the checks found a problem). Nothing is changed, so the result is the
    /// document as it stands: a missing XMP identification is reported here, whereas <see cref="PreparePdfUA"/> and a save with
    /// <see cref="PdfSaveOptions.PdfUA"/> add it. This is not a validator and not a statement of conformance; <c>docs/pdfua.md</c> lists
    /// what is checked and what is not.
    /// </summary>
    /// <param name="cancellationToken">Checked between pages and structure elements.</param>
    /// <exception cref="PdfLimitException">The document has more structure elements or content operators than <see cref="PdfLimits"/> allows.</exception>
    public IReadOnlyList<PdfUAViolation> CheckPdfUA(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return new PdfUAChecker(this, structureOnly: false, cancellationToken).Run();
    }

    /// <summary>
    /// Makes the repairs that need no judgement and returns what is still wrong: marks a document with a structure tree as tagged
    /// (<c>/MarkInfo</c>), writes <see cref="PdfUAOptions.Language"/> when the catalog has no <c>/Lang</c>, sets
    /// <c>/ViewerPreferences /DisplayDocTitle</c> when there is a title, sets <c>/Tabs /S</c> on pages with annotations, gives link
    /// annotations a <c>/Contents</c> from their structure element's alternate text or their address, embeds Liberation faces for the
    /// standard fonts OnePDF added (<see cref="PdfUAOptions.EmbedLibraryFonts"/>), and writes the XMP packet with the PDF/UA-1
    /// identification and the title. It never tags content, invents titles or writes alternate text. The changes are made to the document
    /// in memory; calling it again changes nothing more.
    /// </summary>
    /// <param name="options">Language and font options; null means defaults.</param>
    /// <param name="cancellationToken">Checked between pages.</param>
    /// <exception cref="InvalidOperationException">The document is not editable.</exception>
    public IReadOnlyList<PdfUAViolation> PreparePdfUA(PdfUAOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        PdfUAPreparer.Prepare(this, options ?? new PdfUAOptions(), null, cancellationToken);
        return new PdfUAChecker(this, structureOnly: false, cancellationToken).Run();
    }
}

internal static class PdfUAProcessor
{
    /// <summary>Prepares and checks the document for a save with <see cref="PdfSaveOptions.PdfUA"/> (alone, or together with PDF/A) and returns the options the saver should use.</summary>
    public static PdfSaveOptions PrepareForSave(PdfDocument doc, PdfSaveOptions options, CancellationToken ct)
    {
        PdfUAOptions ua = options.PdfUA!;
        PdfAOptions? pa = options.PdfA;
        if (pa is not null && options.Encryption == PdfEncryptionAction.Set)
        {
            throw new ArgumentException("PDF/A files cannot be encrypted (ISO 19005-2, 6.1.3).", nameof(options));
        }

        PdfSaveOptions result;
        var failures = new List<PdfUAViolation>();
        if (pa is not null)
        {
            PdfAPreparer.Prepare(doc, pa, ct, pdfUA: true);
        }

        PdfUAPreparer.Prepare(doc, ua, pa, ct);
        failures.AddRange(new PdfUAChecker(doc, structureOnly: false, ct).Run());
        if (options.Encryption == PdfEncryptionAction.Set && options.NewEncryption is { } enc && (enc.Permissions & PdfPermissions.ExtractForAccessibility) == 0)
        {
            failures.Add(new PdfUAViolation("26", "7.16", "The new encryption clears the accessibility-extraction permission, which blocks assistive technology.", -1, -1));
        }

        if (pa is not null)
        {
            IReadOnlyList<PdfAViolation> av = new PdfAChecker(doc, pa, saving: true, decrypting: options.Encryption == PdfEncryptionAction.Remove, ct).Run();
            if (av.Count > 0 || failures.Count > 0)
            {
                var all = new List<PdfAViolation>(av);
                foreach (PdfUAViolation v in failures)
                {
                    all.Add(new PdfAViolation("PDF/UA-1 " + v.Clause, v.Message, v.ObjectNumber, v.PageIndex));
                }

                throw new PdfAException(pa.Name + " with PDF/UA-1", all);
            }

            result = new PdfSaveOptions { ExactVersion = "1.7", PdfA = pa };
        }
        else
        {
            if (failures.Count > 0)
            {
                throw new PdfUAException(failures);
            }

            result = new PdfSaveOptions { MinimumVersion = options.MinimumVersion };
        }

        return new PdfSaveOptions
        {
            XrefFormat = options.XrefFormat,
            UseObjectStreams = options.UseObjectStreams,
            CompressStreams = options.CompressStreams,
            MinimumVersion = options.MinimumVersion,
            ExactVersion = result.ExactVersion,
            DocumentId = options.DocumentId,
            Encryption = options.Encryption,
            NewEncryption = options.NewEncryption,
            PdfA = pa,
            PdfUA = ua,
        };
    }
}
