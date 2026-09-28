using System.Text;
using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters;

/// <summary>
/// <see cref="RunLengthFilter"/> (ISO 32000-1 §7.4.5), plus its
/// <see cref="PdfFilterRegistry"/> registration under both <c>RunLengthDecode</c> and the
/// abbreviated <c>RL</c> name.
/// </summary>
public class RunLengthFilterTests
{
    [Fact]
    public void Decode_LiteralRun_CopiesBytesAsIs()
    {
        // Length byte 4 -> 5 literal bytes follow, then EOD (128).
        byte[] data = [4, (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 128];

        var decoded = RunLengthFilter.Decode(data, PdfOptions.Default, null, null);

        Assert.Equal("Hello"u8.ToArray(), decoded);
    }

    [Fact]
    public void Decode_RepeatedRun_RepeatsTheSingleByte()
    {
        // Length byte 254 -> 257 - 254 = 3 repetitions of the following byte.
        byte[] data = [254, (byte)'X', 128];

        var decoded = RunLengthFilter.Decode(data, PdfOptions.Default, null, null);

        Assert.Equal("XXX"u8.ToArray(), decoded);
    }

    [Fact]
    public void Decode_MinimumLiteralLength_Zero_CopiesOneByte()
    {
        byte[] data = [0, (byte)'A', 128];

        var decoded = RunLengthFilter.Decode(data, PdfOptions.Default, null, null);

        Assert.Equal([(byte)'A'], decoded);
    }

    [Fact]
    public void Decode_MaximumLiteralLength_127_Copies128Bytes()
    {
        var literal = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        byte[] data = [127, .. literal, 128];

        var decoded = RunLengthFilter.Decode(data, PdfOptions.Default, null, null);

        Assert.Equal(literal, decoded);
    }

    [Fact]
    public void Decode_MinimumRepeatLength_255_RepeatsTwice()
    {
        // Length byte 255 -> 257 - 255 = 2 repetitions.
        byte[] data = [255, (byte)'Z', 128];

        var decoded = RunLengthFilter.Decode(data, PdfOptions.Default, null, null);

        Assert.Equal("ZZ"u8.ToArray(), decoded);
    }

    [Fact]
    public void Decode_MaximumRepeatLength_129_Repeats128Times()
    {
        // Length byte 129 -> 257 - 129 = 128 repetitions.
        byte[] data = [129, (byte)'Q', 128];

        var decoded = RunLengthFilter.Decode(data, PdfOptions.Default, null, null);

        Assert.Equal(128, decoded.Length);
        Assert.All(decoded, b => Assert.Equal((byte)'Q', b));
    }

    [Fact]
    public void Decode_MultipleRuns_ConcatenatesInOrder()
    {
        byte[] data =
        [
            2, (byte)'a', (byte)'b', (byte)'c', // literal: "abc"
            253, (byte)'d', // repeat 4x 'd'
            128, // EOD
        ];

        var decoded = RunLengthFilter.Decode(data, PdfOptions.Default, null, null);

        Assert.Equal("abcdddd"u8.ToArray(), decoded);
    }

    [Fact]
    public void Decode_TruncatedLiteralRun_RecordsDiagnosticAndUsesAvailableBytes()
    {
        var diagnostics = new DiagnosticCollection();
        byte[] data = [4, (byte)'H', (byte)'i']; // declares 5 literal bytes, only 2 present, no EOD

        var decoded = RunLengthFilter.Decode(data, PdfOptions.Default, diagnostics, null);

        Assert.Equal("Hi"u8.ToArray(), decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3041");
        Assert.Contains(diagnostics, d => d.Code == "PLUME3040"); // also missing EOD
    }

    [Fact]
    public void Decode_TruncatedRepeatedRun_RecordsDiagnostic()
    {
        var diagnostics = new DiagnosticCollection();
        byte[] data = [254]; // declares a repeated run but the byte to repeat is missing

        var decoded = RunLengthFilter.Decode(data, PdfOptions.Default, diagnostics, null);

        Assert.Empty(decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3041");
    }

    [Fact]
    public void Decode_MissingEod_RecordsDiagnosticAndDecodesAvailableBytes()
    {
        var diagnostics = new DiagnosticCollection();
        byte[] data = [1, (byte)'O', (byte)'K']; // literal run, no trailing 128

        var decoded = RunLengthFilter.Decode(data, PdfOptions.Default, diagnostics, null);

        Assert.Equal("OK"u8.ToArray(), decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3040");
    }

    [Fact]
    public void Decode_MissingEod_Strict_Throws()
    {
        var strict = PdfOptions.Default with { Strict = true };
        byte[] data = [1, (byte)'O', (byte)'K'];

        var ex = Assert.Throws<PlumePdfException>(() => RunLengthFilter.Decode(data, strict, null, null));
        Assert.Equal("PLUME3040", ex.Code);
    }

    [Fact]
    public void Decode_TruncatedRun_Strict_Throws()
    {
        var strict = PdfOptions.Default with { Strict = true };
        byte[] data = [254];

        var ex = Assert.Throws<PlumePdfException>(() => RunLengthFilter.Decode(data, strict, null, null));
        Assert.Equal("PLUME3041", ex.Code);
    }

    [Fact]
    public void Decode_EmptyInput_ReturnsEmptyWithMissingEodDiagnostic()
    {
        var diagnostics = new DiagnosticCollection();

        var decoded = RunLengthFilter.Decode([], PdfOptions.Default, diagnostics, null);

        Assert.Empty(decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3040");
    }

    [Fact]
    public void Registry_Default_DecodesUnderFullAndAbbreviatedNames()
    {
        byte[] data = [4, (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 128];
        var full = new PdfDictionary();
        full.Set(PdfName.Filter, PdfName.Get("RunLengthDecode"));
        var abbreviated = new PdfDictionary();
        abbreviated.Set(PdfName.Filter, PdfName.Get("RL"));

        var decodedFull = PdfFilterRegistry.Default.Decode(full, data, PdfOptions.Default);
        var decodedAbbreviated = PdfFilterRegistry.Default.Decode(abbreviated, data, PdfOptions.Default);

        Assert.Equal("Hello"u8.ToArray(), decodedFull);
        Assert.Equal("Hello"u8.ToArray(), decodedAbbreviated);
    }

    [Fact]
    public void Decode_OutputBeyondCap_ThrowsCodedBombGuard()
    {
        // Review critical: RunLength had no MaxDecompressedStreamBytes cap - chained
        // /RunLengthDecode stages amplified 2 input bytes to 33 MB (64x per stage).
        byte[] bomb = [0x81, 0x81]; // one repeat-run: 128 copies of 0x81
        var options = PdfOptions.Default with { MaxDecompressedStreamBytes = 64 };

        var ex = Assert.Throws<PlumePdfException>(
            () => RunLengthFilter.Decode(bomb, options, null, null));

        Assert.Equal("PLUME3042", ex.Code);
    }
}

