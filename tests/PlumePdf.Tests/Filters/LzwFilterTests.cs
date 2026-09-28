using System.Text;
using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters;

/// <summary>
/// <see cref="LzwFilter"/> (ISO 32000-1 §7.4.4), its <c>/EarlyChange</c>
/// wiring through <see cref="LzwFilterAdapter"/> and <see cref="PdfFilterRegistry"/>, and
/// its <see cref="PdfOptions.MaxDecompressedStreamBytes"/> cap. Round-trip fixtures are
/// built with a small reference LZW encoder written for this test file, mirroring the
/// decoder's own code-width schedule, so coverage isn't limited to a single hand-computed
/// vector.
/// </summary>
public class LzwFilterTests
{
    [Fact]
    public void Decode_SingleLiteralCode_ThenEod_DecodesOneByte()
    {
        // Hand-built vector: 9-bit code 65 ('A'), then 9-bit EOD (257). No dictionary
        // growth happens before EOD, so the width never needs to change.
        var data = PackFixedWidthCodes(9, 65, 257);

        var decoded = LzwFilter.Decode(data, earlyChange: true, PdfOptions.Default, null, null);

        Assert.Equal([(byte)'A'], decoded);
    }

    [Fact]
    public void Decode_ClearCodeMidStream_ResetsDictionaryAndWidth()
    {
        // Hand-built vector: clear(256), 'A'(65), 'B'(66), clear(256), 'A'(65), EOD(257).
        // Every code stays a literal single-byte lookup, so 9 bits suffices throughout and
        // this exercises the clear-code reset path without needing width-growth math.
        var data = PackFixedWidthCodes(9, 256, 65, 66, 256, 65, 257);

        var decoded = LzwFilter.Decode(data, earlyChange: true, PdfOptions.Default, null, null);

        Assert.Equal("ABA"u8.ToArray(), decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("AA")]
    [InlineData("AAAA")]
    [InlineData("ABABABAB")]
    [InlineData("The quick brown fox jumps over the lazy dog.")]
    public void Decode_RoundTripsThroughReferenceEncoder_EarlyChangeTrue(string text)
    {
        var original = Encoding.ASCII.GetBytes(text);
        var encoded = ReferenceLzwEncoder.Encode(original, earlyChange: true);

        var decoded = LzwFilter.Decode(encoded, earlyChange: true, PdfOptions.Default, null, null);

        Assert.Equal(original, decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("ABABABAB")]
    [InlineData("The quick brown fox jumps over the lazy dog.")]
    public void Decode_RoundTripsThroughReferenceEncoder_EarlyChangeFalse(string text)
    {
        var original = Encoding.ASCII.GetBytes(text);
        var encoded = ReferenceLzwEncoder.Encode(original, earlyChange: false);

        var decoded = LzwFilter.Decode(encoded, earlyChange: false, PdfOptions.Default, null, null);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_RoundTrips_AcrossCodeWidthGrowthBoundary()
    {
        // Long enough (well past 512 distinct-phrase codes) to force the code width from
        // 9 to 10+ bits mid-stream, exercising the growth arithmetic itself rather than
        // only ever running at the starting width.
        var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 60)));
        var encoded = ReferenceLzwEncoder.Encode(original, earlyChange: true);

        var decoded = LzwFilter.Decode(encoded, earlyChange: true, PdfOptions.Default, null, null);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_BinaryPayload_RoundTrips()
    {
        var original = new byte[2000];
        new Random(11).NextBytes(original);
        var encoded = ReferenceLzwEncoder.Encode(original, earlyChange: true);

        var decoded = LzwFilter.Decode(encoded, earlyChange: true, PdfOptions.Default, null, null);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_WrongEarlyChangeValue_CorruptsOutput_AcrossGrowthBoundary()
    {
        // Proves /EarlyChange actually changes decode behavior (rather than being plumbed
        // through and silently ignored): encoding and decoding with mismatched values, once
        // the stream is long enough to cross a code-width boundary, must not round-trip.
        var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 60)));
        var encoded = ReferenceLzwEncoder.Encode(original, earlyChange: true);

        var decoded = LzwFilter.Decode(encoded, earlyChange: false, PdfOptions.Default, null, null);

        Assert.NotEqual(original, decoded);
    }

    [Fact]
    public void Decode_CodeReferencingUnpopulatedEntry_RecordsDiagnosticAndReturnsPartial()
    {
        var diagnostics = new DiagnosticCollection();

        // 'A'(65), then code 300 - far beyond the 258 entries that exist after just one
        // literal code - which the dictionary has no entry for.
        var data = PackFixedWidthCodes(9, 65, 300);

        var decoded = LzwFilter.Decode(data, earlyChange: true, PdfOptions.Default, diagnostics, null);

        Assert.Equal([(byte)'A'], decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3050");
    }

    [Fact]
    public void Decode_CodeReferencingUnpopulatedEntry_Strict_Throws()
    {
        var strict = PdfOptions.Default with { Strict = true };
        var data = PackFixedWidthCodes(9, 65, 300);

        var ex = Assert.Throws<PlumePdfException>(() => LzwFilter.Decode(data, earlyChange: true, strict, null, null));
        Assert.Equal("PLUME3050", ex.Code);
    }

    [Fact]
    public void Decode_MissingEodCode_RecordsDiagnosticAndReturnsPartial()
    {
        var diagnostics = new DiagnosticCollection();

        // Just 'A'(65), no EOD - input runs out mid-read of the next code.
        var data = PackFixedWidthCodes(9, 65);

        var decoded = LzwFilter.Decode(data, earlyChange: true, PdfOptions.Default, diagnostics, null);

        Assert.Equal([(byte)'A'], decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3051");
    }

    [Fact]
    public void Decode_MissingEodCode_Strict_Throws()
    {
        var strict = PdfOptions.Default with { Strict = true };
        var data = PackFixedWidthCodes(9, 65);

        var ex = Assert.Throws<PlumePdfException>(() => LzwFilter.Decode(data, earlyChange: true, strict, null, null));
        Assert.Equal("PLUME3051", ex.Code);
    }

    [Fact]
    public void Decode_ExceedsDecompressionCap_Throws()
    {
        var original = Encoding.ASCII.GetBytes(new string('B', 20_000));
        var encoded = ReferenceLzwEncoder.Encode(original, earlyChange: true);
        var tight = PdfOptions.Default with { MaxDecompressedStreamBytes = 1024 };

        var ex = Assert.Throws<PlumePdfException>(() => LzwFilter.Decode(encoded, earlyChange: true, tight, null, null));
        Assert.Equal("PLUME3052", ex.Code);
    }

    [Fact]
    public void Adapter_NoDecodeParms_DefaultsEarlyChangeToTrue()
    {
        var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 60)));
        var encoded = ReferenceLzwEncoder.Encode(original, earlyChange: true);
        var adapter = new LzwFilterAdapter();

        var decoded = adapter.Decode(encoded, PdfOptions.Default, null, null);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Adapter_DecodeParmsEarlyChangeZero_IsHonored()
    {
        var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 60)));
        var encoded = ReferenceLzwEncoder.Encode(original, earlyChange: false);
        var adapter = new LzwFilterAdapter();
        var parms = new PdfDictionary();
        parms.Set(PdfName.EarlyChange, PdfNumber.Get(0));

        var decoded = adapter.Decode(encoded, parms, PdfOptions.Default, null, null);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Adapter_InvalidDecodeParmsEarlyChangeValue_RecordsDiagnosticAndDefaultsToTrue()
    {
        var original = Encoding.ASCII.GetBytes("round trips with the default");
        var encoded = ReferenceLzwEncoder.Encode(original, earlyChange: true);
        var adapter = new LzwFilterAdapter();
        var parms = new PdfDictionary();
        parms.Set(PdfName.EarlyChange, PdfNumber.Get(7));
        var diagnostics = new DiagnosticCollection();

        var decoded = adapter.Decode(encoded, parms, PdfOptions.Default, diagnostics, null);

        Assert.Equal(original, decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3053");
    }

    [Fact]
    public void Registry_Default_DecodesUnderFullAndAbbreviatedNames()
    {
        var original = Encoding.ASCII.GetBytes("Hello via the registry");
        var encoded = ReferenceLzwEncoder.Encode(original, earlyChange: true);
        var full = new PdfDictionary();
        full.Set(PdfName.Filter, PdfName.Get("LZWDecode"));
        var abbreviated = new PdfDictionary();
        abbreviated.Set(PdfName.Filter, PdfName.Get("LZW"));

        var decodedFull = PdfFilterRegistry.Default.Decode(full, encoded, PdfOptions.Default);
        var decodedAbbreviated = PdfFilterRegistry.Default.Decode(abbreviated, encoded, PdfOptions.Default);

        Assert.Equal(original, decodedFull);
        Assert.Equal(original, decodedAbbreviated);
    }

    [Fact]
    public void Registry_HonorsEarlyChangeFromDecodeParms()
    {
        var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 60)));
        var encoded = ReferenceLzwEncoder.Encode(original, earlyChange: false);

        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("LZWDecode"));
        var parms = new PdfDictionary();
        parms.Set(PdfName.EarlyChange, PdfNumber.Get(0));
        dict.Set(PdfName.DecodeParms, parms);

        var decoded = PdfFilterRegistry.Default.Decode(dict, encoded, PdfOptions.Default);

        Assert.Equal(original, decoded);
    }

    /// <summary>Packs a sequence of fixed-width codes MSB-first, matching <see cref="LzwFilter"/>'s bit order - only valid for vectors small enough that the dictionary never grows past the given width.</summary>
    private static byte[] PackFixedWidthCodes(int width, params int[] codes)
    {
        var bits = new List<bool>();
        foreach (var code in codes)
        {
            for (var i = width - 1; i >= 0; i--)
            {
                bits.Add(((code >> i) & 1) != 0);
            }
        }

        var bytes = new List<byte>((bits.Count + 7) / 8);
        for (var i = 0; i < bits.Count; i += 8)
        {
            byte b = 0;
            for (var j = 0; j < 8; j++)
            {
                b <<= 1;
                if (i + j < bits.Count && bits[i + j])
                {
                    b |= 1;
                }
            }

            bytes.Add(b);
        }

        return [.. bytes];
    }
}

/// <summary>
/// A minimal reference LZW encoder, written independently of <see cref="LzwFilter"/>, used
/// only to build round-trip fixtures for the decoder under test. Mirrors the decoder's own
/// code-width growth schedule (dictionary size vs. <c>/EarlyChange</c>) so encoded fixtures
/// are valid input for it.
/// </summary>
internal static class ReferenceLzwEncoder
{
    private const int ClearCode = 256;
    private const int EodCode = 257;

    public static byte[] Encode(byte[] input, bool earlyChange)
    {
        var dictionary = new Dictionary<string, int>();
        for (var i = 0; i < 256; i++)
        {
            dictionary[((char)i).ToString()] = i;
        }

        var nextCode = 258;
        var codeBits = 9;
        var earlyChangeDelta = earlyChange ? 1 : 0;
        var emitted = new List<(int Code, int Bits)>();

        void Grow()
        {
            // Checked *before* incrementing nextCode: the decoder only learns about a new
            // dictionary entry - and can only bump its own width - one code *after* the
            // encoder does (the encoder adds dict[candidate] the moment it emits the code
            // for the shorter prefix; the decoder can't mirror that add until it has
            // decoded the *next* code too, since forming the new entry needs both the
            // previous decoded entry and the just-decoded one). Checking pre-increment here
            // reproduces that one-code lag so this reference encoder's width schedule lines
            // up with LzwFilter's decode-side schedule.
            if (nextCode + earlyChangeDelta >= (1 << codeBits) && codeBits < 12)
            {
                codeBits++;
            }

            nextCode++;
        }

        var current = string.Empty;
        foreach (var b in input)
        {
            var candidate = current + (char)b;
            if (dictionary.ContainsKey(candidate))
            {
                current = candidate;
                continue;
            }

            emitted.Add((dictionary[current], codeBits));
            dictionary[candidate] = nextCode;
            Grow();
            current = ((char)b).ToString();
        }

        if (current.Length > 0)
        {
            emitted.Add((dictionary[current], codeBits));
        }

        emitted.Add((EodCode, codeBits));

        return Pack(emitted);
    }

    private static byte[] Pack(List<(int Code, int Bits)> codes)
    {
        var bits = new List<bool>();
        foreach (var (code, width) in codes)
        {
            for (var i = width - 1; i >= 0; i--)
            {
                bits.Add(((code >> i) & 1) != 0);
            }
        }

        var bytes = new List<byte>((bits.Count + 7) / 8);
        for (var i = 0; i < bits.Count; i += 8)
        {
            byte b = 0;
            for (var j = 0; j < 8; j++)
            {
                b <<= 1;
                if (i + j < bits.Count && bits[i + j])
                {
                    b |= 1;
                }
            }

            bytes.Add(b);
        }

        return [.. bytes];
    }
}
