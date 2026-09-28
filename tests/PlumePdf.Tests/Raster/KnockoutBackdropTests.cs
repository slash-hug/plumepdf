using PlumePdf.Raster.Transparency;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary><see cref="Backdrop"/>'s non-isolated capture/removal, <see cref="TransparencyGroup"/>'s knockout/non-isolated compositing extensions, and <see cref="SoftMask"/>'s <c>/Matte</c> pre-blended-alpha handling (PLUME7737).</summary>
public class KnockoutBackdropTests
{
    // ------------------------------------------------------------------
    // Backdrop.Capture.
    // ------------------------------------------------------------------

    [Fact]
    public void Capture_WithinBounds_CopiesExactRegion()
    {
        // A 4x4 BGRA destination; capture the inner 2x2 region starting at (1,1).
        var destination = new byte[4 * 4 * 4];
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)(i % 256);
        }

        var captured = Backdrop.Capture(destination, destWidth: 4, destHeight: 4, destStrideBytes: 16, originX: 1, originY: 1, width: 2, height: 2);

        Assert.Equal(2 * 2 * 4, captured.Length);
        // Row 0 of the capture = destination row 1, columns 1-2 (bytes 4*4+4 .. +8).
        Assert.Equal(destination.AsSpan(16 + 4, 8).ToArray(), captured.AsSpan(0, 8).ToArray());
        // Row 1 of the capture = destination row 2, columns 1-2.
        Assert.Equal(destination.AsSpan(32 + 4, 8).ToArray(), captured.AsSpan(8, 8).ToArray());
    }

    [Fact]
    public void Capture_EntirelyOffSurface_ReturnsTransparentBlack()
    {
        var destination = new byte[4 * 4 * 4];
        Array.Fill(destination, (byte)200);

        var captured = Backdrop.Capture(destination, destWidth: 4, destHeight: 4, destStrideBytes: 16, originX: 10, originY: 10, width: 2, height: 2);

        Assert.All(captured, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Capture_PartiallyOffSurface_TransparentOutsidePaintedInside()
    {
        // 2x2 capture at origin (3,3) on a 4x4 surface: only pixel (3,3) is on-surface.
        var destination = new byte[4 * 4 * 4];
        var onSurfaceOffset = ((3 * 4) + 3) * 4;
        destination[onSurfaceOffset] = 10;
        destination[onSurfaceOffset + 1] = 20;
        destination[onSurfaceOffset + 2] = 30;
        destination[onSurfaceOffset + 3] = 255;

        var captured = Backdrop.Capture(destination, destWidth: 4, destHeight: 4, destStrideBytes: 16, originX: 3, originY: 3, width: 2, height: 2);

        // Capture pixel (0,0) maps to destination (3,3) — the only on-surface pixel.
        Assert.Equal((10, 20, 30, (byte)255), (captured[0], captured[1], captured[2], captured[3]));
        // The other 3 capture pixels are off-surface — transparent black.
        for (var i = 1; i < 4; i++)
        {
            var o = i * 4;
            Assert.Equal((0, 0, 0, 0), (captured[o], captured[o + 1], captured[o + 2], captured[o + 3]));
        }
    }

    [Fact]
    public void Capture_OverCap_ThrowsPlume7739()
    {
        var destination = new byte[16];
        var ex = Assert.Throws<PlumePdfException>(() =>
            Backdrop.Capture(destination, destWidth: 100_000, destHeight: 100_000, destStrideBytes: 400_000, originX: 0, originY: 0, width: 100_000, height: 100_000));

        Assert.Equal("PLUME7739", ex.Code);
    }

    // ------------------------------------------------------------------
    // Backdrop.RemoveBackdrop — inverts BlendModes.Composite(Normal, backdrop, source).
    // ------------------------------------------------------------------

    [Fact]
    public void RemoveBackdrop_InvertsNormalComposite_RecoversOriginalSource()
    {
        (byte B, byte G, byte R, byte A) backdrop = (50, 60, 70, 200);
        (byte B, byte G, byte R, byte A) source = (10, 20, 30, 150);

        var composited = BlendModes.Composite(BlendMode.Normal, (backdrop.R, backdrop.G, backdrop.B, backdrop.A), (source.R, source.G, source.B, source.A));

        byte[] groupBgra = [composited.B, composited.G, composited.R, composited.A];
        byte[] backdropBgra = [backdrop.B, backdrop.G, backdrop.R, backdrop.A];

        Backdrop.RemoveBackdrop(groupBgra, backdropBgra, pixelCount: 1);

        // Two lossy 8-bit-rounded compositing steps compound a few units of error — a tight
        // tolerance still proves this is the correct inverse formula, not a coincidence.
        Assert.InRange(groupBgra[0], source.B - 5, source.B + 5);
        Assert.InRange(groupBgra[1], source.G - 5, source.G + 5);
        Assert.InRange(groupBgra[2], source.R - 5, source.R + 5);
        Assert.InRange(groupBgra[3], source.A - 5, source.A + 5);
    }

    [Fact]
    public void RemoveBackdrop_OpaqueBackdrop_LeavesFullyOpaqueRatherThanDividingByZero()
    {
        byte[] groupBgra = [80, 90, 100, 255];
        byte[] backdropBgra = [1, 2, 3, 255]; // fully opaque backdrop — alpha is unrecoverable (see remarks).

        var exception = Record.Exception(() => Backdrop.RemoveBackdrop(groupBgra, backdropBgra, pixelCount: 1));

        Assert.Null(exception);
        Assert.Equal(255, groupBgra[3]);
    }

    [Fact]
    public void RemoveBackdrop_FullyTransparentResult_ZerosOutRatherThanNegativeAlpha()
    {
        // Result alpha equal to backdrop alpha means the group itself contributed nothing here.
        byte[] groupBgra = [50, 60, 70, 100];
        byte[] backdropBgra = [50, 60, 70, 100];

        Backdrop.RemoveBackdrop(groupBgra, backdropBgra, pixelCount: 1);

        Assert.Equal((0, 0, 0, 0), (groupBgra[0], groupBgra[1], groupBgra[2], groupBgra[3]));
    }

    // ------------------------------------------------------------------
    // TransparencyGroup.CompositeNonIsolated.
    // ------------------------------------------------------------------

    [Fact]
    public void CompositeNonIsolated_RoundTrip_MatchesDirectIsolatedComposite()
    {
        // BGRA byte order throughout, matching TransparencyGroup/Backdrop's own buffer
        // convention (destination[+2] is R, not [+0]).
        byte[] backdropBgra = [50, 60, 70, 200];
        byte[] sourceBgra = [10, 20, 30, 150];

        // Render "source" content directly onto a fresh destination that already has the
        // backdrop painted (the isolated-equivalent ground truth)...
        var groundTruthDest = (byte[])backdropBgra.Clone();
        TransparencyGroup.Composite(groundTruthDest, 1, 1, 4, sourceBgra, 1, 1, 0, 0, 1.0, BlendMode.Normal);

        // ...versus the non-isolated path: render the SAME source composited over a COPY of the
        // backdrop (simulating what a non-isolated group's own content would have produced),
        // capture that backdrop, then CompositeNonIsolated onto a fresh destination carrying the
        // same backdrop.
        var nonIsolatedDest = (byte[])backdropBgra.Clone();
        var capturedBackdrop = Backdrop.Capture(nonIsolatedDest, 1, 1, 4, 0, 0, 1, 1);
        var composited = BlendModes.Composite(
            BlendMode.Normal,
            (backdropBgra[2], backdropBgra[1], backdropBgra[0], backdropBgra[3]),
            (sourceBgra[2], sourceBgra[1], sourceBgra[0], sourceBgra[3]));
        byte[] groupBgra = [composited.B, composited.G, composited.R, composited.A];

        TransparencyGroup.CompositeNonIsolated(nonIsolatedDest, 1, 1, 4, groupBgra, capturedBackdrop, 1, 1, 0, 0, 1.0, BlendMode.Normal);

        // A round trip through RemoveBackdrop's inverse formula compounds a few units of 8-bit
        // rounding error (see RemoveBackdrop_InvertsNormalComposite_RecoversOriginalSource) — a
        // small tolerance still proves the two paths agree, not a coincidence.
        for (var i = 0; i < 4; i++)
        {
            Assert.InRange(nonIsolatedDest[i], groundTruthDest[i] - 5, groundTruthDest[i] + 5);
        }
    }

    // ------------------------------------------------------------------
    // TransparencyGroup.KnockoutLayer.
    // ------------------------------------------------------------------

    [Fact]
    public void KnockoutLayer_TwoOverlappingSemiTransparentSiblings_DoNotAccumulateOpacity()
    {
        byte[] initialBackdrop = [0, 0, 0, 0]; // transparent — an isolated knockout group.
        byte[] groupResult = [0, 0, 0, 0];
        byte[] layer = [0, 0, 255, 128]; // red, ~50% alpha.

        TransparencyGroup.KnockoutLayer(groupResult, initialBackdrop, layer, BlendMode.Normal, pixelCount: 1);
        TransparencyGroup.KnockoutLayer(groupResult, initialBackdrop, layer, BlendMode.Normal, pixelCount: 1); // same sibling painting the same pixel again

        // Knockout: the second sibling replaces, not accumulates — alpha stays exactly 128.
        Assert.Equal((0, 0, 255, 128), (groupResult[0], groupResult[1], groupResult[2], groupResult[3]));

        // Contrast with ordinary (non-knockout) sequential compositing, which WOULD accumulate
        // opacity — proving the two are genuinely different operations, not a no-op stand-in.
        byte[] nonKnockoutResult = [0, 0, 0, 0];
        var step1 = BlendModes.Composite(BlendMode.Normal, (0, 0, 0, 0), (255, 0, 0, 128));
        var step2 = BlendModes.Composite(BlendMode.Normal, (step1.R, step1.G, step1.B, step1.A), (255, 0, 0, 128));
        Assert.True(step2.A > 128, $"Expected non-knockout accumulation to exceed a single layer's alpha (128), got {step2.A}.");
    }

    [Fact]
    public void KnockoutLayer_ComposesAgainstInitialBackdropNeverRunningResult()
    {
        // Multiply is backdrop-dependent, so this distinguishes "against initial" from "against
        // running result" cleanly: Multiply(white, gray) = gray; Multiply(black, gray) is much
        // darker. If KnockoutLayer incorrectly blended against the running result (black, from
        // layer 1), layer 2 would come out darkened instead of the plain gray this asserts.
        byte[] initialBackdrop = [255, 255, 255, 255]; // opaque white.
        byte[] groupResult = [255, 255, 255, 255];

        byte[] layer1 = [0, 0, 0, 255]; // opaque black.
        TransparencyGroup.KnockoutLayer(groupResult, initialBackdrop, layer1, BlendMode.Multiply, pixelCount: 1);
        Assert.Equal((0, 0, 0, 255), (groupResult[0], groupResult[1], groupResult[2], groupResult[3]));

        byte[] layer2 = [128, 128, 128, 255]; // opaque gray.
        TransparencyGroup.KnockoutLayer(groupResult, initialBackdrop, layer2, BlendMode.Multiply, pixelCount: 1);

        Assert.Equal((128, 128, 128, 255), (groupResult[0], groupResult[1], groupResult[2], groupResult[3]));
    }

    [Fact]
    public void KnockoutLayer_ElementDoesNotPaintPixel_LeavesRunningResultUntouched()
    {
        byte[] initialBackdrop = [0, 0, 0, 0];
        byte[] groupResult = [0, 0, 255, 255]; // opaque red, from an earlier layer.
        byte[] emptyLayer = [0, 0, 0, 0]; // this layer never painted here.

        TransparencyGroup.KnockoutLayer(groupResult, initialBackdrop, emptyLayer, BlendMode.Normal, pixelCount: 1);

        Assert.Equal((0, 0, 255, 255), (groupResult[0], groupResult[1], groupResult[2], groupResult[3]));
    }

    // ------------------------------------------------------------------
    // SoftMask /Matte (PLUME7737).
    // ------------------------------------------------------------------

    private static PdfArray NumArray(params double[] values)
    {
        var array = new PdfArray();
        foreach (var v in values)
        {
            array.Add(PdfNumber.Get(v));
        }

        return array;
    }

    [Fact]
    public void TryParseMatte_Valid_ReturnsComponents()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Matte"), NumArray(0.2, 0.3, 0.4));

        var matte = SoftMask.TryParseMatte(dict, 3, _ => throw new InvalidOperationException(), null);

        Assert.NotNull(matte);
        Assert.Equal([0.2, 0.3, 0.4], matte);
    }

    [Fact]
    public void TryParseMatte_Absent_ReturnsNullWithoutDiagnostic()
    {
        var diagnostics = new DiagnosticCollection();
        var matte = SoftMask.TryParseMatte(new PdfDictionary(), 3, _ => throw new InvalidOperationException(), diagnostics);

        Assert.Null(matte);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void TryParseMatte_WrongComponentCount_ReturnsNullWithPlume7737()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Matte"), NumArray(0.2, 0.3)); // expects 3

        var diagnostics = new DiagnosticCollection();
        var matte = SoftMask.TryParseMatte(dict, 3, _ => throw new InvalidOperationException(), diagnostics);

        Assert.Null(matte);
        Assert.Contains(diagnostics, d => d.Code == "PLUME7737");
    }

    [Fact]
    public void ApplyMatte_RoundTrips_RecoversOriginalColor()
    {
        double[] matte = [0.2, 0.3, 0.4];
        double[] trueColor = [0.6, 0.7, 0.8];
        const double alpha = 0.5;

        // The pre-blended (stored) sample a matted image encodes: matte + (true - matte) * alpha.
        double[] stored = [0.4, 0.5, 0.6];

        SoftMask.ApplyMatte(stored, matte, alpha);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(trueColor[i], stored[i], precision: 9);
        }
    }

    [Fact]
    public void ApplyMatte_ZeroAlpha_ReturnsMatteColorUnchanged()
    {
        double[] matte = [0.2, 0.3, 0.4];
        double[] components = [0.9, 0.9, 0.9];

        SoftMask.ApplyMatte(components, matte, alpha: 0.0);

        Assert.Equal(matte, components);
    }

    [Fact]
    public void ApplyMatte_ClampsResultTo01()
    {
        double[] matte = [0.5];
        double[] components = [1.0]; // an out-of-range stored sample given a small alpha.

        SoftMask.ApplyMatte(components, matte, alpha: 0.01);

        Assert.InRange(components[0], 0.0, 1.0);
    }
}
