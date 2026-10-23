using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OnePdf.Asn1;

namespace OnePdf.Internal;

/// <summary>
/// Makes the CMS for one signature: signed attributes, the signature value (from a local key or an external signer, always checked
/// with the certificate's public key before use) and the optional time-stamp token. It needs only BCL hashing, RSA and ECDSA.
/// </summary>
internal sealed class CmsSigner
{
    private readonly PdfSigningCredential _credential;
    private readonly int _keyBits;

    private CmsSigner(PdfSigningCredential credential, PdfSigningKeyType keyType, int keyBits, HashAlgorithmName hash, PdfRsaPadding padding, bool cades)
    {
        _credential = credential;
        KeyType = keyType;
        _keyBits = keyBits;
        Hash = hash;
        Padding = padding;
        Cades = cades;
    }

    public PdfSigningKeyType KeyType { get; }

    public HashAlgorithmName Hash { get; }

    public PdfRsaPadding Padding { get; }

    public bool Cades { get; }

    public X509Certificate2 Certificate => _credential.Certificate;

    /// <summary>Checks the credential against the options before anything in the document is touched.</summary>
    public static CmsSigner Create(PdfSigningCredential credential, HashAlgorithmName hash, PdfRsaPadding padding, bool cades)
    {
        if (hash != HashAlgorithmName.SHA256 && hash != HashAlgorithmName.SHA384 && hash != HashAlgorithmName.SHA512)
        {
            throw new PdfSigningException(PdfSigningError.UnsupportedAlgorithm, "The digest must be SHA-256, SHA-384 or SHA-512.");
        }

        X509Certificate2 cert = credential.Certificate;
        PdfSigningKeyType type;
        int bits;
        using (RSA? rsa = cert.GetRSAPublicKey())
        {
            if (rsa is not null)
            {
                type = PdfSigningKeyType.Rsa;
                bits = rsa.KeySize;
            }
            else
            {
                using ECDsa? ec = cert.GetECDsaPublicKey();
                if (ec is null)
                {
                    throw new PdfSigningException(PdfSigningError.UnsupportedAlgorithm, "Only RSA and ECDSA certificates can sign.");
                }

                type = PdfSigningKeyType.Ecdsa;
                bits = ec.KeySize;
            }
        }

        if (credential.External is null)
        {
            bool usable = false;
            try
            {
                if (cert.HasPrivateKey && type == PdfSigningKeyType.Rsa)
                {
                    using RSA? key = cert.GetRSAPrivateKey();
                    usable = key is not null;
                }
                else if (cert.HasPrivateKey)
                {
                    using ECDsa? key = cert.GetECDsaPrivateKey();
                    usable = key is not null;
                }
            }
            catch (CryptographicException)
            {
                usable = false;
            }

            if (!usable)
            {
                throw new PdfSigningException(PdfSigningError.NoPrivateKey, "The signer certificate has no private key available; supply a certificate with its key or an external signer.");
            }
        }

        return new CmsSigner(credential, type, bits, hash, padding, cades);
    }

    /// <summary>A generous guess of the DER size of the CMS, so that the placeholder in <c>/Contents</c> fits it.</summary>
    public int EstimateBytes(bool withTimestamp)
    {
        long total = 1800;
        total += _credential.Certificate.RawData.Length;
        foreach (X509Certificate2 c in _credential.Chain)
        {
            total += c.RawData.Length;
        }

        total += KeyType == PdfSigningKeyType.Rsa ? (_keyBits + 7) / 8 : (2 * ((_keyBits + 7) / 8)) + 16;
        total += withTimestamp ? 8192 : 0;
        return (int)Math.Min(total, 1 << 22);
    }

    /// <summary>
    /// Builds the CMS over <paramref name="contentDigest"/> (the digest of the signed byte ranges in <see cref="Hash"/>).
    /// <paramref name="timestamp"/>, when given, is asked for a token over the signature value.
    /// </summary>
    public byte[] Build(byte[] contentDigest, DateTimeOffset signingTime, PdfTimestampCallback? timestamp, CancellationToken ct)
    {
        byte[] attrs = CmsWriter.SignedAttributes(contentDigest, Certificate, Cades, Cades ? null : signingTime);
        byte[] signature = SignAttributes(attrs);
        ct.ThrowIfCancellationRequested();
        byte[]? token = null;
        if (timestamp is not null)
        {
            token = RequestToken(timestamp, SignatureHash(signature), Hash);
        }

        byte[] algorithm = CmsWriter.SignatureAlgorithm(KeyType, Padding, Hash);
        return CmsWriter.SignedData(Hash, Certificate, _credential.Chain, attrs, algorithm, signature, token);
    }

    private byte[] SignatureHash(byte[] signature) => CryptographicOperations.HashData(Hash, signature);

    /// <summary>Gets a token for <paramref name="imprint"/> (computed with <paramref name="hash"/>) and checks it.</summary>
    public static byte[] RequestToken(PdfTimestampCallback callback, byte[] imprint, HashAlgorithmName hash)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(8);
        nonce[0] &= 0x7F;
        nonce[0] |= 0x01;
        byte[] request = Rfc3161.BuildRequest(hash, imprint, nonce);
        byte[] response = callback(new PdfTimestampRequest(hash, imprint, nonce, request))
            ?? throw new PdfSigningException(PdfSigningError.TimestampRejected, "The time-stamp callback returned null.");
        if (!Rfc3161.TryExtractToken(response, hash, imprint, nonce, out byte[] token, out string error))
        {
            throw new PdfSigningException(PdfSigningError.TimestampRejected, error);
        }

        return token;
    }

    private byte[] SignAttributes(byte[] attributes)
    {
        try
        {
            byte[] signature;
            if (_credential.External is { } external)
            {
                byte[] digest = CryptographicOperations.HashData(Hash, attributes);
                byte[] raw = external(new PdfSignRequest(digest, Hash, KeyType, Padding))
                    ?? throw new PdfSigningException(PdfSigningError.SignerFailed, "The external signer returned null.");
                signature = KeyType == PdfSigningKeyType.Ecdsa ? EcdsaToDer(raw) : raw;
            }
            else if (KeyType == PdfSigningKeyType.Rsa)
            {
                using RSA rsa = Certificate.GetRSAPrivateKey()!;
                signature = rsa.SignData(attributes, Hash, ToBcl(Padding));
            }
            else
            {
                using ECDsa ec = Certificate.GetECDsaPrivateKey()!;
                signature = ec.SignData(attributes, Hash, DSASignatureFormat.Rfc3279DerSequence);
            }

            if (!VerifyOwn(attributes, signature))
            {
                throw new PdfSigningException(PdfSigningError.SignerFailed, "The signature value does not verify with the signer certificate's public key.");
            }

            return signature;
        }
        catch (CryptographicException e)
        {
            throw new PdfSigningException(PdfSigningError.SignerFailed, "The signing key failed: " + e.Message, e);
        }
    }

    private bool VerifyOwn(byte[] attributes, byte[] signature)
    {
        try
        {
            if (KeyType == PdfSigningKeyType.Rsa)
            {
                using RSA rsa = Certificate.GetRSAPublicKey()!;
                return rsa.VerifyData(attributes, signature, Hash, ToBcl(Padding));
            }

            using ECDsa ec = Certificate.GetECDsaPublicKey()!;
            return ec.VerifyData(attributes, signature, Hash, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static RSASignaturePadding ToBcl(PdfRsaPadding p) => p == PdfRsaPadding.Pss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;

    // An external ECDSA signer may return the DER Ecdsa-Sig-Value or the IEEE P1363 pair r||s (as HSMs and PKCS#11 do).
    private byte[] EcdsaToDer(byte[] raw)
    {
        int coordinate = (_keyBits + 7) / 8;
        if (raw.Length == 2 * coordinate)
        {
            return DerWriter.Sequence(DerWriter.UnsignedInteger(raw.AsSpan(0, coordinate)), DerWriter.UnsignedInteger(raw.AsSpan(coordinate)));
        }

        return raw;
    }
}
