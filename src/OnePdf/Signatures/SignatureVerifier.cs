using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OnePdf.Asn1;

namespace OnePdf.Internal;

/// <summary>
/// Verifies one signature using only the platform's hash, RSA, ECDSA and X.509 primitives: the CMS structure is read by
/// <see cref="CmsSignedData"/>, so no CMS library is needed. Nothing here touches a network: chain building has downloads
/// turned off and revocation set to "no check".
/// </summary>
internal static class SignatureVerifier
{
    public static PdfSignatureVerification Verify(PdfSignature sig, PdfSignatureVerifyOptions options, CancellationToken ct)
    {
        var v = new PdfSignatureVerification(sig) { RequireKeyUsage = options.RequireSigningKeyUsage };
        var warnings = new List<string>();
        ByteRangeInfo br = sig.Range;
        v.ByteRange = br.Status;
        v.CoveredRevision = sig.CoveredRevision;
        v.HasLaterRevisions = sig.HasLaterRevisions;
        if (br.Detail is not null)
        {
            warnings.Add(br.Detail);
        }

        if (br.DelimitersExcluded)
        {
            warnings.Add("The byte range excludes the angle brackets of /Contents; ISO 32000-2 12.8.1 says the whole value is left out.");
        }

        bool rangesUsable = br.Status is PdfByteRangeStatus.Valid or PdfByteRangeStatus.DoesNotCoverRevision;
        var digests = new Dictionary<string, byte[]>();

        byte[]? RangeDigest(HashAlgorithmName name)
        {
            if (!rangesUsable)
            {
                return null;
            }

            if (digests.TryGetValue(name.Name!, out byte[]? cached))
            {
                return cached;
            }

            byte[] h = HashRanges(sig.Document, br, name, ct);
            digests[name.Name!] = h;
            return h;
        }

        ReadOnlyMemory<byte> contents = sig.Contents;
        string? subFilter = sig.SubFilter;
        var pool = new CertPool(options.ExtraCertificates, warnings);
        try
        {
            if (sig.Kind == PdfSignatureKind.DocumentTimestamp)
            {
                VerifyDocumentTimestamp(v, contents, RangeDigest, pool, options, warnings);
            }
            else if (subFilter is "adbe.x509.rsa_sha1")
            {
                v.UsesWeakAlgorithm = true;
                v.Integrity = PdfCheckStatus.NotChecked;
                v.SignatureCheck = PdfCheckStatus.NotChecked;
                warnings.Add("The legacy sub-filter adbe.x509.rsa_sha1 is not supported and is weak (SHA-1 over a bare RSA signature); the signature was not verified.");
            }
            else if (subFilter is not ("adbe.pkcs7.detached" or "adbe.pkcs7.sha1" or "ETSI.CAdES.detached"))
            {
                warnings.Add(subFilter is null ? "The signature has no /SubFilter." : $"The sub-filter {subFilter} is not supported; the signature was not verified.");
                v.Integrity = PdfCheckStatus.NotChecked;
            }
            else
            {
                VerifyCms(v, contents, subFilter, RangeDigest, pool, options, warnings, ct);
            }

            if (sig.Kind == PdfSignatureKind.Signature && v.SignerCertificate is null)
            {
                v.Chain = PdfChainStatus.NotChecked;
            }
        }
        finally
        {
            pool.Dispose();
        }

        if (options.RevocationCheck is null && v.Chain != PdfChainStatus.NotChecked)
        {
            warnings.Add("Revocation was not checked: no revocation data was supplied, and OnePDF never fetches any.");
        }

        if (options.AnalyzeModifications)
        {
            ModificationAnalyzer.Analyze(sig, v, warnings, ct);
        }
        else
        {
            v.Coverage = br.Status != PdfByteRangeStatus.Valid ? PdfSignatureCoverage.Unknown
                : sig.HasLaterRevisions ? PdfSignatureCoverage.EarlierRevisionAllowedChanges : PdfSignatureCoverage.WholeDocument;
            if (sig.HasLaterRevisions)
            {
                warnings.Add("The file has revisions after this signature; they were not analysed.");
            }
        }

        v.Warnings = warnings;
        return v;
    }

    // ---- CMS signatures -------------------------------------------------------------------------------------------------

    private static void VerifyCms(PdfSignatureVerification v, ReadOnlyMemory<byte> contents, string subFilter, Func<HashAlgorithmName, byte[]?> rangeDigest,
        CertPool pool, PdfSignatureVerifyOptions options, List<string> warnings, CancellationToken ct)
    {
        v.Integrity = PdfCheckStatus.Invalid;
        v.SignatureCheck = PdfCheckStatus.Invalid;
        if (!CmsSignedData.TryParse(contents, out CmsSignedData? sd, out string? error))
        {
            warnings.Add("The signature value is not a valid CMS SignedData: " + error);
            return;
        }

        if (sd!.Signers.Count == 0)
        {
            warnings.Add("The CMS has no signer information.");
            return;
        }

        if (sd.Signers.Count > 1)
        {
            warnings.Add($"The CMS has {sd.Signers.Count} signers; only the first was verified.");
        }

        pool.AddEmbedded(sd);
        CmsSignerInfo si = sd.Signers[0];
        Func<HashAlgorithmName, byte[]?> contentDigest = rangeDigest;
        bool legacySha1 = subFilter == "adbe.pkcs7.sha1";
        if (legacySha1)
        {
            // The digest of the byte ranges is the encapsulated content; the signer signs that content.
            v.UsesWeakAlgorithm = true;
            warnings.Add("The legacy sub-filter adbe.pkcs7.sha1 is weak (SHA-1).");
            byte[]? rd = rangeDigest(HashAlgorithmName.SHA1);
            if (sd.Content is null || rd is null || !sd.Content.AsSpan().SequenceEqual(rd))
            {
                warnings.Add("The SHA-1 digest of the byte ranges does not match the content held in the signature.");
                contentDigest = static _ => null;
                if (rd is not null)
                {
                    // The signature itself can still be checked against what it holds.
                    byte[] held = sd.Content ?? [];
                    contentDigest = name => HashBytes(name, held);
                }

                SignerOutcome bad = CheckSigner(sd, si, contentDigest, pool, warnings);
                Apply(v, bad);
                v.Integrity = PdfCheckStatus.Invalid;
                FinishCms(v, si, bad, pool, options, warnings, ct);
                return;
            }

            byte[] content = sd.Content;
            contentDigest = name => HashBytes(name, content);
        }
        else if (sd.Content is not null)
        {
            warnings.Add("The CMS holds its own content although the sub-filter is detached; it is ignored.");
        }

        SignerOutcome o = CheckSigner(sd, si, contentDigest, pool, warnings);
        Apply(v, o);
        FinishCms(v, si, o, pool, options, warnings, ct);
    }

    private static void Apply(PdfSignatureVerification v, SignerOutcome o)
    {
        v.Integrity = o.Integrity;
        v.SignatureCheck = o.Signature;
        v.SignerCertificate = o.Cert;
        v.DigestAlgorithm = o.DigestName;
        v.SignatureAlgorithm = o.SigName;
        v.UsesWeakAlgorithm |= o.Weak;
        v.ClaimedSigningTime = o.SigningTime ?? v.Signature.SigningTime;
    }

    private static void FinishCms(PdfSignatureVerification v, CmsSignerInfo si, SignerOutcome o, CertPool pool, PdfSignatureVerifyOptions options,
        List<string> warnings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (si.FindUnsigned(Oids.AdobeRevocationInfo) is not null)
        {
            warnings.Add("The signature embeds revocation data (adbe-revocationInfoArchival); OnePDF does not use it.");
        }

        PdfTimestampResult? ts = null;
        if (si.FindUnsigned(Oids.SignatureTimeStampToken) is { } tsAttr && tsAttr.Values.Count > 0)
        {
            ts = VerifyTimestampToken(tsAttr.Values[0], name => HashBytes(name, si.Signature), pool, options);
            v.Timestamp = ts;
        }

        if (options.ValidationTime is { } explicitTime)
        {
            v.ValidationTime = explicitTime;
            v.ValidationTimeSource = PdfValidationTimeSource.Explicit;
        }
        else if (options.TimePolicy == PdfValidationTimePolicy.Auto && ts is { IsValid: true, Time: { } tsTime })
        {
            v.ValidationTime = tsTime;
            v.ValidationTimeSource = PdfValidationTimeSource.Timestamp;
        }
        else if (options.TimePolicy == PdfValidationTimePolicy.Auto && v.ClaimedSigningTime is { } claimed)
        {
            v.ValidationTime = claimed;
            v.ValidationTimeSource = PdfValidationTimeSource.SigningTime;
            warnings.Add("Certificates were validated at the signer's own claimed signing time, which is not authenticated by a time-stamp.");
        }
        else
        {
            v.ValidationTime = DateTimeOffset.UtcNow;
            v.ValidationTimeSource = PdfValidationTimeSource.Now;
        }

        if (o.Cert is not null)
        {
            v.KeyUsage = o.KeyUsageProblem is null ? PdfCheckStatus.Valid : PdfCheckStatus.Invalid;
            if (o.KeyUsageProblem is not null)
            {
                warnings.Add(o.KeyUsageProblem + (options.RequireSigningKeyUsage ? string.Empty : " (allowed by the options)"));
            }

            ChainResult chain = BuildChain(o.Cert, pool, options, v.ValidationTime, warnings);
            v.Chain = chain.Status;
            v.ChainElements = chain.Elements;
            v.Revocation = chain.Revocation;
        }
        else
        {
            v.Chain = PdfChainStatus.NotChecked;
        }
    }

    private sealed class SignerOutcome
    {
        public PdfCheckStatus Integrity = PdfCheckStatus.Invalid;
        public PdfCheckStatus Signature = PdfCheckStatus.Invalid;
        public X509Certificate2? Cert;
        public byte[] CertDer = [];
        public string? KeyUsageProblem;
        public string? DigestName;
        public string? SigName;
        public bool Weak;
        public DateTimeOffset? SigningTime;
    }

    private static SignerOutcome CheckSigner(CmsSignedData sd, CmsSignerInfo si, Func<HashAlgorithmName, byte[]?> contentDigest, CertPool pool, List<string> warnings)
    {
        var o = new SignerOutcome
        {
            DigestName = Oids.Name(si.DigestAlgorithm.Oid),
            SigName = Oids.Name(si.SignatureAlgorithm.Oid),
        };
        if (si.FindSigned(Oids.SigningTime) is { } st && st.TryFirst(out Asn1Element ste) && Asn1Reader.TryTime(ste, out DateTimeOffset when))
        {
            o.SigningTime = when;
        }

        HashAlgorithmName? maybeHash = Oids.HashFor(si.DigestAlgorithm.Oid);
        if (maybeHash is not { } hash)
        {
            o.Integrity = PdfCheckStatus.NotChecked;
            o.Signature = PdfCheckStatus.NotChecked;
            warnings.Add($"The digest algorithm {o.DigestName} is not supported.");
            return o;
        }

        if (hash == HashAlgorithmName.SHA1)
        {
            o.Weak = true;
            warnings.Add("The signature uses SHA-1, which is weak.");
        }

        o.Cert = pool.Find(si, out byte[] certDer);
        o.CertDer = certDer;
        if (o.Cert is null)
        {
            warnings.Add("The signer's certificate is not in the signature or the extra certificates.");
        }

        byte[]? expected = contentDigest(hash);
        bool integrityKnown = true;
        byte[] signedData;
        if (si.HasSignedAttributes)
        {
            CmsAttribute? md = si.FindSigned(Oids.MessageDigest);
            if (md is null || md.Values.Count != 1 || !md.TryFirst(out Asn1Element mde) || mde.TagClass != 0 || mde.TagNumber != Asn1Tags.OctetString)
            {
                warnings.Add("The signed attributes have no messageDigest.");
                o.Integrity = PdfCheckStatus.Invalid;
            }
            else if (expected is null)
            {
                o.Integrity = PdfCheckStatus.Invalid;
                warnings.Add("The signed bytes could not be determined from the byte range.");
            }
            else if (!mde.Content.Span.SequenceEqual(expected))
            {
                o.Integrity = PdfCheckStatus.Invalid;
                warnings.Add("The document bytes do not match the digest in the signature (the signed content was altered).");
            }
            else
            {
                o.Integrity = PdfCheckStatus.Valid;
            }

            CmsAttribute? ct = si.FindSigned(Oids.ContentType);
            if (ct is null)
            {
                warnings.Add("The signed attributes have no contentType.");
            }
            else if (!ct.TryFirst(out Asn1Element cte) || Asn1Reader.TryOid(cte) != sd.ContentType)
            {
                warnings.Add("The contentType attribute differs from the encapsulated content type.");
                o.Integrity = PdfCheckStatus.Invalid;
            }

            // RFC 5652 5.4: the signature is over the DER encoding of the attributes as a SET OF.
            byte[] raw = si.SignedAttributesEncoded.ToArray();
            raw[0] = 0x31;
            signedData = HashBytes(hash, raw);
        }
        else
        {
            if (expected is null)
            {
                warnings.Add("The signed bytes could not be determined from the byte range.");
                return o;
            }

            signedData = expected;
            integrityKnown = false;
        }

        // Which hash and padding the signature algorithm names.
        string sigOid = si.SignatureAlgorithm.Oid;
        (bool, bool, HashAlgorithmName?) scheme = sigOid switch
        {
            Oids.RsaEncryption => (true, false, (HashAlgorithmName?)hash),
            Oids.Sha1WithRsa => (true, false, HashAlgorithmName.SHA1),
            Oids.Sha256WithRsa => (true, false, HashAlgorithmName.SHA256),
            Oids.Sha384WithRsa => (true, false, HashAlgorithmName.SHA384),
            Oids.Sha512WithRsa => (true, false, HashAlgorithmName.SHA512),
            Oids.RsaPss => (true, true, null),
            Oids.EcPublicKey => (false, false, hash),
            Oids.EcdsaSha1 => (false, false, HashAlgorithmName.SHA1),
            Oids.EcdsaSha256 => (false, false, HashAlgorithmName.SHA256),
            Oids.EcdsaSha384 => (false, false, HashAlgorithmName.SHA384),
            Oids.EcdsaSha512 => (false, false, HashAlgorithmName.SHA512),
            _ => (false, false, null),
        };
        (bool rsa, bool pss, HashAlgorithmName? sigHash) = scheme;
        bool known = sigOid is Oids.RsaEncryption or Oids.Sha1WithRsa or Oids.Sha256WithRsa or Oids.Sha384WithRsa or Oids.Sha512WithRsa or Oids.RsaPss
            or Oids.EcPublicKey or Oids.EcdsaSha1 or Oids.EcdsaSha256 or Oids.EcdsaSha384 or Oids.EcdsaSha512;
        if (!known)
        {
            o.Signature = PdfCheckStatus.NotChecked;
            if (!integrityKnown)
            {
                o.Integrity = PdfCheckStatus.NotChecked;
            }

            warnings.Add($"The signature algorithm {sigOid} is not supported.");
            return o;
        }

        int saltLength = 0;
        if (pss)
        {
            if (!TryPssParameters(si.SignatureAlgorithm.Parameters, out HashAlgorithmName pssHash, out saltLength, out string? pssError))
            {
                o.Signature = PdfCheckStatus.NotChecked;
                if (!integrityKnown)
                {
                    o.Integrity = PdfCheckStatus.NotChecked;
                }

                warnings.Add("RSASSA-PSS parameters are not supported: " + pssError);
                return o;
            }

            sigHash = pssHash;
            if (pssHash == HashAlgorithmName.SHA1)
            {
                o.Weak = true;
            }
        }

        if (sigHash != hash)
        {
            o.Signature = PdfCheckStatus.Invalid;
            if (!integrityKnown)
            {
                o.Integrity = PdfCheckStatus.Invalid;
            }

            warnings.Add($"The signature algorithm uses {sigHash} but the digest algorithm is {hash}.");
            return o;
        }

        if (o.Cert is null)
        {
            o.Signature = PdfCheckStatus.NotChecked;
            if (!integrityKnown)
            {
                o.Integrity = PdfCheckStatus.NotChecked;
            }

            return o;
        }

        bool ok = false;
        try
        {
            if (rsa)
            {
                using RSA? key = o.Cert.GetRSAPublicKey();
                if (key is null)
                {
                    warnings.Add("The signer certificate does not hold an RSA key.");
                }
                else
                {
                    if (key.KeySize < 2048)
                    {
                        o.Weak = true;
                        warnings.Add($"The signer's RSA key is only {key.KeySize} bits.");
                    }

                    ok = key.VerifyHash(signedData, si.Signature, hash, pss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
                    if (!ok && pss)
                    {
                        warnings.Add($"RSASSA-PSS verification failed (salt length {saltLength}; the platform supports a salt as long as the hash).");
                    }
                }
            }
            else
            {
                using ECDsa? key = o.Cert.GetECDsaPublicKey();
                if (key is null)
                {
                    warnings.Add("The signer certificate does not hold an ECDSA key.");
                }
                else
                {
                    ok = key.VerifyHash(signedData, si.Signature, DSASignatureFormat.Rfc3279DerSequence);
                }
            }
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            warnings.Add("The signature could not be verified: " + e.Message);
        }

        o.Signature = ok ? PdfCheckStatus.Valid : PdfCheckStatus.Invalid;
        if (!ok)
        {
            warnings.Add("The signature value does not verify with the signer certificate's public key.");
        }

        if (!integrityKnown)
        {
            o.Integrity = o.Signature;
        }

        // The signing-certificate attribute binds the signature to one certificate (RFC 5035).
        if (!CheckSigningCertificate(si, o.CertDer, warnings))
        {
            o.Signature = PdfCheckStatus.Invalid;
        }

        o.KeyUsageProblem = KeyUsageProblem(o.Cert);

        return o;
    }

    // Purposes under which a certificate may sign a document: none stated, or one of these (ISO 32000-2 12.8.3, RFC 5280 4.2.1.12,
    // RFC 9336). A certificate limited to other purposes (time-stamping, TLS server, code signing) is not a document signer's.
    private static readonly string[] DocumentSigningPurposes =
    [
        "2.5.29.37.0", "1.3.6.1.5.5.7.3.4", "1.2.840.113583.1.1.5", "1.3.6.1.4.1.311.10.3.12", "1.3.6.1.5.5.7.3.36",
    ];

    /// <summary>Why this certificate may not sign documents (RFC 5280 4.2.1.3 key usage, 4.2.1.12 extended key usage), or null.</summary>
    internal static string? KeyUsageProblem(X509Certificate2 cert)
    {
        try
        {
            foreach (X509Extension ext in cert.Extensions)
            {
                if (ext is X509KeyUsageExtension ku && (ku.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
                {
                    return "The signer certificate's key usage does not include digitalSignature or nonRepudiation.";
                }

                if (ext is X509EnhancedKeyUsageExtension eku && eku.EnhancedKeyUsages.Count > 0)
                {
                    bool ok = false;
                    foreach (Oid oid in eku.EnhancedKeyUsages)
                    {
                        ok |= oid.Value is not null && Array.IndexOf(DocumentSigningPurposes, oid.Value) >= 0;
                    }

                    if (!ok)
                    {
                        return "The signer certificate's extended key usage does not allow signing documents.";
                    }
                }
            }

            return null;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return "The signer certificate's extensions could not be read.";
        }
    }

    // Returns false when an ESS signing-certificate attribute exists and does not match the certificate.
    private static bool CheckSigningCertificate(CmsSignerInfo si, byte[] certDer, List<string> warnings)
    {
        foreach ((string oid, bool v2) in new[] { (Oids.SigningCertificateV2, true), (Oids.SigningCertificate, false) })
        {
            if (si.FindSigned(oid) is not { } attr || !attr.TryFirst(out Asn1Element top))
            {
                continue;
            }

            var seq = new List<Asn1Element>(2);
            var ids = new List<Asn1Element>(4);
            if (!top.IsSequence || !Asn1Reader.TryChildren(top, 3, seq, out _) || seq.Count == 0 || !seq[0].IsSequence || !Asn1Reader.TryChildren(seq[0], 8, ids, out _) || ids.Count == 0)
            {
                warnings.Add("The signing-certificate attribute is malformed.");
                return false;
            }

            var parts = new List<Asn1Element>(3);
            if (!ids[0].IsSequence || !Asn1Reader.TryChildren(ids[0], 4, parts, out _) || parts.Count == 0)
            {
                warnings.Add("The signing-certificate attribute is malformed.");
                return false;
            }

            HashAlgorithmName alg = v2 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1;
            int at = 0;
            if (v2 && parts[0].IsSequence)
            {
                if (!CmsAlgorithm.TryParse(parts[0], out CmsAlgorithm a) || Oids.HashFor(a.Oid) is not { } h)
                {
                    warnings.Add("The signing-certificate attribute uses an unsupported hash.");
                    return true;
                }

                alg = h;
                at = 1;
            }

            if (at >= parts.Count || !parts[at].IsUniversal(Asn1Tags.OctetString))
            {
                warnings.Add("The signing-certificate attribute is malformed.");
                return false;
            }

            if (!parts[at].Content.Span.SequenceEqual(HashBytes(alg, certDer)))
            {
                warnings.Add("The signing-certificate attribute names a different certificate than the one that signed.");
                return false;
            }

            return true;
        }

        return true;
    }

    private static bool TryPssParameters(ReadOnlyMemory<byte> p, out HashAlgorithmName hash, out int salt, out string? error)
    {
        hash = HashAlgorithmName.SHA1;
        HashAlgorithmName mgfHash = HashAlgorithmName.SHA1;
        salt = 20;
        error = null;
        if (p.IsEmpty)
        {
            return true;
        }

        if (!Asn1Reader.TryRead(p, out Asn1Element seq, out _, out _))
        {
            error = "malformed";
            return false;
        }

        if (seq.IsUniversal(Asn1Tags.Null))
        {
            return true;
        }

        var kids = new List<Asn1Element>(4);
        if (!seq.IsSequence || !Asn1Reader.TryChildren(seq, 5, kids, out _))
        {
            error = "malformed";
            return false;
        }

        foreach (Asn1Element k in kids)
        {
            var inner = new List<Asn1Element>(1);
            if (k.TagClass != 2 || !Asn1Reader.TryChildren(k, 2, inner, out _) || inner.Count != 1)
            {
                error = "malformed";
                return false;
            }

            switch (k.TagNumber)
            {
                case 0:
                    if (!CmsAlgorithm.TryParse(inner[0], out CmsAlgorithm h) || Oids.HashFor(h.Oid) is not { } hn)
                    {
                        error = "unsupported hash";
                        return false;
                    }

                    hash = hn;
                    break;
                case 1:
                    if (!CmsAlgorithm.TryParse(inner[0], out CmsAlgorithm m) || m.Oid != Oids.Mgf1 || !Asn1Reader.TryRead(m.Parameters, out Asn1Element mh, out _, out _)
                        || !CmsAlgorithm.TryParse(mh, out CmsAlgorithm mhash) || Oids.HashFor(mhash.Oid) is not { } mn)
                    {
                        error = "unsupported mask generation function";
                        return false;
                    }

                    mgfHash = mn;
                    break;
                case 2:
                    salt = Asn1Reader.TrySmallInt(inner[0]);
                    if (salt < 0)
                    {
                        error = "bad salt length";
                        return false;
                    }

                    break;
                case 3:
                    if (Asn1Reader.TrySmallInt(inner[0]) != 1)
                    {
                        error = "unsupported trailer field";
                        return false;
                    }

                    break;
            }
        }

        if (mgfHash != hash)
        {
            error = "the mask generation hash differs from the message hash";
            return false;
        }

        return true;
    }

    // ---- Time-stamp tokens (RFC 3161) -----------------------------------------------------------------------------------

    private static void VerifyDocumentTimestamp(PdfSignatureVerification v, ReadOnlyMemory<byte> contents, Func<HashAlgorithmName, byte[]?> rangeDigest,
        CertPool pool, PdfSignatureVerifyOptions options, List<string> warnings)
    {
        PdfTimestampResult ts = VerifyTimestampToken(contents, rangeDigest, pool, options);
        v.Timestamp = ts;
        v.Integrity = ts.Imprint;
        v.SignatureCheck = ts.Signature;
        v.SignerCertificate = ts.Certificate;
        v.Chain = ts.Chain;
        v.ChainElements = ts.ChainElements;
        v.ClaimedSigningTime = ts.Time;
        v.ValidationTime = options.ValidationTime ?? ts.Time ?? DateTimeOffset.UtcNow;
        v.ValidationTimeSource = options.ValidationTime is not null ? PdfValidationTimeSource.Explicit : PdfValidationTimeSource.Timestamp;
        foreach (string w in ts.Warnings)
        {
            warnings.Add(w);
        }
    }

    private static PdfTimestampResult VerifyTimestampToken(ReadOnlyMemory<byte> token, Func<HashAlgorithmName, byte[]?> imprintData, CertPool pool, PdfSignatureVerifyOptions options)
    {
        var r = new PdfTimestampResult { Imprint = PdfCheckStatus.Invalid, Signature = PdfCheckStatus.Invalid };
        var w = new List<string>();
        r.Warnings = w;
        if (!CmsSignedData.TryParse(token, out CmsSignedData? sd, out string? error) || sd!.Signers.Count == 0)
        {
            w.Add("The time-stamp token is not a valid CMS SignedData: " + (error ?? "no signer"));
            return r;
        }

        if (sd.ContentType != Oids.TstInfo || sd.Content is null)
        {
            w.Add("The time-stamp token does not hold TSTInfo.");
            return r;
        }

        // TSTInfo ::= SEQUENCE { version, policy, messageImprint, serialNumber, genTime, ... }
        var f = new List<Asn1Element>(10);
        var imprint = new List<Asn1Element>(2);
        if (!Asn1Reader.TryRead(sd.Content, out Asn1Element tst, out _, out _) || !tst.IsSequence || !Asn1Reader.TryChildren(tst, 16, f, out _) || f.Count < 5
            || !f[2].IsSequence || !Asn1Reader.TryChildren(f[2], 3, imprint, out _) || imprint.Count != 2 || !CmsAlgorithm.TryParse(imprint[0], out CmsAlgorithm imprintAlg)
            || !imprint[1].IsUniversal(Asn1Tags.OctetString) || !Asn1Reader.TryTime(f[4], out DateTimeOffset genTime))
        {
            w.Add("The TSTInfo in the time-stamp token is malformed.");
            return r;
        }

        r.Time = genTime;
        if (Oids.HashFor(imprintAlg.Oid) is not { } hashName)
        {
            r.Imprint = PdfCheckStatus.NotChecked;
            r.Signature = PdfCheckStatus.NotChecked;
            w.Add($"The time-stamp imprint hash {Oids.Name(imprintAlg.Oid)} is not supported.");
            return r;
        }

        byte[]? data = imprintData(hashName);
        r.Imprint = data is not null && imprint[1].Content.Span.SequenceEqual(data) ? PdfCheckStatus.Valid : PdfCheckStatus.Invalid;
        if (r.Imprint != PdfCheckStatus.Valid)
        {
            w.Add("The time-stamp's hash does not match the data it stamps.");
        }

        // The token is a SignedData whose content is the TSTInfo; verify its signer like any other.
        byte[] content = sd.Content;
        using var tokenPool = new CertPool(options.ExtraCertificates, w);
        tokenPool.AddEmbedded(sd);
        foreach (CertPool.Entry e in pool.Entries)
        {
            tokenPool.AddCopy(e);
        }

        SignerOutcome o = CheckSigner(sd, sd.Signers[0], name => HashBytes(name, content), tokenPool, w);
        r.Signature = o.Integrity == PdfCheckStatus.Valid ? o.Signature : PdfCheckStatus.Invalid;
        r.Certificate = o.Cert is null ? null : X509CertificateLoader.LoadCertificate(o.CertDer);
        if (o.Cert is not null)
        {
            bool timestamping = false;
            try
            {
                foreach (X509Extension ext in o.Cert.Extensions)
                {
                    if (ext is X509EnhancedKeyUsageExtension eku)
                    {
                        foreach (Oid oid in eku.EnhancedKeyUsages)
                        {
                            timestamping |= oid.Value == "1.3.6.1.5.5.7.3.8";
                        }
                    }
                }
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException)
            {
                w.Add("The time-stamp authority's certificate extensions could not be read.");
            }

            if (!timestamping)
            {
                // RFC 3161 2.3: the authority's certificate must carry the time-stamping purpose.
                w.Add("The time-stamp authority's certificate does not carry the time-stamping extended key usage.");
                r.Signature = PdfCheckStatus.Invalid;
            }

            ChainResult chain = BuildChain(o.Cert, tokenPool, options, options.ValidationTime ?? genTime, w);
            r.Chain = chain.Status;
            r.ChainElements = chain.Elements;
        }

        return r;
    }

    // ---- Certificates and chains ----------------------------------------------------------------------------------------

    private sealed class ChainResult
    {
        public PdfChainStatus Status;
        public List<PdfChainElement> Elements = [];
        public PdfRevocationStatus Revocation;
    }

    private static ChainResult BuildChain(X509Certificate2 leaf, CertPool pool, PdfSignatureVerifyOptions options, DateTimeOffset time, List<string> warnings)
    {
        ChainResult result = TryChain(leaf, pool, options, time, systemStore: false, warnings);
        if (result.Status != PdfChainStatus.Trusted && options.UseSystemTrustStore && options.TrustAnchors.Count > 0)
        {
            ChainResult system = TryChain(leaf, pool, options, time, systemStore: true, warnings);
            if (system.Status == PdfChainStatus.Trusted)
            {
                result = system;
            }
        }

        if (result.Status == PdfChainStatus.Trusted && options.RevocationCheck is { } check)
        {
            bool unknown = false;
            bool revoked = false;
            for (int i = 0; i + 1 < result.Elements.Count; i++)
            {
                PdfRevocationStatus s = check(result.Elements[i].Certificate, result.Elements[i + 1].Certificate);
                revoked |= s == PdfRevocationStatus.Revoked;
                unknown |= s is PdfRevocationStatus.Unknown or PdfRevocationStatus.NotChecked;
            }

            result.Revocation = revoked ? PdfRevocationStatus.Revoked : unknown ? PdfRevocationStatus.Unknown : PdfRevocationStatus.Good;
            if (revoked)
            {
                result.Status = PdfChainStatus.Revoked;
            }
        }

        return result;
    }

    private static ChainResult TryChain(X509Certificate2 leaf, CertPool pool, PdfSignatureVerifyOptions options, DateTimeOffset time, bool systemStore, List<string> warnings)
    {
        var result = new ChainResult();
        using var chain = new X509Chain();
        X509ChainPolicy policy = chain.ChainPolicy;
        policy.RevocationMode = X509RevocationMode.NoCheck;
        policy.DisableCertificateDownloads = true;
        policy.VerificationTime = time.UtcDateTime;
        if (systemStore)
        {
            policy.TrustMode = X509ChainTrustMode.System;
        }
        else
        {
            policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            foreach (X509Certificate2 a in options.TrustAnchors)
            {
                if (CertPool.IsUsable(a))
                {
                    policy.CustomTrustStore.Add(a);
                }
                else
                {
                    warnings.Add("A trust anchor is damaged and was ignored.");
                }
            }

            if (options.UseSystemTrustStore && options.TrustAnchors.Count == 0)
            {
                policy.TrustMode = X509ChainTrustMode.System;
            }
        }

        foreach (CertPool.Entry e in pool.Entries)
        {
            policy.ExtraStore.Add(e.Cert);
        }

        X509ChainStatusFlags flags = X509ChainStatusFlags.NoError;
        bool anyExpired = false;
        bool anyNotYet = false;
        try
        {
            chain.Build(leaf);
            foreach (X509ChainStatus s in chain.ChainStatus)
            {
                flags |= s.Status;
            }

            foreach (X509ChainElement el in chain.ChainElements)
            {
                var problems = new List<string>();
                foreach (X509ChainStatus s in el.ChainElementStatus)
                {
                    if (s.Status is not (X509ChainStatusFlags.NoError or X509ChainStatusFlags.RevocationStatusUnknown or X509ChainStatusFlags.OfflineRevocation))
                    {
                        problems.Add(s.Status.ToString());
                    }
                }

                DateTimeOffset nb = el.Certificate.NotBefore.ToUniversalTime();
                DateTimeOffset na = el.Certificate.NotAfter.ToUniversalTime();
                anyExpired |= na < time;
                anyNotYet |= nb > time;
                result.Elements.Add(new PdfChainElement(X509CertificateLoader.LoadCertificate(el.Certificate.RawData), problems));
            }
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            // A certificate that loads but cannot be read further (for example an unreadable validity period).
            warnings.Add("The certificate chain could not be built: " + e.Message);
            result.Status = PdfChainStatus.Invalid;
            return result;
        }

        const X509ChainStatusFlags ignorable = X509ChainStatusFlags.NoError | X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;
        if ((flags & X509ChainStatusFlags.Revoked) != 0)
        {
            result.Status = PdfChainStatus.Revoked;
        }
        else if ((flags & X509ChainStatusFlags.NotTimeValid) != 0)
        {
            result.Status = anyExpired || !anyNotYet ? PdfChainStatus.Expired : PdfChainStatus.NotYetValid;
        }
        else if ((flags & X509ChainStatusFlags.UntrustedRoot) != 0)
        {
            result.Status = PdfChainStatus.UntrustedRoot;
        }
        else if ((flags & X509ChainStatusFlags.PartialChain) != 0)
        {
            result.Status = PdfChainStatus.Incomplete;
        }
        else if ((flags & ~ignorable) != 0)
        {
            result.Status = PdfChainStatus.Invalid;
            warnings.Add("The certificate chain is not valid: " + (flags & ~ignorable));
        }
        else
        {
            result.Status = PdfChainStatus.Trusted;
        }

        return result;
    }

    /// <summary>The certificates available for building a chain, owned and disposed here.</summary>
    internal sealed class CertPool : IDisposable
    {
        private readonly List<string> _warn;

        public CertPool(IReadOnlyList<X509Certificate2> extras, List<string> warnings)
        {
            _warn = warnings;
            for (int i = 0; i < Math.Min(extras.Count, CmsSignedData.MaxCertificates); i++)
            {
                byte[] raw;
                try
                {
                    raw = extras[i].RawData;
                }
                catch (Exception e) when (e is CryptographicException or ArgumentException)
                {
                    _warn.Add("An extra certificate could not be read and was ignored.");
                    continue;
                }

                AddDer(raw);
            }
        }

        public List<Entry> Entries { get; } = [];

        public void AddEmbedded(CmsSignedData sd)
        {
            foreach (ReadOnlyMemory<byte> der in sd.Certificates)
            {
                if (Entries.Count >= CmsSignedData.MaxCertificates * 2)
                {
                    _warn.Add("The signature carries more certificates than are used; the rest are ignored.");
                    break;
                }

                AddDer(der.ToArray());
            }
        }

        public void AddCopy(Entry e)
        {
            AddDer(e.Der);
        }

        public X509Certificate2? Find(CmsSignerInfo si, out byte[] der)
        {
            foreach (Entry e in Entries)
            {
                if (e.Identity.Matches(si))
                {
                    der = e.Der;
                    return e.Cert;
                }
            }

            der = [];
            return null;
        }

        public void Dispose()
        {
            foreach (Entry e in Entries)
            {
                e.Cert.Dispose();
            }

            Entries.Clear();
        }

        private void AddDer(byte[] der)
        {
            foreach (Entry e in Entries)
            {
                if (e.Der.AsSpan().SequenceEqual(der))
                {
                    return;
                }
            }

            X509Certificate2? cert = null;
            try
            {
                cert = X509CertificateLoader.LoadCertificate(der);
                if (!IsUsable(cert))
                {
                    cert.Dispose();
                    _warn.Add("A certificate in the signature is damaged and was ignored.");
                    return;
                }

                _ = CertificateIdentity.TryParse(der, out CertificateIdentity id);
                Entries.Add(new Entry(cert, der, id));
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException)
            {
                cert?.Dispose();
                _warn.Add("A certificate in the signature could not be parsed and was ignored.");
            }
        }

        /// <summary>
        /// Some platforms (macOS) accept certificate bytes and parse them only when a property is read, so every property the
        /// verifier uses is read once here; a certificate that fails is left out instead of failing a later check.
        /// </summary>
        internal static bool IsUsable(X509Certificate2 cert)
        {
            try
            {
                _ = cert.RawData;
                _ = cert.NotBefore;
                _ = cert.NotAfter;
                _ = cert.SubjectName.RawData;
                _ = cert.IssuerName.RawData;
                _ = cert.PublicKey.Oid.Value;
                foreach (X509Extension ext in cert.Extensions)
                {
                    _ = ext.RawData;
                }

                // Decoding the key is where a damaged SubjectPublicKeyInfo shows on some platforms.
                using RSA? rsa = cert.GetRSAPublicKey();
                if (rsa is null)
                {
                    using ECDsa? ec = cert.GetECDsaPublicKey();
                }

                return true;
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException)
            {
                return false;
            }
        }

        internal readonly record struct Entry(X509Certificate2 Cert, byte[] Der, CertificateIdentity Identity);
    }

    // ---- Hashing --------------------------------------------------------------------------------------------------------

    internal static byte[] HashBytes(HashAlgorithmName name, ReadOnlySpan<byte> data)
    {
        using IncrementalHash h = IncrementalHash.CreateHash(name);
        h.AppendData(data);
        return h.GetHashAndReset();
    }

    private static byte[] HashRanges(PdfDocument doc, ByteRangeInfo br, HashAlgorithmName name, CancellationToken ct)
    {
        using IncrementalHash h = IncrementalHash.CreateHash(name);
        Feed(doc, h, 0, br.HoleStart, ct);
        Feed(doc, h, br.SecondStart, br.SecondLength, ct);
        return h.GetHashAndReset();
    }

    private static void Feed(PdfDocument doc, IncrementalHash h, long start, long length, CancellationToken ct)
    {
        const int chunk = 1 << 18;
        long pos = start;
        long left = length;
        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(chunk, left);
            h.AppendData(doc.ReadRange(pos, n).Span);
            pos += n;
            left -= n;
        }
    }
}
