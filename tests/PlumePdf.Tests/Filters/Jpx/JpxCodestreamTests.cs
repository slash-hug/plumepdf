using System.Text.Json;
using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// <see cref="JpxCodestream.Parse"/> — marker precedence and
/// placement, tile-part assembly, one hand-patched/hand-built header per refusal/deviation code,
/// a <c>COC</c>-reads-the-shorter-layout regression, a <c>QCC</c>-over-<c>QCD</c> precedence
/// check, and positive parses of every committed fixture's main header cross-checked against
/// <c>MANIFEST.json</c>.
/// </summary>
public class JpxCodestreamTests
{
    private static readonly string[] NonPositiveFixtures =
    [
        "rgn-refusal.j2k", "ppm-refusal.j2k", "part2-refusal.j2k", "precision17-refusal.j2k", "truncated.j2k",
    ];

    private static string FixturesDir => Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "jpx");

    // ================= committed-fixture positive parses (MANIFEST.json cross-check) =================

    public static IEnumerable<object[]> PositiveFixtureNames()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesDir, "MANIFEST.json")));
        foreach (var fixture in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            var file = fixture.GetProperty("file").GetString()!;
            if (NonPositiveFixtures.Contains(file))
            {
                continue;
            }

            // Fixtures whose assert block carries only JP2-box/colour facts (no sotCount) are
            // Jp2Boxes/colour-handling territory -- out of this file's scope.
            if (!fixture.GetProperty("assert").TryGetProperty("sotCount", out _))
            {
                continue;
            }

            yield return [file];
        }
    }

    [Theory]
    [MemberData(nameof(PositiveFixtureNames))]
    public void Parse_CommittedFixture_MatchesManifestAssertions(string fileName)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesDir, "MANIFEST.json")));
        var entry = manifest.RootElement.GetProperty("fixtures").EnumerateArray().First(f => f.GetProperty("file").GetString() == fileName);
        var assert = entry.GetProperty("assert");

        var fileBytes = File.ReadAllBytes(Path.Combine(FixturesDir, fileName));
        var codestream = LocateCodestream(fileBytes);

        var header = JpxCodestream.Parse(codestream, PdfOptions.Default, null);

        Assert.Equal(assert.GetProperty("sotCount").GetInt32(), header.TileParts.Count);

        var precisions = assert.GetProperty("componentsPrecision").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var signed = assert.GetProperty("componentsSigned").EnumerateArray().Select(e => e.GetBoolean()).ToArray();
        var xrsiz = assert.GetProperty("componentsXRsiz").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var yrsiz = assert.GetProperty("componentsYRsiz").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(precisions.Length, header.Siz.Components.Length);
        for (var c = 0; c < precisions.Length; c++)
        {
            Assert.Equal(precisions[c], header.Siz.Components[c].Precision);
            Assert.Equal(signed[c], header.Siz.Components[c].Signed);
            Assert.Equal(xrsiz[c], header.Siz.Components[c].XRsiz);
            Assert.Equal(yrsiz[c], header.Siz.Components[c].YRsiz);
        }

        var cblkStyle = assert.GetProperty("cblkStyle").GetInt32();
        var reversible = assert.GetProperty("transform").GetString() == "5-3";
        var ppxAtR0 = assert.GetProperty("ppxAtR0").GetInt32();
        var ppyAtR0 = assert.GetProperty("ppyAtR0").GetInt32();
        var numLayers = assert.GetProperty("numLayers").GetInt32();
        var progressionOrder = assert.GetProperty("progressionOrder").GetInt32();
        var mct = assert.GetProperty("mct").GetInt32() == 1;
        foreach (var coding in header.Coding)
        {
            Assert.Equal(cblkStyle, coding.CodeBlockStyle);
            Assert.Equal(reversible, coding.Reversible53);
            Assert.Equal(ppxAtR0, coding.PrecinctExponentsX[0]);
            Assert.Equal(ppyAtR0, coding.PrecinctExponentsY[0]);
            Assert.Equal(numLayers, coding.Layers);
            Assert.Equal(progressionOrder, (int)coding.Progression);
            Assert.Equal(mct, coding.Mct);
        }

        var sqcdStyle = assert.GetProperty("sqcdStyle").GetInt32();
        Assert.All(header.Quant, quant => Assert.Equal(sqcdStyle, quant.Style));

        var pocPresent = assert.GetProperty("markers").GetProperty("POC").GetBoolean();
        if (pocPresent)
        {
            var hasMainPoc = header.MainPoc.Length > 0;
            var hasTilePoc = header.TileParts.Count > 0 && header.TileParts[0].Poc is { Length: > 0 };
            Assert.True(hasMainPoc || hasTilePoc, $"{fileName}: markers.POC is true but no POC volumes were parsed anywhere.");
            if (assert.TryGetProperty("pocEntryCountInTile0Tp0", out var pocCount))
            {
                Assert.Equal(pocCount.GetInt32(), header.TileParts[0].Poc!.Length);
            }
        }
        else
        {
            Assert.Empty(header.MainPoc);
        }
    }

    /// <summary>Minimal jp2c box locator (Annex I) — a raw codestream is extracted here just for parsing its own header, independent of <c>Jp2Boxes</c>' own box walk.</summary>
    private static ReadOnlyMemory<byte> LocateCodestream(byte[] file)
    {
        if (file.Length >= 2 && file[0] == 0xFF && file[1] == 0x4F)
        {
            return file; // already a raw .j2k codestream (starts at SOC)
        }

        var position = 0;
        while (position + 8 <= file.Length)
        {
            var lbox = ((uint)file[position] << 24) | ((uint)file[position + 1] << 16) | ((uint)file[position + 2] << 8) | file[position + 3];
            var tbox = (file[position + 4] << 24) | (file[position + 5] << 16) | (file[position + 6] << 8) | file[position + 7];
            var headerSize = 8;
            long boxLength = lbox;
            if (lbox == 1)
            {
                boxLength = 0;
                for (var i = 0; i < 8; i++)
                {
                    boxLength = (boxLength << 8) | file[position + 8 + i];
                }

                headerSize = 16;
            }
            else if (lbox == 0)
            {
                boxLength = file.Length - position;
            }

            const int Jp2cTag = 0x6A703263; // 'jp2c'
            if (tbox == Jp2cTag)
            {
                var contentStart = position + headerSize;
                var contentLength = (int)(boxLength - headerSize);
                return new ReadOnlyMemory<byte>(file, contentStart, contentLength);
            }

            position += (int)boxLength;
        }

        throw new InvalidOperationException("No jp2c box found.");
    }

    // ================= committed refusal/deviation fixtures =================

    [Fact]
    public void Parse_Part2Codestream_ThrowsPart2()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir, "part2-refusal.j2k"));

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.Part2, ex.Code);
    }

    [Fact]
    public void Parse_RgnPresent_ThrowsRoiUnsupported()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir, "rgn-refusal.j2k"));

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.RoiUnsupported, ex.Code);
    }

    [Fact]
    public void Parse_PpmPresent_ThrowsPackedHeadersUnsupported()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir, "ppm-refusal.j2k"));

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.PackedHeadersUnsupported, ex.Code);
    }

    [Fact]
    public void Parse_Precision17_ThrowsPrecisionUnsupported()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir, "precision17-refusal.j2k"));

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.PrecisionUnsupported, ex.Code);
    }

    [Fact]
    public void Parse_TruncatedCodestream_ReportsDeviationAndKeepsCompleteTileParts()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir, "truncated.j2k"));
        var diagnostics = new DiagnosticCollection();

        var header = JpxCodestream.Parse(bytes, PdfOptions.Default, diagnostics);

        Assert.Single(header.TileParts); // manifest: truncatedAfterTileIndex = 0, originalSotCount = 6
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.Truncated);
    }

    [Fact]
    public void Parse_TruncatedCodestream_UnderStrict_Throws()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir, "truncated.j2k"));

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default with { Strict = true }, null));

        Assert.Equal(JpxDiagnosticCodes.Truncated, ex.Code);
    }

    // ================= hand-built headers: the remaining refusal/deviation codes =================

    [Fact]
    public void Parse_SizOriginAtImageSize_ThrowsGeometryInvalid()
    {
        var bytes = new TestCodestream()
            .Siz(xsiz: 64, ysiz: 48, xosiz: 64, yosiz: 0, xtsiz: 64, ytsiz: 48, xtosiz: 0, ytosiz: 0, (8, false, 1, 1))
            .Cod(scod: 0, progression: 0, layers: 1, mct: 0, nl: 2, xcbVal: 4, ycbVal: 4, style: 0, wavelet: 1)
            .Qcd(sqcd: 0x00, steps: [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 1)
            .Build();

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.GeometryInvalid, ex.Code);
    }

    [Fact]
    public void Parse_TileGridNotCoveringOrigin_ThrowsGeometryInvalid()
    {
        // XTOsiz (40) > XOsiz (0): the tile grid's own origin starts past the image origin.
        var bytes = new TestCodestream()
            .Siz(xsiz: 64, ysiz: 48, xosiz: 0, yosiz: 0, xtsiz: 64, ytsiz: 48, xtosiz: 40, ytosiz: 0, (8, false, 1, 1))
            .Cod(0, 0, 1, 0, 2, 4, 4, 0, 1)
            .Qcd(0x00, [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 1)
            .Build();

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.GeometryInvalid, ex.Code);
    }

    [Fact]
    public void Parse_SizZeroComponents_ThrowsGeometryInvalid()
    {
        // A regression test for the bug that SIZ Csiz == 0 reached
        // JpxPacketDecoder.DecodeTilePackets' unconditional tile.Components[0] index as a bare
        // IndexOutOfRangeException instead of a coded, recoverable-vs-not decision at the point
        // the malformed value is actually read.
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0)
            .Build();

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.GeometryInvalid, ex.Code);
    }

    [Fact]
    public void Parse_QcdWithNoStepData_ThrowsHeaderInvalid()
    {
        // A regression test for the bug that a QCD/QCC whose segment length is
        // consumed entirely by Sqcd (no SPqcd bytes at all) left quant.Steps empty, which
        // JpxGeometry.SubbandQuantisation's Math.Clamp(0, 0, quant.Steps.Length - 1) (style 0/2)
        // or quant.Steps[0] (style 1) then indexed as a bare exception.
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(0, 0, 1, 0, 2, 4, 4, 0, 1)
            .Qcd(sqcd: 0x00, steps: [])
            .TilePart(0, 0, 1)
            .Build();

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.HeaderInvalid, ex.Code);
    }

    [Fact]
    public void Parse_PsotZero_TilePartRunsToEocWithoutSpuriousTruncation()
    {
        // A.4.2: Psot = 0 legitimately means "this tile-part runs to the terminating EOC marker".
        // Before the fix, the tile-part's Data slice swallowed the EOC's own 2 bytes AND left the
        // parser position sitting past the EOC, so the next "expect an SOT or EOC marker" read
        // found nothing and reported a spurious PLUME3701 truncation on this perfectly
        // conformant, single-tile-part stream.
        var diagnostics = new DiagnosticCollection();
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(0, 0, 1, 0, 2, 4, 4, 0, 1)
            .Qcd(0x00, [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 1, psotOverride: 0)
            .Build();

        var header = JpxCodestream.Parse(bytes, PdfOptions.Default, diagnostics);

        Assert.Single(header.TileParts);
        Assert.Equal(4, header.TileParts[0].Data.Length); // the tile-part body's own 4 bytes, not the trailing EOC
        Assert.DoesNotContain(diagnostics, d => d.Code == JpxDiagnosticCodes.Truncated);
    }

    [Fact]
    public void Parse_CodMissingFromMainHeader_ThrowsHeaderInvalid()
    {
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Qcd(0x00, [0x08, 0x08, 0x08])
            .TilePart(0, 0, 1)
            .Build();

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.HeaderInvalid, ex.Code);
    }

    [Fact]
    public void Parse_CodInNonFirstTilePart_ThrowsHeaderInvalid()
    {
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(0, 0, 1, 0, 2, 4, 4, 0, 1)
            .Qcd(0x00, [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 2) // TPsot=0, TNsot=2 -- a legitimate first part
            .TilePart(0, 1, 2) // TPsot=1 -- a COD here is illegal (main/first-tile-part only)
            .Cod(0, 0, 1, 0, 2, 4, 4, 0, 1, inTileHeader: true)
            .Build();

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.HeaderInvalid, ex.Code);
    }

    [Fact]
    public void Parse_UnsupportedWaveletValue_ThrowsCodingStyleUnsupported()
    {
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(0, 0, 1, 0, 2, 4, 4, 0, wavelet: 2) // only 0 (9/7) and 1 (5/3) are legal
            .Qcd(0x00, [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 1)
            .Build();

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.CodingStyleUnsupported, ex.Code);
    }

    [Fact]
    public void Parse_DecompositionLevelsAbove32_ThrowsCodingStyleUnsupported()
    {
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(0, 0, 1, 0, nl: 33, 4, 4, 0, 1) // Table A.15: N_L is 0..32
            .Qcd(0x00, [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 1)
            .Build();

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.CodingStyleUnsupported, ex.Code);
    }

    [Fact]
    public void Parse_UnsupportedQuantizationStyle_ThrowsCodingStyleUnsupported()
    {
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(0, 0, 1, 0, 2, 4, 4, 0, 1)
            .Qcd(sqcd: 0x03, steps: []) // low 5 bits = 3: only 0/1/2 are legal styles
            .TilePart(0, 0, 1)
            .Build();

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.CodingStyleUnsupported, ex.Code);
    }

    [Fact]
    public void Parse_IllegalCodeBlockSize_ThrowsCodeBlockSizeInvalid()
    {
        // xcbVal=9 -> xcb=11 > 10.
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(0, 0, 1, 0, 2, xcbVal: 9, ycbVal: 0, style: 0, wavelet: 1)
            .Qcd(0x00, [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 1)
            .Build();

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.CodeBlockSizeInvalid, ex.Code);
    }

    [Fact]
    public void Parse_CodeBlockExponentSumTooLarge_ThrowsCodeBlockSizeInvalid()
    {
        // xcbVal=8, ycbVal=1 -> xcb=10, ycb=3, sum=13 > 12.
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(0, 0, 1, 0, 2, xcbVal: 8, ycbVal: 1, style: 0, wavelet: 1)
            .Qcd(0x00, [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 1)
            .Build();

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.CodeBlockSizeInvalid, ex.Code);
    }

    [Fact]
    public void Parse_MalformedPoc_ReportsDeviationAndUsesCodProgression()
    {
        // A POC segment whose payload length (6 bytes) is not a multiple of the 7-byte entry size
        // (Csiz=1 -> component fields are 1 byte: 1+1+2+1+1+1 = 7) -- malformed, ignored.
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(0, progression: 2, layers: 1, mct: 0, nl: 2, xcbVal: 4, ycbVal: 4, style: 0, wavelet: 1)
            .RawSegment(0xFF5F, [0, 0, 0, 1, 0, 0])
            .Qcd(0x00, [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 1)
            .Build();
        var diagnostics = new DiagnosticCollection();

        var header = JpxCodestream.Parse(bytes, PdfOptions.Default, diagnostics);

        Assert.Empty(header.MainPoc);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.PocInvalid);
        Assert.Equal(JpxProgression.Rpcl, header.Coding[0].Progression); // the COD progression still applies
    }

    [Fact]
    public void Parse_TilePartSequenceOutOfOrder_ReportsDeviationAndKeepsBothParts()
    {
        // Tile 0's tile-parts arrive as TPsot=0 then TPsot=2 (skipping 1) -- broken sequencing,
        // but both parts individually parse fine and are kept.
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(0, 0, 1, 0, 2, 4, 4, 0, 1)
            .Qcd(0x00, [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 3)
            .TilePart(0, 2, 3)
            .Build();
        var diagnostics = new DiagnosticCollection();

        var header = JpxCodestream.Parse(bytes, PdfOptions.Default, diagnostics);

        Assert.Equal(2, header.TileParts.Count);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.TilePartSequence);
    }

    // ================= COC / QCC precedence =================

    [Fact]
    public void Parse_Coc_ReadsShorterComponentScopedLayout_NotCodsSgcod()
    {
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1), (8, false, 1, 1))
            .Cod(scod: 0, progression: 0, layers: 5, mct: 0, nl: 2, xcbVal: 4, ycbVal: 4, style: 0, wavelet: 1)
            .Coc(component: 1, scoc: 0, nl: 1, xcbVal: 2, ycbVal: 2, style: 0x01, wavelet: 0)
            .Qcd(0x40, [0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40])
            .TilePart(0, 0, 1)
            .Build();

        var header = JpxCodestream.Parse(bytes, PdfOptions.Default, null);

        Assert.Equal(2, header.Coding.Length);

        // Component 0 uses COD directly.
        Assert.Equal(2, header.Coding[0].DecompositionLevels);
        Assert.Equal(6, header.Coding[0].Xcb);
        Assert.True(header.Coding[0].Reversible53);

        // Component 1: COC overrides N_L/xcb/ycb/style/wavelet (its own SPcoc)...
        Assert.Equal(1, header.Coding[1].DecompositionLevels);
        Assert.Equal(4, header.Coding[1].Xcb);
        Assert.Equal(4, header.Coding[1].Ycb);
        Assert.Equal(0x01, header.Coding[1].CodeBlockStyle);
        Assert.False(header.Coding[1].Reversible53); // wavelet=0 -> 9/7, unlike COD's 5/3

        // ...but COC has no SGcod at all, so progression/layers/mct/sop/eph must come from COD,
        // proving the parser read the shorter component-scoped layout and not COD's fields.
        Assert.Equal(header.Coding[0].Progression, header.Coding[1].Progression);
        Assert.Equal(header.Coding[0].Layers, header.Coding[1].Layers);
        Assert.Equal(header.Coding[0].Mct, header.Coding[1].Mct);
        Assert.Equal(5, header.Coding[1].Layers);
    }

    [Fact]
    public void Parse_Qcc_OverridesQcdForItsOwnComponentOnly()
    {
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1), (8, false, 1, 1))
            .Cod(0, 0, 1, 0, 2, 4, 4, 0, 1)
            .Qcd(sqcd: (1 << 5) | 0, steps: [0x08, 0x10, 0x18]) // style 0, guard bits 1
            .Qcc(component: 1, sqcc: (3 << 5) | 1, steps: [0x48, 0x64]) // style 1, guard bits 3, one (exponent=9, mantissa=100) step
            .TilePart(0, 0, 1)
            .Build();

        var header = JpxCodestream.Parse(bytes, PdfOptions.Default, null);

        Assert.Equal(0, header.Quant[0].Style);
        Assert.Equal(1, header.Quant[0].GuardBits);

        Assert.Equal(1, header.Quant[1].Style);
        Assert.Equal(3, header.Quant[1].GuardBits);
        var step = Assert.Single(header.Quant[1].Steps);
        Assert.Equal(9, step.Exponent);
        Assert.Equal(100, step.Mantissa);
    }

    // ================= misc parser behaviour =================

    [Fact]
    public void Parse_NotAJpeg2000Stream_ThrowsNotJpeg2000()
    {
        byte[] bytes = [0x25, 0x50, 0x44, 0x46]; // "%PDF"

        var ex = Assert.Throws<PlumePdfException>(() => JpxCodestream.Parse(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.NotJpeg2000, ex.Code);
    }

    [Fact]
    public void Parse_DefaultPrecinctsWhenScodBitClear_AreFifteen()
    {
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(scod: 0, progression: 0, layers: 1, mct: 0, nl: 3, xcbVal: 4, ycbVal: 4, style: 0, wavelet: 1)
            .Qcd(0x00, [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 1)
            .Build();

        var header = JpxCodestream.Parse(bytes, PdfOptions.Default, null);

        Assert.All(header.Coding[0].PrecinctExponentsX, p => Assert.Equal(15, p));
        Assert.All(header.Coding[0].PrecinctExponentsY, p => Assert.Equal(15, p));
    }

    [Fact]
    public void Parse_ExplicitPrecinctSizes_AreReadPerResolution()
    {
        // Scod bit 0 set -> explicit precinct sizes follow SPcod, one byte per resolution
        // (N_L + 1 = 3): low nibble PPx, high nibble PPy.
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(scod: 0x01, progression: 0, layers: 1, mct: 0, nl: 2, xcbVal: 4, ycbVal: 4, style: 0, wavelet: 1, precincts: [0x21, 0x33, 0x44])
            .Qcd(0x00, [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 1)
            .Build();

        var header = JpxCodestream.Parse(bytes, PdfOptions.Default, null);

        Assert.Equal([1, 3, 4], header.Coding[0].PrecinctExponentsX);
        Assert.Equal([2, 3, 4], header.Coding[0].PrecinctExponentsY);
    }

    [Fact]
    public void Parse_EmptyRegistryFilters_AreSkippedByLength()
    {
        // A COM (comment) segment between COD and QCD must be skipped without disturbing anything else.
        var bytes = new TestCodestream()
            .Siz(64, 48, 0, 0, 64, 48, 0, 0, (8, false, 1, 1))
            .Cod(0, 0, 1, 0, 2, 4, 4, 0, 1)
            .RawSegment(0xFF64, [0x00, 0x00, (byte)'h', (byte)'i'])
            .Qcd(0x00, [0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08])
            .TilePart(0, 0, 1)
            .Build();

        var header = JpxCodestream.Parse(bytes, PdfOptions.Default, null);

        Assert.Single(header.TileParts);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlumePdf.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// A minimal, from-scratch J2K codestream builder for hand-built parser tests: computes every
    /// marker segment's own length and every tile-part's <c>Psot</c> automatically, so a test only
    /// states the field it cares about testing.
    /// </summary>
    private sealed class TestCodestream
    {
        private sealed class TilePartSpec
        {
            public int TileIndex;
            public int TPsot;
            public int TNsot;
            public int? PsotOverride;
            public readonly List<byte> Header = [];
            public byte[] Body = [0xAA, 0xBB, 0xCC, 0xDD];
        }

        private readonly List<byte> _mainHeader = [];
        private readonly List<TilePartSpec> _tileParts = [];

        public TestCodestream Siz(int xsiz, int ysiz, int xosiz, int yosiz, int xtsiz, int ytsiz, int xtosiz, int ytosiz, params (int Precision, bool Signed, int XR, int YR)[] components) =>
            Siz(xsiz, ysiz, xosiz, yosiz, xtsiz, ytsiz, xtosiz, ytosiz, rsiz: 0, components);

        public TestCodestream Siz(int xsiz, int ysiz, int xosiz, int yosiz, int xtsiz, int ytsiz, int xtosiz, int ytosiz, ushort rsiz, params (int Precision, bool Signed, int XR, int YR)[] components)
        {
            var payload = new List<byte>();
            AddU16(payload, rsiz);
            AddU32(payload, (uint)xsiz);
            AddU32(payload, (uint)ysiz);
            AddU32(payload, (uint)xosiz);
            AddU32(payload, (uint)yosiz);
            AddU32(payload, (uint)xtsiz);
            AddU32(payload, (uint)ytsiz);
            AddU32(payload, (uint)xtosiz);
            AddU32(payload, (uint)ytosiz);
            AddU16(payload, (ushort)components.Length);
            foreach (var c in components)
            {
                payload.Add((byte)(((c.Signed ? 1 : 0) << 7) | (c.Precision - 1)));
                payload.Add((byte)c.XR);
                payload.Add((byte)c.YR);
            }

            WriteSegment(_mainHeader, 0xFF51, payload);
            return this;
        }

        public TestCodestream Cod(byte scod, byte progression, ushort layers, byte mct, byte nl, byte xcbVal, byte ycbVal, byte style, byte wavelet, byte[]? precincts = null, bool inTileHeader = false)
        {
            var payload = new List<byte> { scod, progression };
            AddU16(payload, layers);
            payload.Add(mct);
            AddSPcod(payload, nl, xcbVal, ycbVal, style, wavelet, precincts);
            WriteSegment(Target(inTileHeader), 0xFF52, payload);
            return this;
        }

        public TestCodestream Coc(int component, byte scoc, byte nl, byte xcbVal, byte ycbVal, byte style, byte wavelet, byte[]? precincts = null, bool inTileHeader = false, int componentFieldSize = 1)
        {
            var payload = new List<byte>();
            AddComponentField(payload, component, componentFieldSize);
            payload.Add(scoc);
            AddSPcod(payload, nl, xcbVal, ycbVal, style, wavelet, precincts);
            WriteSegment(Target(inTileHeader), 0xFF53, payload);
            return this;
        }

        public TestCodestream Qcd(byte sqcd, byte[] steps, bool inTileHeader = false)
        {
            var payload = new List<byte> { sqcd };
            payload.AddRange(steps);
            WriteSegment(Target(inTileHeader), 0xFF5C, payload);
            return this;
        }

        public TestCodestream Qcc(int component, int sqcc, byte[] steps, bool inTileHeader = false, int componentFieldSize = 1)
        {
            var payload = new List<byte>();
            AddComponentField(payload, component, componentFieldSize);
            payload.Add((byte)sqcc);
            payload.AddRange(steps);
            WriteSegment(Target(inTileHeader), 0xFF5D, payload);
            return this;
        }

        public TestCodestream RawSegment(ushort marker, byte[] payload, bool inTileHeader = false)
        {
            WriteSegment(Target(inTileHeader), marker, [.. payload]);
            return this;
        }

        public TestCodestream TilePart(int tileIndex, int tpsot, int tnsot, int? psotOverride = null)
        {
            _tileParts.Add(new TilePartSpec { TileIndex = tileIndex, TPsot = tpsot, TNsot = tnsot, PsotOverride = psotOverride });
            return this;
        }

        public byte[] Build()
        {
            var result = new List<byte> { 0xFF, 0x4F }; // SOC
            result.AddRange(_mainHeader);
            foreach (var tp in _tileParts)
            {
                var tilePartTail = new List<byte>(tp.Header);
                tilePartTail.Add(0xFF);
                tilePartTail.Add(0x93); // SOD
                tilePartTail.AddRange(tp.Body);

                var sotPayload = new List<byte>();
                AddU16(sotPayload, (ushort)tp.TileIndex);
                var psot = tp.PsotOverride ?? 12 + tilePartTail.Count; // SOT segment itself (marker+Lsot+8-byte payload) is always 12 bytes
                AddU32(sotPayload, (uint)psot);
                sotPayload.Add((byte)tp.TPsot);
                sotPayload.Add((byte)tp.TNsot);
                WriteSegment(result, 0xFF90, sotPayload);
                result.AddRange(tilePartTail);
            }

            result.Add(0xFF);
            result.Add(0xD9); // EOC
            return [.. result];
        }

        private List<byte> Target(bool inTileHeader) => inTileHeader ? _tileParts[^1].Header : _mainHeader;

        private static void AddSPcod(List<byte> payload, byte nl, byte xcbVal, byte ycbVal, byte style, byte wavelet, byte[]? precincts)
        {
            payload.Add(nl);
            payload.Add(xcbVal);
            payload.Add(ycbVal);
            payload.Add(style);
            payload.Add(wavelet);
            if (precincts is not null)
            {
                payload.AddRange(precincts);
            }
        }

        private static void AddComponentField(List<byte> payload, int component, int fieldSize)
        {
            if (fieldSize == 1)
            {
                payload.Add((byte)component);
            }
            else
            {
                AddU16(payload, (ushort)component);
            }
        }

        private static void WriteSegment(List<byte> output, ushort marker, List<byte> payload)
        {
            output.Add((byte)(marker >> 8));
            output.Add((byte)marker);
            var length = payload.Count + 2;
            output.Add((byte)(length >> 8));
            output.Add((byte)length);
            output.AddRange(payload);
        }

        private static void AddU16(List<byte> list, ushort value)
        {
            list.Add((byte)(value >> 8));
            list.Add((byte)value);
        }

        private static void AddU32(List<byte> list, uint value)
        {
            list.Add((byte)(value >> 24));
            list.Add((byte)(value >> 16));
            list.Add((byte)(value >> 8));
            list.Add((byte)value);
        }
    }
}
