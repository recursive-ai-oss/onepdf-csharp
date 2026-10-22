using System.Text;
using OnePdf.Filters;
using OnePdf.Security;

namespace OnePdf.Tests.Security;

public class SecurityHandlerTests
{
    private static readonly DecodeLimits Limits = new(64 << 20, 1100, 16 << 20);

    // file, revision, user password, owner password
    public static TheoryData<string, int, string, string> Files => new()
    {
        { "enc-raw-rc4-40.pdf", 2, "user-pw", "owner-pw" },
        { "enc-raw-rc4-128.pdf", 3, "user-pw", "owner-pw" },
        { "enc-chrome-rc4-128.pdf", 3, "user-pw", "owner-pw" },
        { "lo-encrypted-rc4-128.pdf", 3, "user-pw", "owner-pw" },
        { "enc-raw-aes-128.pdf", 4, "user-pw", "owner-pw" },
        { "enc-chrome-aes-128.pdf", 4, "user-pw", "owner-pw" },
        { "enc-raw-aes-128-cleartext-metadata.pdf", 4, "user-pw", "owner-pw" },
        { "enc-raw-aes-256-r5.pdf", 5, "user-pw", "owner-pw" },
        { "enc-raw-aes-256.pdf", 6, "user-pw", "owner-pw" },
        { "enc-chrome-aes-256.pdf", 6, "user-pw", "owner-pw" },
    };

    private static void AssertDecryptsContent(EncryptedPdf pdf, SecurityDecryptor d)
    {
        Assert.NotEmpty(pdf.Streams);
        foreach ((int obj, int gen, byte[] data) in pdf.Streams)
        {
            DecryptResult r = d.DecryptStream(data, obj, gen);
            Assert.Equal(DecryptIssues.None, r.Issues);
            FilterResult f = StreamFilters.Decode("FlateDecode", r.Data, null, Limits);
            Assert.Equal(FilterIssues.None, f.Issues);
            Assert.True(f.Data.Length > 0);
        }

        // At least one stream is page content with text operators.
        bool sawText = false;
        foreach ((int obj, int gen, byte[] data) in pdf.Streams)
        {
            byte[] plain = StreamFilters.Decode("FlateDecode", d.DecryptStream(data, obj, gen).Data, null, Limits).Data.ToArray();
            string s = Encoding.Latin1.GetString(plain);
            sawText |= s.Contains(" Tj", StringComparison.Ordinal) || s.Contains("TJ", StringComparison.Ordinal) || s.Contains("begincmap", StringComparison.Ordinal) || s.Contains("/Type /Page", StringComparison.Ordinal) || s.Contains("/Type/Page", StringComparison.Ordinal);
        }

        Assert.True(sawText);

        foreach ((int obj, int gen, byte[] value) in pdf.InfoStrings)
        {
            DecryptResult r = d.DecryptString(value, obj, gen);
            Assert.Equal(DecryptIssues.None, r.Issues);
            Assert.NotEmpty(r.Data);
            // Text strings are PDFDocEncoding or UTF-16BE with a byte order mark.
            string shown = r.Data is [0xFE, 0xFF, ..] ? Encoding.BigEndianUnicode.GetString(r.Data, 2, r.Data.Length - 2) : Encoding.Latin1.GetString(r.Data);
            Assert.All(shown, ch => Assert.InRange(ch, ' ', '\u00FF'));
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void UserPasswordOpensAndDecrypts(string file, int revision, string user, string owner)
    {
        EncryptedPdf pdf = EncryptedPdf.Load(file);
        Assert.Equal(revision, pdf.Parameters.R);
        SecurityResult r = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, user);
        Assert.Equal(SecurityOutcome.User, r.Outcome);
        using SecurityDecryptor d = r.Decryptor!;
        AssertDecryptsContent(pdf, d);
        Assert.Equal(SecurityNotes.None, r.Notes);
        _ = owner;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void OwnerPasswordOpensAndDecrypts(string file, int revision, string user, string owner)
    {
        EncryptedPdf pdf = EncryptedPdf.Load(file);
        SecurityResult r = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, owner);
        Assert.Equal(SecurityOutcome.Owner, r.Outcome);
        using SecurityDecryptor d = r.Decryptor!;
        AssertDecryptsContent(pdf, d);
        _ = (revision, user);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void WrongPasswordFails(string file, int revision, string user, string owner)
    {
        EncryptedPdf pdf = EncryptedPdf.Load(file);
        foreach (string pw in new[] { "wrong", user + "x", owner.ToUpperInvariant(), user[..^1] })
        {
            SecurityResult r = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, pw);
            Assert.Equal(SecurityOutcome.Failed, r.Outcome);
            Assert.Null(r.Decryptor);
        }

        // No password at all is an empty user password, which these files don't accept.
        Assert.Equal(SecurityOutcome.Failed, StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, null).Outcome);
        _ = revision;
    }

    [Theory]
    [InlineData("enc-raw-rc4-128-empty-user.pdf")]
    [InlineData("enc-raw-aes-128-empty-user.pdf")]
    [InlineData("enc-raw-aes-256-empty-user.pdf")]
    public void EmptyUserPasswordIsTriedAutomatically(string file)
    {
        EncryptedPdf pdf = EncryptedPdf.Load(file);
        foreach (string? typed in new string?[] { null, string.Empty, "some-wrong-guess" })
        {
            SecurityResult r = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, typed);
            Assert.Equal(SecurityOutcome.User, r.Outcome);
            using SecurityDecryptor d = r.Decryptor!;
            AssertDecryptsContent(pdf, d);
        }

        SecurityResult owner = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, "owner-pw");
        Assert.Equal(SecurityOutcome.Owner, owner.Outcome);
        owner.Decryptor!.Dispose();
    }

    [Fact]
    public void CleartextMetadataIsLeftAlone()
    {
        EncryptedPdf pdf = EncryptedPdf.Load("enc-raw-aes-128-cleartext-metadata.pdf");
        Assert.False(pdf.Parameters.EncryptMetadata);
        using SecurityDecryptor d = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, "user-pw").Decryptor!;
        Assert.False(d.EncryptMetadata);
        byte[] xmp = Encoding.ASCII.GetBytes("<?xpacket begin?>");
        Assert.Equal(xmp, d.DecryptStream(xmp, 9, 0, isMetadata: true).Data);
        // An explicit Identity crypt filter is never decrypted; an ordinary stream is.
        Assert.Equal(xmp, d.DecryptStream(xmp, 9, 0, "Identity").Data);
        Assert.NotEqual(xmp, d.DecryptStream(xmp, 9, 0).Data);
    }

    [Fact]
    public void UnknownCryptFilterIsFlagged()
    {
        EncryptedPdf pdf = EncryptedPdf.Load("enc-raw-aes-128.pdf");
        using SecurityDecryptor d = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, "user-pw").Decryptor!;
        DecryptResult r = d.DecryptStream(new byte[] { 1, 2, 3 }, 1, 0, "Nope");
        Assert.Equal(DecryptIssues.UnknownCryptFilter, r.Issues);
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Data);
    }

    [Theory]
    [InlineData("r6-unicode.pdf", "p\u00e4ssw\u00f6rd\u65e5\u672c", "\u03a9wner")]
    [InlineData("r6-unicode.pdf", "pa\u0308ssw\u00f6rd\u65e5\u672c", "\u03a9wner")] // decomposed a + diaeresis normalizes to the same bytes
    [InlineData("r4-latin1.pdf", "p\u00e4ssw\u00f6rd", "own\u00e9r")]
    [InlineData("r3-latin1.pdf", "p\u00e4ssw\u00f6rd", "own\u00e9r")]
    public void UnicodePasswords(string file, string user, string owner)
    {
        EncryptedPdf pdf = EncryptedPdf.LoadVector(file);
        SecurityResult u = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, user);
        Assert.Equal(SecurityOutcome.User, u.Outcome);
        AssertDecryptsContent(pdf, u.Decryptor!);
        SecurityResult o = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, owner);
        Assert.Equal(SecurityOutcome.Owner, o.Outcome);
        AssertDecryptsContent(pdf, o.Decryptor!);
        Assert.Equal(SecurityOutcome.Failed, StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, user + "!").Outcome);
        u.Decryptor!.Dispose();
        o.Decryptor!.Dispose();
    }

    [Fact]
    public void SaslPrepMapsSpacesAndFallsBackToRawBytes()
    {
        // The file's password holds a literal U+00A0. SASLprep turns it into a plain space, which does not match,
        // so the raw UTF-8 fallback is what opens it, and the result says so.
        EncryptedPdf pdf = EncryptedPdf.LoadVector("r6-nbsp-raw.pdf");
        SecurityResult r = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, "sp\u00a0ace");
        Assert.Equal(SecurityOutcome.User, r.Outcome);
        Assert.True((r.Notes & SecurityNotes.PasswordEncodingFallback) != 0);
        r.Decryptor!.Dispose();

        Assert.Equal("a b", PasswordPreparation.SaslPrep("a\u2003b"));
        Assert.Equal("ab", PasswordPreparation.SaslPrep("a\u00adb\u200d"));
        Assert.Equal("fi", PasswordPreparation.SaslPrep("\ufb01"));
    }

    [Fact]
    public void LegacyPasswordEncoding()
    {
        // PDFDocEncoding: Euro is 0xA0, bullet 0x80; Latin-1 letters are themselves.
        List<byte[]> c = PasswordPreparation.ForLegacy("\u20ac\u2022\u00e9");
        Assert.Equal(new byte[] { 0xA0, 0x80, 0xE9 }, c[0]);
        Assert.Equal(new byte[] { (byte)'a' }, PasswordPreparation.ForLegacy("a")[0]);
        Assert.Equal(32, PasswordPreparation.Pad32(new byte[40]).Length);
        Assert.Equal(PasswordPreparation.Padding.ToArray(), PasswordPreparation.Pad32([]));
    }

    [Fact]
    public void RevisionSixPermsAreChecked()
    {
        EncryptedPdf pdf = EncryptedPdf.Load("enc-raw-aes-256.pdf");
        SecurityResult r = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, "user-pw");
        Assert.Equal(PermsCheck.Valid, r.PermsCheck);
        r.Decryptor!.Dispose();

        // A /P that disagrees with /Perms is reported, and the file still opens.
        var bad = new EncryptionParameters
        {
            Filter = pdf.Parameters.Filter, V = pdf.Parameters.V, R = pdf.Parameters.R, Length = pdf.Parameters.Length,
            O = pdf.Parameters.O, U = pdf.Parameters.U, OE = pdf.Parameters.OE, UE = pdf.Parameters.UE, Perms = pdf.Parameters.Perms,
            P = -1044, EncryptMetadata = true, CryptFilters = pdf.Parameters.CryptFilters, StmF = pdf.Parameters.StmF, StrF = pdf.Parameters.StrF,
        };
        SecurityResult b = StandardSecurityHandler.Authenticate(bad, pdf.Id, "user-pw");
        Assert.Equal(SecurityOutcome.User, b.Outcome);
        Assert.Equal(PermsCheck.Invalid, b.PermsCheck);
        b.Decryptor!.Dispose();
    }

    [Fact]
    public void PermissionBitsDecode()
    {
        EncryptedPdf pdf = EncryptedPdf.Load("lo-encrypted-rc4-128.pdf"); // P = -1028
        SecurityResult r = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, "user-pw");
        Assert.Equal(-1028, r.RawPermissions);
        Assert.Equal(SecurityPermissions.Print | SecurityPermissions.ModifyContents | SecurityPermissions.CopyContent
            | SecurityPermissions.ModifyAnnotations | SecurityPermissions.FillForms | SecurityPermissions.ExtractForAccessibility
            | SecurityPermissions.PrintHighQuality, r.Permissions);
        r.Decryptor!.Dispose();

        // The same value written as an unsigned 32-bit number.
        Assert.Equal(-4, unchecked((int)4294967292L));
        var unsigned = new EncryptionParameters { Filter = "Standard", V = 2, R = 3, Length = 128, O = pdf.Parameters.O, U = pdf.Parameters.U, P = 4294967292L };
        Assert.Equal(-4, StandardSecurityHandler.Authenticate(unsigned, pdf.Id, "nope").RawPermissions);
    }

    [Fact]
    public void LenientAndUnsupportedHandling()
    {
        EncryptedPdf pdf = EncryptedPdf.Load("enc-raw-rc4-128.pdf");
        EncryptionParameters p = pdf.Parameters;

        // Key length outside the legal range: clamped and noted. Whether it then opens depends on the file, but it must not throw.
        var odd = new EncryptionParameters { Filter = "Standard", V = 2, R = 3, Length = 512, O = p.O, U = p.U, P = p.P };
        SecurityResult r = StandardSecurityHandler.Authenticate(odd, pdf.Id, "user-pw");
        Assert.True((r.Notes & SecurityNotes.KeyLengthClamped) != 0);

        Assert.Throws<SecurityUnsupportedException>(() => StandardSecurityHandler.Authenticate(
            new EncryptionParameters { Filter = "Adobe.PubSec", V = 4, R = 4 }, pdf.Id, ""));
        Assert.Throws<SecurityUnsupportedException>(() => StandardSecurityHandler.Authenticate(
            new EncryptionParameters { Filter = "Standard", V = 3, R = 3, O = p.O, U = p.U }, pdf.Id, ""));
        Assert.Throws<SecurityUnsupportedException>(() => StandardSecurityHandler.Authenticate(
            new EncryptionParameters { Filter = "Standard", V = 2, R = 3, O = [1, 2], U = [3] }, pdf.Id, ""));

        // A missing /V is inferred from /R.
        var noV = new EncryptionParameters { Filter = null, V = 0, R = 3, Length = 128, O = p.O, U = p.U, P = p.P };
        SecurityResult nv = StandardSecurityHandler.Authenticate(noV, pdf.Id, "user-pw");
        Assert.Equal(SecurityOutcome.User, nv.Outcome);
        Assert.True((nv.Notes & SecurityNotes.VersionInferred) != 0);
        nv.Decryptor!.Dispose();
    }

    [Fact]
    public void BadAesDataIsKeptAndFlagged()
    {
        EncryptedPdf pdf = EncryptedPdf.Load("enc-raw-aes-128.pdf");
        using SecurityDecryptor d = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, "user-pw").Decryptor!;
        (int obj, int gen, byte[] data) = pdf.Streams[0];
        byte[] good = d.DecryptStream(data, obj, gen).Data;

        // Truncated mid-block: whole blocks decrypt, the tail is dropped.
        DecryptResult partial = d.DecryptStream(data.AsSpan(0, data.Length - 5), obj, gen);
        Assert.True((partial.Issues & DecryptIssues.PartialBlock) != 0);

        // Last block missing: the earlier blocks come back; the padding check fails and is flagged, nothing throws.
        DecryptResult cut = d.DecryptStream(data.AsSpan(0, data.Length - 16), obj, gen);
        Assert.True((cut.Issues & DecryptIssues.BadPadding) != 0);
        Assert.Equal(good.AsSpan(0, Math.Min(good.Length, cut.Data.Length) - 1).ToArray(), cut.Data.AsSpan(0, Math.Min(good.Length, cut.Data.Length) - 1).ToArray());

        Assert.Equal(DecryptIssues.MissingIv, d.DecryptStream(new byte[7], 1, 0).Issues);
        Assert.Empty(d.DecryptStream([], 1, 0).Data);
        Assert.Empty(d.DecryptStream(new byte[16], 1, 0).Data);
    }

    [Fact]
    public void Rc4KnownVectors()
    {
        // RFC 6229-style check: key "Key", plaintext "Plaintext".
        byte[] data = Encoding.ASCII.GetBytes("Plaintext");
        Rc4.TransformInPlace(Encoding.ASCII.GetBytes("Key"), data);
        Assert.Equal("BBF316E8D940AF0AD3", Convert.ToHexString(data));
        Rc4.TransformInPlace(Encoding.ASCII.GetBytes("Key"), data);
        Assert.Equal("Plaintext", Encoding.ASCII.GetString(data));
        Assert.Throws<ArgumentException>(() => new Rc4([]));
    }

    [Fact]
    public void ParallelDecryptionIsConsistent()
    {
        EncryptedPdf pdf = EncryptedPdf.Load("enc-chrome-aes-128.pdf");
        using SecurityDecryptor d = StandardSecurityHandler.Authenticate(pdf.Parameters, pdf.Id, "user-pw").Decryptor!;
        Parallel.For(0, 200, i =>
        {
            (int obj, int gen, byte[] data) = pdf.Streams[i % pdf.Streams.Count];
            Assert.Equal(DecryptIssues.None, d.DecryptStream(data, obj, gen).Issues);
        });
    }
}
