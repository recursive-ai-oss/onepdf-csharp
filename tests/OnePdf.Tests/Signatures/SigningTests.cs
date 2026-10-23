using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OnePdf.Asn1;
using static OnePdf.Tests.Signatures.SigningFixture;

namespace OnePdf.Tests.Signatures;

public class SigningTests
{
    public static IEnumerable<object[]> Matrix()
    {
        foreach (string key in new[] { "rsa-pkcs1", "rsa-pss", "ec256", "ec384" })
        {
            foreach (string hash in new[] { "SHA256", "SHA384" })
            {
                foreach (bool pades in new[] { true, false })
                {
                    foreach (bool stamped in new[] { false, true })
                    {
                        yield return [key, hash, pades, stamped];
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void OurVerifierValidatesOurOutput(string key, string hash, bool pades, bool stamped)
    {
        (X509Certificate2 cert, PdfRsaPadding padding) = Key(key);
        var tsa = new TestTsa();
        byte[] input = UnsignedPdf();
        using PdfDocument doc = Editable(input);
        byte[] signed = SignToBytes(doc, Credential(cert), new PdfSigningOptions
        {
            Hash = new HashAlgorithmName(hash),
            RsaPadding = padding,
            Format = pades ? PdfSignatureFormat.PadesBaseline : PdfSignatureFormat.Pkcs7Detached,
            Timestamp = stamped ? tsa.Callback : null,
            Reason = "Testing",
            Location = "Lab",
        });

        Assert.True(signed.AsSpan(0, input.Length).SequenceEqual(input), "the original bytes are a prefix of the output");
        using PdfDocument check = PdfDocument.Open(signed);
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.True(report.AllValid, WarningsOf(report));
        PdfSignatureVerification v = Assert.Single(report.Signatures);
        Assert.Equal(pades ? "ETSI.CAdES.detached" : "adbe.pkcs7.detached", v.Signature.SubFilter);
        Assert.Equal(stamped, v.Timestamp is not null);
        Assert.Equal(PdfSignatureCoverage.WholeDocument, v.Coverage);
        Assert.Equal("Testing", v.Signature.Reason);
        Assert.Equal(stamped ? 1 : 0, tsa.Calls);
    }

    [Fact]
    public void CadesProfileMatchesBaselineRequirements()
    {
        byte[] signed = SignToBytes(Editable(UnsignedPdf()), Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { Timestamp = new TestTsa().Callback });
        using PdfDocument doc = PdfDocument.Open(signed);
        PdfSignature sig = doc.GetSignatures()[0];
        Assert.True(CmsSignedData.TryParse(sig.Contents, out CmsSignedData? sd, out string? error), error);
        CmsSignerInfo si = Assert.Single(sd!.Signers);
        Assert.NotNull(si.FindSigned(Oids.ContentType));
        Assert.NotNull(si.FindSigned(Oids.MessageDigest));
        Assert.NotNull(si.FindSigned(Oids.SigningCertificateV2));
        Assert.Null(si.FindSigned(Oids.SigningTime));
        Assert.Null(si.FindSigned(Oids.SigningCertificate));
        Assert.NotNull(si.FindUnsigned(Oids.SignatureTimeStampToken));
        Assert.Null(sd.Content);
        Assert.True(sd.Certificates.Count >= 2, "signer and intermediate are embedded");
        Assert.NotNull(sig.SigningTime);

        byte[] pkcs7 = SignToBytes(Editable(UnsignedPdf()), Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { Format = PdfSignatureFormat.Pkcs7Detached });
        using PdfDocument doc2 = PdfDocument.Open(pkcs7);
        Assert.True(CmsSignedData.TryParse(doc2.GetSignatures()[0].Contents, out CmsSignedData? sd2, out _));
        Assert.NotNull(sd2!.Signers[0].FindSigned(Oids.SigningTime));
        Assert.Null(sd2.Signers[0].FindUnsigned(Oids.SignatureTimeStampToken));
    }

    [Theory]
    [InlineData("rsa-pkcs1", false)]
    [InlineData("rsa-pss", false)]
    [InlineData("ec256", false)]
    [InlineData("ec256", true)]
    [InlineData("ec384", true)]
    public void ExternalSignerPathIsValid(string key, bool p1363)
    {
        (X509Certificate2 cert, PdfRsaPadding padding) = Key(key);
        X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(cert.RawData);
        var seen = new List<PdfSignRequest>();
        PdfExternalSigner signer = request =>
        {
            seen.Add(request);
            if (request.KeyType == PdfSigningKeyType.Rsa)
            {
                using RSA rsa = cert.GetRSAPrivateKey()!;
                return rsa.SignHash(request.Hash.ToArray(), request.HashAlgorithm, request.RsaPadding == PdfRsaPadding.Pss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
            }

            using ECDsa ec = cert.GetECDsaPrivateKey()!;
            return ec.SignHash(request.Hash.ToArray(), p1363 ? DSASignatureFormat.IeeeP1363FixedFieldConcatenation : DSASignatureFormat.Rfc3279DerSequence);
        };

        byte[] signed = SignToBytes(Editable(UnsignedPdf()), PdfSigningCredential.FromExternalSigner(publicOnly, signer, [TestPki.Shared.Intermediate]),
            new PdfSigningOptions { RsaPadding = padding, Hash = HashAlgorithmName.SHA384 });
        PdfRequest(seen);
        using PdfDocument check = PdfDocument.Open(signed);
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.True(report.AllValid, WarningsOf(report));

        static void PdfRequest(List<PdfSignRequest> requests)
        {
            PdfSignRequest r = Assert.Single(requests);
            Assert.Equal(48, r.Hash.Length);
            Assert.Equal(HashAlgorithmName.SHA384, r.HashAlgorithm);
        }
    }

    [Fact]
    public void CertificationSignatureCarriesDocMdpAndTheFirstLimitIsEnforced()
    {
        PdfCertificationLevel[] levels = [PdfCertificationLevel.NoChanges, PdfCertificationLevel.FormFilling, PdfCertificationLevel.FormFillingAndAnnotations];
        foreach (PdfCertificationLevel level in levels)
        {
            byte[] certified = SignToBytes(Editable(UnsignedPdf()), Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { Certify = level });
            using PdfDocument check = PdfDocument.Open(certified);
            PdfSignature sig = check.GetSignatures()[0];
            Assert.Equal((int)level, sig.CertificationLevel);
            Assert.True(check.VerifySignatures(Trust).AllValid);
        }

        byte[] first = SignToBytes(Editable(UnsignedPdf()), Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { Certify = PdfCertificationLevel.FormFilling });
        var ex = Assert.Throws<PdfSigningException>(() => SignToBytes(Editable(first), Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { Certify = PdfCertificationLevel.NoChanges }));
        Assert.Equal(PdfSigningError.CertificationNotFirst, ex.Reason);
    }

    [Fact]
    public void SecondSignatureAfterNoChangesCertificationIsDisallowedButLevel2AllowsIt()
    {
        byte[] noChanges = SignToBytes(Editable(UnsignedPdf()), Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { Certify = PdfCertificationLevel.NoChanges });
        byte[] twice = SignToBytes(Editable(noChanges), Credential(TestPki.Shared.EcSigner), new PdfSigningOptions());
        using (PdfDocument check = PdfDocument.Open(twice))
        {
            PdfSignatureReport report = check.VerifySignatures(Trust);
            Assert.Equal(2, report.Signatures.Count);
            Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, report.Signatures[0].Coverage);
            Assert.False(report.Signatures[0].IsValid);
            Assert.True(report.Signatures[1].IsValid, WarningsOf(report));
        }

        byte[] level2 = SignToBytes(Editable(UnsignedPdf()), Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { Certify = PdfCertificationLevel.FormFilling });
        byte[] both = SignToBytes(Editable(level2), Credential(TestPki.Shared.EcSigner), new PdfSigningOptions());
        using PdfDocument check2 = PdfDocument.Open(both);
        PdfSignatureReport report2 = check2.VerifySignatures(Trust);
        Assert.True(report2.AllValid, WarningsOf(report2));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, report2.Signatures[0].Coverage);
    }

    [Fact]
    public void SignFillSignAgainThenDocumentTimestampKeepsEverythingValid()
    {
        byte[] input = UnsignedPdf();
        var tsa = new TestTsa();

        byte[] one;
        using (PdfDocument doc = Editable(input))
        {
            one = SignToBytes(doc, Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { FieldName = "Sig2", SignerName = "Rita" });
        }

        byte[] two;
        using (PdfDocument doc = Editable(one))
        {
            doc.Form!.SetValue("Name", "filled in after the first signature");
            two = SignToBytes(doc, Credential(TestPki.Shared.EcSigner), new PdfSigningOptions { FieldName = "Second", Timestamp = tsa.Callback });
        }

        byte[] three;
        using (PdfDocument doc = Editable(two))
        {
            using var ms = new MemoryStream();
            PdfSigningResult result = doc.AddDocumentTimestamp(ms, tsa.Callback);
            Assert.Equal("Timestamp1", result.FieldName);
            Assert.True(result.HasTimestamp);
            three = ms.ToArray();
        }

        Assert.True(two.AsSpan(0, one.Length).SequenceEqual(one));
        Assert.True(three.AsSpan(0, two.Length).SequenceEqual(two));
        using PdfDocument check = PdfDocument.Open(three);
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.Equal(3, report.Signatures.Count);
        Assert.True(report.AllValid, WarningsOf(report));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, report.Signatures[0].Coverage);
        Assert.Contains(report.Signatures[0].Changes, static c => c.Category == PdfChangeCategory.FormFieldValue && c.Allowed);
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, report.Signatures[1].Coverage);
        Assert.Equal(PdfSignatureCoverage.WholeDocument, report.Signatures[2].Coverage);
        Assert.Equal(PdfSignatureKind.DocumentTimestamp, report.Signatures[2].Signature.Kind);
        Assert.Equal("ETSI.RFC3161", report.Signatures[2].Signature.SubFilter);
    }

    [Fact]
    public void DocumentTimestampOnItsOwnIsValidForEveryHash()
    {
        foreach (string hash in new[] { "SHA256", "SHA384", "SHA512" })
        {
            var tsa = new TestTsa();
            using PdfDocument doc = Editable(UnsignedPdf());
            using var ms = new MemoryStream();
            doc.AddDocumentTimestamp(ms, tsa.Callback, new PdfTimestampOptions { Hash = new HashAlgorithmName(hash), FieldName = "Stamp" });
            using PdfDocument check = PdfDocument.Open(ms.ToArray());
            PdfSignatureReport report = check.VerifySignatures(Trust);
            Assert.True(report.AllValid, WarningsOf(report));
            Assert.Equal("Stamp", report.Signatures[0].Signature.FieldName);
            Assert.Equal(hash == "SHA256" ? 32 : hash == "SHA384" ? 48 : 64, tsa.Last!.MessageImprint.Length);
        }
    }

    [Fact]
    public void FieldLockIsWrittenAndHonouredByTheAnalyzer()
    {
        byte[] one;
        using (PdfDocument doc = Editable(UnsignedPdf()))
        {
            one = SignToBytes(doc, Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions
            {
                FieldLock = new PdfSignatureLock(PdfLockAction.Include, ["Name"], null),
            });
        }

        using (PdfDocument check = PdfDocument.Open(one))
        {
            PdfSignatureLock l = Assert.IsType<PdfSignatureLock>(check.GetSignatures()[0].FieldLock);
            Assert.Equal(PdfLockAction.Include, l.Action);
            Assert.Equal(["Name"], l.Fields);
        }

        byte[] lockedEdit;
        using (PdfDocument doc = Editable(one))
        {
            doc.Form!.SetValue("Name", "changed");
            lockedEdit = SignToBytes(doc, Credential(TestPki.Shared.EcSigner));
        }

        using PdfDocument after = PdfDocument.Open(lockedEdit);
        PdfSignatureReport report = after.VerifySignatures(Trust);
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, report.Signatures[0].Coverage);

        byte[] openEdit;
        using (PdfDocument doc = Editable(one))
        {
            doc.Form!.SetValue("Other", "free");
            openEdit = SignToBytes(doc, Credential(TestPki.Shared.EcSigner));
        }

        using PdfDocument ok = PdfDocument.Open(openEdit);
        Assert.True(ok.VerifySignatures(Trust).AllValid);
    }

    [Fact]
    public void ErrorsAreTypedAndWriteNothing()
    {
        TestPki pki = TestPki.Shared;
        X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(pki.RsaSigner.RawData);
        using var sink = new MemoryStream();

        PdfSigningException noKey = Assert.Throws<PdfSigningException>(() => Editable(UnsignedPdf()).Sign(sink, PdfSigningCredential.FromCertificate(publicOnly)));
        Assert.Equal(PdfSigningError.NoPrivateKey, noKey.Reason);

        PdfSigningException garbage = Assert.Throws<PdfSigningException>(() => Editable(UnsignedPdf()).Sign(sink,
            PdfSigningCredential.FromExternalSigner(publicOnly, static _ => [1, 2, 3, 4])));
        Assert.Equal(PdfSigningError.SignerFailed, garbage.Reason);

        PdfSigningException wrongKey = Assert.Throws<PdfSigningException>(() => Editable(UnsignedPdf()).Sign(sink,
            PdfSigningCredential.FromExternalSigner(publicOnly, request =>
            {
                using RSA other = RSA.Create(2048);
                return other.SignHash(request.Hash.ToArray(), request.HashAlgorithm, RSASignaturePadding.Pkcs1);
            })));
        Assert.Equal(PdfSigningError.SignerFailed, wrongKey.Reason);

        foreach (TestTsa bad in new[]
        {
            new TestTsa { WrongImprint = true },
            new TestTsa { WrongNonce = true },
            new TestTsa { DropNonce = true },
            new TestTsa { Refuse = true },
        })
        {
            PdfSigningException ts = Assert.Throws<PdfSigningException>(() => Editable(UnsignedPdf()).Sign(sink, Credential(pki.RsaSigner), new PdfSigningOptions { Timestamp = bad.Callback }));
            Assert.Equal(PdfSigningError.TimestampRejected, ts.Reason);
            PdfSigningException dts = Assert.Throws<PdfSigningException>(() => Editable(UnsignedPdf()).AddDocumentTimestamp(sink, bad.Callback));
            Assert.Equal(PdfSigningError.TimestampRejected, dts.Reason);
        }

        PdfSigningException garbageToken = Assert.Throws<PdfSigningException>(() => Editable(UnsignedPdf()).AddDocumentTimestamp(sink, static _ => [0x30, 0x03, 0x02, 0x01, 0x05]));
        Assert.Equal(PdfSigningError.TimestampRejected, garbageToken.Reason);

        PdfSigningException small = Assert.Throws<PdfSigningException>(() => Editable(UnsignedPdf()).Sign(sink, Credential(pki.RsaSigner), new PdfSigningOptions { ContentsReserve = 200 }));
        Assert.Equal(PdfSigningError.ContentsTooSmall, small.Reason);
        Assert.True(small.RequiredBytes > 200);

        PdfSigningException badField = Assert.Throws<PdfSigningException>(() => Editable(UnsignedPdf()).Sign(sink, Credential(pki.RsaSigner), new PdfSigningOptions { FieldName = "Name" }));
        Assert.Equal(PdfSigningError.BadField, badField.Reason);
        Assert.Throws<PdfSigningException>(() => Editable(UnsignedPdf()).Sign(sink, Credential(pki.RsaSigner), new PdfSigningOptions { FieldName = "a.b" }));

        using (PdfDocument readOnly = PdfDocument.Open(UnsignedPdf()))
        {
            InvalidOperationException ro = Assert.Throws<InvalidOperationException>(() => readOnly.Sign(sink, Credential(pki.RsaSigner)));
            Assert.Contains("AllowEditing", ro.Message, StringComparison.Ordinal);
        }

        using (PdfDocument used = Editable(UnsignedPdf()))
        {
            used.Sign(new MemoryStream(), Credential(pki.RsaSigner));
            PdfSigningException again = Assert.Throws<PdfSigningException>(() => used.Sign(sink, Credential(pki.RsaSigner)));
            Assert.Equal(PdfSigningError.DocumentUsed, again.Reason);
        }

        Assert.Equal(0, sink.Length);
    }

    [Fact]
    public void ReserveCanBeTunedFromTheResult()
    {
        using PdfDocument doc = Editable(UnsignedPdf());
        using var ms = new MemoryStream();
        PdfSigningResult first = doc.Sign(ms, Credential(TestPki.Shared.RsaSigner));
        Assert.True(first.ContentsUsed <= first.ContentsCapacity);
        Assert.Equal(ms.Length, first.SignedLength);

        using PdfDocument again = Editable(UnsignedPdf());
        using var ms2 = new MemoryStream();
        PdfSigningResult second = again.Sign(ms2, Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { ContentsReserve = first.ContentsUsed });
        Assert.Equal(first.ContentsUsed, second.ContentsCapacity);
        using PdfDocument check = PdfDocument.Open(ms2.ToArray());
        Assert.True(check.VerifySignatures(Trust).AllValid);
    }

    [Fact]
    public void VisibleSignatureHasAnAppearanceAndAnImageFits()
    {
        byte[] input = UnsignedPdf();
        byte[] signed;
        using (PdfDocument doc = Editable(input))
        {
            byte[] rgb = new byte[8 * 8 * 3];
            Array.Fill(rgb, (byte)0x30);
            PdfCreatedImage image = doc.AddRawImage(8, 8, PdfImageColor.Rgb, rgb);
            signed = SignToBytes(doc, Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions
            {
                FieldName = "Sig2",
                SignerName = "Rita",
                Reason = "I approve",
                Appearance = new PdfSignatureAppearance { Image = image },
            });
        }

        using PdfDocument check = PdfDocument.Open(signed);
        Assert.True(check.VerifySignatures(Trust).AllValid);
        PdfFormField field = check.Form!.FindField("Sig2")!;
        PdfDictionary widget = field.Widgets[0];
        Assert.NotNull(widget.GetDictionary("AP")?.GetStream("N"));
        var list = new PdfDisplayList();
        check.Pages[0].Interpret(list);
        string text = list.ToText();
        Assert.Contains("begin-annot /Widget", text, StringComparison.Ordinal);
        Assert.Contains(HexOf("Digitally"), text, StringComparison.Ordinal);
    }

    private static string HexOf(string s) => Convert.ToHexStringLower(System.Text.Encoding.ASCII.GetBytes(s));

    [Fact]
    public void NewVisibleFieldIsPlacedOnTheRequestedPage()
    {
        using PdfDocument doc = Editable(UnsignedPdf(fields: false));
        byte[] signed = SignToBytes(doc, Credential(TestPki.Shared.EcSigner), new PdfSigningOptions
        {
            FieldName = "Approval",
            Rectangle = new PdfRectangle(20, 20, 200, 60),
            SignerName = "Edna",
        });
        using PdfDocument check = PdfDocument.Open(signed);
        Assert.True(check.VerifySignatures(Trust).AllValid);
        PdfFormField f = check.Form!.FindField("Approval")!;
        Assert.Equal(new PdfRectangle(20, 20, 200, 60), PdfRectangle.From(f.Widgets[0].Get("Rect")));
        Assert.Equal(1, check.Pages[0].Dictionary.GetArray("Annots")!.Count);
        var list = new PdfDisplayList();
        check.Pages[0].Interpret(list);
        Assert.Contains(HexOf("Digitally signed by Edna"), list.ToText(), StringComparison.Ordinal);
    }
}
