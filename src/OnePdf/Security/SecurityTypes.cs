using System;
using System.Collections.Generic;

namespace OnePdf.Security;

/// <summary>Cipher selected by a crypt filter (ISO 32000-2 7.6.6, Table 25 /CFM).</summary>
internal enum CryptMethod
{
    /// <summary>No encryption (/CFM /None or the Identity filter).</summary>
    None,

    /// <summary>RC4 with a per-object key (/CFM /V2, and V 1 and 2 files).</summary>
    Rc4,

    /// <summary>AES-128-CBC with a per-object key (/CFM /AESV2).</summary>
    AesV2,

    /// <summary>AES-256-CBC with the file key used directly (/CFM /AESV3).</summary>
    AesV3,
}

/// <summary>One entry of the /CF dictionary.</summary>
/// <param name="Cfm">The /CFM name without the slash: <c>None</c>, <c>V2</c>, <c>AESV2</c> or <c>AESV3</c>. Null reads as None.</param>
/// <param name="Length">The /Length entry, when present. Bytes in most files, bits in some; only used for V 4 RC4 key length hints.</param>
internal sealed record CryptFilterSpec(string? Cfm, int? Length = null);

/// <summary>
/// The /Encrypt dictionary values the standard security handler needs, as plain data so the handler does not depend
/// on the object model. Strings are the raw (already unescaped) bytes; absent or empty entries are null or empty.
/// </summary>
internal sealed class EncryptionParameters
{
    /// <summary>/Filter name without the slash. Only <c>Standard</c> (or absent, leniently) is handled.</summary>
    public string? Filter { get; init; }

    /// <summary>/V: 1, 2, 4 or 5. 0 or absent is inferred from /R.</summary>
    public int V { get; init; }

    /// <summary>/R: 2 to 6.</summary>
    public int R { get; init; }

    /// <summary>/Length in bits; null when absent.</summary>
    public int? Length { get; init; }

    /// <summary>/O (owner string).</summary>
    public byte[] O { get; init; } = [];

    /// <summary>/U (user string).</summary>
    public byte[] U { get; init; } = [];

    /// <summary>/OE (R 5 and 6).</summary>
    public byte[] OE { get; init; } = [];

    /// <summary>/UE (R 5 and 6).</summary>
    public byte[] UE { get; init; } = [];

    /// <summary>/Perms (R 6; also R 5 files written by some producers).</summary>
    public byte[] Perms { get; init; } = [];

    /// <summary>/P as written: signed, or unsigned 32-bit. Only the low 32 bits count.</summary>
    public long P { get; init; }

    /// <summary>/EncryptMetadata; true when absent.</summary>
    public bool EncryptMetadata { get; init; } = true;

    /// <summary>/CF crypt filter map (V 4 and 5), keyed by filter name without the slash.</summary>
    public IReadOnlyDictionary<string, CryptFilterSpec>? CryptFilters { get; init; }

    /// <summary>/StmF name; null or <c>Identity</c> leaves streams unencrypted.</summary>
    public string? StmF { get; init; }

    /// <summary>/StrF name; null or <c>Identity</c> leaves strings unencrypted.</summary>
    public string? StrF { get; init; }

    /// <summary>/EFF name for embedded file streams; null falls back to /StmF.</summary>
    public string? Eff { get; init; }
}

/// <summary>Who the supplied password authenticated as.</summary>
internal enum SecurityOutcome
{
    /// <summary>No password matched (including the empty user password).</summary>
    Failed,

    /// <summary>The user password matched, or the empty user password was accepted.</summary>
    User,

    /// <summary>The owner password matched.</summary>
    Owner,
}

/// <summary>Result of checking the R 6 /Perms entry (ISO 32000-2 7.6.4.3.3, Algorithm 2.A, last step).</summary>
internal enum PermsCheck
{
    /// <summary>Not an R 5/6 file, no /Perms, or authentication failed.</summary>
    NotChecked,

    /// <summary>/Perms decrypts to "adb", agrees with /P and /EncryptMetadata.</summary>
    Valid,

    /// <summary>/Perms is malformed or disagrees with /P or /EncryptMetadata. Reported only; the file still opens.</summary>
    Invalid,
}

/// <summary>Things the handler repaired or tolerated while authenticating.</summary>
[Flags]
internal enum SecurityNotes
{
    /// <summary>Nothing to report.</summary>
    None = 0,

    /// <summary>The key length was outside the spec's range and was clamped (40 to 128 bits).</summary>
    KeyLengthClamped = 1,

    /// <summary>/V was missing or 0 and was inferred from /R.</summary>
    VersionInferred = 2,

    /// <summary>/Filter was not present; Standard was assumed.</summary>
    FilterAssumed = 4,

    /// <summary>The /ID array had no first string; an empty one was used.</summary>
    MissingId = 8,

    /// <summary>The password only worked after a fallback encoding (not the one the spec prescribes).</summary>
    PasswordEncodingFallback = 16,

    /// <summary>An R 5/6 file named a crypt method other than AESV3 or an R 2-4 file one other than V2/AESV2.</summary>
    UnusualCryptMethod = 32,
}

/// <summary>User access permissions, bit positions from ISO 32000-2 Table 22 (bit n is <c>1 &lt;&lt; (n-1)</c>).</summary>
[Flags]
internal enum SecurityPermissions : uint
{
    /// <summary>No permission bits set.</summary>
    None = 0,

    /// <summary>Bit 3: print (R 3+: low resolution only unless <see cref="PrintHighQuality"/>).</summary>
    Print = 1u << 2,

    /// <summary>Bit 4: modify contents other than annotations, forms, assembly.</summary>
    ModifyContents = 1u << 3,

    /// <summary>Bit 5: copy or extract text and graphics.</summary>
    CopyContent = 1u << 4,

    /// <summary>Bit 6: add or modify annotations; fill forms when <see cref="FillForms"/> is also set.</summary>
    ModifyAnnotations = 1u << 5,

    /// <summary>Bit 9 (R 3+): fill in existing form fields.</summary>
    FillForms = 1u << 8,

    /// <summary>Bit 10 (R 3+): extract text and graphics for accessibility.</summary>
    ExtractForAccessibility = 1u << 9,

    /// <summary>Bit 11 (R 3+): insert, rotate or delete pages and create bookmarks.</summary>
    AssembleDocument = 1u << 10,

    /// <summary>Bit 12 (R 3+): print a faithful high-quality copy.</summary>
    PrintHighQuality = 1u << 11,
}

/// <summary>Result of decrypting one string or stream.</summary>
/// <param name="Data">The decrypted bytes (best effort when <paramref name="Issues"/> is not None).</param>
/// <param name="Issues">What was wrong with the ciphertext, if anything.</param>
internal readonly record struct DecryptResult(byte[] Data, DecryptIssues Issues);

/// <summary>Problems met while decrypting one item. Data is kept as far as it decrypted.</summary>
[Flags]
internal enum DecryptIssues
{
    /// <summary>Clean.</summary>
    None = 0,

    /// <summary>The AES padding was invalid; the whole decrypted block run was returned unstripped.</summary>
    BadPadding = 1,

    /// <summary>The ciphertext was not a whole number of blocks; the trailing bytes were dropped.</summary>
    PartialBlock = 2,

    /// <summary>AES data shorter than the 16-byte IV; empty output.</summary>
    MissingIv = 4,

    /// <summary>The named crypt filter is not in /CF; the data was returned as is.</summary>
    UnknownCryptFilter = 8,
}

/// <summary>Everything the handler learned from one authentication attempt.</summary>
internal sealed class SecurityResult
{
    /// <summary>Who authenticated, or Failed.</summary>
    public SecurityOutcome Outcome { get; init; }

    /// <summary>Decryptor for strings and streams; null when <see cref="Outcome"/> is Failed. The caller disposes it.</summary>
    public SecurityDecryptor? Decryptor { get; init; }

    /// <summary>The /P bits as flags. Reported, never enforced.</summary>
    public SecurityPermissions Permissions { get; init; }

    /// <summary>The low 32 bits of /P.</summary>
    public int RawPermissions { get; init; }

    /// <summary>The R 5/6 /Perms cross-check.</summary>
    public PermsCheck PermsCheck { get; init; }

    /// <summary>Repairs and tolerated oddities.</summary>
    public SecurityNotes Notes { get; init; }
}

/// <summary>
/// The encryption cannot be handled: unsupported handler or revision, malformed /O, /U and friends, or no AES in this
/// runtime (browser WASM). The reader reports it as an encryption error; a wrong password is not this exception.
/// </summary>
internal sealed class SecurityUnsupportedException : Exception
{
    public SecurityUnsupportedException(string message)
        : base(message)
    {
    }

    public SecurityUnsupportedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
