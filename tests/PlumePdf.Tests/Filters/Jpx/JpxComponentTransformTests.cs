using PlumePdf;
using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// <see cref="JpxComponentTransform"/> — the reversible and
/// irreversible component transforms' exact/near inverses, the DC level shift, palette expansion,
/// nearest-neighbour upsampling to the reference grid, and the sYCC → RGB conversion.
/// </summary>
public class JpxComponentTransformTests
{
    // -- RCT (G.2): exact integer inverse ------------------------------------------------------

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(255, 255, 255)]
    [InlineData(-128, 127, -1)]
    [InlineData(12, 200, 77)]
    public void InverseRct_UndoesTheForwardTransform_Exactly(int r, int g, int b)
    {
        // Forward RCT (G.2): Y = floor((R+2G+B)/4), Cb = B-G, Cr = R-G.
        var y = (r + (2 * g) + b) >> 2;
        var cb = b - g;
        var cr = r - g;

        Span<int> c0 = [y];
        Span<int> c1 = [cb];
        Span<int> c2 = [cr];
        JpxComponentTransform.InverseRct(c0, c1, c2);

        Assert.Equal(r, c0[0]);
        Assert.Equal(g, c1[0]);
        Assert.Equal(b, c2[0]);
    }

    [Fact]
    public void InverseRct_RandomVectors_RoundTripExactly()
    {
        var rng = new Random(12345);
        const int n = 500;
        var r = new int[n];
        var g = new int[n];
        var b = new int[n];
        var y = new int[n];
        var cb = new int[n];
        var cr = new int[n];
        for (var i = 0; i < n; i++)
        {
            r[i] = rng.Next(-1024, 1024);
            g[i] = rng.Next(-1024, 1024);
            b[i] = rng.Next(-1024, 1024);
            y[i] = (r[i] + (2 * g[i]) + b[i]) >> 2;
            cb[i] = b[i] - g[i];
            cr[i] = r[i] - g[i];
        }

        JpxComponentTransform.InverseRct(y, cb, cr);

        Assert.Equal(r, y);
        Assert.Equal(g, cb);
        Assert.Equal(b, cr);
    }

    // -- ICT (G.3): within ±1 of the double forward transform ----------------------------------

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(37, 210, 128)]
    public void InverseIct_MatchesTheOriginalWithinOne(int r, int g, int b)
    {
        var y = (0.299 * r) + (0.587 * g) + (0.114 * b);
        var cb = (-0.168736 * r) - (0.331264 * g) + (0.5 * b);
        var cr = (0.5 * r) - (0.418688 * g) - (0.081312 * b);

        Span<int> c0 = [(int)Math.Round(y, MidpointRounding.AwayFromZero)];
        Span<int> c1 = [(int)Math.Round(cb, MidpointRounding.AwayFromZero)];
        Span<int> c2 = [(int)Math.Round(cr, MidpointRounding.AwayFromZero)];
        JpxComponentTransform.InverseIct(c0, c1, c2);

        Assert.True(Math.Abs(c0[0] - r) <= 1, $"R: expected {r}, got {c0[0]}");
        Assert.True(Math.Abs(c1[0] - g) <= 1, $"G: expected {g}, got {c1[0]}");
        Assert.True(Math.Abs(c2[0] - b) <= 1, $"B: expected {b}, got {c2[0]}");
    }

    // -- Level shift (G.1.2) --------------------------------------------------------------------

    [Theory]
    [InlineData(8, -128, 0)]
    [InlineData(8, 127, 255)]
    [InlineData(8, 0, 128)]
    [InlineData(12, -2048, 0)]
    [InlineData(12, 2047, 4095)]
    public void LevelShiftUnsigned_AddsHalfRange(int precision, int input, int expected)
    {
        Span<int> samples = [input];
        JpxComponentTransform.LevelShiftUnsigned(samples, precision);
        Assert.Equal(expected, samples[0]);
    }

    [Fact]
    public void ClampToPrecision_SignedComponent_SkipsTheShift_AndRoundTripsThroughJpxPlane()
    {
        // A signed component never gets LevelShiftUnsigned; the packing path stores the natural
        // signed range's bit pattern and JpxPlane.Sample sign-extends it back out unchanged.
        var info = new JpxComponentInfo(Precision: 8, Signed: true, XRsiz: 1, YRsiz: 1);
        var plane = Pack([-128, 0, 127], info);

        Assert.Equal(-128, plane.Sample(0));
        Assert.Equal(0, plane.Sample(1));
        Assert.Equal(127, plane.Sample(2));
    }

    [Fact]
    public void ClampToPrecision_UnsignedComponent_AfterLevelShift_RoundTripsThroughJpxPlane()
    {
        var info = new JpxComponentInfo(Precision: 8, Signed: false, XRsiz: 1, YRsiz: 1);
        Span<int> samples = [-128, 0, 127];
        JpxComponentTransform.LevelShiftUnsigned(samples, precision: 8);
        var plane = Pack(samples, info);

        Assert.Equal(0, plane.Sample(0));
        Assert.Equal(128, plane.Sample(1));
        Assert.Equal(255, plane.Sample(2));
    }

    [Fact]
    public void ClampToPrecision_UnsignedComponentOvershootsRange_ClampsRatherThanWraps()
    {
        // A regression test for the bug (T.800 G.1.2 / opj_int_clamp): the
        // irreversible (9/7) transform's quantisation ringing routinely overshoots a component's
        // nominal [0, 2^precision-1] range, and masking (the old behaviour) wraps that overshoot
        // around instead of saturating it -- 258 became 2, -3 became 253 -- turning a clean edge
        // into isolated black/white speckles.
        var info = new JpxComponentInfo(Precision: 8, Signed: false, XRsiz: 1, YRsiz: 1);

        Assert.Equal(255, JpxComponentTransform.ClampToPrecision(258, info)); // clamped up, not wrapped to 2
        Assert.Equal(0, JpxComponentTransform.ClampToPrecision(-3, info)); // clamped down, not wrapped to 253
        Assert.Equal(0, JpxComponentTransform.ClampToPrecision(0, info));
        Assert.Equal(255, JpxComponentTransform.ClampToPrecision(255, info));

        var plane = Pack([258, -3, 0, 255], info);
        Assert.Equal(255, plane.Sample(0));
        Assert.Equal(0, plane.Sample(1));
    }

    [Fact]
    public void ClampToPrecision_SignedComponentOvershootsRange_ClampsToTheSignedRange()
    {
        // T.800 G.1.2 saturates signed components too: the 9/7 filter's
        // ringing overshoots a signed 8-bit range exactly as it does an unsigned one, and the old
        // two's-complement mask turned -129 into +127 and 130 into -126 -- sign-flipped speckles
        // at every sharp edge. Clamp to [-2^(p-1), 2^(p-1)-1], then store the bit pattern.
        var info = new JpxComponentInfo(Precision: 8, Signed: true, XRsiz: 1, YRsiz: 1);

        Assert.Equal(0x80, JpxComponentTransform.ClampToPrecision(-129, info)); // -128's pattern, not +127
        Assert.Equal(0x7F, JpxComponentTransform.ClampToPrecision(130, info)); // +127's pattern, not -126
        Assert.Equal(0xFF, JpxComponentTransform.ClampToPrecision(-1, info)); // in range: the two's-complement pattern

        var plane = Pack([-129, 130, -128, 127, -1], info);
        Assert.Equal(-128, plane.Sample(0)); // clamped down, not wrapped to +127
        Assert.Equal(127, plane.Sample(1)); // clamped up, not wrapped to -126
        Assert.Equal(-128, plane.Sample(2));
        Assert.Equal(127, plane.Sample(3));
        Assert.Equal(-1, plane.Sample(4)); // in range: the two's-complement pattern round-trips
    }

    /// <summary>
    /// The live packing path, as <c>JpxImageDecoder.WriteTileComponent</c> performs it per sample:
    /// <see cref="JpxComponentTransform.ClampToPrecision"/>, then the narrowing store into the
    /// plane's native width. (The former <c>ToPlane</c> helper duplicated this and had no
    /// production caller.)
    /// </summary>
    private static JpxPlane Pack(ReadOnlySpan<int> samples, JpxComponentInfo info)
    {
        var narrow = info.Precision <= 8 ? new byte[samples.Length] : null;
        var wide = narrow is null ? new ushort[samples.Length] : null;
        for (var i = 0; i < samples.Length; i++)
        {
            var bits = JpxComponentTransform.ClampToPrecision(samples[i], info);
            if (narrow is not null)
            {
                narrow[i] = (byte)bits;
            }
            else
            {
                wide![i] = (ushort)bits;
            }
        }

        return new JpxPlane { Width = samples.Length, Height = 1, Precision = info.Precision, Signed = info.Signed, Samples8 = narrow, Samples16 = wide };
    }

    // -- Upsample geometry ------------------------------------------------------------------------

    [Fact]
    public void UpsampleNearest_127x95GridFrom64x48At2x2_MapsToTheRightSourceSample()
    {
        var source = new byte[64 * 48];
        for (var y = 0; y < 48; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                source[(y * 64) + x] = (byte)((x + (y * 64)) % 256);
            }
        }

        var plane = new JpxPlane { Width = 64, Height = 48, Precision = 8, Signed = false, Samples8 = source };
        var upsampled = JpxComponentTransform.UpsampleNearest(plane, gridWidth: 127, gridHeight: 95, xrsiz: 2, yrsiz: 2, x0Offset: 0, y0Offset: 0);

        Assert.Equal(127, upsampled.Width);
        Assert.Equal(95, upsampled.Height);

        // (x,y) -> floor(x/2), floor(y/2)
        Assert.Equal(plane.Sample(0), upsampled.Sample(0)); // (0,0) -> (0,0)
        Assert.Equal(plane.Sample((0 * 64) + 0), upsampled.Sample((1 * 127) + 1)); // (1,1) -> (0,0)
        Assert.Equal(plane.Sample((47 * 64) + 63), upsampled.Sample((94 * 127) + 126)); // (126,94) -> (63,47), last valid source sample
        Assert.Equal(plane.Sample((10 * 64) + 20), upsampled.Sample((21 * 127) + 41)); // (41,21) -> (20,10)
    }

    [Fact]
    public void UpsampleNearest_NoSubsampling_ReturnsTheSamePlaneInstance()
    {
        var plane = new JpxPlane { Width = 4, Height = 4, Precision = 8, Signed = false, Samples8 = new byte[16] };
        var result = JpxComponentTransform.UpsampleNearest(plane, gridWidth: 4, gridHeight: 4, xrsiz: 1, yrsiz: 1, x0Offset: 0, y0Offset: 0);
        Assert.Same(plane, result);
    }

    // -- Palette expansion (I.5.3.4/I.5.3.5) -----------------------------------------------------

    [Fact]
    public void ExpandPalette_ThreeChannelTable_ProducesOnePlanePerChannel()
    {
        // Entries: 0=(10,20,30), 1=(40,50,60), 2=(70,80,90), 3=(255,0,128).
        byte[] table =
        [
            10, 20, 30,
            40, 50, 60,
            70, 80, 90,
            255, 0, 128,
        ];
        var index = new JpxPlane { Width = 2, Height = 2, Precision = 8, Signed = false, Samples8 = [0, 3, 1, 2] };

        var planes = JpxComponentTransform.ExpandPalette(index, (Depth: 8, Entries: 4, Table: table), channelMap: null, PdfOptions.Default, null);

        Assert.Equal(3, planes.Length);
        Assert.Equal(new byte[] { 10, 255, 40, 70 }, planes[0].Samples8);
        Assert.Equal(new byte[] { 20, 0, 50, 80 }, planes[1].Samples8);
        Assert.Equal(new byte[] { 30, 128, 60, 90 }, planes[2].Samples8);
    }

    [Fact]
    public void ExpandPalette_ChannelMap_ReordersOutputPlanes()
    {
        byte[] table =
        [
            10, 20, 30,
            40, 50, 60,
        ];
        var index = new JpxPlane { Width = 1, Height = 1, Precision = 8, Signed = false, Samples8 = [1] };

        // Reversed order: output plane 0 reads palette column 2, plane 2 reads column 0.
        var planes = JpxComponentTransform.ExpandPalette(index, (Depth: 8, Entries: 2, Table: table), channelMap: [2, 1, 0], PdfOptions.Default, null);

        Assert.Equal(60, planes[0].Samples8![0]);
        Assert.Equal(50, planes[1].Samples8![0]);
        Assert.Equal(40, planes[2].Samples8![0]);
    }

    [Fact]
    public void ExpandPalette_OutOfRangeIndex_ClampsToEntryZero()
    {
        byte[] table = [200, 201, 202];
        var index = new JpxPlane { Width = 1, Height = 1, Precision = 8, Signed = false, Samples8 = [99] };

        var planes = JpxComponentTransform.ExpandPalette(index, (Depth: 8, Entries: 1, Table: table), channelMap: null, PdfOptions.Default, null);

        Assert.Equal(200, planes[0].Samples8![0]);
        Assert.Equal(201, planes[1].Samples8![0]);
        Assert.Equal(202, planes[2].Samples8![0]);
    }

    [Fact]
    public void ExpandPalette_ChannelMapNamesAColumnBeyondThePalette_ReportsDeviationAndFallsBackToIdentityOrder()
    {
        // A regression test for the bug that a cmap box's PCOL is trusted input
        // nothing cross-checks against the palette's own column count at parse time (cmap and
        // pclr parse independently) -- ReadPaletteEntry's offset computation would otherwise
        // index the palette table past its own bounds. column 5 has no meaning for a 3-column
        // (RGB) palette.
        byte[] table =
        [
            10, 20, 30,
            40, 50, 60,
        ];
        var index = new JpxPlane { Width = 1, Height = 1, Precision = 8, Signed = false, Samples8 = [1] };
        var diagnostics = new DiagnosticCollection();

        var planes = JpxComponentTransform.ExpandPalette(index, (Depth: 8, Entries: 2, Table: table), channelMap: [0, 1, 5], PdfOptions.Default, diagnostics);

        // Falls back to identity order (column c for output plane c) rather than indexing OOB.
        Assert.Equal(40, planes[0].Samples8![0]);
        Assert.Equal(50, planes[1].Samples8![0]);
        Assert.Equal(60, planes[2].Samples8![0]);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid);
    }

    // -- sYCC → RGB (EnumCS 18) -------------------------------------------------------------------

    [Fact]
    public void SyccToRgb_NeutralChroma_LeavesLumaAsGray()
    {
        // Cb=Cr=128 (the half-range "no chroma" point at 8-bit) must decode to R=G=B=Y exactly:
        // this is what makes the skip-when-Mct rule safe to violate accidentally for gray content
        // but not for anything with real chroma (the next test).
        var y = new JpxPlane { Width = 1, Height = 1, Precision = 8, Signed = false, Samples8 = [200] };
        var cb = new JpxPlane { Width = 1, Height = 1, Precision = 8, Signed = false, Samples8 = [128] };
        var cr = new JpxPlane { Width = 1, Height = 1, Precision = 8, Signed = false, Samples8 = [128] };

        JpxComponentTransform.SyccToRgb(y, cb, cr);

        Assert.Equal(200, y.Samples8![0]);
        Assert.Equal(200, cb.Samples8![0]);
        Assert.Equal(200, cr.Samples8![0]);
    }

    [Fact]
    public void SyccToRgb_KnownTriple_MatchesTheBt601Matrix()
    {
        // Encode a known RGB via the same forward matrix InverseIct's test uses, level-shift the
        // chroma channels to the unsigned half-range representation SyccToRgb expects, then decode.
        const int r = 220;
        const int g = 40;
        const int b = 60;
        var yValue = (0.299 * r) + (0.587 * g) + (0.114 * b);
        var cbValue = (-0.168736 * r) - (0.331264 * g) + (0.5 * b) + 128;
        var crValue = (0.5 * r) - (0.418688 * g) - (0.081312 * b) + 128;

        var y = new JpxPlane { Width = 1, Height = 1, Precision = 8, Signed = false, Samples8 = [(byte)Math.Round(yValue, MidpointRounding.AwayFromZero)] };
        var cb = new JpxPlane { Width = 1, Height = 1, Precision = 8, Signed = false, Samples8 = [(byte)Math.Round(cbValue, MidpointRounding.AwayFromZero)] };
        var cr = new JpxPlane { Width = 1, Height = 1, Precision = 8, Signed = false, Samples8 = [(byte)Math.Round(crValue, MidpointRounding.AwayFromZero)] };

        JpxComponentTransform.SyccToRgb(y, cb, cr);

        Assert.True(Math.Abs(y.Samples8![0] - r) <= 1, $"R: expected {r}, got {y.Samples8[0]}");
        Assert.True(Math.Abs(cb.Samples8![0] - g) <= 1, $"G: expected {g}, got {cb.Samples8[0]}");
        Assert.True(Math.Abs(cr.Samples8![0] - b) <= 1, $"B: expected {b}, got {cr.Samples8[0]}");
    }

    [Fact]
    public void SyccToRgb_SkipRule_HasNoMctParameter_TheCallerMustDecideNotToInvokeItTwice()
    {
        // JpxComponentTransform.SyccToRgb has no Mct flag: it always performs the sYCC matrix.
        // The rule ("skipped when the MCT already produced RGB") is therefore enforced
        // entirely by the caller (JpxImageDecoder) never invoking this method a second
        // time on the same planes — demonstrated here by showing it is NOT idempotent on
        // already-decoded RGB (an already-RGB triple with real color is corrupted by a second pass).
        var r = new JpxPlane { Width = 1, Height = 1, Precision = 8, Signed = false, Samples8 = [220] };
        var g = new JpxPlane { Width = 1, Height = 1, Precision = 8, Signed = false, Samples8 = [40] };
        var b = new JpxPlane { Width = 1, Height = 1, Precision = 8, Signed = false, Samples8 = [60] };

        JpxComponentTransform.SyccToRgb(r, g, b);

        Assert.False(r.Samples8![0] == 220 && g.Samples8![0] == 40 && b.Samples8![0] == 60);
    }
}
