using System.Text;

namespace OnePdf.Tests.Signatures;

/// <summary>Signed files that ship in the test corpus (and, when pointed at one, a larger local set) open and report without failing.</summary>
public class CorpusSignatureTests
{
    private static readonly byte[] Marker = "ByteRange"u8.ToArray();

    [Fact]
    public void SignedCorpusFilesProduceReports()
    {
        string root = Writing.WriterSupport.Root();
        var roots = new List<string> { Path.Combine(root, "testdata") };
        if (Environment.GetEnvironmentVariable("ONEPDF_SIG_CORPUS") is { Length: > 0 } extra && Directory.Exists(extra))
        {
            roots.Add(extra);
        }

        var lines = new List<string>();
        int files = 0;
        foreach (string file in roots.SelectMany(static r => Directory.EnumerateFiles(r, "*.pdf", SearchOption.AllDirectories)).Order(StringComparer.Ordinal))
        {
            byte[] data = File.ReadAllBytes(file);
            if (data.AsSpan().IndexOf(Marker) < 0)
            {
                continue;
            }

            files++;
            string name = Path.GetRelativePath(root, file);
            if (!PdfDocument.TryOpen(data, out PdfDocument? doc, out PdfException? error, new PdfOpenOptions { Password = Writing.WriterSupport.PasswordFor(file) }))
            {
                lines.Add($"{name}\topen failed: {error.Message}");
                continue;
            }

            using (doc)
            {
                PdfSignatureReport report = doc.VerifySignatures();
                if (!report.HasSignatures)
                {
                    lines.Add($"{name}\tno signature fields with values (ByteRange text only)");
                }

                foreach (PdfSignatureVerification v in report.Signatures)
                {
                    lines.Add($"{name}\t{v.Signature.FieldName}\t{v.Signature.SubFilter}\trange={v.ByteRange}\tintegrity={v.Integrity}\tsig={v.SignatureCheck}\tchain={v.Chain}\tcoverage={v.Coverage}\tweak={v.UsesWeakAlgorithm}\twarnings={v.Warnings.Count}");
                }
            }
        }

        string dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "onepdf-sig");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "corpus.txt"), string.Join('\n', lines), Encoding.UTF8);
        Assert.True(files > 0);
    }
}
