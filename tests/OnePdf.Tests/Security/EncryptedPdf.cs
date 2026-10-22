using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using OnePdf.Security;

namespace OnePdf.Tests.Security;

/// <summary>
/// Test-only extraction of the /Encrypt values, /ID, one encrypted string and the encrypted streams of a PDF written
/// by qpdf or a similar producer. It reads the text form of the file with regular expressions and does not use the reader.
/// </summary>
internal sealed class EncryptedPdf
{
    public required EncryptionParameters Parameters { get; init; }

    public required byte[] Id { get; init; }

    /// <summary>Hex strings of the /Info dictionary (object number, value bytes).</summary>
    public required List<(int Obj, int Gen, byte[] Value)> InfoStrings { get; init; }

    /// <summary>Flate streams (not XRef, not Metadata).</summary>
    public required List<(int Obj, int Gen, byte[] Data)> Streams { get; init; }

    public static string RepoRoot()
    {
        string dir = Path.GetDirectoryName(SourcePath())!;
        while (!Directory.Exists(Path.Combine(dir, "testdata")))
        {
            dir = Path.GetDirectoryName(dir)!;
        }

        return dir;
    }

    private static string SourcePath([CallerFilePath] string path = "") => path;

    public static EncryptedPdf Load(string fileName) => Parse(File.ReadAllBytes(Path.Combine(RepoRoot(), "testdata", "generated", "pdf", fileName)));

    public static EncryptedPdf LoadVector(string fileName) => Parse(File.ReadAllBytes(Path.Combine(RepoRoot(), "tests", "OnePdf.Tests", "Security", "Vectors", fileName)));

    public static EncryptedPdf Parse(byte[] file)
    {
        string text = Encoding.Latin1.GetString(file);
        Match enc = Regex.Match(text, @"/Encrypt\s+(\d+)\s+\d+\s+R");
        string dict = ObjectDictionary(text, int.Parse(enc.Groups[1].Value, CultureInfo.InvariantCulture));
        Match idm = Regex.Match(text, @"/ID\s*\[\s*<([0-9a-fA-F]*)>");

        string cf = Balanced(dict, "/CF");
        string outer = cf.Length == 0 ? dict : dict.Replace(cf, string.Empty, StringComparison.Ordinal);
        Dictionary<string, CryptFilterSpec>? filters = null;
        if (cf.Length > 0)
        {
            filters = [];
            foreach (Match m in Regex.Matches(cf.Substring(cf.IndexOf("<<", StringComparison.Ordinal) + 2), @"/(\w+)\s*(<<.*?>>)"))
            {
                string body = m.Groups[2].Value;
                Match cfm = Regex.Match(body, @"/CFM\s*/(\w+)");
                Match len = Regex.Match(body, @"/Length\s+(\d+)");
                filters[m.Groups[1].Value] = new CryptFilterSpec(cfm.Success ? cfm.Groups[1].Value : null, len.Success ? int.Parse(len.Groups[1].Value, CultureInfo.InvariantCulture) : null);
            }
        }

        int Int(string key) => Regex.Match(outer, "/" + key + @"\s+(-?\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        byte[] Hex(string key) => Regex.Match(outer, "/" + key + @"\s*<([0-9a-fA-F]*)>") is { Success: true } m ? Convert.FromHexString(m.Groups[1].Value) : [];
        string? Name(string key) => Regex.Match(outer, "/" + key + @"\s*/(\w+)") is { Success: true } m ? m.Groups[1].Value : null;

        var parameters = new EncryptionParameters
        {
            Filter = Name("Filter"),
            V = Int("V"),
            R = Int("R"),
            Length = Regex.IsMatch(outer, @"/Length\s+\d") ? Int("Length") : null,
            O = Hex("O"),
            U = Hex("U"),
            OE = Hex("OE"),
            UE = Hex("UE"),
            Perms = Hex("Perms"),
            P = Int("P"),
            EncryptMetadata = !Regex.IsMatch(outer, @"/EncryptMetadata\s+false"),
            CryptFilters = filters,
            StmF = Name("StmF"),
            StrF = Name("StrF"),
            Eff = Name("EFF"),
        };

        var info = new List<(int, int, byte[])>();
        foreach (Match om in Regex.Matches(text, @"(?<=\n)(\d+) (\d+) obj\s*<<((?:(?!endobj).)*?)>>\s*endobj", RegexOptions.Singleline))
        {
            string body = om.Groups[3].Value;
            if (!body.Contains("/Producer", StringComparison.Ordinal) && !body.Contains("/Author", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match hm in Regex.Matches(body, @"<([0-9a-fA-F]{2,})>"))
            {
                info.Add((int.Parse(om.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(om.Groups[2].Value, CultureInfo.InvariantCulture), Convert.FromHexString(hm.Groups[1].Value)));
            }
        }

        var streams = new List<(int, int, byte[])>();
        foreach (Match sm in Regex.Matches(text, @"(?<=\n)(\d+) (\d+) obj\s*<<((?:(?!stream|endobj).)*?)>>\s*stream\r?\n", RegexOptions.Singleline))
        {
            string d = sm.Groups[3].Value;
            Match lm = Regex.Match(d, @"/Length\s+(\d+)(?!\d|\s+\d+\s+R)");
            if (!lm.Success || d.Contains("/XRef", StringComparison.Ordinal) || d.Contains("/Metadata", StringComparison.Ordinal) || !d.Contains("/FlateDecode", StringComparison.Ordinal))
            {
                continue;
            }

            int len = int.Parse(lm.Groups[1].Value, CultureInfo.InvariantCulture);
            int start = sm.Index + sm.Length;
            streams.Add((int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(sm.Groups[2].Value, CultureInfo.InvariantCulture), file.AsSpan(start, len).ToArray()));
        }

        return new EncryptedPdf { Parameters = parameters, Id = idm.Success ? Convert.FromHexString(idm.Groups[1].Value) : [], InfoStrings = info, Streams = streams };
    }

    private static string ObjectDictionary(string text, int obj)
    {
        Match m = Regex.Match(text, @"(?<=\n)" + obj + @" \d+ obj\s*(<<.*?)\s*endobj", RegexOptions.Singleline);
        return m.Groups[1].Value;
    }

    // Returns "/Key << ... >>" including nested dictionaries, or "".
    private static string Balanced(string s, string key)
    {
        int k = s.IndexOf(key, StringComparison.Ordinal);
        if (k < 0)
        {
            return string.Empty;
        }

        int open = s.IndexOf("<<", k, StringComparison.Ordinal);
        int depth = 0;
        for (int i = open; i < s.Length - 1; i++)
        {
            if (s[i] == '<' && s[i + 1] == '<')
            {
                depth++;
                i++;
            }
            else if (s[i] == '>' && s[i + 1] == '>')
            {
                depth--;
                i++;
                if (depth == 0)
                {
                    return s.Substring(k, i + 1 - k);
                }
            }
        }

        return string.Empty;
    }
}
