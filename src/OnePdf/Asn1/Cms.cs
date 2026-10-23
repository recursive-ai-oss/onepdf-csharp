using System.Security.Cryptography;

namespace OnePdf.Asn1;

/// <summary>Object identifiers used by CMS signatures, time-stamps and the certificates around them.</summary>
internal static class Oids
{
    public const string Data = "1.2.840.113549.1.7.1";
    public const string SignedData = "1.2.840.113549.1.7.2";
    public const string TstInfo = "1.2.840.113549.1.9.16.1.4";

    public const string ContentType = "1.2.840.113549.1.9.3";
    public const string MessageDigest = "1.2.840.113549.1.9.4";
    public const string SigningTime = "1.2.840.113549.1.9.5";
    public const string SigningCertificate = "1.2.840.113549.1.9.16.2.12";
    public const string SigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";
    public const string SignatureTimeStampToken = "1.2.840.113549.1.9.16.2.14";
    public const string AdobeRevocationInfo = "1.2.840.113583.1.1.8";

    public const string Sha1 = "1.3.14.3.2.26";
    public const string Sha224 = "2.16.840.1.101.3.4.2.4";
    public const string Sha256 = "2.16.840.1.101.3.4.2.1";
    public const string Sha384 = "2.16.840.1.101.3.4.2.2";
    public const string Sha512 = "2.16.840.1.101.3.4.2.3";

    public const string RsaEncryption = "1.2.840.113549.1.1.1";
    public const string Sha1WithRsa = "1.2.840.113549.1.1.5";
    public const string Sha256WithRsa = "1.2.840.113549.1.1.11";
    public const string Sha384WithRsa = "1.2.840.113549.1.1.12";
    public const string Sha512WithRsa = "1.2.840.113549.1.1.13";
    public const string RsaPss = "1.2.840.113549.1.1.10";
    public const string Mgf1 = "1.2.840.113549.1.1.8";
    public const string EcPublicKey = "1.2.840.10045.2.1";
    public const string EcdsaSha1 = "1.2.840.10045.4.1";
    public const string EcdsaSha256 = "1.2.840.10045.4.3.2";
    public const string EcdsaSha384 = "1.2.840.10045.4.3.3";
    public const string EcdsaSha512 = "1.2.840.10045.4.3.4";

    public const string SubjectKeyIdentifier = "2.5.29.14";

    public static HashAlgorithmName? HashFor(string? oid) => oid switch
    {
        Sha1 => HashAlgorithmName.SHA1,
        Sha256 => HashAlgorithmName.SHA256,
        Sha384 => HashAlgorithmName.SHA384,
        Sha512 => HashAlgorithmName.SHA512,
        _ => null,
    };

    public static string? Name(string? oid) => oid switch
    {
        Sha1 => "SHA-1",
        Sha224 => "SHA-224",
        Sha256 => "SHA-256",
        Sha384 => "SHA-384",
        Sha512 => "SHA-512",
        RsaEncryption => "RSA",
        Sha1WithRsa => "SHA-1 with RSA",
        Sha256WithRsa => "SHA-256 with RSA",
        Sha384WithRsa => "SHA-384 with RSA",
        Sha512WithRsa => "SHA-512 with RSA",
        RsaPss => "RSASSA-PSS",
        EcdsaSha1 => "ECDSA with SHA-1",
        EcdsaSha256 => "ECDSA with SHA-256",
        EcdsaSha384 => "ECDSA with SHA-384",
        EcdsaSha512 => "ECDSA with SHA-512",
        _ => oid,
    };
}

/// <summary>One CMS attribute: its object identifier and the encodings of its values.</summary>
internal sealed class CmsAttribute
{
    public CmsAttribute(string oid, IReadOnlyList<ReadOnlyMemory<byte>> values)
    {
        Oid = oid;
        Values = values;
    }

    public string Oid { get; }

    public IReadOnlyList<ReadOnlyMemory<byte>> Values { get; }

    public bool TryFirst(out Asn1Element e)
    {
        e = default;
        return Values.Count > 0 && Asn1Reader.TryRead(Values[0], out e, out _, out _);
    }
}

/// <summary>An AlgorithmIdentifier: the object identifier and the raw parameters element, if any.</summary>
internal readonly struct CmsAlgorithm
{
    public CmsAlgorithm(string oid, ReadOnlyMemory<byte> parameters)
    {
        Oid = oid;
        Parameters = parameters;
    }

    public string Oid { get; }

    /// <summary>The encoded parameters element, empty when absent.</summary>
    public ReadOnlyMemory<byte> Parameters { get; }

    public static bool TryParse(in Asn1Element e, out CmsAlgorithm algorithm)
    {
        algorithm = default;
        var kids = new List<Asn1Element>(2);
        if (!e.IsSequence || !Asn1Reader.TryChildren(e, 4, kids, out _) || kids.Count == 0 || Asn1Reader.TryOid(kids[0]) is not { } oid)
        {
            return false;
        }

        algorithm = new CmsAlgorithm(oid, kids.Count > 1 ? kids[1].Encoded : ReadOnlyMemory<byte>.Empty);
        return true;
    }
}

/// <summary>A SignerInfo of RFC 5652 5.3.</summary>
internal sealed class CmsSignerInfo
{
    public int Version { get; init; }

    /// <summary>DER of the issuer Name when the signer is identified by issuer and serial number.</summary>
    public ReadOnlyMemory<byte> IssuerName { get; init; }

    public ReadOnlyMemory<byte> SerialNumber { get; init; }

    /// <summary>The subjectKeyIdentifier when that form of SignerIdentifier is used.</summary>
    public ReadOnlyMemory<byte> SubjectKeyIdentifier { get; init; }

    public CmsAlgorithm DigestAlgorithm { get; init; }

    /// <summary>The encoded <c>[0]</c> signed attributes, as they appear in the file, or empty.</summary>
    public ReadOnlyMemory<byte> SignedAttributesEncoded { get; init; }

    public IReadOnlyList<CmsAttribute> SignedAttributes { get; init; } = [];

    public CmsAlgorithm SignatureAlgorithm { get; init; }

    public byte[] Signature { get; init; } = [];

    public IReadOnlyList<CmsAttribute> UnsignedAttributes { get; init; } = [];

    public bool HasSignedAttributes => !SignedAttributesEncoded.IsEmpty;

    public CmsAttribute? FindSigned(string oid) => Find(SignedAttributes, oid);

    public CmsAttribute? FindUnsigned(string oid) => Find(UnsignedAttributes, oid);

    private static CmsAttribute? Find(IReadOnlyList<CmsAttribute> list, string oid)
    {
        foreach (CmsAttribute a in list)
        {
            if (a.Oid == oid)
            {
                return a;
            }
        }

        return null;
    }
}

/// <summary>A parsed CMS <c>SignedData</c> (RFC 5652 5.1) with the content type and certificates around it.</summary>
internal sealed class CmsSignedData
{
    public const int MaxSigners = 16;
    public const int MaxCertificates = 256;
    public const int MaxAttributes = 64;
    public const int MaxContentBytes = 1 << 20;

    public int Version { get; private init; }

    public IReadOnlyList<string> DigestAlgorithms { get; private init; } = [];

    public string ContentType { get; private init; } = string.Empty;

    /// <summary>The encapsulated content (for a time-stamp token, the TSTInfo), or null when detached.</summary>
    public byte[]? Content { get; private init; }

    public IReadOnlyList<ReadOnlyMemory<byte>> Certificates { get; private init; } = [];

    public IReadOnlyList<CmsSignerInfo> Signers { get; private init; } = [];

    /// <summary>Parses a ContentInfo holding SignedData. The input may be followed by zero padding, as <c>/Contents</c> is.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> data, out CmsSignedData? result, out string? error)
    {
        result = null;
        if (!Asn1Reader.TryReadPadded(data, out Asn1Element ci, out error))
        {
            return false;
        }

        var kids = new List<Asn1Element>(2);
        if (!ci.IsSequence || !Asn1Reader.TryChildren(ci, 3, kids, out error) || kids.Count != 2 || Asn1Reader.TryOid(kids[0]) != Oids.SignedData || !kids[1].IsContext(0))
        {
            error ??= "The data is not a CMS ContentInfo holding SignedData.";
            return false;
        }

        var inner = new List<Asn1Element>(1);
        if (!Asn1Reader.TryChildren(kids[1], 2, inner, out error) || inner.Count != 1 || !inner[0].IsSequence)
        {
            error ??= "The SignedData wrapper is malformed.";
            return false;
        }

        var f = new List<Asn1Element>(8);
        if (!Asn1Reader.TryChildren(inner[0], 8, f, out error) || f.Count < 4)
        {
            error ??= "The SignedData is malformed.";
            return false;
        }

        int i = 0;
        int version = Asn1Reader.TrySmallInt(f[i++]);
        if (version < 0 || !f[i].IsSet)
        {
            error = "The SignedData header is malformed.";
            return false;
        }

        var digestAlgs = new List<string>();
        var algEls = new List<Asn1Element>();
        if (!Asn1Reader.TryChildren(f[i++], MaxSigners * 2, algEls, out error))
        {
            return false;
        }

        foreach (Asn1Element a in algEls)
        {
            if (CmsAlgorithm.TryParse(a, out CmsAlgorithm alg))
            {
                digestAlgs.Add(alg.Oid);
            }
        }

        // EncapsulatedContentInfo
        var enc = new List<Asn1Element>(2);
        if (!f[i].IsSequence || !Asn1Reader.TryChildren(f[i++], 3, enc, out error) || enc.Count is 0 or > 2 || Asn1Reader.TryOid(enc[0]) is not { } contentType)
        {
            error ??= "The encapsulated content info is malformed.";
            return false;
        }

        byte[]? content = null;
        if (enc.Count == 2)
        {
            var c = new List<Asn1Element>(1);
            if (!enc[1].IsContext(0) || !Asn1Reader.TryChildren(enc[1], 2, c, out error) || c.Count != 1 || !Asn1Reader.TryOctetString(c[0], MaxContentBytes, out byte[] held))
            {
                error ??= "The encapsulated content is malformed or too large.";
                return false;
            }

            content = held;
        }

        var certs = new List<ReadOnlyMemory<byte>>();
        while (i < f.Count && (f[i].IsContext(0) || f[i].IsContext(1)))
        {
            if (f[i].IsContext(0))
            {
                var certEls = new List<Asn1Element>();
                if (!Asn1Reader.TryChildren(f[i], MaxCertificates, certEls, out error))
                {
                    return false;
                }

                foreach (Asn1Element ce in certEls)
                {
                    if (ce.IsSequence)
                    {
                        certs.Add(ce.Encoded);
                    }
                }
            }

            i++;
        }

        if (i >= f.Count || !f[i].IsSet)
        {
            error = "The SignedData has no signer information.";
            return false;
        }

        var signerEls = new List<Asn1Element>();
        if (!Asn1Reader.TryChildren(f[i], MaxSigners, signerEls, out error))
        {
            return false;
        }

        var signers = new List<CmsSignerInfo>();
        foreach (Asn1Element se in signerEls)
        {
            if (!TryParseSigner(se, out CmsSignerInfo? si, out error))
            {
                return false;
            }

            signers.Add(si!);
        }

        result = new CmsSignedData
        {
            Version = version,
            DigestAlgorithms = digestAlgs,
            ContentType = contentType,
            Content = content,
            Certificates = certs,
            Signers = signers,
        };
        return true;
    }

    private static bool TryParseSigner(in Asn1Element e, out CmsSignerInfo? si, out string? error)
    {
        si = null;
        error = null;
        var f = new List<Asn1Element>(8);
        if (!e.IsSequence || !Asn1Reader.TryChildren(e, 9, f, out error) || f.Count < 5)
        {
            error ??= "A signer information is malformed.";
            return false;
        }

        int i = 0;
        int version = Asn1Reader.TrySmallInt(f[i++]);
        ReadOnlyMemory<byte> issuer = default, serial = default, ski = default;
        Asn1Element sid = f[i++];
        if (sid.IsSequence)
        {
            var iss = new List<Asn1Element>(2);
            if (!Asn1Reader.TryChildren(sid, 3, iss, out error) || iss.Count != 2 || !iss[0].IsSequence || !iss[1].IsUniversal(Asn1Tags.Integer))
            {
                error ??= "The signer identifier is malformed.";
                return false;
            }

            issuer = iss[0].Encoded;
            serial = iss[1].Content;
        }
        else if (sid.IsContext(0) && !sid.Constructed)
        {
            ski = sid.Content;
        }
        else
        {
            error = "The signer identifier is malformed.";
            return false;
        }

        if (version < 0 || !CmsAlgorithm.TryParse(f[i++], out CmsAlgorithm digest))
        {
            error = "The signer information is malformed.";
            return false;
        }

        ReadOnlyMemory<byte> signedEncoded = default;
        IReadOnlyList<CmsAttribute> signed = [];
        if (f[i].IsContext(0))
        {
            if (!TryAttributes(f[i], out signed, out error))
            {
                return false;
            }

            signedEncoded = f[i].Encoded;
            i++;
        }

        if (i + 1 >= f.Count || !CmsAlgorithm.TryParse(f[i++], out CmsAlgorithm sigAlg) || !f[i].IsUniversal(Asn1Tags.OctetString) || f[i].Constructed)
        {
            error = "The signature is missing or malformed.";
            return false;
        }

        byte[] signature = f[i++].Content.ToArray();
        IReadOnlyList<CmsAttribute> unsigned = [];
        if (i < f.Count && f[i].IsContext(1) && !TryAttributes(f[i], out unsigned, out error))
        {
            return false;
        }

        si = new CmsSignerInfo
        {
            Version = version,
            IssuerName = issuer,
            SerialNumber = serial,
            SubjectKeyIdentifier = ski,
            DigestAlgorithm = digest,
            SignedAttributesEncoded = signedEncoded,
            SignedAttributes = signed,
            SignatureAlgorithm = sigAlg,
            Signature = signature,
            UnsignedAttributes = unsigned,
        };
        error = null;
        return true;
    }

    private static bool TryAttributes(in Asn1Element set, out IReadOnlyList<CmsAttribute> attributes, out string? error)
    {
        var list = new List<CmsAttribute>();
        attributes = list;
        var els = new List<Asn1Element>();
        if (!Asn1Reader.TryChildren(set, MaxAttributes, els, out error))
        {
            return false;
        }

        foreach (Asn1Element a in els)
        {
            var kids = new List<Asn1Element>(2);
            if (!a.IsSequence || !Asn1Reader.TryChildren(a, 3, kids, out error) || kids.Count != 2 || Asn1Reader.TryOid(kids[0]) is not { } oid || !kids[1].IsSet)
            {
                error ??= "An attribute is malformed.";
                return false;
            }

            var vals = new List<Asn1Element>();
            if (!Asn1Reader.TryChildren(kids[1], 16, vals, out error))
            {
                return false;
            }

            var encs = new List<ReadOnlyMemory<byte>>(vals.Count);
            foreach (Asn1Element v in vals)
            {
                encs.Add(v.Encoded);
            }

            list.Add(new CmsAttribute(oid, encs));
        }

        error = null;
        return true;
    }
}

/// <summary>The few certificate fields needed to match a signer to a certificate.</summary>
internal readonly struct CertificateIdentity
{
    public ReadOnlyMemory<byte> IssuerName { get; init; }

    public ReadOnlyMemory<byte> SerialNumber { get; init; }

    public ReadOnlyMemory<byte> SubjectKeyIdentifier { get; init; }

    public static bool TryParse(ReadOnlyMemory<byte> der, out CertificateIdentity id)
    {
        id = default;
        var top = new List<Asn1Element>(3);
        if (!Asn1Reader.TryRead(der, out Asn1Element cert, out _, out _) || !cert.IsSequence || !Asn1Reader.TryChildren(cert, 4, top, out _) || top.Count < 3 || !top[0].IsSequence)
        {
            return false;
        }

        var tbs = new List<Asn1Element>(12);
        if (!Asn1Reader.TryChildren(top[0], 16, tbs, out _))
        {
            return false;
        }

        int i = 0;
        if (tbs.Count > 0 && tbs[0].IsContext(0))
        {
            i++;
        }

        // serial, signature algorithm, issuer
        if (i + 2 >= tbs.Count || !tbs[i].IsUniversal(Asn1Tags.Integer) || !tbs[i + 2].IsSequence)
        {
            return false;
        }

        ReadOnlyMemory<byte> ski = default;
        foreach (Asn1Element t in tbs)
        {
            if (!t.IsContext(3))
            {
                continue;
            }

            var seq = new List<Asn1Element>(1);
            var exts = new List<Asn1Element>();
            if (Asn1Reader.TryChildren(t, 2, seq, out _) && seq.Count == 1 && Asn1Reader.TryChildren(seq[0], 64, exts, out _))
            {
                foreach (Asn1Element ext in exts)
                {
                    var parts = new List<Asn1Element>(3);
                    if (Asn1Reader.TryChildren(ext, 4, parts, out _) && parts.Count >= 2 && Asn1Reader.TryOid(parts[0]) == Oids.SubjectKeyIdentifier
                        && Asn1Reader.TryRead(parts[^1].Content, out Asn1Element keyId, out _, out _) && keyId.IsUniversal(Asn1Tags.OctetString))
                    {
                        ski = keyId.Content;
                    }
                }
            }
        }

        id = new CertificateIdentity { SerialNumber = tbs[i].Content, IssuerName = tbs[i + 2].Encoded, SubjectKeyIdentifier = ski };
        return true;
    }

    public bool Matches(CmsSignerInfo signer)
    {
        if (!signer.SubjectKeyIdentifier.IsEmpty)
        {
            return !SubjectKeyIdentifier.IsEmpty && SubjectKeyIdentifier.Span.SequenceEqual(signer.SubjectKeyIdentifier.Span);
        }

        return IssuerName.Span.SequenceEqual(signer.IssuerName.Span) && SameNumber(SerialNumber.Span, signer.SerialNumber.Span);
    }

    private static bool SameNumber(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        while (a.Length > 1 && a[0] == 0)
        {
            a = a[1..];
        }

        while (b.Length > 1 && b[0] == 0)
        {
            b = b[1..];
        }

        return a.SequenceEqual(b);
    }
}
