using System.Text;
using PlumePdf.Fonts.Reading;
using Xunit;

namespace PlumePdf.Tests.Fonts.Reading;

/// <summary>
/// Unit tests for <see cref="CMapParser"/>: <c>bfchar</c>/<c>bfrange</c> (including a
/// surrogate-pair UTF-16BE target and the array-target form), codespace-driven multi-byte code
/// lengths, <c>cidrange</c>/<c>cidchar</c>, <c>usecmap</c> recording, and degenerate/hostile
/// input (an entry-count bomb, malformed entries). No corpora needed — every fixture is a
/// hand-written CMap stream body.
/// </summary>
public class CMapParserTests
{
    private static byte[] Bytes(string cmapSource) => Encoding.ASCII.GetBytes(cmapSource);

    [Fact]
    public void BeginBfChar_MapsIndividualCodes()
    {
        var cmap = CMapParser.Parse(Bytes("""
            2 beginbfchar
            <41> <0042>
            <42> <00660066>
            endbfchar
            """), PdfOptions.Default, null);

        Assert.True(cmap.TryGetUnicode(0x41, out var a));
        Assert.Equal("B", a);

        Assert.True(cmap.TryGetUnicode(0x42, out var ligature));
        Assert.Equal("ff", ligature);

        Assert.False(cmap.TryGetUnicode(0x43, out _));
    }

    [Fact]
    public void BeginBfRange_HexTarget_IncrementsAcrossTheRange()
    {
        var cmap = CMapParser.Parse(Bytes("""
            1 beginbfrange
            <0000> <0002> <0041>
            endbfrange
            """), PdfOptions.Default, null);

        Assert.True(cmap.TryGetUnicode(0, out var first));
        Assert.Equal("A", first);
        Assert.True(cmap.TryGetUnicode(1, out var second));
        Assert.Equal("B", second);
        Assert.True(cmap.TryGetUnicode(2, out var third));
        Assert.Equal("C", third);
        Assert.False(cmap.TryGetUnicode(3, out _));
    }

    [Fact]
    public void BeginBfRange_ArrayTarget_MapsEachCodeToItsOwnTarget()
    {
        var cmap = CMapParser.Parse(Bytes("""
            1 beginbfrange
            <0000> <0002> [ <0041> <00660066> <0043> ]
            endbfrange
            """), PdfOptions.Default, null);

        Assert.True(cmap.TryGetUnicode(0, out var first));
        Assert.Equal("A", first);
        Assert.True(cmap.TryGetUnicode(1, out var second));
        Assert.Equal("ff", second);
        Assert.True(cmap.TryGetUnicode(2, out var third));
        Assert.Equal("C", third);
    }

    [Fact]
    public void BeginBfRange_SurrogatePairTarget_IncrementsThroughSupplementaryPlaneCodepoints()
    {
        // <D800DC00> is the UTF-16BE surrogate pair for U+10000; incrementing across a small
        // range must walk U+10000, U+10001, U+10002 - not corrupt the low surrogate.
        var cmap = CMapParser.Parse(Bytes("""
            1 beginbfrange
            <0000> <0002> <D800DC00>
            endbfrange
            """), PdfOptions.Default, null);

        Assert.True(cmap.TryGetUnicode(0, out var first));
        Assert.Equal(0x10000, char.ConvertToUtf32(first, 0));

        Assert.True(cmap.TryGetUnicode(1, out var second));
        Assert.Equal(0x10001, char.ConvertToUtf32(second, 0));

        Assert.True(cmap.TryGetUnicode(2, out var third));
        Assert.Equal(0x10002, char.ConvertToUtf32(third, 0));
    }

    [Fact]
    public void CodespaceRanges_DetermineMultiByteCodeLength()
    {
        var cmap = CMapParser.Parse(Bytes("""
            2 begincodespacerange
            <00> <80>
            <8100> <FFFF>
            endcodespacerange
            """), PdfOptions.Default, null);

        Assert.Equal(1, cmap.GetCodeLength([0x41, 0x00]));
        Assert.Equal(2, cmap.GetCodeLength([0x81, 0x23]));
    }

    [Fact]
    public void BeginCidRange_And_BeginCidChar_ResolveCids()
    {
        var cmap = CMapParser.Parse(Bytes("""
            1 begincidrange
            <0020> <007E> 1
            endcidrange
            1 begincidchar
            <00A0> 500
            endcidchar
            """), PdfOptions.Default, null);

        Assert.True(cmap.TryGetCid(0x20, out var spaceCid));
        Assert.Equal(1, spaceCid);
        Assert.True(cmap.TryGetCid(0x21, out var bangCid));
        Assert.Equal(2, bangCid);
        Assert.True(cmap.TryGetCid(0xA0, out var singleCid));
        Assert.Equal(500, singleCid);
        Assert.False(cmap.TryGetCid(0xFF, out _));
    }

    [Fact]
    public void UseCMap_IsRecordedButNotResolved()
    {
        var cmap = CMapParser.Parse(Bytes("/Identity-H usecmap\n1 begincidchar\n<0000> 0\nendcidchar\n"), PdfOptions.Default, null);

        Assert.Equal("Identity-H", cmap.UseCMapName);
    }

    [Fact]
    public void MalformedEntry_IsSkippedWithDiagnostic_RestOfBlockStillParses()
    {
        var diagnostics = new DiagnosticCollection();

        var cmap = CMapParser.Parse(Bytes("""
            2 beginbfchar
            <41> XYZ
            <42> <0042>
            endbfchar
            """), PdfOptions.Default, diagnostics);

        // The first entry's target is a bare keyword, not a hex string - malformed and
        // skipped, but the second, well-formed entry must still resolve.
        Assert.True(cmap.TryGetUnicode(0x42, out var mapped));
        Assert.Equal("B", mapped);
        Assert.Contains(diagnostics, d => d.Code == "PLUME8016");
    }

    [Fact]
    public void MalformedEntry_UnderStrict_Throws8016()
    {
        var strict = PdfOptions.Default with { Strict = true };

        var ex = Assert.Throws<PlumePdfException>(() => CMapParser.Parse(Bytes("""
            1 beginbfchar
            <41>
            endbfchar
            """), strict, null));

        Assert.Equal("PLUME8016", ex.Code);
    }

    [Fact]
    public void EntryCountBomb_StopsAtTheLimitWithAPartialMapAndDiagnostic()
    {
        var builder = new StringBuilder();
        builder.AppendLine("100 beginbfchar");
        for (var i = 0; i < 100; i++)
        {
            builder.AppendLine($"<{i:X4}> <0041>");
        }

        builder.AppendLine("endbfchar");

        var diagnostics = new DiagnosticCollection();
        var cmap = CMapParser.Parse(Bytes(builder.ToString()), PdfOptions.Default, diagnostics, maxEntries: 10);

        // Only the first 10 entries (declaration order) should have made it in.
        Assert.True(cmap.TryGetUnicode(0x0000, out _));
        Assert.False(cmap.TryGetUnicode(0x0063, out _)); // code 99 (0x63) is well past the cutoff
        Assert.Contains(diagnostics, d => d.Code == "PLUME8015");
    }

    [Fact]
    public void EntryCountBomb_UnderStrict_Throws8015()
    {
        var builder = new StringBuilder();
        builder.AppendLine("20 beginbfchar");
        for (var i = 0; i < 20; i++)
        {
            builder.AppendLine($"<{i:X4}> <0041>");
        }

        builder.AppendLine("endbfchar");

        var strict = PdfOptions.Default with { Strict = true };
        var ex = Assert.Throws<PlumePdfException>(() => CMapParser.Parse(Bytes(builder.ToString()), strict, null, maxEntries: 5));

        Assert.Equal("PLUME8015", ex.Code);
    }

    [Fact]
    public void EmptyContent_ProducesAnEmptyUsableCMap()
    {
        var cmap = CMapParser.Parse([], PdfOptions.Default, null);

        Assert.Empty(cmap.CodespaceRanges);
        Assert.False(cmap.TryGetUnicode(0, out _));
        Assert.False(cmap.TryGetCid(0, out _));
    }
}
