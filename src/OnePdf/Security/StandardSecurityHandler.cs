using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace OnePdf.Security;

/// <summary>
/// The standard security handler (ISO 32000-2 7.6.4; ISO 32000-1 7.6.3 for revisions 2 to 4): checks a password against
/// the /Encrypt values, derives the file key and hands back a <see cref="SecurityDecryptor"/>.
/// </summary>
/// <remarks>
/// Order tried by <see cref="Authenticate"/>: the password as user password, the password as owner password, then the
/// empty user password (so a document with no user password opens whatever the caller typed). Revisions 2 to 4 use
/// MD5/RC4/AES-128 (Algorithms 2 to 7); revisions 5 and 6 use SHA-2/AES-256 (Algorithm 2.A, with Algorithm 2.B for
/// revision 6). The file key is always derived from the password that authenticated; for the owner password of
/// revisions 2 to 4 the user password is first recovered from /O (Algorithm 7).
/// </remarks>
internal static class StandardSecurityHandler
{
    /// <summary>
    /// Authenticates <paramref name="password"/>. Wrong password is a result (<see cref="SecurityOutcome.Failed"/>), not an exception.
    /// </summary>
    /// <param name="parameters">The /Encrypt values.</param>
    /// <param name="documentId">The first string of the trailer /ID array; empty when the file has none.</param>
    /// <param name="password">The password as typed; null or empty means the empty password.</param>
    /// <exception cref="SecurityUnsupportedException">Unsupported handler or revision, malformed entries, or no AES in this runtime.</exception>
    public static SecurityResult Authenticate(EncryptionParameters parameters, ReadOnlySpan<byte> documentId, string? password)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        try
        {
            return AuthenticateCore(parameters, documentId, password ?? string.Empty);
        }
        catch (PlatformNotSupportedException e)
        {
            throw new SecurityUnsupportedException("This runtime has no AES, MD5 or SHA implementation, so the document cannot be decrypted.", e);
        }
    }

    private static SecurityResult AuthenticateCore(EncryptionParameters p, ReadOnlySpan<byte> id, string password)
    {
        SecurityNotes notes = SecurityNotes.None;
        if (p.Filter is null)
        {
            notes |= SecurityNotes.FilterAssumed;
        }
        else if (p.Filter != "Standard")
        {
            throw new SecurityUnsupportedException("Unsupported security handler /" + p.Filter + ".");
        }

        int r = p.R;
        if (r is < 2 or > 6)
        {
            throw new SecurityUnsupportedException("Unsupported standard security handler revision " + r + ".");
        }

        int v = p.V;
        if (v == 0)
        {
            v = r switch { 2 => 1, 3 => 2, 4 => 4, _ => 5 };
            notes |= SecurityNotes.VersionInferred;
        }

        if (v is 3 or < 0 or > 5 || (v == 5) != (r >= 5) || (v == 1 && r != 2))
        {
            throw new SecurityUnsupportedException("Unsupported encryption version " + p.V + " with revision " + r + ".");
        }

        if (id.IsEmpty)
        {
            notes |= SecurityNotes.MissingId;
        }

        int rawP = unchecked((int)p.P);
        var permissions = (SecurityPermissions)(uint)rawP & AllPermissionBits;

        SecurityResult Fail() => new() { Outcome = SecurityOutcome.Failed, Permissions = permissions, RawPermissions = rawP, Notes = notes };

        CryptSetup setup = ResolveCrypt(p, v, ref notes);
        if (setup.NeedsAes && !AesCbc.IsAvailable)
        {
            throw new SecurityUnsupportedException("This document uses AES, which is not available in the browser runtime.");
        }

        if (r >= 5)
        {
            if (p.U.Length < 48 || p.UE.Length < 32)
            {
                throw new SecurityUnsupportedException("Malformed /U or /UE for revision " + r + ".");
            }

            List<byte[]> candidates = PasswordPreparation.ForAes256(password);
            List<byte[]> empty = [[]];
            for (int pass = 0; pass < 3; pass++)
            {
                // pass 0: user, pass 1: owner, pass 2: empty user.
                if (pass == 2 && password.Length == 0)
                {
                    break;
                }

                List<byte[]> list = pass == 2 ? empty : candidates;
                for (int c = 0; c < list.Count; c++)
                {
                    byte[]? key = pass == 1 ? TryOwnerAes256(p, list[c], r) : TryUserAes256(p, list[c], r);
                    if (key is not null)
                    {
                        var n = c > 0 && pass < 2 ? notes | SecurityNotes.PasswordEncodingFallback : notes;
                        PermsCheck check = CheckPerms(p, key);
                        return Success(pass == 1 ? SecurityOutcome.Owner : SecurityOutcome.User, key, setup, p, permissions, rawP, check, n);
                    }
                }
            }

            return Fail();
        }

        // Revisions 2 to 4.
        if (p.O.Length < 32 || p.U.Length < 32)
        {
            throw new SecurityUnsupportedException("Malformed /O or /U.");
        }

        int keyBytes = KeyLength(p, v, r, setup, ref notes);
        List<byte[]> legacy = PasswordPreparation.ForLegacy(password);
        for (int pass = 0; pass < 3; pass++)
        {
            if (pass == 2 && password.Length == 0)
            {
                break;
            }

            List<byte[]> list = pass == 2 ? [[]] : legacy;
            for (int c = 0; c < list.Count; c++)
            {
                byte[] padded = PasswordPreparation.Pad32(list[c]);
                byte[]? key = pass == 1
                    ? TryOwnerLegacy(p, padded, id, r, keyBytes)
                    : TryUserLegacy(p, padded, id, r, keyBytes);
                if (key is not null)
                {
                    var n = c > 0 && pass < 2 ? notes | SecurityNotes.PasswordEncodingFallback : notes;
                    return Success(pass == 1 ? SecurityOutcome.Owner : SecurityOutcome.User, key, setup, p, permissions, rawP, PermsCheck.NotChecked, n);
                }
            }
        }

        return Fail();
    }

    private const SecurityPermissions AllPermissionBits =
        SecurityPermissions.Print | SecurityPermissions.ModifyContents | SecurityPermissions.CopyContent
        | SecurityPermissions.ModifyAnnotations | SecurityPermissions.FillForms | SecurityPermissions.ExtractForAccessibility
        | SecurityPermissions.AssembleDocument | SecurityPermissions.PrintHighQuality;

    private static SecurityResult Success(
        SecurityOutcome outcome, byte[] fileKey, CryptSetup s, EncryptionParameters p,
        SecurityPermissions permissions, int rawP, PermsCheck check, SecurityNotes notes)
    {
        SecurityDecryptor decryptor;
        try
        {
            decryptor = new SecurityDecryptor(fileKey, s.StringMethod, s.StreamMethod, s.EmbeddedMethod, s.Filters, p.EncryptMetadata);
        }
        catch (SecurityUnsupportedException)
        {
            CryptographicOperations.ZeroMemory(fileKey);
            throw;
        }

        return new SecurityResult
        {
            Outcome = outcome,
            Decryptor = decryptor,
            Permissions = permissions,
            RawPermissions = rawP,
            PermsCheck = check,
            Notes = notes,
        };
    }

    // ---- Crypt filter resolution (ISO 32000-2 7.6.6) ----

    private readonly record struct CryptSetup(
        CryptMethod StringMethod, CryptMethod StreamMethod, CryptMethod EmbeddedMethod,
        Dictionary<string, CryptMethod> Filters, int? RcLengthHint)
    {
        public bool NeedsAes =>
            StringMethod is CryptMethod.AesV2 or CryptMethod.AesV3 || StreamMethod is CryptMethod.AesV2 or CryptMethod.AesV3
            || EmbeddedMethod is CryptMethod.AesV2 or CryptMethod.AesV3;
    }

    private static CryptSetup ResolveCrypt(EncryptionParameters p, int v, ref SecurityNotes notes)
    {
        var filters = new Dictionary<string, CryptMethod>(StringComparer.Ordinal);
        if (v < 4)
        {
            return new CryptSetup(CryptMethod.Rc4, CryptMethod.Rc4, CryptMethod.Rc4, filters, null);
        }

        int? hint = null;
        if (p.CryptFilters is not null)
        {
            foreach (KeyValuePair<string, CryptFilterSpec> kv in p.CryptFilters)
            {
                CryptMethod m = kv.Value.Cfm switch
                {
                    "V2" => CryptMethod.Rc4,
                    "AESV2" => CryptMethod.AesV2,
                    "AESV3" => CryptMethod.AesV3,
                    _ => CryptMethod.None,
                };
                filters[kv.Key] = m;
            }
        }

        CryptMethod Lookup(string? name)
        {
            if (name is null or "Identity")
            {
                return CryptMethod.None;
            }

            if (filters.TryGetValue(name, out CryptMethod m))
            {
                return m;
            }

            // A name with no /CF entry is treated as Identity.
            return CryptMethod.None;
        }

        CryptMethod str = Lookup(p.StrF);
        CryptMethod stm = Lookup(p.StmF);
        CryptMethod emb = p.Eff is null ? stm : Lookup(p.Eff);
        if (p.StmF is not null && p.CryptFilters is not null && p.CryptFilters.TryGetValue(p.StmF, out CryptFilterSpec? spec))
        {
            hint = spec.Length;
        }

        if ((v == 5 && (str is CryptMethod.Rc4 or CryptMethod.AesV2 || stm is CryptMethod.Rc4 or CryptMethod.AesV2))
            || (v == 4 && (str == CryptMethod.AesV3 || stm == CryptMethod.AesV3)))
        {
            notes |= SecurityNotes.UnusualCryptMethod;
        }

        return new CryptSetup(str, stm, emb, filters, hint);
    }

    // Key length in bytes for the key derivation of revisions 2 to 4 (Algorithm 2 step (a)/(h)).
    private static int KeyLength(EncryptionParameters p, int v, int r, CryptSetup s, ref SecurityNotes notes)
    {
        if (v == 1 || r == 2)
        {
            return 5;
        }

        int bits;
        if (v == 4)
        {
            // AESV2 always uses 128-bit keys. For other V 4 files use /Length, else the filter's /Length
            // (bytes if small, bits otherwise), else 128.
            if (s.StreamMethod == CryptMethod.AesV2 || s.StringMethod == CryptMethod.AesV2 || s.EmbeddedMethod == CryptMethod.AesV2 || s.Filters.ContainsValue(CryptMethod.AesV2))
            {
                return 16;
            }

            bits = p.Length ?? (s.RcLengthHint is int h ? (h <= 16 ? h * 8 : h) : 128);
        }
        else
        {
            bits = p.Length ?? 40;
        }

        int clamped = Math.Clamp(bits, 40, 128);
        if (clamped != bits || (bits & 7) != 0)
        {
            notes |= SecurityNotes.KeyLengthClamped;
        }

        return clamped / 8;
    }

    // ---- Revisions 2 to 4 ----

    // Algorithm 2: compute the encryption key from a (padded) user password.
    private static byte[] ComputeKeyLegacy(EncryptionParameters p, ReadOnlySpan<byte> paddedUserPassword, ReadOnlySpan<byte> id, int r, int n)
    {
        int len = 32 + 32 + 4 + id.Length + 4;
        byte[] input = new byte[len];
        try
        {
            paddedUserPassword.CopyTo(input);
            p.O.AsSpan(0, 32).CopyTo(input.AsSpan(32));
            int pos = 64;
            int perm = unchecked((int)p.P);
            input[pos++] = (byte)perm;
            input[pos++] = (byte)(perm >> 8);
            input[pos++] = (byte)(perm >> 16);
            input[pos++] = (byte)(perm >> 24);
            id.CopyTo(input.AsSpan(pos));
            pos += id.Length;
            if (r >= 4 && !p.EncryptMetadata)
            {
                input[pos++] = 0xFF;
                input[pos++] = 0xFF;
                input[pos++] = 0xFF;
                input[pos++] = 0xFF;
            }

            byte[] hash = PdfMd5.Hash(input.AsSpan(0, pos));
            if (r >= 3)
            {
                for (int i = 0; i < 50; i++)
                {
                    byte[] next = PdfMd5.Hash(hash.AsSpan(0, n));
                    CryptographicOperations.ZeroMemory(hash);
                    hash = next;
                }
            }

            byte[] key = hash.AsSpan(0, n).ToArray();
            CryptographicOperations.ZeroMemory(hash);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    // Algorithms 4 and 5 (verification of the user password through /U).
    private static byte[]? TryUserLegacy(EncryptionParameters p, byte[] padded, ReadOnlySpan<byte> id, int r, int n)
    {
        byte[] key = ComputeKeyLegacy(p, padded, id, r, n);
        bool ok;
        if (r == 2)
        {
            byte[] check = PasswordPreparation.Padding.ToArray();
            Rc4.TransformInPlace(key, check);
            ok = CryptographicOperations.FixedTimeEquals(check, p.U.AsSpan(0, 32));
        }
        else
        {
            byte[] data = new byte[16];
            byte[] input = new byte[32 + id.Length];
            PasswordPreparation.Padding.CopyTo(input);
            id.CopyTo(input.AsSpan(32));
            PdfMd5.Hash(input, data);
            Rc4.TransformInPlace(key, data);
            byte[] xored = new byte[key.Length];
            for (int i = 1; i < 20; i++)
            {
                for (int k = 0; k < key.Length; k++)
                {
                    xored[k] = (byte)(key[k] ^ i);
                }

                Rc4.TransformInPlace(xored, data);
            }

            CryptographicOperations.ZeroMemory(xored);
            ok = CryptographicOperations.FixedTimeEquals(data, p.U.AsSpan(0, 16));
        }

        if (ok)
        {
            return key;
        }

        CryptographicOperations.ZeroMemory(key);
        return null;
    }

    // Algorithm 7: recover the user password from /O with the owner password, then check it as a user password.
    private static byte[]? TryOwnerLegacy(EncryptionParameters p, byte[] paddedOwner, ReadOnlySpan<byte> id, int r, int n)
    {
        byte[] hash = PdfMd5.Hash(paddedOwner);
        if (r >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                byte[] next = PdfMd5.Hash(hash);
                CryptographicOperations.ZeroMemory(hash);
                hash = next;
            }
        }

        byte[] ownerKey = hash.AsSpan(0, n).ToArray();
        CryptographicOperations.ZeroMemory(hash);
        byte[] userPassword = p.O.AsSpan(0, 32).ToArray();
        if (r == 2)
        {
            Rc4.TransformInPlace(ownerKey, userPassword);
        }
        else
        {
            byte[] k = new byte[n];
            for (int i = 19; i >= 0; i--)
            {
                for (int j = 0; j < n; j++)
                {
                    k[j] = (byte)(ownerKey[j] ^ i);
                }

                Rc4.TransformInPlace(k, userPassword);
            }

            CryptographicOperations.ZeroMemory(k);
        }

        CryptographicOperations.ZeroMemory(ownerKey);
        byte[]? result = TryUserLegacy(p, userPassword, id, r, n);
        CryptographicOperations.ZeroMemory(userPassword);
        return result;
    }

    // ---- Revisions 5 and 6 (ISO 32000-2 7.6.4.3.3, Algorithm 2.A) ----

    private static byte[]? TryUserAes256(EncryptionParameters p, byte[] password, int r)
    {
        ReadOnlySpan<byte> u = p.U;
        byte[] h = Hash(password, u.Slice(32, 8), [], r);
        bool ok = CryptographicOperations.FixedTimeEquals(h, u.Slice(0, 32));
        CryptographicOperations.ZeroMemory(h);
        if (!ok)
        {
            return null;
        }

        return UnwrapFileKey(password, u.Slice(40, 8), [], p.UE, r);
    }

    private static byte[]? TryOwnerAes256(EncryptionParameters p, byte[] password, int r)
    {
        if (p.O.Length < 48 || p.OE.Length < 32)
        {
            return null;
        }

        ReadOnlySpan<byte> o = p.O;
        ReadOnlySpan<byte> u48 = p.U.AsSpan(0, 48);
        byte[] h = Hash(password, o.Slice(32, 8), u48, r);
        bool ok = CryptographicOperations.FixedTimeEquals(h, o.Slice(0, 32));
        CryptographicOperations.ZeroMemory(h);
        if (!ok)
        {
            return null;
        }

        return UnwrapFileKey(password, o.Slice(40, 8), u48, p.OE, r);
    }

    private static byte[] UnwrapFileKey(byte[] password, ReadOnlySpan<byte> keySalt, ReadOnlySpan<byte> udata, byte[] wrapped, int r)
    {
        byte[] intermediate = Hash(password, keySalt, udata, r);
        try
        {
            // AES-256, CBC, zero IV, no padding.
            return AesCbc.DecryptNoPadding(intermediate, new byte[16], wrapped.AsSpan(0, 32));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(intermediate);
        }
    }

    private static byte[] Hash(byte[] password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> udata, int r)
    {
        byte[] input = new byte[password.Length + salt.Length + udata.Length];
        password.CopyTo(input, 0);
        salt.CopyTo(input.AsSpan(password.Length));
        udata.CopyTo(input.AsSpan(password.Length + salt.Length));
        try
        {
            // R 5 is the plain SHA-256 of Algorithm 2.A (Adobe extension level 3); R 6 is Algorithm 2.B.
            return r == 5 ? SHA256.HashData(input) : Hash2B(input, password.Length, udata);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    // Algorithm 2.B. `input` is password + salt + udata; the loop re-uses the password and udata slices.
    private static byte[] Hash2B(byte[] input, int passwordLength, ReadOnlySpan<byte> udata)
    {
        byte[] k = SHA256.HashData(input);
        int unitMax = passwordLength + 64 + udata.Length;
        byte[] k1 = new byte[64 * unitMax];
        using Aes aes = Aes.Create();
        try
        {
            int round = 0;
            byte[] e;
            while (true)
            {
                int unit = passwordLength + k.Length + udata.Length;
                Span<byte> one = k1.AsSpan(0, unit);
                input.AsSpan(0, passwordLength).CopyTo(one);
                k.CopyTo(one.Slice(passwordLength));
                udata.CopyTo(one.Slice(passwordLength + k.Length));
                for (int i = 1; i < 64; i++)
                {
                    one.CopyTo(k1.AsSpan(i * unit, unit));
                }

                e = AesCbc.EncryptNoPadding(aes, k.AsSpan(0, 16), k.AsSpan(16, 16), k1.AsSpan(0, 64 * unit));

                // The first 16 bytes as a big-endian number mod 3 equal their byte sum mod 3 (256 = 1 mod 3).
                int sum = 0;
                for (int i = 0; i < 16; i++)
                {
                    sum += e[i];
                }

                byte[] next = (sum % 3) switch
                {
                    0 => SHA256.HashData(e),
                    1 => SHA384.HashData(e),
                    _ => SHA512.HashData(e),
                };
                CryptographicOperations.ZeroMemory(k);
                k = next;
                byte last = e[^1];
                CryptographicOperations.ZeroMemory(e);
                round++;
                // Stop once at least 64 rounds are done and the last byte of E is <= (rounds done - 32).
                // Checked against 300 qpdf-written files; the clause's "round number" counts rounds completed.
                if (round >= 64 && last <= round - 32)
                {
                    break;
                }
            }

            return k.AsSpan(0, 32).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(k);
            CryptographicOperations.ZeroMemory(k1);
        }
    }

    // Algorithm 2.A, last step: /Perms is AES-256-ECB of P (4 bytes LE, then FFFFFFFF), 'T'/'F', "adb", 4 random bytes.
    private static PermsCheck CheckPerms(EncryptionParameters p, byte[] fileKey)
    {
        if (p.Perms.Length < 16)
        {
            return PermsCheck.NotChecked;
        }

        byte[] d = AesCbc.DecryptEcbNoPadding(fileKey, p.Perms.AsSpan(0, 16));
        int perm = d[0] | (d[1] << 8) | (d[2] << 16) | (d[3] << 24);
        bool ok = d[9] == (byte)'a' && d[10] == (byte)'d' && d[11] == (byte)'b'
            && perm == unchecked((int)p.P)
            && (d[8] == (byte)'T') == p.EncryptMetadata
            && d[8] is (byte)'T' or (byte)'F';
        CryptographicOperations.ZeroMemory(d);
        return ok ? PermsCheck.Valid : PermsCheck.Invalid;
    }
}
