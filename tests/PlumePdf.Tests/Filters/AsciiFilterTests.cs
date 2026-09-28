using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters;

/// <summary>
/// <see cref="AsciiHexFilter"/> (ISO 32000-1 §7.4.2) and
/// <see cref="Ascii85Filter"/> (§7.4.3), plus their <see cref="PdfFilterRegistry"/>
/// registrations under both full and abbreviated (<c>AHx</c>/<c>A85</c>) names.
/// </summary>
public class AsciiHexFilterTests
{
    [Fact]
    public void Decode_SimpleHex_RoundTrips()
    {
        var decoded = AsciiHexFilter.Decode("48656C6C6F>"u8, PdfOptions.Default, null, null);

        Assert.Equal("Hello"u8.ToArray(), decoded);
    }

    [Fact]
    public void Decode_WhitespaceBetweenDigits_IsIgnored()
    {
        var decoded = AsciiHexFilter.Decode("48 65\n6C\t6C 6F\r>"u8, PdfOptions.Default, null, null);

        Assert.Equal("Hello"u8.ToArray(), decoded);
    }

    [Fact]
    public void Decode_LowercaseDigits_DecodeTheSameAsUppercase()
    {
        var upper = AsciiHexFilter.Decode("48656C6C6F>"u8, PdfOptions.Default, null, null);
        var lower = AsciiHexFilter.Decode("48656c6c6f>"u8, PdfOptions.Default, null, null);

        Assert.Equal(upper, lower);
    }

    [Fact]
    public void Decode_OddTrailingDigit_IsPaddedWithZeroNibble_NoDiagnostic()
    {
        var diagnostics = new DiagnosticCollection();

        // A single trailing hex digit '4' before EOD is completed as 0x40, per §7.4.2 -
        // defined behavior, not a deviation.
        var decoded = AsciiHexFilter.Decode("4>"u8, PdfOptions.Default, diagnostics, null);

        Assert.Equal([0x40], decoded);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Decode_InvalidCharacter_RecordsDiagnosticAndSkipsIt()
    {
        var diagnostics = new DiagnosticCollection();

        var decoded = AsciiHexFilter.Decode("41!42>"u8, PdfOptions.Default, diagnostics, null);

        Assert.Equal("AB"u8.ToArray(), decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3020");
    }

    [Fact]
    public void Decode_MissingEodMarker_RecordsDiagnosticAndDecodesAvailableBytes()
    {
        var diagnostics = new DiagnosticCollection();

        var decoded = AsciiHexFilter.Decode("48656C6C6F"u8, PdfOptions.Default, diagnostics, null);

        Assert.Equal("Hello"u8.ToArray(), decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3021");
    }

    [Fact]
    public void Decode_InvalidCharacter_Strict_Throws()
    {
        var strict = PdfOptions.Default with { Strict = true };

        var ex = Assert.Throws<PlumePdfException>(() => AsciiHexFilter.Decode("41!42>"u8, strict, null, null));
        Assert.Equal("PLUME3020", ex.Code);
    }

    [Fact]
    public void Decode_MissingEodMarker_Strict_Throws()
    {
        var strict = PdfOptions.Default with { Strict = true };

        var ex = Assert.Throws<PlumePdfException>(() => AsciiHexFilter.Decode("48656C6C6F"u8, strict, null, null));
        Assert.Equal("PLUME3021", ex.Code);
    }

    [Fact]
    public void Registry_Default_DecodesUnderFullAndAbbreviatedNames()
    {
        var full = new PdfDictionary();
        full.Set(PdfName.Filter, PdfName.Get("ASCIIHexDecode"));
        var abbreviated = new PdfDictionary();
        abbreviated.Set(PdfName.Filter, PdfName.Get("AHx"));

        var decodedFull = PdfFilterRegistry.Default.Decode(full, "48656C6C6F>"u8, PdfOptions.Default);
        var decodedAbbreviated = PdfFilterRegistry.Default.Decode(abbreviated, "48656C6C6F>"u8, PdfOptions.Default);

        Assert.Equal("Hello"u8.ToArray(), decodedFull);
        Assert.Equal("Hello"u8.ToArray(), decodedAbbreviated);
    }

    [Fact]
    public void Open_PdfJsAsciiHexDecodeFixture_OpensCleanly()
    {
        if (!FilterCorpusFixtures.SkipUnlessAsciiHexSampleAvailable(out var path))
        {
            return;
        }

        using var document = PdfDocument.Open(path);

        Assert.NotNull(document);
    }
}

/// <summary>
/// <see cref="Ascii85Filter"/> (ISO 32000-1 §7.4.3). Round-trip cases encode
/// with a small reference encoder written for this test file (rather than a hand-copied
/// spec example vector) so the fixtures aren't tied to any one canonical example string.
/// </summary>
public class Ascii85FilterTests
{
    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("AB")]
    [InlineData("ABC")]
    [InlineData("ABCD")]
    [InlineData("The quick brown fox jumps over the lazy dog.")]
    public void Decode_RoundTripsThroughReferenceEncoder(string text)
    {
        var original = Encoding.ASCII.GetBytes(text);
        var encoded = EncodeAscii85(original);

        var decoded = Ascii85Filter.Decode(encoded, PdfOptions.Default, null, null);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_BinaryPayload_RoundTrips()
    {
        var original = new byte[257];
        new Random(7).NextBytes(original);
        var encoded = EncodeAscii85(original);

        var decoded = Ascii85Filter.Decode(encoded, PdfOptions.Default, null, null);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_ZShortcut_DecodesToFourZeroBytes()
    {
        var decoded = Ascii85Filter.Decode("z~>"u8, PdfOptions.Default, null, null);

        Assert.Equal(new byte[4], decoded);
    }

    [Fact]
    public void Decode_ZShortcut_MidGroup_RecordsDiagnosticAndIgnoresIt()
    {
        var diagnostics = new DiagnosticCollection();

        // '!' starts a group (1 digit in), then 'z' appears mid-group - invalid per §7.4.3
        // ('z' is only a shortcut for a *whole* zero group) - so it's ignored and the group
        // continues to accumulate real digits.
        var decoded = Ascii85Filter.Decode("!z!!!~>"u8, PdfOptions.Default, diagnostics, null);

        Assert.Contains(diagnostics, d => d.Code == "PLUME3031");
        Assert.NotNull(decoded);
    }

    [Fact]
    public void Decode_ByteOutsideDigitRange_RecordsDiagnosticAndSkipsIt()
    {
        var diagnostics = new DiagnosticCollection();
        var original = Encoding.ASCII.GetBytes("AB");
        var encoded = EncodeAscii85(original);
        var withGarbage = new byte[encoded.Length + 1];
        withGarbage[0] = 0x7F; // DEL - outside '!'-'u'
        encoded.CopyTo(withGarbage, 1);

        var decoded = Ascii85Filter.Decode(withGarbage, PdfOptions.Default, diagnostics, null);

        Assert.Equal(original, decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3030");
    }

    [Fact]
    public void Decode_FinalPartialGroupOfOneDigit_RecordsDiagnosticAndDiscardsIt()
    {
        var diagnostics = new DiagnosticCollection();

        var decoded = Ascii85Filter.Decode("!~>"u8, PdfOptions.Default, diagnostics, null);

        Assert.Empty(decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3032");
    }

    [Fact]
    public void Decode_MissingEodMarker_RecordsDiagnosticAndDecodesAvailableBytes()
    {
        var diagnostics = new DiagnosticCollection();
        var encoded = EncodeAscii85(Encoding.ASCII.GetBytes("AB"));
        var withoutEod = encoded[..^2]; // strip the trailing "~>"

        var decoded = Ascii85Filter.Decode(withoutEod, PdfOptions.Default, diagnostics, null);

        Assert.Equal("AB"u8.ToArray(), decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3033");
    }

    [Fact]
    public void Decode_MissingEodMarker_Strict_Throws()
    {
        var strict = PdfOptions.Default with { Strict = true };
        var encoded = EncodeAscii85(Encoding.ASCII.GetBytes("AB"));
        var withoutEod = encoded[..^2];

        var ex = Assert.Throws<PlumePdfException>(() => Ascii85Filter.Decode(withoutEod, strict, null, null));
        Assert.Equal("PLUME3033", ex.Code);
    }

    [Fact]
    public void Decode_FinalPartialGroupOfOneDigit_Strict_Throws()
    {
        var strict = PdfOptions.Default with { Strict = true };

        var ex = Assert.Throws<PlumePdfException>(() => Ascii85Filter.Decode("!~>"u8, strict, null, null));
        Assert.Equal("PLUME3032", ex.Code);
    }

    [Fact]
    public void Registry_Default_DecodesUnderFullAndAbbreviatedNames()
    {
        var encoded = EncodeAscii85(Encoding.ASCII.GetBytes("Hello"));
        var full = new PdfDictionary();
        full.Set(PdfName.Filter, PdfName.Get("ASCII85Decode"));
        var abbreviated = new PdfDictionary();
        abbreviated.Set(PdfName.Filter, PdfName.Get("A85"));

        var decodedFull = PdfFilterRegistry.Default.Decode(full, encoded, PdfOptions.Default);
        var decodedAbbreviated = PdfFilterRegistry.Default.Decode(abbreviated, encoded, PdfOptions.Default);

        Assert.Equal("Hello"u8.ToArray(), decodedFull);
        Assert.Equal("Hello"u8.ToArray(), decodedAbbreviated);
    }

    /// <summary>A minimal reference ASCII85 encoder used only to build round-trip fixtures for the decoder under test.</summary>
    private static byte[] EncodeAscii85(byte[] data)
    {
        var text = new StringBuilder();
        var i = 0;
        while (i + 4 <= data.Length)
        {
            AppendGroup(text, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i, 4)), 5);
            i += 4;
        }

        var remaining = data.Length - i;
        if (remaining > 0)
        {
            var padded = new byte[4];
            Array.Copy(data, i, padded, 0, remaining);
            AppendGroup(text, BinaryPrimitives.ReadUInt32BigEndian(padded), remaining + 1);
        }

        text.Append("~>");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static void AppendGroup(StringBuilder text, uint value, int charCount)
    {
        if (charCount == 5 && value == 0)
        {
            text.Append('z');
            return;
        }

        Span<char> chars = stackalloc char[5];
        for (var j = 4; j >= 0; j--)
        {
            chars[j] = (char)('!' + (value % 85));
            value /= 85;
        }

        text.Append(chars[..charCount]);
    }
}

/// <summary>
/// Locates the pinned <c>corpora/pdfjs-subset-0adc2e7c6652.../asciihexdecode.pdf</c> fixture
/// fetched by <c>scripts/fetch-corpora.sh</c> (gitignored, never committed). Mirrors
/// <c>Fonts/SfntReaderTests.cs</c>'s <c>FontFixtures</c> self-skip shape — a skipped lane
/// must be distinguishable from a passing one — rather than reusing that type directly,
/// since it lives in a different test namespace.
/// </summary>
internal static class FilterCorpusFixtures
{
    private const string SolutionFileName = "PlumePdf.sln";

    public static bool SkipUnlessAsciiHexSampleAvailable(out string path)
    {
        var repoRoot = FindRepoRoot();
        var corporaRoot = Path.Combine(repoRoot, "corpora");
        path = string.Empty;

        if (!Directory.Exists(corporaRoot))
        {
            Report(corporaRoot);
            return false;
        }

        var match = Directory.EnumerateFiles(corporaRoot, "asciihexdecode.pdf", SearchOption.AllDirectories).FirstOrDefault();
        if (match is null)
        {
            Report(corporaRoot);
            return false;
        }

        path = match;
        return true;
    }

    private static void Report(string corporaRoot)
    {
        var message = $"SKIPPED (corpus not fetched — run scripts/fetch-corpora.sh to populate {corporaRoot})";
        Trace.WriteLine(message);
        Console.WriteLine(message);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate the repository root ({SolutionFileName}) above {AppContext.BaseDirectory}.");
    }

}

/// <summary>Review-finding regressions: per-byte diagnostic amplification on hostile input.</summary>
public class AsciiFilterAmplificationTests
{
    [Fact]
    public void AsciiHex_ManyInvalidBytes_ReportsFirstOccurrencePlusSummaryOnly()
    {
        // Guards against one interpolated diagnostic PER garbage input byte (~340x memory
        // amplification on a 1 MiB hostile payload). Now: first occurrence + one summary.
        var data = new byte[1000];
        Array.Fill(data, (byte)'Z'); // 'Z' is not a hex digit
        var diagnostics = new DiagnosticCollection();

        AsciiHexFilter.Decode(data, PdfOptions.Default, diagnostics, null);

        Assert.True(diagnostics.Count(d => d.Code == "PLUME3020") <= 2,
            $"expected at most first-occurrence + summary, got {diagnostics.Count(d => d.Code == "PLUME3020")}");
    }

    [Fact]
    public void Ascii85_ManyInvalidBytes_ReportsFirstOccurrencePlusSummaryOnly()
    {
        var data = new byte[1000];
        Array.Fill(data, (byte)0x7F); // outside '!'..'u'
        var diagnostics = new DiagnosticCollection();

        Ascii85Filter.Decode(data, PdfOptions.Default, diagnostics, null);

        Assert.True(diagnostics.Count(d => d.Code == "PLUME3030") <= 2,
            $"expected at most first-occurrence + summary, got {diagnostics.Count(d => d.Code == "PLUME3030")}");
    }
}
