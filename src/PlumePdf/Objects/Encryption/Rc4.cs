namespace PlumePdf.Objects;

/// <summary>
/// RC4 (ISO 32000-1 §7.6.2, "the RC4 encryption algorithm defined in ... [RC4]"), hand-rolled
/// because it is absent from the modern .NET BCL (deliberately: it's cryptographically
/// broken for new designs, but PDF's Standard Security Handler revisions 2-4 still specify
/// it, so a reader has to speak it). RC4 is a symmetric stream cipher — the same function
/// both encrypts and decrypts.
/// </summary>
internal static class Rc4
{
    /// <summary>Encrypts or decrypts <paramref name="data"/> in place using <paramref name="key"/> and returns the result.</summary>
    public static byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("RC4 key must not be empty.", nameof(key));
        }

        Span<byte> state = stackalloc byte[256];
        for (var i = 0; i < 256; i++)
        {
            state[i] = (byte)i;
        }

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
        }

        var output = new byte[data.Length];
        var a = 0;
        var b = 0;
        for (var k = 0; k < data.Length; k++)
        {
            a = (a + 1) & 0xFF;
            b = (b + state[a]) & 0xFF;
            (state[a], state[b]) = (state[b], state[a]);
            var keystreamByte = state[(state[a] + state[b]) & 0xFF];
            output[k] = (byte)(data[k] ^ keystreamByte);
        }

        return output;
    }
}
