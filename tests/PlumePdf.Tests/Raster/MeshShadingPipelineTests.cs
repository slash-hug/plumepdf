using System.Linq;
using System.Text;
using PlumePdf.Raster;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// Phase 9 bug (mesh shadings never reached production paint): a genuine <c>sh</c>
/// operator naming a mesh (<c>/ShadingType</c> 4-7) resource is a <c>PdfStream</c>, not a
/// <c>PdfDictionary</c> — <see cref="MeshShadingTests"/> exercises <c>MeshShading.Parse</c>/
/// <c>ShadingFactory.Build</c> directly, bypassing <c>RasterInterpreter.HandleSh</c>/<c>PaintObject</c>
/// entirely, so it could (and did) stay green while a real <c>sh</c> invocation through
/// <see cref="Rasterizer.Rasterize"/> painted a flat mid-gray placeholder instead of the mesh.
/// These tests drive a type-4 mesh through the full <see cref="Rasterizer.Rasterize"/> pipeline —
/// content stream, resource dictionary, <c>sh</c> operator — proving the stream is actually
/// threaded from the resource lookup through to per-pixel painting.
/// </summary>
public class MeshShadingPipelineTests
{
    private static byte[] Bytes(string content) => Encoding.ASCII.GetBytes(content);

    private static PdfArray NumArray(params double[] values) => new(values.Select(v => (PdfObject)PdfNumber.Get(v)));

    // A single free-form Gouraud triangle (/ShadingType 4): A=(10,10) red, B=(90,10) green,
    // C=(10,90) blue, all in the page's own user-space coordinates (no `cm`) so the resulting
    // device pixels are easy to reason about. Vertex record = flag(1B) + x(1B) + y(1B) +
    // R,G,B(1B each), matching MeshShadingTests' own fixture format.
    private static PdfStream BuildTriangleMeshStream()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("ShadingType"), PdfNumber.Get(4));
        dict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceRGB"));
        dict.Set(PdfName.Get("BitsPerCoordinate"), PdfNumber.Get(8));
        dict.Set(PdfName.Get("BitsPerComponent"), PdfNumber.Get(8));
        dict.Set(PdfName.Get("BitsPerFlag"), PdfNumber.Get(8));
        dict.Set(PdfName.Get("Decode"), NumArray(0, 255, 0, 255, 0, 1, 0, 1, 0, 1));

        byte[] payload =
        [
            0, 10, 10, 255, 0, 0, // A: red
            0, 90, 10, 0, 255, 0, // B: green
            0, 10, 90, 0, 0, 255, // C: blue
        ];

        return new PdfStream(dict, payload);
    }

    private static PdfDictionary ResourcesWithShading(PdfStream shadingStream)
    {
        var resources = new PdfDictionary();
        var shadingDict = new PdfDictionary();
        shadingDict.Set(PdfName.Get("Sh1"), shadingStream);
        resources.Set(PdfName.Get("Shading"), shadingDict);
        return resources;
    }

    [Fact]
    public void Rasterize_ShOperatorOnMeshStreamResource_PaintsRealPerVertexColors()
    {
        var resources = ResourcesWithShading(BuildTriangleMeshStream());
        var content = Bytes("/Sh1 sh");

        var frame = Rasterizer.Rasterize(content, resources, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100, PdfOptions.Default, RasterPaintContext.Default);
        var span = frame.Pixels.Span;

        // Page (10,10) -> device (10,90): near vertex A, red should dominate.
        AssertDominantChannel(span, 100, x: 13, y: 87, dominant: 0);
        // Page (90,10) -> device (90,90): near vertex B, green should dominate.
        AssertDominantChannel(span, 100, x: 87, y: 87, dominant: 1);
        // Page (10,90) -> device (10,10): near vertex C, blue should dominate.
        AssertDominantChannel(span, 100, x: 13, y: 13, dominant: 2);
    }

    [Fact]
    public void Rasterize_ShOperatorOnMeshStreamResource_DoesNotFloodFlatMidGray()
    {
        // The dead-wiring regression this whole test class exists to catch: before the
        // stream was threaded through, every mesh sh invocation fell back to ShadingFactory's
        // flat (128,128,128) mid-gray placeholder over the ENTIRE clip (the whole page here, no
        // W n). A real per-vertex render must show visibly different colors at the three corners
        // rather than one uniform gray value everywhere.
        var resources = ResourcesWithShading(BuildTriangleMeshStream());
        var content = Bytes("/Sh1 sh");

        var frame = Rasterizer.Rasterize(content, resources, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100, PdfOptions.Default, RasterPaintContext.Default);
        var span = frame.Pixels.Span;

        var (rA, gA, bA) = PixelRgb(span, 100, 13, 87);
        var (rB, gB, bB) = PixelRgb(span, 100, 87, 87);
        var (rC, gC, bC) = PixelRgb(span, 100, 13, 13);

        Assert.False(rA == 128 && gA == 128 && bA == 128, "vertex A pixel is the flat mid-gray placeholder, not a real mesh render");
        Assert.False(rB == 128 && gB == 128 && bB == 128, "vertex B pixel is the flat mid-gray placeholder, not a real mesh render");
        Assert.False(rC == 128 && gC == 128 && bC == 128, "vertex C pixel is the flat mid-gray placeholder, not a real mesh render");
        Assert.True(rA != rB || gA != gB || bA != bB, "vertex A and B pixels are identical — not a real per-vertex gradient");
    }

    private static (byte R, byte G, byte B) PixelRgb(System.ReadOnlySpan<byte> rgbaSpan, int width, int x, int y)
    {
        var offset = ((y * width) + x) * 4;
        return (rgbaSpan[offset], rgbaSpan[offset + 1], rgbaSpan[offset + 2]);
    }

    private static void AssertDominantChannel(System.ReadOnlySpan<byte> rgbaSpan, int width, int x, int y, int dominant)
    {
        var (r, g, b) = PixelRgb(rgbaSpan, width, x, y);
        var channels = new[] { r, g, b };
        for (var i = 0; i < 3; i++)
        {
            if (i != dominant)
            {
                Assert.True(channels[dominant] > channels[i], $"Pixel ({x},{y}) expected channel {dominant} ({channels[dominant]}) to dominate channel {i} ({channels[i]}).");
            }
        }
    }
}
