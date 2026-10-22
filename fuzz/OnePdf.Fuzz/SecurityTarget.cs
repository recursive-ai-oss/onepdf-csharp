using System;
using System.Collections.Generic;
using OnePdf.Security;

namespace OnePdf.Fuzz;

/// <summary>
/// Standard security handler target. The input picks the /Encrypt values (revision, version, crypt filters, key
/// length, the O/U/OE/UE/Perms strings), a document ID and a password; then authentication runs and, when it
/// succeeds, strings and streams are decrypted. A second path builds a decryptor on a fixed key so ciphertext
/// handling (padding, partial blocks, short IVs) is reached even when no password matches.
/// Only <see cref="SecurityUnsupportedException"/> may escape.
/// </summary>
internal static class SecurityTarget
{
    private static readonly string?[] Cfms = ["V2", "AESV2", "AESV3", "None", null];
    private static readonly string?[] Names = ["StdCF", "Identity", null, "Other"];

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            return;
        }

        int r = 2 + (data[0] % 5);
        int v = r switch { 2 => 1 + (data[1] & 1), 3 => 2, 4 => 4, _ => 5 };
        if ((data[1] & 0x40) != 0)
        {
            v = data[1] & 7; // sometimes a nonsensical combination
        }

        var cf = new Dictionary<string, CryptFilterSpec>
        {
            ["StdCF"] = new(Cfms[data[2] % Cfms.Length], (data[3] & 1) == 0 ? null : data[3]),
        };

        ReadOnlySpan<byte> rest = data.Slice(10);
        int oLen = data[4] % 2 == 0 ? 32 : 48;
        byte[] o = Take(ref rest, oLen);
        byte[] u = Take(ref rest, oLen);
        byte[] oe = Take(ref rest, 32);
        byte[] ue = Take(ref rest, 32);
        byte[] perms = Take(ref rest, 16);
        byte[] id = Take(ref rest, 16);
        var p = new EncryptionParameters
        {
            Filter = (data[5] & 0x80) != 0 ? "Other" : "Standard",
            V = v,
            R = r,
            Length = (data[5] & 1) == 0 ? null : data[6] * 3,
            P = (data[7] << 24) | (data[8] << 16) | (data[9] << 8) | data[1],
            EncryptMetadata = (data[5] & 2) == 0,
            CryptFilters = cf,
            StmF = Names[(data[5] >> 2) & 3],
            StrF = Names[(data[5] >> 4) & 3],
            O = o,
            U = u,
            OE = oe,
            UE = ue,
            Perms = perms,
        };
        string password = rest.Length == 0 ? string.Empty : System.Text.Encoding.Latin1.GetString(rest.Slice(0, Math.Min(rest.Length, 20)));

        try
        {
            SecurityResult result = StandardSecurityHandler.Authenticate(p, id, password);
            result.Decryptor?.Dispose();
        }
        catch (SecurityUnsupportedException)
        {
            // The documented way for unsupported parameters to surface.
        }

        // Ciphertext path on a fixed key.
        var method = (CryptMethod)(1 + (data[2] % 3));
        byte[] key = new byte[method switch { CryptMethod.AesV3 => 32, CryptMethod.AesV2 => 16, _ => 5 + (data[6] % 12) }];
        key.AsSpan().Fill(data[7]);
        var filters = new Dictionary<string, CryptMethod> { ["StdCF"] = method };
        using var d = new SecurityDecryptor(key, method, method, method, filters, (data[5] & 2) == 0);
        _ = d.DecryptString(rest, data[8], data[9]);
        _ = d.DecryptStream(rest, data[8], data[9], null, isMetadata: (data[5] & 1) != 0);
        _ = d.DecryptStream(rest, 1, 0, "StdCF");
    }

    private static byte[] Take(ref ReadOnlySpan<byte> s, int n)
    {
        int k = Math.Min(n, s.Length);
        byte[] b = s.Slice(0, k).ToArray();
        s = s.Slice(k);
        return b;
    }
}
