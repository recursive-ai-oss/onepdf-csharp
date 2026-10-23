using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OnePdf.Asn1;

/// <summary>
/// Writes the CMS structures a PDF signature needs (RFC 5652 5, RFC 5035, ETSI EN 319 142-1 6.3): detached
/// <c>SignedData</c> with one signer identified by issuer and serial number. Everything is DER.
/// </summary>
internal static class CmsWriter
{
    /// <summary>The object identifier of a SHA-2 digest.</summary>
    public static string HashOid(HashAlgorithmName hash) =>
        hash == HashAlgorithmName.SHA256 ? Oids.Sha256
        : hash == HashAlgorithmName.SHA384 ? Oids.Sha384
        : hash == HashAlgorithmName.SHA512 ? Oids.Sha512
        : throw new ArgumentException("Only SHA-256, SHA-384 and SHA-512 are supported.", nameof(hash));

    /// <summary>A digest AlgorithmIdentifier with the parameters absent (RFC 5754 2).</summary>
    public static byte[] DigestAlgorithm(HashAlgorithmName hash) => DerWriter.Sequence(DerWriter.Oid(HashOid(hash)));

    /// <summary>The signature AlgorithmIdentifier for the key type, the RSA scheme and the digest.</summary>
    public static byte[] SignatureAlgorithm(PdfSigningKeyType key, PdfRsaPadding padding, HashAlgorithmName hash)
    {
        if (key == PdfSigningKeyType.Ecdsa)
        {
            // RFC 5758 3.2: ecdsa-with-SHA2 identifiers have absent parameters.
            return DerWriter.Sequence(DerWriter.Oid(hash == HashAlgorithmName.SHA256 ? Oids.EcdsaSha256 : hash == HashAlgorithmName.SHA384 ? Oids.EcdsaSha384 : Oids.EcdsaSha512));
        }

        if (padding == PdfRsaPadding.Pss)
        {
            // RFC 4055 3.1: the hash, the MGF1 hash and the salt length are all spelled out (the defaults are SHA-1).
            int salt = hash == HashAlgorithmName.SHA256 ? 32 : hash == HashAlgorithmName.SHA384 ? 48 : 64;
            byte[] hashAlg = DigestAlgorithm(hash);
            byte[] parameters = DerWriter.Sequence(
                DerWriter.Context(0, hashAlg),
                DerWriter.Context(1, DerWriter.Sequence(DerWriter.Oid(Oids.Mgf1), hashAlg)),
                DerWriter.Context(2, DerWriter.Integer(salt)));
            return DerWriter.Sequence(DerWriter.Oid(Oids.RsaPss), parameters);
        }

        string oid = hash == HashAlgorithmName.SHA256 ? Oids.Sha256WithRsa : hash == HashAlgorithmName.SHA384 ? Oids.Sha384WithRsa : Oids.Sha512WithRsa;
        return DerWriter.Sequence(DerWriter.Oid(oid), DerWriter.Null());
    }

    private static byte[] Attribute(string oid, byte[] value) => DerWriter.Sequence(DerWriter.Oid(oid), DerWriter.Set(value));

    /// <summary>
    /// The signed attributes as a DER <c>SET OF</c> (tag 0x31), which is what gets hashed and signed (RFC 5652 5.4). The profile
    /// decides the members: <c>contentType</c> and <c>messageDigest</c> always; <c>signingCertificateV2</c> for CAdES;
    /// <c>signingTime</c> only for the legacy profile (ETSI EN 319 142-1 6.3 leaves the time to the PDF dictionary).
    /// </summary>
    public static byte[] SignedAttributes(byte[] messageDigest, X509Certificate2 signer, bool cades, DateTimeOffset? signingTime)
    {
        var attrs = new List<byte[]>
        {
            Attribute(Oids.ContentType, DerWriter.Oid(Oids.Data)),
            Attribute(Oids.MessageDigest, DerWriter.OctetString(messageDigest)),
        };
        if (cades)
        {
            attrs.Add(Attribute(Oids.SigningCertificateV2, SigningCertificateV2(signer)));
        }

        if (signingTime is { } t)
        {
            attrs.Add(Attribute(Oids.SigningTime, t.Year is >= 1950 and < 2050 ? DerWriter.UtcTime(t) : DerWriter.GeneralizedTime(t)));
        }

        return DerWriter.SetOf(attrs);
    }

    // RFC 5035 3: ESSCertIDv2 with the default SHA-256 left out of the encoding, and the issuer and serial number of the certificate.
    private static byte[] SigningCertificateV2(X509Certificate2 signer)
    {
        byte[] certHash = SHA256.HashData(signer.RawData);
        if (!CertificateIdentity.TryParse(signer.RawData, out CertificateIdentity id))
        {
            throw new ArgumentException("The signer certificate cannot be parsed.", nameof(signer));
        }

        byte[] issuerSerial = DerWriter.Sequence(
            DerWriter.Sequence(DerWriter.Context(4, id.IssuerName.ToArray())),
            DerWriter.Tlv(0x02, id.SerialNumber.ToArray()));
        byte[] essCertId = DerWriter.Sequence(DerWriter.OctetString(certHash), issuerSerial);
        return DerWriter.Sequence(DerWriter.Sequence(essCertId));
    }

    /// <summary>
    /// The complete <c>ContentInfo</c> holding the detached <c>SignedData</c>.
    /// </summary>
    /// <param name="hash">The digest algorithm.</param>
    /// <param name="signer">The signer's certificate; it is also the first certificate embedded.</param>
    /// <param name="chain">Further certificates to embed; duplicates of the signer's are skipped.</param>
    /// <param name="signedAttributes">The result of <see cref="SignedAttributes"/>.</param>
    /// <param name="signatureAlgorithm">The result of <see cref="SignatureAlgorithm"/>.</param>
    /// <param name="signature">The signature value over <paramref name="signedAttributes"/>.</param>
    /// <param name="timestampToken">A DER time-stamp token to carry as the <c>signatureTimeStampToken</c> unsigned attribute, or null.</param>
    public static byte[] SignedData(HashAlgorithmName hash, X509Certificate2 signer, IReadOnlyList<X509Certificate2> chain, byte[] signedAttributes,
        byte[] signatureAlgorithm, byte[] signature, byte[]? timestampToken)
    {
        if (!CertificateIdentity.TryParse(signer.RawData, out CertificateIdentity id))
        {
            throw new ArgumentException("The signer certificate cannot be parsed.", nameof(signer));
        }

        var signerInfo = new List<byte[]>
        {
            DerWriter.Integer(1),
            DerWriter.Sequence(id.IssuerName.ToArray(), DerWriter.Tlv(0x02, id.SerialNumber.ToArray())),
            DigestAlgorithm(hash),
            DerWriter.Retag(signedAttributes, 0xA0),
            signatureAlgorithm,
            DerWriter.OctetString(signature),
        };
        if (timestampToken is not null)
        {
            signerInfo.Add(DerWriter.Tlv(0xA1, Attribute(Oids.SignatureTimeStampToken, timestampToken)));
        }

        var certs = new List<byte[]> { signer.RawData };
        foreach (X509Certificate2 c in chain)
        {
            byte[] raw = c.RawData;
            if (!certs.Exists(x => x.AsSpan().SequenceEqual(raw)))
            {
                certs.Add(raw);
            }
        }

        byte[] signedData = DerWriter.Sequence(
            DerWriter.Integer(1),
            DerWriter.Set(DigestAlgorithm(hash)),
            DerWriter.Sequence(DerWriter.Oid(Oids.Data)),
            DerWriter.Tlv(0xA0, certs.ToArray()),
            DerWriter.Set(DerWriter.Sequence(signerInfo.ToArray())));
        return DerWriter.Sequence(DerWriter.Oid(Oids.SignedData), DerWriter.Context(0, signedData));
    }
}
