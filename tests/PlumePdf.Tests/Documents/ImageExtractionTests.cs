using PlumePdf.Compose;
using PlumePdf.Documents;
using PlumePdf.Elements;
using PlumePdf.Objects;
using PlumePdf.Tests.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Documents;

/// <summary>
/// <see cref="ImageExtractor"/> — DCT pass-through byte identity, Flate sample
/// decode, the JPEG 2000 direct-decode path, and the
/// unregistered-filter diagnostic path.
/// </summary>
public class ImageExtractionTests
{
    [Fact]
    public void ExtractImages_ComposedRgbImage_DecodesBackToOriginalPixels()
    {
        byte[] pixels = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]; // 2x2 RGB
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Image(new Image(pixels, pixelWidth: 2, pixelHeight: 2));
        });

        var images = document.Pages[0].ExtractImages();

        var image = Assert.Single(images);
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.False(image.IsJpeg);
        Assert.False(image.IsRawEncoded);
        Assert.Equal(pixels, image.Data.ToArray());
    }

    [Fact]
    public void ExtractImages_DctFilteredImage_PassesThroughByteIdentical()
    {
        byte[] fakeJpegBytes = [0xFF, 0xD8, 0x00, 0x01, 0x02, 0x03, 0xFF, 0xD9];
        var (document, _) = BuildSyntheticImageDocument(("DCTDecode", fakeJpegBytes));

        var images = document.Pages[0].ExtractImages();

        var image = Assert.Single(images);
        Assert.True(image.IsJpeg);
        Assert.False(image.IsRawEncoded);
        Assert.Equal(fakeJpegBytes, image.Data.ToArray());
    }

    [Fact]
    public void ExtractImages_FlateThenDctChain_UnwrapsToIntactJpegBytes()
    {
        // /Filter [/FlateDecode /DCTDecode] returned the FLATE bytes labeled as "an intact
        // JPEG file". The prefix must be decoded so Data is the real JPEG.
        byte[] fakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 0xFF, 0xD9];
        byte[] deflated;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(fakeJpeg);
            }

            deflated = buffer.ToArray();
        }

        var filterChain = new PdfArray([PdfName.Get("FlateDecode"), PdfName.Get("DCTDecode")]);
        var (document, _) = BuildSyntheticImageDocument(filterChain, deflated);
        using var _doc = document;

        var image = Assert.Single(document.Pages[0].ExtractImages());

        Assert.True(image.IsJpeg);
        Assert.False(image.IsRawEncoded);
        Assert.Equal(fakeJpeg, image.Data.ToArray());
    }

    [Fact]
    public void ExtractImages_UnregisteredVendorFilter_ReturnsRawBytesAndRecordsDiagnostic()
    {
        // JPXDecode is registered by default, so it can no longer stand in for "a filter
        // PlumePDF has no decoder for" - a vendor name a caller hasn't registered an
        // IPdfFilter for (PdfFilterRegistry's extension seam) keeps this test's real premise
        // (no decoder registered -> PLUME3010 -> raw bytes + PLUME6023) true.
        byte[] rawEncoded = [0x01, 0x02, 0x03, 0x04];
        var (document, _) = BuildSyntheticImageDocument(("PlumeVendorTestDecode", rawEncoded));

        var (images, diagnostics) = document.Pages[0].ExtractImagesWithDiagnostics();

        var image = Assert.Single(images);
        Assert.True(image.IsRawEncoded);
        Assert.False(image.IsJpeg);
        Assert.Equal(rawEncoded, image.Data.ToArray());
        Assert.Contains(diagnostics, d => d.Code == "PLUME6023");
    }

    /// <summary>
    /// A terminal, non-overridden <c>JPXDecode</c> decodes directly
    /// via <c>JpxImageDecoder</c> - <see cref="ExtractedImage.Width"/>/<see cref="ExtractedImage.Height"/>/
    /// <see cref="ExtractedImage.BitsPerComponent"/> come from the codestream's own <c>SIZ</c>
    /// facts, not the deliberately-wrong 1x1 dictionary <c>BuildSyntheticImageDocument</c>
    /// declares - proving the codestream, not the dictionary, is authoritative for JPX geometry,
    /// exactly like the DCT pass-through's own file-is-authoritative contract.
    /// </summary>
    [Fact]
    public void ExtractImages_Jpx_DecodesWithCodestreamMetadata()
    {
        // The real 8x8 lossless JP2_RGB_8x8 payload, read back from its own committed fixture
        // rather than re-embedding the literal here - one frozen payload, one source of truth.
        var fixturePath = Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "images", "jpx-small.pdf");
        using var jpxSource = PdfDocument.Open(fixturePath);
        var sourceResources = (PdfDictionary)jpxSource.Pages[0].Dictionary[PdfName.Get("Resources")];
        var sourceXObjects = (PdfDictionary)sourceResources[PdfName.Get("XObject")];
        var sourceImageRef = (PdfReference)sourceXObjects[PdfName.Get("Im0")];
        var sourceImageStream = (PdfStream)jpxSource.Objects[sourceImageRef.Target];
        var jp2Bytes = sourceImageStream.RawBytes.ToArray();

        var (document, _) = BuildSyntheticImageDocument(("JPXDecode", jp2Bytes));
        using var _doc = document;

        var (images, diagnostics) = document.Pages[0].ExtractImagesWithDiagnostics();

        var image = Assert.Single(images);
        Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME6023");
        Assert.False(image.IsJpeg);
        Assert.False(image.IsRawEncoded);
        Assert.Null(image.Decode);
        Assert.Equal(8, image.Width); // the codestream's own SIZ - NOT the synthetic dict's declared 1x1.
        Assert.Equal(8, image.Height);
        Assert.Equal(8, image.BitsPerComponent); // codestream precision <= 8 -> 8, never the dict's own declared value.
        Assert.Equal("DeviceGray", image.ColorSpaceName); // dictionary's own declared /ColorSpace still wins when present.
        Assert.Equal(8 * 8 * 3, image.Data.Length); // 8x8 RGB, 3 colour channels, 8-bit interleaved - the adapter byte contract.

        // Declared DeviceGray (1 component) disagrees with
        // the codestream's real 3 colour channels, and Data is packed at the codestream's
        // count - a caller trusting ColorSpaceName alone would compute the wrong buffer size.
        // The extraction seam must record the same PLUME7753 disagreement the rasterizer's
        // ImageXObjectResolver already reports for this exact mismatch shape.
        var mismatch = Assert.Single(diagnostics, d => d.Code == "PLUME7753");
        Assert.Equal(DiagnosticSeverity.Warning, mismatch.Severity);
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

    [Fact]
    public void ExtractImages_NoImages_ReturnsEmptyList()
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("No images here");
        });

        var images = document.Pages[0].ExtractImages();

        Assert.Empty(images);
    }

    [Fact]
    public void ExtractImages_IndirectDecodeParms_ResolvesAndAppliesPredictor()
    {
        // PdfFilterRegistry.Decode accepts
        // an optional resolver so an indirect /DecodeParms entry (uncommon, but conformant per
        // ISO 32000-1 §7.4 — both /Filter and /DecodeParms may be indirect) resolves instead
        // of being silently treated as absent, the retired PLUME3011 degradation.
        // ImageExtractor is one of the call sites now threading document.Objects through as
        // that resolver. Predictor 2 (TIFF horizontal differencing), 1 color, 8bpc,
        // 2 columns: the original 2x2 grayscale rows [10, 20] and [30, 40] encode to
        // [10, 10] and [30, 10] (byte 0 unchanged, byte 1 = original - previous byte in row).
        // Without the resolver, /DecodeParms reads as absent, predictor un-filtering never
        // runs, and Data would still be the un-predicted [10, 10, 30, 10] Flate output.
        byte[] predicted = [10, 10, 30, 10];
        byte[] deflated;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(predicted);
            }

            deflated = buffer.ToArray();
        }

        var decodeParmsDict = new PdfDictionary();
        decodeParmsDict.Set(PdfName.Predictor, PdfNumber.Get(2));
        decodeParmsDict.Set(PdfName.Colors, PdfNumber.Get(1));
        decodeParmsDict.Set(PdfName.BitsPerComponent, PdfNumber.Get(8));
        decodeParmsDict.Set(PdfName.Columns, PdfNumber.Get(2));

        using var document = BuildSyntheticImageDocumentWithIndirectDecodeParms(deflated, decodeParmsDict);

        var image = Assert.Single(document.Pages[0].ExtractImages());

        Assert.Equal(new byte[] { 10, 20, 30, 40 }, image.Data.ToArray());
    }

    // Same shape as BuildSyntheticImageDocument below, but the image's /DecodeParms is an
    // indirect reference to a separate object (6 0 obj) rather than a direct dictionary — the
    // uncommon-but-conformant shape the resolver seam exists to handle.
    private static PdfDocument BuildSyntheticImageDocumentWithIndirectDecodeParms(byte[] rawBytes, PdfDictionary decodeParmsDict)
    {
        var decodeParmsReference = new IndirectReference(6, 0);

        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Type, PdfName.Get("XObject"));
        imageDict.Set(PdfName.Subtype, PdfName.Get("Image"));
        imageDict.Set(PdfName.Get("Width"), PdfNumber.Get(2));
        imageDict.Set(PdfName.Get("Height"), PdfNumber.Get(2));
        imageDict.Set(PdfName.BitsPerComponent, PdfNumber.Get(8));
        imageDict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceGray"));
        imageDict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        imageDict.Set(PdfName.DecodeParms, new PdfReference(decodeParmsReference));
        var imageStream = new PdfStream(imageDict, rawBytes);

        var xobjectDict = new PdfDictionary();
        xobjectDict.Set(PdfName.Get("Im0"), new PdfReference(new IndirectReference(4, 0)));

        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjectDict);

        var pageDict = new PdfDictionary();
        pageDict.Set(PdfName.Type, PdfName.Get("Page"));
        pageDict.Set(PdfName.Get("Parent"), new PdfReference(new IndirectReference(2, 0)));
        pageDict.Set(PdfName.Get("MediaBox"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(200), PdfNumber.Get(200)]));
        pageDict.Set(PdfName.Get("Resources"), resources);

        var pagesDict = new PdfDictionary();
        pagesDict.Set(PdfName.Type, PdfName.Get("Pages"));
        pagesDict.Set(PdfName.Get("Kids"), new PdfArray([new PdfReference(new IndirectReference(3, 0))]));
        pagesDict.Set(PdfName.Get("Count"), PdfNumber.Get(1));

        var catalogDict = new PdfDictionary();
        catalogDict.Set(PdfName.Type, PdfName.Get("Catalog"));
        catalogDict.Set(PdfName.Get("Pages"), new PdfReference(new IndirectReference(2, 0)));

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(7));
        trailer.Set(PdfName.Root, new PdfReference(new IndirectReference(1, 0)));

        var objects = new Dictionary<int, PdfObject>
        {
            [1] = catalogDict,
            [2] = pagesDict,
            [3] = pageDict,
            [4] = imageStream,
            [decodeParmsReference.Number] = decodeParmsDict,
        };

        var source = new InMemoryObjectSource(trailer, objects);
        return PdfDocument.CreateSynthetic(source);
    }

    // Builds a single-page synthetic document (bypassing PdfDocument.Open entirely, via the
    // same InMemoryObjectSource CreateSynthetic uses for Pdf.Merge/Split) whose one image
    // XObject has exactly the given filter and raw stream bytes - lets image-extraction tests
    // exercise the DCT/unsupported-filter paths PdfDocument.Compose has no writer support for.
    /// <summary>
    /// Regression test: a JP2
    /// whose only component is <c>cdef</c> opacity has zero colour channels, so there are no
    /// samples to return. The old path "succeeded" with a 16x16 <c>DeviceGray</c>
    /// <see cref="ExtractedImage"/> whose <c>Data.Length == 0</c>; it must instead take the same
    /// degrade-to-raw-bytes branch a failed decode takes (<c>IsRawEncoded</c>, the still-encoded
    /// bytes, <c>PLUME6023</c>), as the rasterizer's own <c>PLUME7746</c> refusal does on its side.
    /// </summary>
    [Fact]
    public void ExtractImages_JpxAlphaOnly_DegradesToRawBytesWith6023()
    {
        var codestream = new J2kBuilder { Csiz = 1, Body = new byte[2] }.BuildCodestream();
        var jp2 = J2kBuilder.Jp2(codestream, J2kBuilder.Ihdr(16, 16, 1, 7), J2kBuilder.ColrEnum(17), J2kBuilder.Cdef((0, 1, 0)));

        var (document, _) = BuildSyntheticImageDocument(("JPXDecode", jp2));
        using var _doc = document;

        var (images, diagnostics) = document.Pages[0].ExtractImagesWithDiagnostics();

        var image = Assert.Single(images);
        Assert.True(image.IsRawEncoded);
        Assert.False(image.IsJpeg);
        Assert.Equal(jp2, image.Data.ToArray());
        var degraded = Assert.Single(diagnostics, d => d.Code == "PLUME6023");
        Assert.Contains("no colour channels", degraded.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed in-house JPEG 2000 decode records ONE diagnostic —
    /// <c>PLUME6023</c> — and no separate <c>PLUME37xx</c> entry, so the underlying code must be
    /// named inside that message (the <c>CODE: message</c> shape the rasterizer's <c>PLUME7744</c>
    /// already uses) or the caller has no way to learn WHY the codestream was refused.
    /// <c>jpx-garbage.pdf</c>'s payload is 32 zero bytes: not JPEG 2000 at all → <c>PLUME3700</c>.
    /// </summary>
    [Fact]
    public void ExtractImages_JpxGarbage_6023MessageNamesTheUnderlyingCode()
    {
        using var document = PdfDocument.Open(Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "images", "jpx-garbage.pdf"));

        var (images, diagnostics) = document.Pages[0].ExtractImagesWithDiagnostics();

        var image = Assert.Single(images);
        Assert.True(image.IsRawEncoded);
        var degraded = Assert.Single(diagnostics, d => d.Code == "PLUME6023");
        Assert.Contains("(PLUME3700: ", degraded.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME3700"); // named inside 6023, never recorded alongside.
    }

    /// <summary>
    /// Regression test: under <c>/Indexed</c> the JPX samples
    /// ARE palette indices and must reach the caller at their raw value — the full-scale rescale
    /// (index 8 of a 4-bit plane → 136; 12-bit index 2048 → 32776) produced values that index
    /// nothing, while the rasterizer's own resolver looks up the raw <c>SampleUnsigned</c>. The
    /// all-zero <see cref="J2kBuilder"/> codestream decodes to the DC-shift value
    /// <c>2^(precision−1)</c> on every sample, which is exactly the value the rescale would have
    /// moved. <c>BitsPerComponent</c> stays the container width (8 for ≤8-bit indices, 16 above,
    /// big-endian) that <see cref="ExtractedImage.Data"/>'s layout contract describes.
    /// </summary>
    [Theory]
    [InlineData(4, 8, new byte[] { 0x08 })]
    [InlineData(8, 8, new byte[] { 0x80 })]
    [InlineData(12, 16, new byte[] { 0x08, 0x00 })]
    public void ExtractImages_JpxIndexed_ReturnsRawIndicesAtNativePrecision(int precision, int expectedBitsPerComponent, byte[] expectedSampleBytes)
    {
        var codestream = new J2kBuilder { Components = [(precision - 1, 1, 1)], Body = new byte[2] }.BuildCodestream();
        var lookup = new byte[256 * 3];
        var indexed = new PdfArray([PdfName.Get("Indexed"), PdfName.Get("DeviceRGB"), PdfNumber.Get(255), PdfString.FromLiteral(lookup)]);

        var (document, _) = BuildSyntheticImageDocument(PdfName.Get("JPXDecode"), codestream, indexed);
        using var _doc = document;

        var (images, diagnostics) = document.Pages[0].ExtractImagesWithDiagnostics();

        var image = Assert.Single(images);
        Assert.False(image.IsRawEncoded);
        Assert.Equal("Indexed", image.ColorSpaceName);
        Assert.Equal(expectedBitsPerComponent, image.BitsPerComponent);
        Assert.Equal(16 * 16 * expectedSampleBytes.Length, image.Data.Length);
        var expected = Enumerable.Repeat(expectedSampleBytes, 16 * 16).SelectMany(static b => b).ToArray();
        Assert.Equal(expected, image.Data.ToArray());
        Assert.DoesNotContain(diagnostics, d => d.Code is "PLUME6023" or "PLUME7753");
    }

    private static (PdfDocument Document, IndirectReference ImageReference) BuildSyntheticImageDocument((string Filter, byte[] RawBytes) image) =>
        BuildSyntheticImageDocument(PdfName.Get(image.Filter), image.RawBytes);

    private static (PdfDocument Document, IndirectReference ImageReference) BuildSyntheticImageDocument(PdfObject filterValue, byte[] rawBytes, PdfObject? colorSpace = null)
    {
        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Type, PdfName.Get("XObject"));
        imageDict.Set(PdfName.Subtype, PdfName.Get("Image"));
        imageDict.Set(PdfName.Get("Width"), PdfNumber.Get(1));
        imageDict.Set(PdfName.Get("Height"), PdfNumber.Get(1));
        imageDict.Set(PdfName.BitsPerComponent, PdfNumber.Get(8));
        imageDict.Set(PdfName.Get("ColorSpace"), colorSpace ?? PdfName.Get("DeviceGray"));
        imageDict.Set(PdfName.Filter, filterValue);
        var imageStream = new PdfStream(imageDict, rawBytes);

        var xobjectDict = new PdfDictionary();
        xobjectDict.Set(PdfName.Get("Im0"), new PdfReference(new IndirectReference(4, 0)));

        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjectDict);

        var pageDict = new PdfDictionary();
        pageDict.Set(PdfName.Type, PdfName.Get("Page"));
        pageDict.Set(PdfName.Get("Parent"), new PdfReference(new IndirectReference(2, 0)));
        pageDict.Set(PdfName.Get("MediaBox"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(200), PdfNumber.Get(200)]));
        pageDict.Set(PdfName.Get("Resources"), resources);

        var pagesDict = new PdfDictionary();
        pagesDict.Set(PdfName.Type, PdfName.Get("Pages"));
        pagesDict.Set(PdfName.Get("Kids"), new PdfArray([new PdfReference(new IndirectReference(3, 0))]));
        pagesDict.Set(PdfName.Get("Count"), PdfNumber.Get(1));

        var catalogDict = new PdfDictionary();
        catalogDict.Set(PdfName.Type, PdfName.Get("Catalog"));
        catalogDict.Set(PdfName.Get("Pages"), new PdfReference(new IndirectReference(2, 0)));

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(5));
        trailer.Set(PdfName.Root, new PdfReference(new IndirectReference(1, 0)));

        var objects = new Dictionary<int, PdfObject>
        {
            [1] = catalogDict,
            [2] = pagesDict,
            [3] = pageDict,
            [4] = imageStream,
        };

        var source = new InMemoryObjectSource(trailer, objects);
        var document = PdfDocument.CreateSynthetic(source);
        return (document, new IndirectReference(4, 0));
    }
}
