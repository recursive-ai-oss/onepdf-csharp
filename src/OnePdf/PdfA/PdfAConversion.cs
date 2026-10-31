using System.Linq;

namespace OnePdf.PdfA;

/// <summary>
/// The conversion pipeline behind <see cref="PdfDocument.ConvertToPdfA(Stream, PdfAConversionOptions?, CancellationToken)"/>: repairs that remove or
/// rewrite what ISO 19005 forbids, then the preparers that add what it requires, then the checker. The repairs are
/// the part that needs judgement about what a reader loses; each one is counted in a <see cref="ChangeLog"/> so the caller sees it.
/// </summary>
internal static class PdfAConversion
{
    public static void Run(PdfDocument doc, PdfAConversionOptions options, CancellationToken ct, out PdfSaveOptions save, out PdfAConversionResult result)
    {
        if (options.Conformance == PdfAConformance.A)
        {
            throw new ArgumentException("Level A needs a logical structure that a conversion does not invent. Tag the document first (see the tagging API), then save with PdfSaveOptions.PdfA.", nameof(options));
        }

        doc.EnsureEditable(0);
        var log = new ChangeLog();
        bool encrypted = doc.IsEncrypted;
        if (encrypted)
        {
            log.Add(PdfAChangeKind.EncryptionRemoved, "Encryption was removed.");
        }

        PdfAOptions pa = options.ToPdfAOptions();
        var repairs = new ConversionRepairs(doc, options, log, ct);
        if (options.RemoveForbiddenFeatures)
        {
            repairs.RemoveForbiddenFeatures();
            repairs.GiveResourcesToForms();
        }

        repairs.RepairEmbeddedFiles();
        repairs.EmbedFonts();
        repairs.RebuildMetadata();
        repairs.AuditFonts();
        if (options.Conformance == PdfAConformance.U)
        {
            repairs.AddUnicodeMaps();
        }

        // Which device colours the file uses decides the output intent (the probe is a plain check run; its findings are not used).
        var probe = new PdfAChecker(doc, pa, saving: true, decrypting: encrypted, ct);
        probe.Run();
        (bool rgb, bool gray, bool cmyk) = probe.DeviceColours;
        repairs.ChooseColour(rgb, gray, cmyk, probe.CmykNeedsIntent);
        if (options.RemoveForbiddenFeatures)
        {
            repairs.ResetOverprintMode();
        }

        int before = doc.Pages.Count;
        PdfAPreparer.Prepare(doc, pa, ct);
        if (options.RemoveForbiddenFeatures)
        {
            repairs.AfterPrepare();
        }

        if (doc.Pages.Count != before)
        {
            throw new InvalidOperationException("The page count changed during conversion.");
        }

        if (options.Language is { Length: > 0 } lang && !doc.Catalog.ContainsKey("Lang"))
        {
            doc.Catalog.SetEntry("Lang", PdfString.FromText(lang));
        }

        List<PdfAViolation> violations = [.. new PdfAChecker(doc, pa, saving: true, decrypting: encrypted, ct).Run()];
        foreach ((string clause, string finding) in repairs.ExtraFindings)
        {
            violations.Add(new PdfAViolation(clause, finding, -1, -1));
        }
        if (violations.Count > 0)
        {
            throw new PdfAException(pa.Name, violations);
        }

        // ISO 19005-2 6.1.3: the trailer has a non-empty /ID. A file without one gets a fresh identifier (a caller who wants the same bytes for the same input passes DocumentId).
        byte[]? documentId = options.DocumentId;
        if (documentId is not { Length: > 0 } && !(doc.Trailer.GetArray("ID") is { Count: >= 1 } ids && ids[0] is PdfString { Bytes.Length: > 0 }))
        {
            documentId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        }

        save = new PdfSaveOptions
        {
            PdfA = pa,
            Encryption = encrypted ? PdfEncryptionAction.Remove : PdfEncryptionAction.Keep,
            DocumentId = documentId,
        };
        result = new PdfAConversionResult(pa.Name, log.ToChanges(), log.Warnings);
    }
}

/// <summary>Counts of what a conversion changed, by kind, with the first description of each kind.</summary>
internal sealed class ChangeLog
{
    private readonly Dictionary<PdfAChangeKind, (string Description, int Count)> _counts = [];
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings => _warnings;

    public void Add(PdfAChangeKind kind, string description, int count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        _counts[kind] = _counts.TryGetValue(kind, out (string Description, int Count) e) ? (e.Description, e.Count + count) : (description, count);
    }

    public void Warn(string message)
    {
        if (_warnings.Count < 200)
        {
            _warnings.Add(message);
        }
    }

    public IReadOnlyList<PdfAChange> ToChanges() =>
        [.. _counts.OrderBy(static e => e.Key).Select(static e => new PdfAChange(e.Key, e.Value.Description, e.Value.Count))];
}
