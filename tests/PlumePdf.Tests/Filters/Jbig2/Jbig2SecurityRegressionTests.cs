using PlumePdf.Filters;
using PlumePdf.Filters.Jbig2;
using Xunit;

namespace PlumePdf.Tests.Filters.Jbig2;

/// <summary>
/// Security-review regressions (pixel caps enforced <b>before</b> allocation): a hostile
/// JBIG2 stream reachable from plain
/// <c>PdfDocument.Open(untrusted).Pages[0].ExtractImages()</c> must never surface an
/// uncatchable <see cref="OutOfMemoryException"/> (unbounded <c>new bool[height, width]</c>
/// from document-supplied segment fields) or an <see cref="IndexOutOfRangeException"/>
/// (positive AT-pixel offsets indexing past the bitmap height). Both must instead land as
/// coded <see cref="PlumePdfException"/>s / Warning-diagnostic per-segment degradation
/// (the same LZW-precedent decode-as-far-as-possible shape used elsewhere). The three embedded
/// PDFs are the review's mutated-fixture repros (pp-pdffuzz-1/2/3), kept byte-identical here so
/// the tests stay hermetic; the hand-built streams below pin each individual allocation site.
/// </summary>
public class Jbig2SecurityRegressionTests
{
    // --- End-to-end repros: the review's fuzzed PDFs through the public extraction seam ---

    // pp-pdffuzz-1: page-info segment declares a ~1.6-billion-pixel-wide page (width field
    // mutated to 0x5F000084) - pre-fix, ProcessPageInfo allocated bool[height, width]
    // straight from those fields and died with an uncaught OutOfMemoryException.
    private const string PageInfoBombPdf =
        "JVBERi0xLooNCiX/9uTnDQoxIDAgb2JqDQo8PA0KL1R5cGUgL0NhdGFsb2cNCi9PcGVuQWN0aW9uIFs0IDAgUiAvRml0XQ0KL1BhZ2VzIDMgMCBSDQo+Pg0K" +
        "ZW5kb2JqDQoNCjIgMCBvYmoNCjw8DQovVHlwZSAvRm9udA0KL1N1YnR5cGUgL1R5cGUxDQovQmFzZUZvbnQgL0hlbHZldGljYQ0KL0VuY29kaW5nIC9XaW5B" +
        "bnNpRW5jb2RpbmcNCj4+DQplbmRvYmoNCg0KMyAwIG9iag0KPDwNCi9UeXBlIC9QYWdlcw0KL0NvdW50IDENCi9LaWRzIFsNCjQgMCBSDQpdDQo+Pg0KZW5k" +
        "b2JqDQoNCjQgMCBvYmoNCjw8DQovVHlwZSAvUGFnBw0KL1BhcmVudCAzIDAgUg0KL0NvbnRlbnRzIDUgMCBSDQovTWVkaWFCb3ggWzAgMCA1OTUuMjc1NiA4" +
        "NDEuODg5OF0NCi9SZXNvdXJjZXMgPDwNCiAgICAvRm9udG08PCAvRjEgMiAwIFIgPj4NCiAgICAvWE9iamVjdCA8PCAvSW0xIDYgMCBSID4+DQogID4+DQo+" +
        "Pg0KZW5kb2JqDQoNCjUgMCBvYmoNCjw8IC9MZW5ndGggNDYgPj4NCnN0cmVhbQ0KcQ0KICA1MDAuMDAgMCAwIDUzLjAzMCA0OCA1NTAgY20NCiAgL0ltMSBE" +
        "bw0KUQ0KZW5kc3RyZWFtDQplbmRvYmoNCg0KNiAwIG9iag0KPDwNCi9UeXBlIC9YT2JqZWN0DQovU3VidHlwZSAvSW1hZ2UNCi9XaWR0aCAxMzINCi9IZWln" +
        "aHQgMTQNCi9CaXRzUGVyQ29tcG9uZW50IDENCi9Db2xvclNwYWNlIC9EZXZpY2VHcmF5DQovRmlsdGVyIC9KQklHMkRlY29kZQ0KL0xlbmd0aCAxOTENCj4+" +
        "DQpzdHJlYW0NCgAAAAAwAAEAAAATXwAAhAAAAA4AAAAAAAAAAAEAAAAAAAEAAQEAAABkCAAC/wAAAAgAAAAIOkg3iqy0BjDgkep75WQd/m2/8EWNvY0gkc5t" +
        "cHTm85FPbi9ou+AvbOtrheIo0lRTEh6R0c5KRCGBPNKLnUP/KtAYszHJwfNPX7s3X2A3ou+KuVPcNBv/rAAAAAIHIAEBAAAAJgAAAIQAAAAOAAAAAAAAAAAA" +
        "AAQAAAAJ6NCe+drwh3BhxTn6v/+sDQplbmRzdHJlYW0NCmVuZG9iag0KDQp4cmVmDQowIDcNCjDKMDAwMDAwMDAgNjU1MzUgZg0KMDAwMDAwMDAxNyAwMDAw" +
        "MCBuDQowMDAwMDAwMTAwIDAwMDAwIG4NCjAwMDAwMDAyMDcgMDAwMDAgbg0KMDAwMDAwMDI3NyAwMDAwMCBuDQowMDAwMDAwNDYzIDAwMDAwIG4NCjAwMDAw" +
        "MDA1NjcgMDAwMDAgbg0KDQp0cmFpbGVyDQo8PCAvU2l6ZSA3DQovUm9vdCAxIDAgUiA+Pg0Kc3RhcnR4cmVmDQo5NDQNCiUlRU9GDQo=";

    // pp-pdffuzz-2: a generic-region segment whose AT pixels were mutated to positive Y
    // offsets - pre-fix, DecodeGenericBitmap's template read checked i0 >= 0 but never
    // i0 < height, so the read indexed past the bitmap and threw IndexOutOfRangeException.
    private const string PositiveAtPixelPdf =
        "JVBERi0xLjQKJbW2CgoxIDAgb2JqCjw8CiAgL1R5cGUgL0NhdGFsb2cKICAvUGFnZXMgMiAwIFIKPj4KZW5kb2JqCgoyIDAgb2JqCjw8CiAgL1R5cGUgL1Bh" +
        "Z2VzCiAgL0tpZHMgWzMgMCBSXQogIC9Db3VudCAxCj4+CmVuZG9iagoKMyAwIG9iago8PAogIC9UeXBlIC9QYWdlCiAgL1BhcmVudCAyIDAgUgogIC9NZWRp" +
        "YUJveCBbMCAwIDM5OSA0MDBdCiAgL0NvbnRlbnRzIDQgMCBSIyAgL1Jlc291cmNlcyA8PAogICAgL1hPYmplY3QgPDwKICAgICAgL0ltIDUgMCBSCiAgICA+" +
        "PgogID4+Cj4+CmVuZG9iagoKNCAwIG9iago8PC9MZW5ndGggMjU+PgpzdHJlYW0KMzk5IDAgMCA0MDAgMCAwIGNtCi9JbSBEbwplbmRzdHJlYW0KZW5kb2Jq" +
        "Cgo1IDAgb2JqCjw8CiAgL0xlbmd0aCA0MzkKICAvVHlwZSAvWE9iamVjdAogIC9TdWJ0eXBlIC9JbWFnZQogIC9XaWR0aCAzOTkKICAvSGVpZ2h0IDQwMAog" +
        "IC9Db2xvclNwYWNlIC9EZXZpY2VHcmF5CiAgL0ZpbHRlciAvSkJJRzJEZWNvZGUKICAvQml0c1BlckNvbXBvbmVudCAxCj4+CnN0cmVhbQoAAAAAMAABAAAA" +
        "EwAAAY8AAAGQAAAAAAAAAAABAAAAAAEAAAEBAAAAqgAAA//9/wIb/v4AAAABAAAAARCcJtJuELZfe5ufhC1JfP8OW+/f54v+9l0u/FAyclXiOLd6l8LzOhj4" +
        "gTr9ZvpPAsE0fSM6xahy6jPXOpVRSIRXJZoKsb+jhG6s0TV2JGzocOYx/03Ox2mEeGc7Q6fb2OqFzwX5GZD6EPZOqm53fLYcklhfZA4hrxwmV9B5LFEW2WW1" +
        "uSBOJ+XkWIaeiipRog2U/0nYL/+sAAABAQcgAQABAAAAGwAAAY8AAADIAAAAAAAAAAAAABgAAAABk+7/rAABAAAAAQEAAACHAAAD//3/Av7+/gAAAAEAAAAB" +
        "EJwm0mffA6t6UC8j0wP/f/9//lTtBP3bAisAQXef5S2GGRiXJsxOhcHgq7f5f4OAPNPL+kZlM2Da9bFURCXHLd65h0/XYth6L6sYlLgyyhbdIG+ase0LpkoA" +
        "vMZP5lkZzhPjdmDzdx1RL7sgMHylv/uCv/+sAAEAAQcgAAEAAAEAAAAbAAABjwAAAMgAAAAAAAAAyAAAGAAAAAGT7v+sCmVuZHN0cmVhbQplbmRvYmoKCnhy" +
        "ZWYKMCA2CjAwMDAwMDAwMDAgNjU1MzYgZiAKMDAwMDAwMDAxNCAwMDAwMCBuIAowMDAwMDAwMDY4IDAwMDAwIG4gCjAwMDAwMDAxMzIgMDAwMDAgbiDLMDAw" +
        "MDAwMDI4OCAwMDAwMCBuIAowMDAwMDAwMzYyIDAwMDAwIG4gCgp0cmFpbGVyCjw8CiAgL1NpemUgNgogIC9Sb290IDEgMCBSCj4+CnN0YXJ0eHJlZgo5ODgK" +
        "JSVFT0YK";

    // pp-pdffuzz-3: a region segment's dimension fields mutated to multi-billion-pixel values
    // (0x49[..]1E00-scale width) - pre-fix, the region decoders allocated the region bitmap
    // before ComposeRegion's after-the-fact cap could run (uncaught OutOfMemoryException; at
    // sub-OOM sizes, a silent multi-hundred-MB allocation).
    private const string RegionBombPdf =
        "JVBERi0xLjQKJbW2CgoxIDAgb2JqCjw8CiAgL1R5cGUgL0NhdGFsb2cKICAvUGFnZXMgMiAwIFIKPj4KZW5kb2JqCgoyIDAgb2JqCjw8CiAgL1R5cGUgL1Bh" +
        "Z2VzCiAgL0tpZHMgWzMgMCBSXQogIC9Db3VudCAxCj4+CmVuZG9iagoKMyAwIG9iago8PAogIC9UeXBlIC9QYWdlCiAgL1BhcmVudCAyIDAgUgogIC9NZWRp" +
        "YUJveCBbMCAwIDM5OSA0MDBdCiAgL0NvbnRlbnRzIDQgMCBSCiAgL1Jlc291cmNlcyA8PAogICAgL1hPYmplY3QgPDwKICAgmCAgL0ltIDUgMCBSCiAgICA+" +
        "PgogID4+Cj4+CmVuZG9iagoKNCAwIG9iago8PC9MZW5ndGggMjU+PgpzdHJlYW0KMzk5IDAgMCA0MDAgMCAwIGNtCi9JbSBEbwplbmRzdHJlYW0KZW5kb2Jq" +
        "Cgo1IDAgb2JqCjw8CiAgL0xlbmd0aCA2NjEKICAvVHlwZSAvWE9iamVjdAogIC9TdWJ0eXBlIC9JbWFnZQogIC9XaWR0aCAzOTkKICAvSGVpZ2h0IDQwMAog" +
        "IC9Db2xvclNwYWNlIC9EZXZpY2VHcmF5CiAgL0ZpbHRlciAvSkJJRzJEZWNvZGUKICAvQml0c1BlckNvbXBvbmVudCAxCj4+CnN0cmVhbQoAAAAAMAABAAAA" +
        "EwAAAY8AAAGQAAAAAAAAAAABAAAAAAABEAEBAAABQQAQEAAAAFqfWQ3TmDFWPq+zBP4KIyAXzo0SdRgeBLCdIBB3byZSCqobXru2NtIOzFFiLcu8E2YWI1nT" +
        "IVbHkHMyHR1xJj8P0Pq+6IrfFsuJY/AJC0Mp1zwNOkA8ukH0h5KqiLEVYg9rXtgStDhCWRtQXd3Qj7+QZE7dWQOgVwrE6fngh2SPZluYPHift5nSjvrBFPeG" +
        "2eAMyI+zq/tY8eH1nUUkcs3oiDHwjvosgpwcrk6GWkjXe/OAmlLRAMB1U+tFiRvF466tWx95HuOji/rHvG1HD9LE/vzu51+slyHI9Sqz7IexNV4e4HxIKRmw" +
        "3A+vBV0MnG6J+Y2Oab4HXvxz8QaPesg8KvMOGxMVzfS/i+yuNIZL46YaWWV2XGVN1fFbolN/KMkC6KUmWP8avbnIcs1TvSDUqGc3AbyVwe//rAAAAAIUIQEB" +
        "AAAAygAAAY8AAAGQAAAAAAAAAAAAAAAAABkAAAAZAAAAAAAAAAAQAAAAqYhv2L2lYZlWKhDfb6qeAY+2kliJ46IoDosztVGw7gdow7yl0SAqEYgEj2A/S2bf" +
        "RsXG6fNDnyzqgk5+RugH5+kB8V4afv9j6pPjNBn7TH//NSeIhjXqWS11iUYxyPTb9L4FUNzidi5uc5sXTp44vlNfSBma2URNsgNOWVhXe8DYLdisCt4teN/O" +
        "a4FIXYu5xsCO1+G7/yDMio/sN2liOWGT/6wAAAADKiACAQAAAEkeAAGPAAABkAAAAAAAAAAAAAD/////rADiWRGtigiTTDL+Mf4Sv/9//3//fiE4d6wVQ9R/" +
        "/3//f/9//3yDQVRt60qdDXmgO/+sCmVuZHN0cmVhbQplbmRvYmoKCnhyZWYKMCA2CjAwMDAwMDAwMDACNjW4MzYgZiAKMDAwMDAwMDAxNCAwMDAwMCBuIAow" +
        "MDAwMDAwMDY4IDAwMDAwIG4gCjAwMDAwMDAxMzIgMDAwMDAgbiAKMDAwMDAwMDI4OCAwMDAwMCBuIAowMDAwMDAwMzYyIDAwMDAwIG4gCgp0cmFpbGVyCjw8" +
        "CiAgL1NpemUgNgogIC9Sb290IDEgMCBSCj4+CnN0YXJ0eHJlZgoxMjEwCiUlRU9Gag==";

    [Theory]
    [InlineData(PageInfoBombPdf)]
    [InlineData(PositiveAtPixelPdf)]
    [InlineData(RegionBombPdf)]
    public void ExtractImages_FuzzedJbig2Stream_CompletesWithoutOomOrIndexOutOfRange(string base64)
    {
        // The regression is the exception TYPE: pre-fix these three documents killed the
        // process-level contract (OutOfMemoryException / IndexOutOfRangeException escaping
        // a plain Open+ExtractImages of untrusted input). Post-fix, extraction completes -
        // hostile segments degrade (skipped with a Warning diagnostic; genuinely
        // undecodable streams fall to ImageExtractor's PLUME6023 raw-bytes shape) - and any
        // bitmap that WAS decoded stayed under the pixel cap.
        using var document = PdfDocument.Open(Convert.FromBase64String(base64));

        var (images, diagnostics) = document.Pages[0].ExtractImagesWithDiagnostics();

        Assert.NotNull(images);
        Assert.NotNull(diagnostics);
        foreach (var image in images)
        {
            if (!image.IsRawEncoded)
            {
                // A decoded JBIG2 result packs 1 bpp: its byte length can never exceed the
                // default MaxImagePixels budget (pre-fix, pp-pdffuzz-3's sibling mutation
                // silently produced ~900MB at sub-OOM dimensions).
                Assert.True(image.Data.Length <= (PdfOptions.Default.MaxImagePixels / 8) + 1, $"decoded image is {image.Data.Length} bytes - the pixel cap did not hold.");
            }
        }
    }

    // --- Hand-built streams pinning each allocation site's up-front cap --------------------

    private const long TightCap = 1024; // Small budget so the bomb streams stay tiny and fast.

    private static void WriteUInt32(List<byte> buffer, uint value)
    {
        buffer.Add((byte)(value >> 24));
        buffer.Add((byte)(value >> 16));
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static void WriteSegmentHeader(List<byte> buffer, uint segmentNumber, byte type, uint pageAssociation, uint dataLength)
    {
        WriteUInt32(buffer, segmentNumber);
        buffer.Add(type); // flags: type in low 6 bits, 1-byte page association.
        buffer.Add(0x00); // referred-to segment count/retention: short form, count = 0.
        buffer.Add((byte)pageAssociation);
        WriteUInt32(buffer, dataLength);
    }

    private static void WriteRegionInfo(List<byte> body, uint width, uint height, uint x = 0, uint y = 0, byte combOp = 0)
    {
        WriteUInt32(body, width);
        WriteUInt32(body, height);
        WriteUInt32(body, x);
        WriteUInt32(body, y);
        body.Add(combOp);
    }

    private static byte[] PageInfoSegment(uint width, uint height)
    {
        var buffer = new List<byte>();
        var body = new List<byte>();
        WriteUInt32(body, width);
        WriteUInt32(body, height);
        WriteUInt32(body, 0); // x resolution
        WriteUInt32(body, 0); // y resolution
        body.Add(0x00); // flags
        body.Add(0x00);
        body.Add(0x00); // striping
        WriteSegmentHeader(buffer, 0, Jbig2SegmentType.PageInfo, pageAssociation: 1, (uint)body.Count);
        buffer.AddRange(body);
        return [.. buffer];
    }

    private static Jbig2DecodeResult DecodeWithCap(byte[] stream, long maxDecodedPixels, DiagnosticCollection? diagnostics, PdfOptions? options = null) =>
        Jbig2Decoder.Decode(stream, globals: null, Jbig2Decoder.DefaultMaxSegments, Jbig2Decoder.DefaultMaxSymbols, maxDecodedPixels, options ?? PdfOptions.Default, diagnostics, null);

    [Fact]
    public void Decode_PageInfoExceedingPixelCap_RefusesAllocationWithCodedError()
    {
        var stream = PageInfoSegment(width: 64, height: 64); // 4096 pixels > TightCap

        var diagnostics = new DiagnosticCollection();
        var ex = Assert.Throws<PlumePdfException>(() => DecodeWithCap(stream, TightCap, diagnostics));

        // The cap refusal (PLUME3503) degrades per-segment (PLUME3552); with no other
        // segment producing content the decode ends with the coded no-content error.
        Assert.Equal("PLUME3501", ex.Code);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3552" && d.Message.Contains("decode cap"));
    }

    [Fact]
    public void Decode_PageInfoExceedingPixelCap_StrictThrowsInsteadOfDegrading()
    {
        var stream = PageInfoSegment(width: 64, height: 64);

        var ex = Assert.Throws<PlumePdfException>(() => DecodeWithCap(stream, TightCap, null, new PdfOptions { Strict = true }));

        Assert.Equal("PLUME3552", ex.Code);
        Assert.Contains("decode cap", ex.Message);
    }

    [Fact]
    public void Decode_MmrGenericRegionExceedingPixelCap_RefusesAllocationWithCodedError()
    {
        var buffer = new List<byte>();
        var body = new List<byte>();
        WriteRegionInfo(body, width: 4096, height: 4096); // 16M pixels > TightCap
        body.Add(0x01); // generic region flags: MMR=1 (no AT pixels follow).
        body.Add(0x00); // one byte of "MMR data".
        WriteSegmentHeader(buffer, 0, Jbig2SegmentType.ImmediateGenericRegion, pageAssociation: 1, (uint)body.Count);
        buffer.AddRange(body);

        var diagnostics = new DiagnosticCollection();
        var ex = Assert.Throws<PlumePdfException>(() => DecodeWithCap([.. buffer], TightCap, diagnostics));

        Assert.Equal("PLUME3501", ex.Code);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3552" && d.Message.Contains("decode cap"));
    }

    [Fact]
    public void Decode_ArithmeticGenericRegionExceedingPixelCap_RefusesAllocationWithCodedError()
    {
        var buffer = new List<byte>();
        var body = new List<byte>();
        WriteRegionInfo(body, width: 4096, height: 4096);
        body.Add(0x00); // generic region flags: MMR=0, template 0, TPGDON=0.
        for (var i = 0; i < 4; i++) // 4 AT pixels for template 0.
        {
            body.Add(0x03);
            body.Add(0xFF);
        }

        body.Add(0x00); // one byte of "arithmetic data".
        WriteSegmentHeader(buffer, 0, Jbig2SegmentType.ImmediateGenericRegion, pageAssociation: 1, (uint)body.Count);
        buffer.AddRange(body);

        var diagnostics = new DiagnosticCollection();
        var ex = Assert.Throws<PlumePdfException>(() => DecodeWithCap([.. buffer], TightCap, diagnostics));

        Assert.Equal("PLUME3501", ex.Code);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3552" && d.Message.Contains("decode cap"));
    }

    [Fact]
    public void Decode_TextRegionExceedingPixelCap_RefusesAllocationWithCodedError()
    {
        var buffer = new List<byte>();
        var body = new List<byte>();
        WriteRegionInfo(body, width: 4096, height: 4096);
        body.Add(0x00); // text region flags high byte: huffman=0, refine=0.
        body.Add(0x00); // text region flags low byte.
        WriteUInt32(body, 1); // number of instances.
        WriteSegmentHeader(buffer, 0, Jbig2SegmentType.ImmediateTextRegion, pageAssociation: 1, (uint)body.Count);
        buffer.AddRange(body);

        var diagnostics = new DiagnosticCollection();
        var ex = Assert.Throws<PlumePdfException>(() => DecodeWithCap([.. buffer], TightCap, diagnostics));

        Assert.Equal("PLUME3501", ex.Code);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3552" && d.Message.Contains("decode cap"));
    }

    [Fact]
    public void Decode_RefinementRegionExceedingPixelCap_SkipsSegmentAndKeepsPage()
    {
        var buffer = new List<byte>(PageInfoSegment(width: 16, height: 16)); // 256 pixels - within TightCap.
        var body = new List<byte>();
        WriteRegionInfo(body, width: 4096, height: 4096);
        body.Add(0x01); // refinement flags: template 1 (no AT pixels to read), TPGRON=0.
        body.Add(0x00); // one byte of "arithmetic data".
        WriteSegmentHeader(buffer, 1, Jbig2SegmentType.ImmediateGenericRefinementRegion, pageAssociation: 1, (uint)body.Count);
        buffer.AddRange(body);

        var diagnostics = new DiagnosticCollection();

        // The hostile refinement segment is skipped without crashing or allocating (cap
        // refusal -> per-segment PLUME3552) — the security property this test exists for.
        // Since that skipped segment is the page's ONLY content, the decode REFUSES
        // (PLUME3501) instead of returning the bare page-info
        // background — the old "background survives" behavior painted a solid sheet over
        // real content whenever a page's segments were all skipped, and the resolver's
        // paint-nothing PLUME7744 posture is the honest degradation.
        var ex = Assert.Throws<PlumePdfException>(() => DecodeWithCap([.. buffer], TightCap, diagnostics));
        Assert.Equal("PLUME3501", ex.Code);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3552" && d.Message.Contains("decode cap"));
    }

    [Fact]
    public void DecodeMmrBitmap_ExceedingPixelCap_ThrowsCodedErrorBeforeAllocating()
    {
        static bool[,] Act()
        {
            var bitPosition = 0;
            return CcittFaxEngine.DecodeMmrBitmap(ReadOnlySpan<byte>.Empty, ref bitPosition, columns: 1 << 20, rows: 1 << 20, maxDecodedPixels: 1L << 27);
        }

        var ex = Assert.Throws<PlumePdfException>(() => Act());

        Assert.Equal("PLUME3405", ex.Code);
        Assert.Contains("decode cap", ex.Message);
    }

    // --- Finding 2: positive AT-pixel offsets must read as 0, not index past the bitmap ----

    [Fact]
    public void Decode_GenericRegionWithPositiveAtPixelYOffsets_DecodesWithoutIndexOutOfRange()
    {
        // AT pixels are signed bytes; +127 in Y points far below the row being decoded.
        // T.88 6.2.5.2 defines out-of-bitmap template pixels as 0 - pre-fix this threw an
        // uncaught IndexOutOfRangeException from the untrusted-input path (pp-pdffuzz-2's
        // minimal shape). The decoded content is arbitrary; completing with the declared
        // dimensions is the regression assertion.
        var buffer = new List<byte>();
        var body = new List<byte>();
        WriteRegionInfo(body, width: 16, height: 16);
        body.Add(0x00); // generic region flags: MMR=0, template 0, TPGDON=0.
        (sbyte X, sbyte Y)[] at = [(3, 127), (-3, 100), (2, 64), (-2, 1)]; // all Y offsets positive: every template read points past the current row.
        foreach (var (x, y) in at)
        {
            body.Add(unchecked((byte)x));
            body.Add(unchecked((byte)y));
        }

        body.AddRange([0x5A, 0xC3, 0x99, 0x0F, 0x77, 0x21, 0xE4, 0x8B]); // arbitrary arithmetic payload.
        WriteSegmentHeader(buffer, 0, Jbig2SegmentType.ImmediateGenericRegion, pageAssociation: 1, (uint)body.Count);
        buffer.AddRange(body);

        var result = Jbig2Decoder.Decode(buffer.ToArray(), globals: null, PdfOptions.Default, null, null);

        Assert.Equal(16, result.Width);
        Assert.Equal(16, result.Height);
    }
}
