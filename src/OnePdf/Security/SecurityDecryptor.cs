using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace OnePdf.Security;

/// <summary>
/// Decrypts strings and streams of one document under the file key found by <see cref="StandardSecurityHandler"/>.
/// Thread-safe: it holds only immutable state after construction. Dispose clears the file key.
/// </summary>
/// <remarks>
/// The caller decides what is encrypted: strings in the /Encrypt dictionary itself, in the trailer /ID and inside object
/// streams are not encrypted, and neither are cross-reference streams (ISO 32000-2 7.6.2). Strings inside an object
/// stream are covered by the stream's own encryption. A stream with an explicit /Crypt filter passes that filter's
/// name; <c>Identity</c> means leave it alone.
/// </remarks>
internal sealed class SecurityDecryptor : IDisposable
{
    private const string IdentityName = "Identity";

    private readonly byte[] _fileKey;
    private readonly CryptMethod _stringMethod;
    private readonly CryptMethod _streamMethod;
    private readonly CryptMethod _embeddedMethod;
    private readonly Dictionary<string, CryptMethod> _filters;

    internal SecurityDecryptor(
        byte[] fileKey,
        CryptMethod stringMethod,
        CryptMethod streamMethod,
        CryptMethod embeddedMethod,
        Dictionary<string, CryptMethod> filters,
        bool encryptMetadata)
    {
        foreach (CryptMethod m in new[] { stringMethod, streamMethod, embeddedMethod }.Concat(filters.Values))
        {
            // AES-256 uses the file key itself; AES-128 needs an object key of 16 bytes, which takes a file key of 11 or more.
            if ((m == CryptMethod.AesV3 && fileKey.Length != 32) || (m == CryptMethod.AesV2 && fileKey.Length < 11))
            {
                throw new SecurityUnsupportedException("The crypt filter method does not fit the key length of this encryption dictionary.");
            }
        }

        _fileKey = fileKey;
        _stringMethod = stringMethod;
        _streamMethod = streamMethod;
        _embeddedMethod = embeddedMethod;
        _filters = filters;
        EncryptMetadata = encryptMetadata;
    }

    /// <summary>False when /EncryptMetadata is false: a metadata stream is then stored in the clear unless it names a Crypt filter.</summary>
    public bool EncryptMetadata { get; }

    /// <summary>True when strings are left unencrypted (StrF Identity or None).</summary>
    public bool StringsAreClear => _stringMethod == CryptMethod.None;

    /// <summary>True when streams are left unencrypted by default (StmF Identity or None).</summary>
    public bool StreamsAreClear => _streamMethod == CryptMethod.None;

    /// <summary>Decrypts a string of object <paramref name="objectNumber"/> <paramref name="generation"/> (uses /StrF).</summary>
    public DecryptResult DecryptString(ReadOnlySpan<byte> data, int objectNumber, int generation) =>
        Decrypt(_stringMethod, data, objectNumber, generation);

    /// <summary>
    /// Decrypts stream data. <paramref name="cryptFilterName"/> is the name from the stream's /Crypt filter
    /// (<c>/DecodeParms /Name</c>), or null for the default (/StmF). Pass <paramref name="isMetadata"/> for a
    /// <c>/Type /Metadata</c> stream: with /EncryptMetadata false and no named filter it comes back unchanged.
    /// Pass <paramref name="isEmbeddedFile"/> for an embedded file stream to use /EFF.
    /// </summary>
    public DecryptResult DecryptStream(
        ReadOnlySpan<byte> data,
        int objectNumber,
        int generation,
        string? cryptFilterName = null,
        bool isMetadata = false,
        bool isEmbeddedFile = false)
    {
        CryptMethod method;
        if (cryptFilterName is not null)
        {
            if (cryptFilterName == IdentityName)
            {
                method = CryptMethod.None;
            }
            else if (!_filters.TryGetValue(cryptFilterName, out method))
            {
                return new DecryptResult(data.ToArray(), DecryptIssues.UnknownCryptFilter);
            }
        }
        else if (isMetadata && !EncryptMetadata)
        {
            method = CryptMethod.None;
        }
        else
        {
            method = isEmbeddedFile ? _embeddedMethod : _streamMethod;
        }

        return Decrypt(method, data, objectNumber, generation);
    }

    /// <summary>Clears the file key.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(_fileKey);

    private DecryptResult Decrypt(CryptMethod method, ReadOnlySpan<byte> data, int objectNumber, int generation)
    {
        try
        {
            switch (method)
            {
                case CryptMethod.None:
                    return new DecryptResult(data.ToArray(), DecryptIssues.None);

                case CryptMethod.Rc4:
                {
                    byte[] output = data.ToArray();
                    Span<byte> key = stackalloc byte[16];
                    int n = ObjectKey(objectNumber, generation, aes: false, key);
                    try
                    {
                        Rc4.TransformInPlace(key.Slice(0, n), output);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(key);
                    }

                    return new DecryptResult(output, DecryptIssues.None);
                }

                case CryptMethod.AesV2:
                {
                    Span<byte> key = stackalloc byte[16];
                    int n = ObjectKey(objectNumber, generation, aes: true, key);
                    try
                    {
                        return AesCbcPkcs(key.Slice(0, n), data);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(key);
                    }
                }

                default:
                    return AesCbcPkcs(_fileKey, data);
            }
        }
        catch (PlatformNotSupportedException e)
        {
            throw new SecurityUnsupportedException("This runtime has no AES or MD5 implementation, so the document cannot be decrypted.", e);
        }
    }

    // ISO 32000-2 7.6.3 Algorithm 1: MD5(fileKey + objnum[3, LE] + gen[2, LE] + "sAlT" for AES), first min(n+5, 16) bytes.
    private int ObjectKey(int objectNumber, int generation, bool aes, Span<byte> destination)
    {
        Span<byte> input = stackalloc byte[_fileKey.Length + 9];
        try
        {
            _fileKey.CopyTo(input);
            int p = _fileKey.Length;
            input[p++] = (byte)objectNumber;
            input[p++] = (byte)(objectNumber >> 8);
            input[p++] = (byte)(objectNumber >> 16);
            input[p++] = (byte)generation;
            input[p++] = (byte)(generation >> 8);
            if (aes)
            {
                input[p++] = 0x73;
                input[p++] = 0x41;
                input[p++] = 0x6C;
                input[p++] = 0x54;
            }

            Span<byte> hash = stackalloc byte[16];
            PdfMd5.Hash(input.Slice(0, p), hash);
            int n = Math.Min(_fileKey.Length + 5, 16);
            hash.Slice(0, n).CopyTo(destination);
            CryptographicOperations.ZeroMemory(hash);
            return n;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    // The first 16 bytes are the IV; the rest is CBC ciphertext with PKCS#5 padding (ISO 32000-2 7.6.3).
    // Lenient: invalid padding keeps every decrypted byte, a partial last block is dropped, both flagged.
    private static DecryptResult AesCbcPkcs(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return new DecryptResult([], DecryptIssues.None);
        }

        if (data.Length < 16)
        {
            return new DecryptResult([], DecryptIssues.MissingIv);
        }

        DecryptIssues issues = DecryptIssues.None;
        int cipherLength = data.Length - 16;
        if ((cipherLength & 15) != 0)
        {
            issues |= DecryptIssues.PartialBlock;
            cipherLength &= ~15;
        }

        if (cipherLength == 0)
        {
            return new DecryptResult([], issues);
        }

        byte[] plain = AesCbc.DecryptNoPadding(key, data.Slice(0, 16), data.Slice(16, cipherLength));
        byte pad = plain[^1];
        if (pad is >= 1 and <= 16 && pad <= plain.Length && HasPadding(plain, pad))
        {
            return new DecryptResult(plain.AsSpan(0, plain.Length - pad).ToArray(), issues);
        }

        return new DecryptResult(plain, issues | DecryptIssues.BadPadding);
    }

    private static bool HasPadding(byte[] plain, int pad)
    {
        for (int i = plain.Length - pad; i < plain.Length; i++)
        {
            if (plain[i] != pad)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>AES-CBC and ECB without padding, over the BCL. Raises <see cref="PlatformNotSupportedException"/> where the runtime has no AES (browser WASM).</summary>
internal static class AesCbc
{
    /// <summary>True when this runtime can run AES at all.</summary>
    public static bool IsAvailable => !OperatingSystem.IsBrowser();

    public static byte[] DecryptNoPadding(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> cipher)
    {
        RequireAvailable();
        using Aes aes = Aes.Create();
        SetKey(aes, key);
        return aes.DecryptCbc(cipher, iv, PaddingMode.None);
    }

    public static byte[] EncryptNoPadding(Aes aes, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> plain)
    {
        SetKey(aes, key);
        return aes.EncryptCbc(plain, iv, PaddingMode.None);
    }

    public static byte[] DecryptEcbNoPadding(ReadOnlySpan<byte> key, ReadOnlySpan<byte> cipher)
    {
        RequireAvailable();
        using Aes aes = Aes.Create();
        SetKey(aes, key);
        return aes.DecryptEcb(cipher, PaddingMode.None);
    }

    // The key copy handed to the BCL is cleared once the BCL has taken its own.
    private static void SetKey(Aes aes, ReadOnlySpan<byte> key)
    {
        byte[] copy = key.ToArray();
        try
        {
            aes.Key = copy;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    public static void RequireAvailable()
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("AES is not available in the browser.");
        }
    }
}
