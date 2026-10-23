using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace OnePdf.Tests.Signatures;

/// <summary>Minimal DER encoder for building CMS test data.</summary>
internal static class Der
{
    public static byte[] Wrap(byte tag, params byte[][] parts)
    {
        int n = parts.Sum(static p => p.Length);
        var body = new byte[n];
        int at = 0;
        foreach (byte[] p in parts)
        {
            p.CopyTo(body, at);
            at += p.Length;
        }

        var head = new List<byte> { tag };
        if (n < 0x80)
        {
            head.Add((byte)n);
        }
        else if (n < 0x100)
        {
            head.AddRange([0x81, (byte)n]);
        }
        else if (n < 0x10000)
        {
            head.AddRange([0x82, (byte)(n >> 8), (byte)n]);
        }
        else
        {
            head.AddRange([0x83, (byte)(n >> 16), (byte)(n >> 8), (byte)n]);
        }

        return [.. head, .. body];
    }

    public static byte[] Seq(params byte[][] parts) => Wrap(0x30, parts);

    public static byte[] Set(params byte[][] parts) => Wrap(0x31, parts);

    /// <summary>A SET OF in DER order: members sorted by their encodings.</summary>
    public static byte[] SetOf(IEnumerable<byte[]> parts)
    {
        byte[][] sorted = [.. parts.OrderBy(static p => p, Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b)))];
        return Wrap(0x31, sorted);
    }

    public static byte[] Oct(byte[] data) => Wrap(0x04, data);

    public static byte[] Null() => [0x05, 0x00];

    public static byte[] Ctx(int n, params byte[][] parts) => Wrap((byte)(0xA0 | n), parts);

    public static byte[] CtxPrim(int n, byte[] data) => Wrap((byte)(0x80 | n), data);

    public static byte[] Int(int v) => IntBytes(BitConverter.GetBytes(v).Reverse().SkipWhile(static b => b == 0).DefaultIfEmpty((byte)0).ToArray());

    public static byte[] IntBytes(byte[] magnitude)
    {
        byte[] m = magnitude.Length == 0 ? [0] : magnitude;
        if ((m[0] & 0x80) != 0)
        {
            m = [0, .. m];
        }

        return Wrap(0x02, m);
    }

    public static byte[] Oid(string dotted)
    {
        string[] parts = dotted.Split('.');
        var bytes = new List<byte> { (byte)((int.Parse(parts[0], CultureInfo.InvariantCulture) * 40) + int.Parse(parts[1], CultureInfo.InvariantCulture)) };
        for (int i = 2; i < parts.Length; i++)
        {
            ulong v = ulong.Parse(parts[i], CultureInfo.InvariantCulture);
            var stack = new Stack<byte>();
            stack.Push((byte)(v & 0x7F));
            v >>= 7;
            while (v > 0)
            {
                stack.Push((byte)(0x80 | (v & 0x7F)));
                v >>= 7;
            }

            bytes.AddRange(stack);
        }

        return Wrap(0x06, [.. bytes]);
    }

    public static byte[] GeneralizedTime(DateTimeOffset t) => Wrap(0x18, Encoding.ASCII.GetBytes(t.UtcDateTime.ToString("yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture)));

    public static byte[] UtcTime(DateTimeOffset t) => Wrap(0x17, Encoding.ASCII.GetBytes(t.UtcDateTime.ToString("yyMMddHHmmss'Z'", CultureInfo.InvariantCulture)));
}

/// <summary>A throwaway PKI: a root, an intermediate, signers with RSA and EC keys, a time-stamp authority and an unrelated root.</summary>
internal sealed class TestPki
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static readonly TestPki Shared = new();

    private TestPki()
    {
        using var rootKey = RSA.Create(2048);
        Root = CreateCa("CN=OnePdf Test Root", rootKey, null, Now.AddYears(-6), Now.AddYears(20), 1);
        using var interKey = RSA.Create(2048);
        Intermediate = CreateCa("CN=OnePdf Test Intermediate", interKey, Root, Now.AddYears(-6), Now.AddYears(15), 0);
        using var signerKey = RSA.Create(2048);
        RsaSigner = Leaf("CN=Rita RSA, O=Test", signerKey, Intermediate, Now.AddYears(-1), Now.AddYears(5), Usage.Sign);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        EcSigner = Leaf("CN=Edna EC, O=Test", ec, Intermediate, Now.AddYears(-1), Now.AddYears(5), Usage.Sign);
        using var ec384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Ec384Signer = Leaf("CN=Ed Three-Eighty-Four, O=Test", ec384, Intermediate, Now.AddYears(-1), Now.AddYears(5), Usage.Sign);
        using var tsaKey = RSA.Create(2048);
        Tsa = Leaf("CN=Test TSA", tsaKey, Intermediate, Now.AddYears(-1), Now.AddYears(5), Usage.Timestamp);
        using var oldKey = RSA.Create(2048);
        ExpiredSigner = Leaf("CN=Old Olaf", oldKey, Intermediate, Now.AddYears(-4), Now.AddYears(-2), Usage.Sign);
        using var otherKey = RSA.Create(2048);
        OtherRoot = CreateCa("CN=Other Root", otherKey, null, Now.AddYears(-6), Now.AddYears(20), 0);
        using var kuKey = RSA.Create(2048);
        KeyEnciphermentSigner = Leaf("CN=Key Only", kuKey, Intermediate, Now.AddYears(-1), Now.AddYears(5), Usage.KeyEncipherment);
        using var tlsKey = RSA.Create(2048);
        ServerAuthSigner = Leaf("CN=Tls Only", tlsKey, Intermediate, Now.AddYears(-1), Now.AddYears(5), Usage.ServerAuth);
        using var mailKey = RSA.Create(2048);
        EmailSigner = Leaf("CN=Mail Signer", mailKey, Intermediate, Now.AddYears(-1), Now.AddYears(5), Usage.EmailProtection);
        using var strangerKey = RSA.Create(2048);
        Stranger = Leaf("CN=Stranger", strangerKey, OtherRoot, Now.AddYears(-1), Now.AddYears(5), Usage.Sign);
    }

    public X509Certificate2 Root { get; }

    public X509Certificate2 Intermediate { get; }

    public X509Certificate2 RsaSigner { get; }

    public X509Certificate2 EcSigner { get; }

    public X509Certificate2 Ec384Signer { get; }

    public X509Certificate2 Tsa { get; }

    public X509Certificate2 ExpiredSigner { get; }

    public X509Certificate2 OtherRoot { get; }

    public X509Certificate2 Stranger { get; }

    /// <summary>A certificate whose key usage is keyEncipherment only.</summary>
    public X509Certificate2 KeyEnciphermentSigner { get; }

    /// <summary>A certificate whose extended key usage is TLS server authentication only.</summary>
    public X509Certificate2 ServerAuthSigner { get; }

    /// <summary>A certificate with the e-mail protection purpose (allowed for signing documents).</summary>
    public X509Certificate2 EmailSigner { get; }

    /// <summary>The root as a public-only certificate, for use as a trust anchor.</summary>
    public X509Certificate2 RootAnchor => X509CertificateLoader.LoadCertificate(Root.RawData);

    public PdfSignatureVerifyOptions Trusting() => new() { TrustAnchors = [RootAnchor] };

    private enum Usage
    {
        Sign,
        Timestamp,
        KeyEncipherment,
        ServerAuth,
        EmailProtection,
    }

    private static X509Certificate2 Issue(CertificateRequest req, X509Certificate2 issuer, DateTimeOffset from, DateTimeOffset to)
    {
        using RSA issuerKey = issuer.GetRSAPrivateKey()!;
        return req.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1), from, to, Serial());
    }

    private static byte[] Serial()
    {
        byte[] s = RandomNumberGenerator.GetBytes(8);
        s[0] &= 0x7F;
        s[0] |= 0x01;
        return s;
    }

    private static X509Certificate2 CreateCa(string subject, RSA key, X509Certificate2? issuer, DateTimeOffset from, DateTimeOffset to, int pathLen)
    {
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, pathLen, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        if (issuer is null)
        {
            return req.CreateSelfSigned(from, to);
        }

        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        using X509Certificate2 pub = Issue(req, issuer, from, to);
        return pub.CopyWithPrivateKey(key);
    }

    private static X509Certificate2 Leaf(string subject, AsymmetricAlgorithm key, X509Certificate2 issuer, DateTimeOffset from, DateTimeOffset to, Usage usage)
    {
        CertificateRequest req = key switch
        {
            RSA r => new CertificateRequest(subject, r, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            ECDsa e => new CertificateRequest(subject, e, HashAlgorithmName.SHA256),
            _ => throw new ArgumentException("Unsupported key type.", nameof(key)),
        };
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(usage == Usage.KeyEncipherment ? X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        if (usage == Usage.Timestamp)
        {
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], true));
        }
        else if (usage == Usage.ServerAuth)
        {
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        }
        else if (usage == Usage.EmailProtection)
        {
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.4")], false));
        }

        using X509Certificate2 pub = Issue(req, issuer, from, to);
        return key switch
        {
            RSA r => pub.CopyWithPrivateKey(r),
            ECDsa e => pub.CopyWithPrivateKey(e),
            _ => throw new ArgumentException("Unsupported key type.", nameof(key)),
        };
    }
}

public enum SigScheme
{
    RsaPkcs1,
    RsaPss,
    Ecdsa,
}

/// <summary>Options for <see cref="CmsKit.Build"/>; the Bad* switches produce the broken variants tests look for.</summary>
internal sealed class CmsSpec
{
    public HashAlgorithmName Hash { get; init; } = HashAlgorithmName.SHA256;

    public SigScheme Scheme { get; init; } = SigScheme.RsaPkcs1;

    public bool Cades { get; init; }

    public bool SignedAttributes { get; init; } = true;

    public bool Timestamp { get; init; }

    public DateTimeOffset SigningTime { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? TimestampTime { get; init; }

    public IReadOnlyList<X509Certificate2>? Embedded { get; init; }

    /// <summary>Put this wrong digest in the messageDigest attribute (still signs the attributes).</summary>
    public byte[]? MessageDigestOverride { get; init; }

    /// <summary>Name this hash in the signature algorithm but another one in the digest algorithm.</summary>
    public HashAlgorithmName? SignatureHashOverride { get; init; }

    /// <summary>Identify this certificate as the signer although another key signs.</summary>
    public X509Certificate2? ClaimedSigner { get; init; }

    public bool PkcsSha1 { get; init; }

    /// <summary>The certificate that signs the time-stamp token (the test authority when null).</summary>
    public X509Certificate2? TimestampAuthority { get; init; }
}

internal static class CmsKit
{
    public static string HashOid(HashAlgorithmName n) =>
        n == HashAlgorithmName.SHA1 ? "1.3.14.3.2.26" : n == HashAlgorithmName.SHA256 ? "2.16.840.1.101.3.4.2.1" : n == HashAlgorithmName.SHA384 ? "2.16.840.1.101.3.4.2.2" : "2.16.840.1.101.3.4.2.3";

    public static byte[] Hash(HashAlgorithmName n, byte[] data) => CryptographicOperations.HashData(n, data);

    private static byte[] Alg(string oid, bool withNull = true) => withNull ? Der.Seq(Der.Oid(oid), Der.Null()) : Der.Seq(Der.Oid(oid));

    private static byte[] SerialOf(X509Certificate2 c) => Der.IntBytes(Convert.FromHexString(c.SerialNumber));

    private static byte[] Attr(string oid, byte[] value) => Der.Seq(Der.Oid(oid), Der.Set(value));

    /// <summary>A CMS ContentInfo signing <paramref name="contentDigest"/> (the hash of the PDF byte ranges, in <c>spec.Hash</c>).</summary>
    public static byte[] Build(byte[] contentDigest, X509Certificate2 signer, IReadOnlyList<X509Certificate2> chain, CmsSpec spec, byte[]? encapsulated = null, string contentType = "1.2.840.113549.1.7.1")
    {
        X509Certificate2 sidCert = spec.ClaimedSigner ?? signer;
        var signed = new List<byte[]>
        {
            Attr("1.2.840.113549.1.9.3", Der.Oid(contentType)),
            Attr("1.2.840.113549.1.9.4", Der.Oct(spec.MessageDigestOverride ?? contentDigest)),
            Attr("1.2.840.113549.1.9.5", Der.UtcTime(spec.SigningTime)),
        };
        if (spec.Cades)
        {
            byte[] certHash = Hash(HashAlgorithmName.SHA256, sidCert.RawData);
            byte[] essId = Der.Seq(certHash.Length == 0 ? [] : Der.Oct(certHash));
            signed.Add(Attr("1.2.840.113549.1.9.16.2.47", Der.Seq(Der.Seq(essId))));
        }

        byte[] setOfAttrs = Der.SetOf(signed);
        byte[] toSign = spec.SignedAttributes ? Hash(spec.Hash, setOfAttrs) : contentDigest;
        HashAlgorithmName sigHash = spec.SignatureHashOverride ?? spec.Hash;
        byte[] toSignWithSigHash = spec.SignedAttributes ? Hash(sigHash, setOfAttrs) : contentDigest;
        _ = toSign;
        byte[] signature;
        byte[] sigAlg;
        switch (spec.Scheme)
        {
            case SigScheme.RsaPkcs1:
                {
                    using RSA rsa = signer.GetRSAPrivateKey()!;
                    signature = rsa.SignHash(toSignWithSigHash, sigHash, RSASignaturePadding.Pkcs1);
                    sigAlg = Alg(spec.SignatureHashOverride is null ? "1.2.840.113549.1.1.1" : sigHash == HashAlgorithmName.SHA384 ? "1.2.840.113549.1.1.12" : sigHash == HashAlgorithmName.SHA512 ? "1.2.840.113549.1.1.13" : "1.2.840.113549.1.1.11");
                    break;
                }

            case SigScheme.RsaPss:
                {
                    using RSA rsa = signer.GetRSAPrivateKey()!;
                    signature = rsa.SignHash(toSignWithSigHash, sigHash, RSASignaturePadding.Pss);
                    int salt = sigHash == HashAlgorithmName.SHA256 ? 32 : sigHash == HashAlgorithmName.SHA384 ? 48 : 64;
                    byte[] hashAlg = Alg(HashOid(sigHash));
                    byte[] parms = Der.Seq(Der.Ctx(0, hashAlg), Der.Ctx(1, Der.Seq(Der.Oid("1.2.840.113549.1.1.8"), hashAlg)), Der.Ctx(2, Der.Int(salt)));
                    sigAlg = Der.Seq(Der.Oid("1.2.840.113549.1.1.10"), parms);
                    break;
                }

            default:
                {
                    using ECDsa ec = signer.GetECDsaPrivateKey()!;
                    signature = ec.SignHash(toSignWithSigHash, DSASignatureFormat.Rfc3279DerSequence);
                    sigAlg = Alg(sigHash == HashAlgorithmName.SHA256 ? "1.2.840.10045.4.3.2" : sigHash == HashAlgorithmName.SHA384 ? "1.2.840.10045.4.3.3" : "1.2.840.10045.4.3.4", false);
                    break;
                }
        }

        var si = new List<byte[]>
        {
            Der.Int(1),
            Der.Seq(sidCert.IssuerName.RawData, SerialOf(sidCert)),
            Alg(HashOid(spec.Hash)),
        };
        if (spec.SignedAttributes)
        {
            si.Add(Der.Wrap(0xA0, [.. signed.OrderBy(static p => p, Comparer<byte[]>.Create(static (a, b) => a.AsSpan().SequenceCompareTo(b)))]));
        }

        si.Add(sigAlg);
        si.Add(Der.Oct(signature));
        if (spec.Timestamp)
        {
            byte[] token = Timestamp(Hash(HashAlgorithmName.SHA256, signature), HashAlgorithmName.SHA256, spec.TimestampTime ?? DateTimeOffset.UtcNow, spec.TimestampAuthority);
            si.Add(Der.Ctx(1, Attr("1.2.840.113549.1.9.16.2.14", token)));
        }

        var certs = new List<byte[]>();
        foreach (X509Certificate2 c in spec.Embedded ?? chain)
        {
            certs.Add(c.RawData);
        }

        var sd = new List<byte[]>
        {
            Der.Int(1),
            Der.Set(Alg(HashOid(spec.Hash))),
            encapsulated is null ? Der.Seq(Der.Oid(contentType)) : Der.Seq(Der.Oid(contentType), Der.Ctx(0, Der.Oct(encapsulated))),
        };
        if (certs.Count > 0)
        {
            sd.Add(Der.Wrap(0xA0, [.. certs]));
        }

        sd.Add(Der.Set(Der.Seq([.. si])));
        return Der.Seq(Der.Oid("1.2.840.113549.1.7.2"), Der.Ctx(0, Der.Seq([.. sd])));
    }

    /// <summary>An RFC 3161 token from the shared test authority over <paramref name="imprint"/>.</summary>
    public static byte[] Timestamp(byte[] imprint, HashAlgorithmName imprintHash, DateTimeOffset time, X509Certificate2? tsa = null, byte[]? nonce = null)
    {
        TestPki pki = TestPki.Shared;
        tsa ??= pki.Tsa;
        byte[] tst = Der.Seq(
            Der.Int(1),
            Der.Oid("1.2.3.4.1"),
            Der.Seq(Alg(HashOid(imprintHash)), Der.Oct(imprint)),
            Der.Int(42),
            Der.GeneralizedTime(time),
            nonce is null ? [] : Der.IntBytes(nonce));
        var spec = new CmsSpec { Hash = HashAlgorithmName.SHA256, Cades = true, SigningTime = time, Embedded = [tsa, pki.Intermediate] };
        return Build(Hash(HashAlgorithmName.SHA256, tst), tsa, [tsa, pki.Intermediate], spec, tst, "1.2.840.113549.1.9.16.1.4");
    }
}
