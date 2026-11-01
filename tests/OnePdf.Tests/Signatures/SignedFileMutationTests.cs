using System.Text;
using static OnePdf.Tests.Signatures.SigningFixture;

namespace OnePdf.Tests.Signatures;

/// <summary>
/// Damaged signed files: whatever <see cref="PdfDocument.TryOpen(ReadOnlyMemory{byte}, out PdfDocument?, out PdfException?, PdfOpenOptions?)"/>
/// accepts, the read-only structure accessors must answer (possibly with less) instead of throwing.
/// </summary>
public class SignedFileMutationTests
{
    private static byte[] SignedSeed(bool certifyAndFields)
    {
        using PdfDocument doc = Editable(UnsignedPdf());
        using var ms = new MemoryStream();
        doc.Sign(ms, Credential(TestPki.Shared.RsaSigner), certifyAndFields
            ? new PdfSigningOptions { FieldName = "Sig2", Certify = PdfCertificationLevel.FormFilling, Reason = "r" }
            : null);
        return ms.ToArray();
    }

    private static readonly (string Name, Func<PdfDocument, object?> Read)[] Accessors =
    [
        ("GetSignatures", static d => d.GetSignatures()),
        ("VerifySignatures", static d => d.VerifySignatures(Trust)),
        ("PageCount", static d => d.PageCount),
        ("Pages", static d => d.Pages.Count),
        ("Form", static d => d.Form?.AllFields.Count),
        ("Info", static d => d.Info.Title),
        ("Xmp", static d => d.Xmp),
        ("Outline", static d => d.Outline.Count),
        ("PageLabels", static d => d.PageLabels),
        ("EmbeddedFiles", static d => d.EmbeddedFiles.Count),
        ("JavaScript", static d => d.JavaScript.Count),
        ("IsTagged", static d => d.IsTagged),
        ("Language", static d => d.Language),
        ("StructureTreeRoot", static d => d.StructureTreeRoot),
        ("Revisions", static d => d.Revisions.Count),
    ];

    private static IEnumerable<byte[]> Mutants(byte[] seed, int rngSeed)
    {
        for (int cut = 0; cut < seed.Length; cut += cut < seed.Length - 700 ? 13 : 1)
        {
            yield return seed.AsSpan(0, cut).ToArray();
        }

        var rng = new Random(rngSeed);
        for (int i = 0; i < 2500; i++)
        {
            byte[] m = (byte[])seed.Clone();
            // Most flips land in the structural tail (trailer, cross-reference section, update objects), the rest anywhere.
            int at = i % 3 == 0 ? rng.Next(seed.Length) : seed.Length - 1 - rng.Next(Math.Min(900, seed.Length));
            m[at] = (byte)rng.Next(256);
            yield return m;
        }

        // Aim at the number after "/Root".
        string text = Encoding.Latin1.GetString(seed);
        for (int from = 0; (from = text.IndexOf("/Root ", from, StringComparison.Ordinal)) >= 0; from++)
        {
            for (int d = 0; d < 10; d++)
            {
                byte[] m = (byte[])seed.Clone();
                m[from + 6] = (byte)('0' + d);
                yield return m;
            }
        }
    }

    [Fact]
    public void AcceptedFilesNeverThrowFromReadOnlyAccessors()
    {
        var failures = new SortedDictionary<string, int>();
        int accepted = 0;
        int total = 0;
        foreach (bool variant in new[] { false, true })
        {
            byte[] seed = SignedSeed(variant);
            foreach (byte[] mutant in Mutants(seed, variant ? 7 : 3))
            {
                total++;
                if (!PdfDocument.TryOpen(mutant, out PdfDocument? doc, out _))
                {
                    continue;
                }

                accepted++;
                using (doc)
                {
                    foreach ((string name, Func<PdfDocument, object?> read) in Accessors)
                    {
                        try
                        {
                            read(doc);
                        }
                        catch (PdfException e) when (e is PdfFormatException or PdfLimitException)
                        {
                            string key = name + ": " + e.GetType().Name + ": " + e.Message;
                            failures[key] = failures.GetValueOrDefault(key) + 1;
                        }
                    }
                }
            }
        }

        Assert.True(accepted > 500, $"only {accepted} of {total} mutants opened; the loop is not exercising anything");
        Assert.True(failures.Count == 0, $"{failures.Values.Sum()} accessor calls threw on {accepted} accepted files of {total}: " + string.Join(" | ", failures.Select(static f => $"{f.Value}x {f.Key}")));
    }

    [Fact]
    public void RootPointingNowhereFallsBackToTheCatalogObjectOrIsRejected()
    {
        byte[] good = UnsignedPdf();
        string text = Encoding.Latin1.GetString(good);
        string badRoot = text.Replace("/Root 1 0 R", "/Root 99 0 R", StringComparison.Ordinal);
        Assert.NotEqual(text, badRoot);

        // The catalog object is still in the file: it is used, with a diagnostic.
        Assert.True(PdfDocument.TryOpen(Encoding.Latin1.GetBytes(badRoot), out PdfDocument? doc, out PdfException? error), error?.Message);
        using (doc)
        {
            Assert.Empty(doc.GetSignatures());
            Assert.Equal(1, doc.PageCount);
            Assert.Contains(doc.Diagnostics, static d => d.Message.Contains("/Root", StringComparison.Ordinal));
        }

        // No catalog object at all: nothing to read, so the open itself fails (and nothing throws later).
        string noCatalog = badRoot.Replace("/Type /Catalog", "/Type /Other", StringComparison.Ordinal);
        Assert.False(PdfDocument.TryOpen(Encoding.Latin1.GetBytes(noCatalog), out _, out PdfException? e2));
        Assert.Contains("no catalog", e2!.Message, StringComparison.Ordinal);
    }
}
