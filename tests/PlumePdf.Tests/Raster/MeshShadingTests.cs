using System.Linq;
using PlumePdf.Content;
using PlumePdf.Raster;
using PlumePdf.Raster.Color;
using PlumePdf.Raster.DisplayList;
using PlumePdf.Raster.Shading;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary><see cref="MeshShading"/>'s type 4-7 parsing, per-pixel Gouraud/Coons/tensor rendering, and vertex-stream caps (PLUME7735/7736).</summary>
public class MeshShadingTests
{
    private static readonly Func<IndirectReference, PdfObject> NoIndirectRefsExpected = _ => throw new InvalidOperationException("Test fixtures use no indirect references.");

    // Decode: x/y pass through raw byte value directly (0-255); R/G/B pass through as a 0-1
    // fraction of the raw byte (so byte 255 -> full channel intensity, byte 0 -> none).
    private static PdfArray Decode3Comp() => NumArray(0, 255, 0, 255, 0, 1, 0, 1, 0, 1);

    private static PdfArray NumArray(params double[] values) => new(values.Select(v => (PdfObject)PdfNumber.Get(v)));

    private static PdfDictionary MeshDict(int shadingType, int bitsPerFlag, PdfDictionary? extra = null)
    {
        var dict = extra ?? new PdfDictionary();
        dict.Set(PdfName.Get("ShadingType"), PdfNumber.Get(shadingType));
        dict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceRGB"));
        dict.Set(PdfName.Get("BitsPerCoordinate"), PdfNumber.Get(8));
        dict.Set(PdfName.Get("BitsPerComponent"), PdfNumber.Get(8));
        if (bitsPerFlag > 0)
        {
            dict.Set(PdfName.Get("BitsPerFlag"), PdfNumber.Get(bitsPerFlag));
        }

        dict.Set(PdfName.Get("Decode"), Decode3Comp());
        return dict;
    }

    private static RasterSurface PaintMesh(PdfDictionary dict, byte[] payload)
    {
        var stream = new PdfStream(dict, payload);
        var shading = MeshShading.Parse(stream, DeviceRgbColorSpace.Instance, null, PdfMatrix.Identity, 255, NoIndirectRefsExpected, PdfOptions.Default, null);
        var surface = RasterSurface.Create(150, 150);
        shading.Paint(surface, new ClipWindow(0, 0, 150, 150, null));
        return surface;
    }

    private static void AssertDominant(RasterSurface surface, int x, int y, int channel)
    {
        // channel: 0=R, 1=G, 2=B.
        var (b, g, r, a) = surface.GetPixel(x, y);
        Assert.True(a > 0, $"Pixel ({x},{y}) was never painted.");
        var channels = new[] { r, g, b };
        var dominant = channel;
        for (var i = 0; i < 3; i++)
        {
            if (i != dominant)
            {
                Assert.True(channels[dominant] > channels[i], $"Pixel ({x},{y}) expected channel {dominant} ({channels[dominant]}) to dominate channel {i} ({channels[i]}).");
            }
        }
    }

    // ------------------------------------------------------------------
    // Type 4 — free-form Gouraud-shaded triangle mesh.
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_Type4_SingleTriangle_PaintsPerVertexColorsInterpolated()
    {
        // One triangle: A=(0,0) red, B=(100,0) green, C=(0,100) blue. Each vertex record is
        // flag(1B) + x(1B) + y(1B) + R,G,B(1B each) = 6 bytes; all flags 0 (a single new triangle).
        byte[] payload =
        [
            0, 0, 0, 255, 0, 0, // A: red
            0, 100, 0, 0, 255, 0, // B: green
            0, 0, 100, 0, 0, 255, // C: blue
        ];

        var surface = PaintMesh(MeshDict(shadingType: 4, bitsPerFlag: 8), payload);

        AssertDominant(surface, 3, 3, channel: 0); // near A -> red
        AssertDominant(surface, 90, 3, channel: 1); // near B -> green
        AssertDominant(surface, 3, 90, channel: 2); // near C -> blue
    }

    [Fact]
    public void Parse_Type4_SharedEdgeFlag1_AddsSecondTriangle()
    {
        // Triangle 1 (flag 0): A=(0,0) red, B=(100,0) green, C=(0,100) blue.
        // Triangle 2 (flag 1): reuses (B, C), new vertex D=(100,100) yellow.
        byte[] payload =
        [
            0, 0, 0, 255, 0, 0,
            0, 100, 0, 0, 255, 0,
            0, 0, 100, 0, 0, 255,
            1, 100, 100, 255, 255, 0,
        ];

        var surface = PaintMesh(MeshDict(shadingType: 4, bitsPerFlag: 8), payload);

        // D's corner should show yellow (high R and G, low B).
        var (b, g, r, a) = surface.GetPixel(96, 96);
        Assert.True(a > 0);
        Assert.True(r > b);
        Assert.True(g > b);
    }

    [Fact]
    public void Parse_Type4_InvalidFlagValue_ThrowsPlume7736()
    {
        byte[] payload = [3, 0, 0, 255, 0, 0]; // flag 3 is not valid for type 4 (0, 1, 2 only)

        var dict = MeshDict(shadingType: 4, bitsPerFlag: 8);
        var stream = new PdfStream(dict, payload);
        var ex = Assert.Throws<PlumePdfException>(() =>
            MeshShading.Parse(stream, DeviceRgbColorSpace.Instance, null, PdfMatrix.Identity, 255, NoIndirectRefsExpected, PdfOptions.Default, null));

        Assert.Equal("PLUME7736", ex.Code);
    }

    [Fact]
    public void Parse_Type4_TruncatedStream_ThrowsPlume7736()
    {
        byte[] payload = [0, 0, 0, 255]; // flag + x + y + only 1 of 3 color components

        var dict = MeshDict(shadingType: 4, bitsPerFlag: 8);
        var stream = new PdfStream(dict, payload);
        var ex = Assert.Throws<PlumePdfException>(() =>
            MeshShading.Parse(stream, DeviceRgbColorSpace.Instance, null, PdfMatrix.Identity, 255, NoIndirectRefsExpected, PdfOptions.Default, null));

        Assert.Equal("PLUME7736", ex.Code);
    }

    // ------------------------------------------------------------------
    // Type 5 — lattice-form Gouraud-shaded triangle mesh.
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_Type5_TwoByTwoLattice_PaintsFourCorners()
    {
        var dict = MeshDict(shadingType: 5, bitsPerFlag: 0);
        dict.Set(PdfName.Get("VerticesPerRow"), PdfNumber.Get(2));

        // Row 0: (0,0) red, (100,0) green. Row 1: (0,100) blue, (100,100) yellow.
        // No flag field for type 5 — each vertex is x,y,R,G,B (5 bytes).
        byte[] payload =
        [
            0, 0, 255, 0, 0,
            100, 0, 0, 255, 0,
            0, 100, 0, 0, 255,
            100, 100, 255, 255, 0,
        ];

        var surface = PaintMesh(dict, payload);

        AssertDominant(surface, 3, 3, channel: 0); // (0,0) -> red
        AssertDominant(surface, 90, 3, channel: 1); // (100,0) -> green
        AssertDominant(surface, 3, 90, channel: 2); // (0,100) -> blue

        var (b, g, r, a) = surface.GetPixel(96, 96); // (100,100) -> yellow
        Assert.True(a > 0);
        Assert.True(r > b);
        Assert.True(g > b);
    }

    [Fact]
    public void Parse_Type5_VerticesPerRowExceedsCap_ThrowsPlume7735()
    {
        var dict = MeshDict(shadingType: 5, bitsPerFlag: 0);
        dict.Set(PdfName.Get("VerticesPerRow"), PdfNumber.Get(MeshShading.MaxVerticesPerRow + 1));

        // The cap must be enforced before any allocation driven by /VerticesPerRow — an empty
        // payload proves the check happens before the stream is even read.
        var stream = new PdfStream(dict, Array.Empty<byte>());
        var ex = Assert.Throws<PlumePdfException>(() =>
            MeshShading.Parse(stream, DeviceRgbColorSpace.Instance, null, PdfMatrix.Identity, 255, NoIndirectRefsExpected, PdfOptions.Default, null));

        Assert.Equal("PLUME7735", ex.Code);
    }

    [Fact]
    public void Parse_Type5_VerticesPerRowTooSmall_ThrowsPlume7736()
    {
        var dict = MeshDict(shadingType: 5, bitsPerFlag: 0);
        dict.Set(PdfName.Get("VerticesPerRow"), PdfNumber.Get(1));

        var stream = new PdfStream(dict, Array.Empty<byte>());
        var ex = Assert.Throws<PlumePdfException>(() =>
            MeshShading.Parse(stream, DeviceRgbColorSpace.Instance, null, PdfMatrix.Identity, 255, NoIndirectRefsExpected, PdfOptions.Default, null));

        Assert.Equal("PLUME7736", ex.Code);
    }

    // ------------------------------------------------------------------
    // Type 6 — Coons patch mesh.
    // ------------------------------------------------------------------

    private static byte[] CoonsPatchPayload()
    {
        // 12 boundary control points around a roughly-square patch, corners at (0,0)/(100,0)/
        // (100,100)/(0,100); 4 corner colors red/green/blue/yellow.
        (byte X, byte Y)[] points =
        [
            (0, 0), (33, 0), (67, 0), (100, 0),
            (100, 33), (100, 67), (100, 100),
            (67, 100), (33, 100), (0, 100),
            (0, 67), (0, 33),
        ];
        (byte R, byte G, byte B)[] colors = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0)];

        List<byte> bytes = [0]; // flag 0: new patch
        foreach (var p in points)
        {
            bytes.Add(p.X);
            bytes.Add(p.Y);
        }

        foreach (var c in colors)
        {
            bytes.Add(c.R);
            bytes.Add(c.G);
            bytes.Add(c.B);
        }

        return [.. bytes];
    }

    [Fact]
    public void Parse_Type6_CoonsPatch_PaintsFourCornerColors()
    {
        var surface = PaintMesh(MeshDict(shadingType: 6, bitsPerFlag: 8), CoonsPatchPayload());

        AssertDominant(surface, 2, 2, channel: 0); // p1 (0,0) -> red
        AssertDominant(surface, 97, 2, channel: 1); // p4 (100,0) -> green
        AssertDominant(surface, 97, 97, channel: 2); // p7 (100,100) -> blue

        var (b, g, r, a) = surface.GetPixel(2, 97); // p10 (0,100) -> yellow
        Assert.True(a > 0);
        Assert.True(r > b);
        Assert.True(g > b);
    }

    [Fact]
    public void Parse_Type6_InvalidFlag_ThrowsPlume7736()
    {
        byte[] payload = [4, .. new byte[36]]; // flag 4 is not valid (0-3 only)

        var stream = new PdfStream(MeshDict(shadingType: 6, bitsPerFlag: 8), payload);
        var ex = Assert.Throws<PlumePdfException>(() =>
            MeshShading.Parse(stream, DeviceRgbColorSpace.Instance, null, PdfMatrix.Identity, 255, NoIndirectRefsExpected, PdfOptions.Default, null));

        Assert.Equal("PLUME7736", ex.Code);
    }

    [Fact]
    public void Parse_Type6_FlagWithNoPrecedingPatch_ThrowsPlume7736()
    {
        byte[] payload = [1, .. new byte[24]]; // flag 1 (continuation) with nothing to continue from

        var stream = new PdfStream(MeshDict(shadingType: 6, bitsPerFlag: 8), payload);
        var ex = Assert.Throws<PlumePdfException>(() =>
            MeshShading.Parse(stream, DeviceRgbColorSpace.Instance, null, PdfMatrix.Identity, 255, NoIndirectRefsExpected, PdfOptions.Default, null));

        Assert.Equal("PLUME7736", ex.Code);
    }

    // ------------------------------------------------------------------
    // Type 7 — tensor-product patch mesh.
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_Type7_TensorPatch_PaintsFourCornerColors()
    {
        // Same 12 boundary points as the Coons fixture, plus 4 interior points roughly at the
        // bilinear thirds — the tensor patch's extra control points over Coons.
        var boundary = CoonsPatchPayload();
        (byte X, byte Y)[] interior = [(33, 33), (67, 33), (67, 67), (33, 67)];

        List<byte> bytes = [.. boundary[..^12]]; // flag + 12 boundary points, drop the 4 colors appended at the end
        foreach (var p in interior)
        {
            bytes.Add(p.X);
            bytes.Add(p.Y);
        }

        bytes.AddRange(boundary[^12..]); // re-append the 4 corner colors after the interior points

        var surface = PaintMesh(MeshDict(shadingType: 7, bitsPerFlag: 8), [.. bytes]);

        AssertDominant(surface, 2, 2, channel: 0);
        AssertDominant(surface, 97, 2, channel: 1);
        AssertDominant(surface, 97, 97, channel: 2);

        var (b, g, r, a) = surface.GetPixel(2, 97);
        Assert.True(a > 0);
        Assert.True(r > b);
        Assert.True(g > b);
    }

    // ------------------------------------------------------------------
    // Dictionary/decode validation.
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_DecodeArrayWrongLength_ThrowsPlume7736()
    {
        var dict = MeshDict(shadingType: 4, bitsPerFlag: 8);
        dict.Set(PdfName.Get("Decode"), NumArray(0, 255, 0, 255)); // missing the 3 color component pairs

        var stream = new PdfStream(dict, Array.Empty<byte>());
        var ex = Assert.Throws<PlumePdfException>(() =>
            MeshShading.Parse(stream, DeviceRgbColorSpace.Instance, null, PdfMatrix.Identity, 255, NoIndirectRefsExpected, PdfOptions.Default, null));

        Assert.Equal("PLUME7736", ex.Code);
    }

    [Fact]
    public void Parse_InvalidBitsPerCoordinate_ThrowsPlume7736()
    {
        var dict = MeshDict(shadingType: 4, bitsPerFlag: 8);
        dict.Set(PdfName.Get("BitsPerCoordinate"), PdfNumber.Get(7)); // not one of the valid widths

        var stream = new PdfStream(dict, Array.Empty<byte>());
        var ex = Assert.Throws<PlumePdfException>(() =>
            MeshShading.Parse(stream, DeviceRgbColorSpace.Instance, null, PdfMatrix.Identity, 255, NoIndirectRefsExpected, PdfOptions.Default, null));

        Assert.Equal("PLUME7736", ex.Code);
    }

    [Fact]
    public void Parse_UnsupportedShadingType_ThrowsPlume7736()
    {
        var dict = MeshDict(shadingType: 3, bitsPerFlag: 8); // not a mesh type

        var stream = new PdfStream(dict, Array.Empty<byte>());
        var ex = Assert.Throws<PlumePdfException>(() =>
            MeshShading.Parse(stream, DeviceRgbColorSpace.Instance, null, PdfMatrix.Identity, 255, NoIndirectRefsExpected, PdfOptions.Default, null));

        Assert.Equal("PLUME7736", ex.Code);
    }

    // ------------------------------------------------------------------
    // ShadingFactory integration — extends the existing Build contract.
    // ------------------------------------------------------------------

    [Fact]
    public void ShadingFactory_Build_WithMeshStream_PaintsRealGradientNotFlatGray()
    {
        var shadingDict = MeshDict(shadingType: 4, bitsPerFlag: 8);
        byte[] payload =
        [
            0, 0, 0, 255, 0, 0,
            0, 100, 0, 0, 255, 0,
            0, 0, 100, 0, 0, 255,
        ];
        var triangleStream = new PdfStream(shadingDict, payload);

        var shadingObject = new ShadingPageObject
        {
            Shading = shadingDict,
            Ctm = PdfMatrix.Identity,
            FillAlpha = 1.0,
        };

        var built = ShadingFactory.Build(shadingObject, PdfOptions.Default, objects: null, diagnostics: null, shadingStream: triangleStream);
        var surface = RasterSurface.Create(150, 150);
        built.Paint(surface, new ClipWindow(0, 0, 150, 150, null));

        AssertDominant(surface, 3, 3, channel: 0);
        AssertDominant(surface, 90, 3, channel: 1);
        AssertDominant(surface, 3, 90, channel: 2);
    }

    [Fact]
    public void ShadingFactory_Build_WithoutStream_StillDegradesToFlatMidGray()
    {
        var shadingDict = MeshDict(shadingType: 4, bitsPerFlag: 8);
        var shadingObject = new ShadingPageObject
        {
            Shading = shadingDict,
            Ctm = PdfMatrix.Identity,
            FillAlpha = 1.0,
        };

        // No shadingStream supplied — Build must preserve the exact fallback behavior.
        var built = ShadingFactory.Build(shadingObject, PdfOptions.Default, objects: null, diagnostics: null);
        var surface = RasterSurface.Create(10, 10);
        built.Paint(surface, new ClipWindow(0, 0, 10, 10, null));

        var (b, g, r, a) = surface.GetPixel(5, 5);
        Assert.Equal(128, r);
        Assert.Equal(128, g);
        Assert.Equal(128, b);
        Assert.Equal(255, a);
    }
}
