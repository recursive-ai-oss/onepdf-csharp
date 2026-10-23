using System.Security.Cryptography;

namespace OnePdf.Security;

/// <summary>A freshly built <c>/Encrypt</c> dictionary and the encryptor for its file key.</summary>
internal sealed class NewEncryption
{
    public NewEncryption(PdfDictionary dictionary, SecurityDecryptor encryptor, string minimumVersion)
    {
        Dictionary = dictionary;
        Encryptor = encryptor;
        MinimumVersion = minimumVersion;
    }

    public PdfDictionary Dictionary { get; }

    public SecurityDecryptor Encryptor { get; }

    public string MinimumVersion { get; }
}

/// <summary>
/// Builds standard security handler dictionaries: revision 4 with AES-128 (ISO 32000-1 7.6.3, Algorithms 2, 3 and 5) and
/// revision 6 with AES-256 (ISO 32000-2 7.6.4.4, Algorithms 8 and 9 with Algorithm 2.B).
/// </summary>
internal static class StandardSecurityWriter
{
    // Bits 7-8 and 13-32 of /P are reserved and must be 1 (Table 22); bits 1-2 must be 0.
    private const uint ReservedOnes = 0xFFFFF0C0;
    private const uint DefinedBits = 0xF3C;

    public static NewEncryption Create(PdfEncryptionOptions o, ReadOnlySpan<byte> firstId)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (!AesCbc.IsAvailable)
        {
            throw new SecurityUnsupportedException("This runtime has no AES, so a document cannot be encrypted.");
        }

        int p = unchecked((int)(ReservedOnes | ((uint)o.Permissions & DefinedBits)));
        string owner = string.IsNullOrEmpty(o.OwnerPassword) ? o.UserPassword : o.OwnerPassword;
        return o.Cipher switch
        {
            PdfCipher.Aes128 => CreateR4(o, owner, p, firstId),
            PdfCipher.Aes256 => CreateR6(o, owner, p),
            _ => throw new ArgumentException("New encryption uses PdfCipher.Aes128 or PdfCipher.Aes256.", nameof(o)),
        };
    }

    private static NewEncryption CreateR4(PdfEncryptionOptions o, string owner, int p, ReadOnlySpan<byte> id)
    {
        const int n = 16;
        byte[] user = PasswordPreparation.Pad32(PasswordPreparation.ForLegacy(o.UserPassword)[0]);
        byte[] own = PasswordPreparation.Pad32(PasswordPreparation.ForLegacy(owner)[0]);

        // Algorithm 3: /O is the user password padded, RC4-encrypted under a key made from the owner password.
        byte[] hash = PdfMd5.Hash(own);
        for (int i = 0; i < 50; i++)
        {
            hash = PdfMd5.Hash(hash);
        }

        byte[] ownerKey = hash.AsSpan(0, n).ToArray();
        byte[] oValue = user.AsSpan().ToArray();
        Rc4.TransformInPlace(ownerKey, oValue);
        byte[] k = new byte[n];
        for (int i = 1; i <= 19; i++)
        {
            for (int j = 0; j < n; j++)
            {
                k[j] = (byte)(ownerKey[j] ^ i);
            }

            Rc4.TransformInPlace(k, oValue);
        }

        // Algorithm 2: the file key.
        var parameters = new EncryptionParameters { V = 4, R = 4, O = oValue, P = p, EncryptMetadata = o.EncryptMetadata };
        byte[] fileKey = StandardSecurityHandler.ComputeKeyLegacy(parameters, user, id, 4, n);

        // Algorithm 5: /U is the MD5 of padding and /ID, RC4-encrypted 20 times; 16 more bytes are arbitrary.
        byte[] input = new byte[32 + id.Length];
        PasswordPreparation.Padding.CopyTo(input);
        id.CopyTo(input.AsSpan(32));
        byte[] u = new byte[32];
        PdfMd5.Hash(input, u.AsSpan(0, 16));
        Rc4.TransformInPlace(fileKey, u.AsSpan(0, 16));
        for (int i = 1; i <= 19; i++)
        {
            for (int j = 0; j < n; j++)
            {
                k[j] = (byte)(fileKey[j] ^ i);
            }

            Rc4.TransformInPlace(k, u.AsSpan(0, 16));
        }

        var dict = new PdfDictionary();
        dict.Set("Filter", new PdfName("Standard"));
        dict.Set("V", new PdfInteger(4));
        dict.Set("R", new PdfInteger(4));
        dict.Set("Length", new PdfInteger(128));
        dict.Set("CF", CryptFilters("AESV2", 16));
        dict.Set("StmF", new PdfName("StdCF"));
        dict.Set("StrF", new PdfName("StdCF"));
        dict.Set("O", new PdfString(oValue));
        dict.Set("U", new PdfString(u));
        dict.Set("P", new PdfInteger(p));
        if (!o.EncryptMetadata)
        {
            dict.Set("EncryptMetadata", new PdfBoolean(false));
        }

        var filters = new Dictionary<string, CryptMethod> { ["StdCF"] = CryptMethod.AesV2 };
        var enc = new SecurityDecryptor(fileKey, CryptMethod.AesV2, CryptMethod.AesV2, CryptMethod.AesV2, filters, o.EncryptMetadata);
        return new NewEncryption(dict, enc, "1.6");
    }

    private static NewEncryption CreateR6(PdfEncryptionOptions o, string owner, int p)
    {
        byte[] fileKey = RandomNumberGenerator.GetBytes(32);
        byte[] user = PasswordPreparation.ForAes256(o.UserPassword)[0];
        byte[] own = PasswordPreparation.ForAes256(owner)[0];

        // Algorithm 8: /U, /UE.
        byte[] uSalt = RandomNumberGenerator.GetBytes(16);
        byte[] u = new byte[48];
        StandardSecurityHandler.Hash(user, uSalt.AsSpan(0, 8), [], 6).CopyTo(u, 0);
        uSalt.CopyTo(u, 32);
        byte[] ue = WrapFileKey(user, uSalt.AsSpan(8, 8), [], fileKey);

        // Algorithm 9: /O, /OE, which also hash the 48 bytes of /U.
        byte[] oSalt = RandomNumberGenerator.GetBytes(16);
        byte[] oValue = new byte[48];
        StandardSecurityHandler.Hash(own, oSalt.AsSpan(0, 8), u, 6).CopyTo(oValue, 0);
        oSalt.CopyTo(oValue, 32);
        byte[] oe = WrapFileKey(own, oSalt.AsSpan(8, 8), u, fileKey);

        // Algorithm 10: /Perms.
        byte[] perms = new byte[16];
        perms[0] = (byte)p;
        perms[1] = (byte)(p >> 8);
        perms[2] = (byte)(p >> 16);
        perms[3] = (byte)(p >> 24);
        perms[4] = perms[5] = perms[6] = perms[7] = 0xFF;
        perms[8] = (byte)(o.EncryptMetadata ? 'T' : 'F');
        perms[9] = (byte)'a';
        perms[10] = (byte)'d';
        perms[11] = (byte)'b';
        RandomNumberGenerator.Fill(perms.AsSpan(12, 4));

        var dict = new PdfDictionary();
        dict.Set("Filter", new PdfName("Standard"));
        dict.Set("V", new PdfInteger(5));
        dict.Set("R", new PdfInteger(6));
        dict.Set("Length", new PdfInteger(256));
        dict.Set("CF", CryptFilters("AESV3", 32));
        dict.Set("StmF", new PdfName("StdCF"));
        dict.Set("StrF", new PdfName("StdCF"));
        dict.Set("O", new PdfString(oValue));
        dict.Set("U", new PdfString(u));
        dict.Set("OE", new PdfString(oe));
        dict.Set("UE", new PdfString(ue));
        dict.Set("Perms", new PdfString(AesCbc.EncryptEcbNoPadding(fileKey, perms)));
        dict.Set("P", new PdfInteger(p));
        if (!o.EncryptMetadata)
        {
            dict.Set("EncryptMetadata", new PdfBoolean(false));
        }

        var filters = new Dictionary<string, CryptMethod> { ["StdCF"] = CryptMethod.AesV3 };
        var enc = new SecurityDecryptor(fileKey, CryptMethod.AesV3, CryptMethod.AesV3, CryptMethod.AesV3, filters, o.EncryptMetadata);
        return new NewEncryption(dict, enc, "2.0");
    }

    // AES-256-CBC with a zero IV and no padding, under the hash of password, key salt and (for the owner) /U.
    private static byte[] WrapFileKey(byte[] password, ReadOnlySpan<byte> keySalt, ReadOnlySpan<byte> udata, byte[] fileKey)
    {
        byte[] intermediate = StandardSecurityHandler.Hash(password, keySalt, udata, 6);
        try
        {
            using Aes aes = Aes.Create();
            return AesCbc.EncryptNoPadding(aes, intermediate, new byte[16], fileKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(intermediate);
        }
    }

    private static PdfDictionary CryptFilters(string method, int length)
    {
        var std = new PdfDictionary();
        std.Set("CFM", new PdfName(method));
        std.Set("AuthEvent", new PdfName("DocOpen"));
        std.Set("Length", new PdfInteger(length));
        var cf = new PdfDictionary();
        cf.Set("StdCF", std);
        return cf;
    }
}
