using System.Security.Cryptography;

namespace OnePdf.Asn1;

/// <summary>
/// The client side of RFC 3161 that OnePDF needs without a network: building a <c>TimeStampReq</c>, and taking a <c>TimeStampResp</c>
/// (or a bare token) apart to check it before it is embedded. Whether the authority is trusted is the verifier's question.
/// </summary>
internal static class Rfc3161
{
    /// <summary>Tokens and responses larger than this are refused before they are parsed.</summary>
    public const int MaxResponseBytes = 1 << 20;

    /// <summary>
    /// <c>TimeStampReq ::= SEQUENCE { version 1, messageImprint, nonce, certReq TRUE }</c> (RFC 3161 2.4.1). The certificate is
    /// requested so the token is self-contained.
    /// </summary>
    public static byte[] BuildRequest(HashAlgorithmName hash, ReadOnlySpan<byte> imprint, ReadOnlySpan<byte> nonce)
    {
        byte[] messageImprint = DerWriter.Sequence(CmsWriter.DigestAlgorithm(hash), DerWriter.OctetString(imprint));
        return DerWriter.Sequence(DerWriter.Integer(1), messageImprint, DerWriter.UnsignedInteger(nonce), DerWriter.Boolean(true));
    }

    /// <summary>
    /// Checks a response or token and returns the token (a CMS ContentInfo). Fails with a message when the status is not granted,
    /// the structure is malformed, the imprint differs from the one sent, or the nonce is missing or different.
    /// </summary>
    public static bool TryExtractToken(byte[] data, HashAlgorithmName hash, ReadOnlySpan<byte> imprint, ReadOnlySpan<byte> nonce, out byte[] token, out string error)
    {
        token = [];
        if (data.Length == 0 || data.Length > MaxResponseBytes)
        {
            error = data.Length == 0 ? "The time-stamp callback returned nothing." : "The time-stamp response is larger than 1 MiB.";
            return false;
        }

        if (!Asn1Reader.TryRead(data, out Asn1Element top, out _, out string? readError) || !top.IsSequence)
        {
            error = "The time-stamp response is not a DER sequence" + (readError is null ? "." : ": " + readError);
            return false;
        }

        var kids = new List<Asn1Element>(3);
        if (!Asn1Reader.TryChildren(top, 4, kids, out _) || kids.Count == 0)
        {
            error = "The time-stamp response is malformed.";
            return false;
        }

        ReadOnlyMemory<byte> tokenBytes;
        if (Asn1Reader.TryOid(kids[0]) is not null)
        {
            // A bare token: ContentInfo ::= SEQUENCE { contentType OID, content [0] }.
            tokenBytes = top.Encoded;
        }
        else
        {
            // TimeStampResp ::= SEQUENCE { status PKIStatusInfo, timeStampToken ContentInfo OPTIONAL }; PKIStatus 0 granted, 1 grantedWithMods.
            var status = new List<Asn1Element>(3);
            if (!kids[0].IsSequence || !Asn1Reader.TryChildren(kids[0], 4, status, out _) || status.Count == 0 || Asn1Reader.TrySmallInt(status[0]) < 0)
            {
                error = "The time-stamp response has no readable status.";
                return false;
            }

            int code = Asn1Reader.TrySmallInt(status[0]);
            if (code > 1)
            {
                error = $"The time-stamp authority refused the request (PKIStatus {code}).";
                return false;
            }

            if (kids.Count < 2 || !kids[1].IsSequence)
            {
                error = "The time-stamp response is granted but holds no token.";
                return false;
            }

            tokenBytes = kids[1].Encoded;
        }

        if (!CmsSignedData.TryParse(tokenBytes, out CmsSignedData? sd, out string? cmsError) || sd!.Signers.Count == 0)
        {
            error = "The time-stamp token is not a valid CMS SignedData: " + (cmsError ?? "it has no signer.");
            return false;
        }

        if (sd.ContentType != Oids.TstInfo || sd.Content is null)
        {
            error = "The time-stamp token does not hold a TSTInfo.";
            return false;
        }

        // TSTInfo ::= SEQUENCE { version, policy, messageImprint, serialNumber, genTime, accuracy?, ordering?, nonce?, tsa?, extensions? }
        var f = new List<Asn1Element>(12);
        var mi = new List<Asn1Element>(2);
        if (!Asn1Reader.TryRead(sd.Content, out Asn1Element tst, out _, out _) || !tst.IsSequence || !Asn1Reader.TryChildren(tst, 16, f, out _) || f.Count < 5
            || !f[2].IsSequence || !Asn1Reader.TryChildren(f[2], 3, mi, out _) || mi.Count != 2 || !CmsAlgorithm.TryParse(mi[0], out CmsAlgorithm alg)
            || !mi[1].IsUniversal(Asn1Tags.OctetString))
        {
            error = "The TSTInfo in the time-stamp token is malformed.";
            return false;
        }

        if (alg.Oid != CmsWriter.HashOid(hash) || !mi[1].Content.Span.SequenceEqual(imprint))
        {
            error = "The time-stamp token stamps different data than was sent (message imprint mismatch).";
            return false;
        }

        byte[] echoed = [];
        bool hasNonce = false;
        for (int i = 5; i < f.Count; i++)
        {
            if (f[i].IsUniversal(Asn1Tags.Integer))
            {
                Asn1Element nonceElement = f[i];
                echoed = Asn1Reader.IntegerMagnitude(nonceElement).ToArray();
                hasNonce = true;
                break;
            }
        }

        // RFC 3161 2.4.2: when the request carried a nonce the token must carry the same one.
        ReadOnlySpan<byte> sent = nonce;
        while (sent.Length > 1 && sent[0] == 0)
        {
            sent = sent[1..];
        }

        if (!hasNonce || !echoed.AsSpan().SequenceEqual(sent))
        {
            error = hasNonce ? "The time-stamp token echoes a different nonce." : "The time-stamp token does not echo the nonce of the request.";
            return false;
        }

        // PAdES wants the SignedData in /Contents as DER (ETSI EN 319 142-1); an authority may have used indefinite lengths.
        if (!BerToDer.TryConvert(tokenBytes, out token))
        {
            token = [];
            error = "The time-stamp token could not be re-encoded as DER.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
