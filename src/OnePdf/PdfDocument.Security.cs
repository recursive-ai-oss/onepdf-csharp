using OnePdf.Internal;
using OnePdf.Security;

namespace OnePdf;

/// <summary>
/// The user access permissions of an encrypted document (ISO 32000-2 Table 22). OnePDF reports them and does not enforce
/// them: honouring them is the application's job.
/// </summary>
[Flags]
public enum PdfPermissions : uint
{
    /// <summary>No permission bit is set.</summary>
    None = 0,

    /// <summary>Print the document (revision 3 and later: possibly only at low resolution, see <see cref="PrintHighQuality"/>).</summary>
    Print = 1u << 2,

    /// <summary>Modify the contents other than by annotations, form filling and page assembly.</summary>
    ModifyContents = 1u << 3,

    /// <summary>Copy or extract text and graphics.</summary>
    CopyContent = 1u << 4,

    /// <summary>Add or modify annotations (and fill forms when <see cref="FillForms"/> is also set).</summary>
    ModifyAnnotations = 1u << 5,

    /// <summary>Fill in existing form fields (revision 3 and later).</summary>
    FillForms = 1u << 8,

    /// <summary>Extract text and graphics for accessibility (revision 3 and later).</summary>
    ExtractForAccessibility = 1u << 9,

    /// <summary>Insert, rotate or delete pages and create bookmarks (revision 3 and later).</summary>
    AssembleDocument = 1u << 10,

    /// <summary>Print a faithful high-quality copy (revision 3 and later).</summary>
    PrintHighQuality = 1u << 11,

    /// <summary>Every permission defined above.</summary>
    All = Print | ModifyContents | CopyContent | ModifyAnnotations | FillForms | ExtractForAccessibility | AssembleDocument | PrintHighQuality,
}

/// <summary>Which password opened a document.</summary>
public enum PdfAccessLevel
{
    /// <summary>The document is not encrypted.</summary>
    NotEncrypted = 0,

    /// <summary>The user password matched, or the empty user password was accepted.</summary>
    User = 1,

    /// <summary>The owner password matched.</summary>
    Owner = 2,
}

/// <summary>The cipher an encrypted document uses for its streams.</summary>
public enum PdfCipher
{
    /// <summary>No encryption.</summary>
    None = 0,

    /// <summary>RC4, 40 to 128 bit key.</summary>
    Rc4 = 1,

    /// <summary>AES with a 128-bit key (CBC).</summary>
    Aes128 = 2,

    /// <summary>AES with a 256-bit key (CBC).</summary>
    Aes256 = 3,
}

/// <summary>What the <c>/Encrypt</c> dictionary of an opened document says, and how the document was opened.</summary>
public sealed class PdfEncryptionInfo
{
    internal PdfEncryptionInfo(int version, int revision, int keyBits, PdfCipher cipher, bool encryptMetadata, PdfAccessLevel access, bool? permsValid, int rawPermissions)
    {
        Version = version;
        Revision = revision;
        KeyLengthBits = keyBits;
        Cipher = cipher;
        EncryptMetadata = encryptMetadata;
        AccessLevel = access;
        PermsEntryValid = permsValid;
        RawPermissions = rawPermissions;
    }

    /// <summary>The algorithm version <c>/V</c> (1, 2, 4 or 5), inferred from the revision when missing.</summary>
    public int Version { get; }

    /// <summary>The standard security handler revision <c>/R</c> (2 to 6).</summary>
    public int Revision { get; }

    /// <summary>The file key length in bits.</summary>
    public int KeyLengthBits { get; }

    /// <summary>The cipher used for streams.</summary>
    public PdfCipher Cipher { get; }

    /// <summary>False when the metadata stream is stored in the clear (<c>/EncryptMetadata false</c>).</summary>
    public bool EncryptMetadata { get; }

    /// <summary>Whether the user or the owner password was matched.</summary>
    public PdfAccessLevel AccessLevel { get; }

    /// <summary>For revision 5 and 6: whether the <c>/Perms</c> entry agrees with <c>/P</c>. Null when not checked. A disagreement is reported, the file still opens.</summary>
    public bool? PermsEntryValid { get; }

    /// <summary>The low 32 bits of <c>/P</c> as written.</summary>
    public int RawPermissions { get; }
}

public sealed partial class PdfDocument
{
    private SecurityDecryptor? _decryptor;
    private int _encryptObjNum = -1;
    private bool _encryptionDone;
    private PdfEncryptionInfo? _encryption;
    private PdfPermissions _permissions = PdfPermissions.All;

    /// <summary>
    /// The permission flags of the document: <see cref="PdfPermissions.All"/> when it is not encrypted, otherwise the
    /// <c>/P</c> bits. Reported only; OnePDF never enforces them, whichever password opened the file.
    /// </summary>
    public PdfPermissions Permissions => _permissions;

    /// <summary>Which password opened the document; <see cref="PdfAccessLevel.NotEncrypted"/> for a plain file.</summary>
    public PdfAccessLevel AccessLevel => _encryption?.AccessLevel ?? PdfAccessLevel.NotEncrypted;

    /// <summary>Details of the encryption, or null when the document is not encrypted.</summary>
    public PdfEncryptionInfo? Encryption => _encryption;

    internal SecurityDecryptor? Decryptor => _decryptor;

    /// <summary>
    /// Authenticates against the <c>/Encrypt</c> dictionary, if the trailer has one, and installs the decryptor. Runs as soon as
    /// the cross-reference data is read, before any object that could be encrypted is trusted, and drops everything parsed
    /// before (the object cache) because those objects were read as stored.
    /// </summary>
    private void SetupEncryption()
    {
        if (_encryptionDone || Trailer.GetRaw("Encrypt") is not { } raw)
        {
            return;
        }

        _encryptObjNum = raw is PdfReference r ? r.Number : -1;
        PdfDictionary? enc = Trailer.GetDictionary("Encrypt");
        if (enc is null)
        {
            _diag.Warn("The trailer's /Encrypt does not lead to a dictionary; the document was read as unencrypted.");
            _encryptionDone = true;
            return;
        }

        EncryptionParameters parameters = ReadEncryptionParameters(enc);
        byte[] id = Trailer.GetArray("ID") is { Count: > 0 } ids && ids[0] is PdfString idString ? idString.Bytes.ToArray() : [];
        SecurityResult result;
        try
        {
            result = StandardSecurityHandler.Authenticate(parameters, id, _options.Password);
        }
        catch (SecurityUnsupportedException e)
        {
            throw new PdfEncryptionException(e.Message, e) { ObjectNumber = _encryptObjNum };
        }

        if (result.Outcome == SecurityOutcome.Failed || result.Decryptor is null)
        {
            throw new PdfPasswordException(string.IsNullOrEmpty(_options.Password)
                ? "The document is encrypted and needs a password."
                : "The password does not open the document.");
        }

        _decryptor = result.Decryptor;
        _permissions = (PdfPermissions)(uint)result.Permissions;
        PdfAccessLevel level = result.Outcome == SecurityOutcome.Owner ? PdfAccessLevel.Owner : PdfAccessLevel.User;
        bool? perms = result.PermsCheck switch { PermsCheck.Valid => true, PermsCheck.Invalid => false, _ => null };
        _encryption = DescribeEncryption(parameters, level, perms, result.RawPermissions);
        Cache = new ObjectCache(Math.Max(0, _options.ObjectCacheBytes));
        _encryptionDone = true;

        if (result.Notes != SecurityNotes.None)
        {
            _diag.Info($"The encryption dictionary needed leniency ({result.Notes}).");
        }

        if (perms == false)
        {
            _diag.Info("The /Perms entry of the encryption dictionary disagrees with /P; the document was opened anyway.");
        }
    }

    private static EncryptionParameters ReadEncryptionParameters(PdfDictionary enc)
    {
        Dictionary<string, CryptFilterSpec>? filters = null;
        if (enc.GetDictionary("CF") is { } cf)
        {
            filters = new Dictionary<string, CryptFilterSpec>(StringComparer.Ordinal);
            foreach (string name in cf.Keys)
            {
                if (cf.GetDictionary(name) is { } f)
                {
                    filters[name] = new CryptFilterSpec(f.GetName("CFM"), f.ContainsKey("Length") ? (int)Math.Clamp(f.GetInt("Length"), 0, int.MaxValue) : null);
                }
            }
        }

        return new EncryptionParameters
        {
            Filter = enc.GetName("Filter"),
            V = (int)Math.Clamp(enc.GetInt("V"), 0, int.MaxValue),
            R = (int)Math.Clamp(enc.GetInt("R"), 0, int.MaxValue),
            Length = enc.ContainsKey("Length") ? (int)Math.Clamp(enc.GetInt("Length"), 0, int.MaxValue) : null,
            O = Bytes(enc, "O"),
            U = Bytes(enc, "U"),
            OE = Bytes(enc, "OE"),
            UE = Bytes(enc, "UE"),
            Perms = Bytes(enc, "Perms"),
            P = enc.GetInt("P"),
            EncryptMetadata = enc.GetBool("EncryptMetadata", true),
            CryptFilters = filters,
            StmF = enc.GetName("StmF"),
            StrF = enc.GetName("StrF"),
            Eff = enc.GetName("EFF"),
        };

        static byte[] Bytes(PdfDictionary d, string key) => d.GetString(key)?.Bytes.ToArray() ?? [];
    }

    private static PdfEncryptionInfo DescribeEncryption(EncryptionParameters p, PdfAccessLevel level, bool? perms, int rawP)
    {
        int v = p.V != 0 ? p.V : (p.R >= 5 ? 5 : p.R >= 4 ? 4 : p.R == 3 ? 2 : 1);
        PdfCipher cipher = PdfCipher.Rc4;
        int bits = p.Length is > 0 ? Math.Clamp(p.Length.Value, 40, 128) : 40;
        if (v >= 4)
        {
            string? cfm = p.StmF is { } sf && p.CryptFilters is { } cfs && cfs.TryGetValue(sf, out CryptFilterSpec? spec) ? spec.Cfm : null;
            cipher = cfm switch
            {
                "AESV2" => PdfCipher.Aes128,
                "AESV3" => PdfCipher.Aes256,
                "V2" => PdfCipher.Rc4,
                _ => p.StmF is null or "Identity" ? PdfCipher.None : PdfCipher.Rc4,
            };
            bits = cipher switch { PdfCipher.Aes128 => 128, PdfCipher.Aes256 => 256, _ => bits };
        }

        return new PdfEncryptionInfo(v, p.R, bits, cipher, p.EncryptMetadata, level, perms, rawP);
    }

    /// <summary>Decrypts a string read from object <paramref name="objectNumber"/>; records what was wrong with damaged ciphertext.</summary>
    internal byte[] DecryptStringBytes(byte[] data, int objectNumber, int generation)
    {
        SecurityDecryptor? d = _decryptor;
        if (d is null || objectNumber < 0 || data.Length == 0)
        {
            return data;
        }

        DecryptResult r;
        try
        {
            r = d.DecryptString(data, objectNumber, generation);
        }
        catch (SecurityUnsupportedException e)
        {
            throw new PdfEncryptionException(e.Message, e) { ObjectNumber = objectNumber };
        }

        if (r.Issues != DecryptIssues.None)
        {
            _diag.Warn($"A string could not be decrypted cleanly ({r.Issues}); the decrypted bytes were used as far as they go.", objectNumber);
        }

        return r.Data;
    }

    /// <summary>Decrypts the stored bytes of a stream (before its filters run). Cross-reference streams are never encrypted (7.6.2).</summary>
    internal ReadOnlyMemory<byte> DecryptStreamData(PdfStream stream, ReadOnlyMemory<byte> raw, PdfReference self, IReadOnlyList<string> filters)
        => DecryptStreamData(stream, raw, self, filters, out _);

    /// <summary>As above; <paramref name="issues"/> says whether the ciphertext was damaged, in which case the result is only what decrypted.</summary>
    internal ReadOnlyMemory<byte> DecryptStreamData(PdfStream stream, ReadOnlyMemory<byte> raw, PdfReference self, IReadOnlyList<string> filters, out DecryptIssues issues)
    {
        issues = DecryptIssues.None;
        SecurityDecryptor? d = _decryptor;
        if (d is null || self.Number == _encryptObjNum)
        {
            return raw;
        }

        string? type = stream.Dictionary.GetName("Type");
        if (type == "XRef")
        {
            return raw;
        }

        // ISO 32000-2 7.4.10: a Crypt filter, which must come first, names the crypt filter of /CF; none means /StmF.
        string? filterName = null;
        if (filters.Count > 0 && filters[0] == "Crypt")
        {
            PdfObject? parms = stream.Dictionary.Get("DecodeParms");
            PdfDictionary? p0 = parms switch { PdfDictionary pd => pd, PdfArray pa when pa.Count > 0 => pa[0] as PdfDictionary, _ => null };
            filterName = p0?.GetName("Name") ?? "Identity";
        }

        DecryptResult r;
        try
        {
            r = d.DecryptStream(raw.Span, self.Number, self.Generation, filterName, type == "Metadata", type == "EmbeddedFile");
        }
        catch (SecurityUnsupportedException e)
        {
            throw new PdfEncryptionException(e.Message, e) { ObjectNumber = self.Number };
        }

        issues = r.Issues;
        if (r.Issues != DecryptIssues.None)
        {
            _diag.Warn($"A stream could not be decrypted cleanly ({r.Issues}); the decrypted bytes were used as far as they go.", self.Number);
        }

        return r.Data;
    }
}
