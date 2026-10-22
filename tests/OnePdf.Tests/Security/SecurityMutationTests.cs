using System.Diagnostics;
using System.Text;
using OnePdf.Fuzz;

namespace OnePdf.Tests.Security;

/// <summary>
/// Bounded, deterministic mutation run of the security fuzz target, seeded with the values of real encrypted files, for
/// regressions between real fuzzing campaigns. Anything but a clean return fails the test.
/// </summary>
public class SecurityMutationTests
{
    private static byte[] Seed(string file, byte r, byte v, byte cfm, byte oddLen, string password)
    {
        EncryptedPdf pdf = EncryptedPdf.Load(file);
        var ms = new MemoryStream();
        ms.Write([r, v, cfm, 0, oddLen, 0, 16, 0xFF, 0, 1]);
        foreach (byte[] part in new[] { pdf.Parameters.O, pdf.Parameters.U, pdf.Parameters.OE, pdf.Parameters.UE, pdf.Parameters.Perms })
        {
            ms.Write(part);
        }

        ms.Write(pdf.Id.AsSpan(0, Math.Min(16, pdf.Id.Length)));
        ms.Write(Encoding.Latin1.GetBytes(password));
        return ms.ToArray();
    }

    [Fact]
    public void MutatedInputsNeverThrow()
    {
        var seeds = new List<byte[]>
        {
            Seed("enc-raw-aes-256.pdf", 4, 5, 2, 1, "user-pw"),
            Seed("enc-raw-aes-256-r5.pdf", 3, 5, 2, 1, "owner-pw"),
            Seed("enc-raw-aes-128.pdf", 2, 4, 1, 0, "user-pw"),
            Seed("enc-raw-rc4-128.pdf", 1, 2, 0, 0, "owner-pw"),
            Seed("enc-raw-rc4-40.pdf", 0, 1, 0, 0, "user-pw"),
        };
        foreach (byte[] s in seeds)
        {
            SecurityTarget.Run(s);
        }

        var rnd = new Random(99);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000)
        {
            byte[] m = (byte[])seeds[rnd.Next(seeds.Count)].Clone();
            int edits = rnd.Next(1, 6);
            for (int e = 0; e < edits; e++)
            {
                int pos = rnd.Next(rnd.Next(2) == 0 ? 12 : m.Length);
                m[pos] = (byte)rnd.Next(256);
            }

            if (rnd.Next(4) == 0)
            {
                Array.Resize(ref m, rnd.Next(m.Length + 1));
            }

            SecurityTarget.Run(m);
        }
    }
}
