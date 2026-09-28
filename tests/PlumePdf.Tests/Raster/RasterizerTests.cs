using System.Text;
using PlumePdf.Raster;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// <see cref="Rasterizer"/>'s entry point end-to-end — renders a
/// simple path+image page to a <see cref="RasterImageFrame"/>, with caps enforced before
/// allocation.
/// </summary>
public class RasterizerTests
{
    private static byte[] Bytes(string content) => Encoding.ASCII.GetBytes(content);

    [Fact]
    public void Rasterize_EmptyContent_ReturnsPlainBackgroundSurface()
    {
        var frame = Rasterizer.Rasterize(ReadOnlyMemory<byte>.Empty, resources: null, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 20, pixelHeight: 20, PdfOptions.Default, RasterPaintContext.Default);
        Assert.Equal(20, frame.Width);
        Assert.Equal(20, frame.Height);
        Assert.Equal(RasterPixelFormat.Rgba32, frame.Format);

        var span = frame.Pixels.Span;
        Assert.Equal(255, span[0]); // R
        Assert.Equal(255, span[1]); // G
        Assert.Equal(255, span[2]); // B
        Assert.Equal(255, span[3]); // A
    }

    [Fact]
    public void Rasterize_FilledRectangleCoveringWholePage_FillsWholeFrameWithColor()
    {
        var content = Bytes("1 0 0 rg 0 0 100 100 re f");
        var frame = Rasterizer.Rasterize(content, resources: null, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 10, pixelHeight: 10, PdfOptions.Default, RasterPaintContext.Default);

        var span = frame.Pixels.Span;
        var centerOffset = ((5 * 10) + 5) * 4;
        Assert.Equal(255, span[centerOffset]); // R
        Assert.Equal(0, span[centerOffset + 1]); // G
        Assert.Equal(0, span[centerOffset + 2]); // B
    }

    [Fact]
    public void Rasterize_PathAndImage_RendersBothOntoOneFrame()
    {
        // Bottom-left quarter (in PDF user space, y-up) painted blue by the path; the image
        // XObject "Do" call is expected to be skipped (no ImageResolver supplied) without
        // throwing — proving path rendering and a graceful image-skip coexist on one page.
        var content = Bytes("0 0 1 rg 0 0 50 50 re f /Im1 Do");
        var resources = new PdfDictionary();
        var xobjects = new PdfDictionary();
        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Subtype, PdfName.Get("Image"));
        xobjects.Set(PdfName.Get("Im1"), new PdfStream(imageDict, ReadOnlyMemory<byte>.Empty));
        resources.Set(PdfName.Get("XObject"), xobjects);

        var frame = Rasterizer.Rasterize(content, resources, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100, PdfOptions.Default, RasterPaintContext.Default);

        var span = frame.Pixels.Span;
        // Bottom-left of the page is the *bottom* of the device image (y-flip), so check near
        // device row 90 (close to the bottom), column 10.
        var offset = ((90 * 100) + 10) * 4;
        Assert.Equal(0, span[offset]); // R
        Assert.Equal(0, span[offset + 1]); // G
        Assert.Equal(255, span[offset + 2]); // B
    }

    [Fact]
    public void Rasterize_ImageXObjectWithResolver_PaintsDecodedPixels()
    {
        // "10 0 0 10 0 0 cm" scales the image's unit square up to fill the whole 10x10-unit page.
        var content = Bytes("q 10 0 0 10 0 0 cm /Im1 Do Q");
        var resources = new PdfDictionary();
        var xobjects = new PdfDictionary();
        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Subtype, PdfName.Get("Image"));
        var stream = new PdfStream(imageDict, ReadOnlyMemory<byte>.Empty);
        xobjects.Set(PdfName.Get("Im1"), stream);
        resources.Set(PdfName.Get("XObject"), xobjects);

        var frame = Rasterizer.Rasterize(
            content,
            resources,
            mediaBoxWidth: 10,
            mediaBoxHeight: 10,
            pixelWidth: 10,
            pixelHeight: 10,
            PdfOptions.Default, RasterPaintContext.Default,
            imageResolver: (_, _) => new RasterImageFrame(new byte[] { 10, 20, 30 }, 1, 1, RasterPixelFormat.Rgb24));

        var span = frame.Pixels.Span;
        var offset = ((5 * 10) + 5) * 4;
        Assert.Equal(10, span[offset]);
        Assert.Equal(20, span[offset + 1]);
        Assert.Equal(30, span[offset + 2]);
    }

    [Fact]
    public void Rasterize_OverSurfaceCap_ThrowsPlume7500BeforeAnyPainting()
    {
        var ex = Assert.Throws<PlumePdfException>(() => Rasterizer.Rasterize(ReadOnlyMemory<byte>.Empty, null, 100, 100, 10_000, 10_000, PdfOptions.Default, RasterPaintContext.Default, maxSurfaceBytes: 1000));
        Assert.Equal("PLUME7500", ex.Code);
    }

    [Fact]
    public void PageToDeviceCtm_FlipsYAndScalesToFillPixelTarget()
    {
        var ctm = Rasterizer.PageToDeviceCtm(mediaBoxWidth: 200, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100);

        // Bottom-left of user space (0,0) maps to bottom-left of device space (y = pixelHeight).
        var (bx, by) = ctm.Transform(0, 0);
        Assert.Equal(0, bx, 6);
        Assert.Equal(100, by, 6);

        // Top-right of user space (200,100) maps to top-right of device space (0,0).
        var (tx, ty) = ctm.Transform(200, 100);
        Assert.Equal(100, tx, 6);
        Assert.Equal(0, ty, 6);
    }

    // ---- Pixel-determinism regression ----

    [Fact]
    public void Rasterize_UnderDeterministic_SamePageRenderedTwice_ProducesByteIdenticalRawBuffers()
    {
        var options = PdfOptions.Default with { Deterministic = true };
        var content = Bytes(
            "q 0.6 0.1 0.9 rg 5 5 40 60 re f Q " +
            "q 1 0 0 1 20 20 cm 0.2 0.7 0.3 RG 3 w 1 J 1 j " +
            "0 0 m 30 40 l 60 0 l S Q " +
            "0 0 0.5 0.5 k 10 10 15 15 re f");

        var first = Rasterizer.Rasterize(content, resources: null, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 64, pixelHeight: 64, options, RasterPaintContext.Default);
        var second = Rasterizer.Rasterize(content, resources: null, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 64, pixelHeight: 64, options, RasterPaintContext.Default);

        Assert.True(first.Pixels.Span.SequenceEqual(second.Pixels.Span), "raw BGRA/RGBA pixel buffers must be byte-identical across repeated deterministic renders of the same page");

        // Sanity: the page actually painted something (not two identical blank backgrounds).
        var allWhite = true;
        var span = first.Pixels.Span;
        for (var i = 0; i < span.Length; i += 4)
        {
            if (span[i] != 255 || span[i + 1] != 255 || span[i + 2] != 255)
            {
                allWhite = false;
                break;
            }
        }

        Assert.False(allWhite, "test fixture should paint visible content, not render a blank page");
    }

    [Fact]
    public void Rasterize_UnderDeterministic_RepeatedRendersAcrossManyIterations_StayIdentical()
    {
        // Guards against nondeterminism that only shows up occasionally (e.g. an accidental
        // Dictionary-iteration-order dependency in the display-list build) rather than
        // every single run.
        var options = PdfOptions.Default with { Deterministic = true };
        var content = Bytes("0.1 0.2 0.3 rg 1 1 8 8 re f 0.9 0.8 0.7 RG 2 w 0 0 m 10 10 l S");

        var baseline = Rasterizer.Rasterize(content, null, 10, 10, 32, 32, options, RasterPaintContext.Default).Pixels.ToArray();
        for (var i = 0; i < 20; i++)
        {
            var repeat = Rasterizer.Rasterize(content, null, 10, 10, 32, 32, options, RasterPaintContext.Default).Pixels.ToArray();
            Assert.True(baseline.AsSpan().SequenceEqual(repeat), $"iteration {i} diverged from the baseline render");
        }
    }
}
