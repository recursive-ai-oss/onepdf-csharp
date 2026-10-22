using System;
using System.Security.Cryptography;

namespace OnePdf.Security;

/// <summary>
/// The RC4 stream cipher (the .NET base class library has none). Used by the standard security handler for
/// 40- to 128-bit keys (ISO 32000-1 7.6.2, Algorithm 1 and 3.x). One instance transforms one message.
/// </summary>
internal sealed class Rc4 : IDisposable
{
    private readonly byte[] _s = new byte[256];
    private byte _i;
    private byte _j;

    /// <summary>Runs the key schedule. The key must be 1 to 256 bytes.</summary>
    public Rc4(ReadOnlySpan<byte> key)
    {
        if (key.Length is 0 or > 256)
        {
            throw new ArgumentException("RC4 keys are 1 to 256 bytes.", nameof(key));
        }

        for (int n = 0; n < 256; n++)
        {
            _s[n] = (byte)n;
        }

        int j = 0;
        for (int n = 0; n < 256; n++)
        {
            j = (j + _s[n] + key[n % key.Length]) & 0xFF;
            (_s[n], _s[j]) = (_s[j], _s[n]);
        }
    }

    /// <summary>Encrypts or decrypts (the operation is symmetric). <paramref name="source"/> and <paramref name="destination"/> may be the same span.</summary>
    public void Transform(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("Destination is shorter than the source.", nameof(destination));
        }

        byte i = _i;
        byte j = _j;
        byte[] s = _s;
        for (int n = 0; n < source.Length; n++)
        {
            i++;
            j = (byte)(j + s[i]);
            (s[i], s[j]) = (s[j], s[i]);
            destination[n] = (byte)(source[n] ^ s[(byte)(s[i] + s[j])]);
        }

        _i = i;
        _j = j;
    }

    /// <summary>One-shot transform of <paramref name="data"/> under <paramref name="key"/>, in place.</summary>
    public static void TransformInPlace(ReadOnlySpan<byte> key, Span<byte> data)
    {
        using var rc4 = new Rc4(key);
        rc4.Transform(data, data);
    }

    /// <summary>Clears the cipher state.</summary>
    public void Dispose() => CryptographicOperations.ZeroMemory(_s);
}
