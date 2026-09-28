using PlumePdf.Filters.Jbig2;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// pdf.js's <c>bitmap-{symbol,halftone}-*</c> JBIG2 decode-parity matrix — each
/// fixture is a small PDF embedding one JBIG2 stream, pinned at pdf.js's own commit
/// (<c>scripts/fetch-corpora.sh</c>'s <c>PDFJS_CODEC_FILES</c> array). <c>fetch-corpora.sh</c>
/// writes every pdf.js fixture — the Phase 1 subset, the forms subset, and the Phase 7 codec
/// files alike — into one shared <c>pdfjs-subset-&lt;commit12&gt;</c> directory
/// (<see cref="CorpusFixture.Phase1PdfJsSubsetRoot"/>'s own resolution, reused here rather
/// than duplicated); there is no separate <c>pdfjs-codec-*</c> directory.
/// </summary>
public class Jbig2BitmapMatrixTests
{
    /// <summary>The same shared pdf.js subset directory <see cref="CorpusFixture.Phase1PdfJsSubsetRoot"/> resolves — see this type's summary for why there is no separate codec-only directory to look for.</summary>
    private static string? CodecCorpusRoot => CorpusFixture.Phase1PdfJsSubsetRoot;

    private static bool CorpusAvailable => CodecCorpusRoot is { } root && Directory.EnumerateFiles(root, "bitmap-*.pdf", SearchOption.AllDirectories).Any();

    private static IEnumerable<string> BitmapFixtureFiles =>
        CodecCorpusRoot is { } root ? Directory.EnumerateFiles(root, "bitmap-*.pdf", SearchOption.AllDirectories) : [];

    /// <summary>
    /// Extracts every <c>/JBIG2Decode</c> stream's raw (still-encoded) bytes from a PDF file
    /// via a byte-level scan for the filter name followed by the nearest <c>stream</c>/<c>endstream</c>
    /// markers — deliberately not a full PDF parse (these are pdf.js JBIG2-conformance test
    /// files, not general PDFs this project's own reader needs to round-trip; a byte scan is
    /// the minimum machinery this corpus lane needs and keeps it decoupled from PdfDocument).
    /// </summary>
    private static IEnumerable<byte[]> ExtractJbig2Streams(byte[] pdfBytes)
    {
        var marker = "/JBIG2Decode"u8.ToArray();
        var streamKeyword = "stream"u8.ToArray();
        var endstreamKeyword = "endstream"u8.ToArray();

        var searchStart = 0;
        while (true)
        {
            var filterIndex = IndexOf(pdfBytes, marker, searchStart);
            if (filterIndex < 0)
            {
                yield break;
            }

            var streamIndex = IndexOf(pdfBytes, streamKeyword, filterIndex);
            if (streamIndex < 0)
            {
                yield break;
            }

            var dataStart = streamIndex + streamKeyword.Length;
            // Skip the CRLF or LF immediately after the "stream" keyword (ISO 32000-1 §7.3.8.1).
            if (dataStart < pdfBytes.Length && pdfBytes[dataStart] == '\r')
            {
                dataStart++;
            }

            if (dataStart < pdfBytes.Length && pdfBytes[dataStart] == '\n')
            {
                dataStart++;
            }

            var endIndex = IndexOf(pdfBytes, endstreamKeyword, dataStart);
            if (endIndex < 0)
            {
                yield break;
            }

            var length = endIndex - dataStart;
            if (length > 0)
            {
                var bytes = new byte[length];
                Array.Copy(pdfBytes, dataStart, bytes, 0, length);
                yield return bytes;
            }

            searchStart = endIndex + endstreamKeyword.Length;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void BitmapCorpus_NonEmptyWhenFetched()
    {
        if (!CorpusAvailable)
        {
            return; // Hermetic lane: corpus not fetched; nothing to assert yet.
        }

        Assert.True(BitmapFixtureFiles.Count() >= 50, "The pdf.js bitmap-* corpus set should contain at least 50 fixtures once fetched.");
    }

    [Fact]
    public void BitmapCorpus_EveryFixture_DecodesOrDegradesGracefully()
    {
        if (!CorpusAvailable)
        {
            return;
        }

        var failures = new List<string>();

        foreach (var path in BitmapFixtureFiles)
        {
            var pdfBytes = File.ReadAllBytes(path);
            var anyStream = false;

            foreach (var jbig2Bytes in ExtractJbig2Streams(pdfBytes))
            {
                anyStream = true;
                try
                {
                    var diagnostics = new DiagnosticCollection();
                    var result = Jbig2Decoder.Decode(jbig2Bytes, globals: null, PdfOptions.Default, diagnostics, null);

                    if (result.Width <= 0 || result.Height <= 0)
                    {
                        failures.Add($"{Path.GetFileName(path)}: decoded to a non-positive size ({result.Width}x{result.Height}).");
                    }
                }
                catch (PlumePdfException ex)
                {
                    // A halftone-only fixture (bitmap-halftone-*) is an expected per-segment
                    // fallback, not a failure - anything else is a real regression.
                    var isHalftoneFixture = Path.GetFileName(path).Contains("halftone", StringComparison.OrdinalIgnoreCase);

                    // Re-pin: a stream whose EVERY content segment is skipped as
                    // unsupported (the Huffman-coded and refinement-only fixtures here) now
                    // REFUSES (PLUME3501) instead of returning the bare page background —
                    // the old degrade-to-background contract painted a solid sheet over real
                    // content; the resolver converts this refusal into paint-nothing +
                    // PLUME7744, which is the graceful degradation this test's name asks for.
                    var isHonestAllSkippedRefusal = ex.Code == "PLUME3501" && ex.Message.Contains("skipped as unsupported", StringComparison.Ordinal);
                    if (!isHalftoneFixture && !isHonestAllSkippedRefusal)
                    {
                        failures.Add($"{Path.GetFileName(path)}: threw {ex.Code} ({ex.Message}).");
                    }
                }
            }

            if (!anyStream)
            {
                failures.Add($"{Path.GetFileName(path)}: no /JBIG2Decode stream found by the byte scan.");
            }
        }

        Assert.True(failures.Count == 0, "JBIG2 decode failures:\n" + string.Join('\n', failures));
    }

    /// <summary>
    /// Regression guard for a real decode bug (found via <c>RasterImageOracleTests</c>'s SSIM
    /// leg landing at 0.87 against the calibrated 0.90 floor): <c>bitmap-symbol-big-segmentid.pdf</c>
    /// has two text regions each referring to a symbol dictionary that exports exactly one
    /// symbol - the one case where <c>SBSYMCODELEN</c> (ITU-T T.88 §7.4.3.1.7) is 0, not 1
    /// (there is only one symbol to choose, so no bits are needed to select it). A decoder that
    /// forces a minimum of 1 there reads one never-encoded arithmetic-coded bit per symbol
    /// placement, desyncing the rest of that region's decode - <see cref="BitmapCorpus_EveryFixture_DecodesOrDegradesGracefully"/>
    /// doesn't catch this because a desynced decode still returns a correctly-sized, merely
    /// blank, page (no exception, no diagnostic): this test additionally asserts real content
    /// painted, hermetically (no external pdfium oracle needed - unlike the SSIM leg that first
    /// caught it), so it runs the moment the corpus is fetched.
    /// </summary>
    [Fact]
    public void BitmapSymbolBigSegmentId_DecodesNonBlank_WhenFetched()
    {
        if (!CorpusAvailable)
        {
            return;
        }

        var fixturePath = BitmapFixtureFiles.FirstOrDefault(static p => Path.GetFileName(p) == "bitmap-symbol-big-segmentid.pdf");
        if (fixturePath is null)
        {
            return; // This particular pdf.js fixture isn't in this checkout's fetched subset.
        }

        var jbig2Bytes = ExtractJbig2Streams(File.ReadAllBytes(fixturePath)).FirstOrDefault();
        Assert.NotNull(jbig2Bytes);

        var result = Jbig2Decoder.Decode(jbig2Bytes!, globals: null, PdfOptions.Default, diagnostics: null, subject: null);

        var paintedPixels = 0;
        for (var y = 0; y < result.Height; y++)
        {
            for (var x = 0; x < result.Width; x++)
            {
                if (result.Page[y, x])
                {
                    paintedPixels++;
                }
            }
        }

        Assert.True(paintedPixels > 0, "bitmap-symbol-big-segmentid.pdf decoded to an entirely blank page - both single-symbol text regions failed to place their symbol (SBSYMCODELEN regression).");
    }

    [Fact]
    public void JbigGlobalsFixture_ResolvesSharedSymbolDictionary_WhenFetched()
    {
        if (!CorpusAvailable || CodecCorpusRoot is not { } root)
        {
            return;
        }

        // JBIG2Globals.pdf lands directly in the shared pdf.js subset directory (fetch-corpora.sh's
        // own JBIG2_GLOBALS_URL step) — it does not match the bitmap-* glob every other test in
        // this file uses, so it needs its own, wider search.
        var globalsFixture = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).Contains("JBIG2Globals", StringComparison.OrdinalIgnoreCase));
        if (globalsFixture is null)
        {
            return; // fetch-corpora.sh's JBIG2Globals.pdf step hasn't run against this corpora/ checkout.
        }

        // The real end-to-end assertion: open the fixture as a document (so an indirect
        // /JBIG2Globals reference resolves through the actual ObjectRegistry's
        // resolver seam), extract its images, and require at least one JBIG2-filtered image to
        // have decoded — not merely "didn't throw".
        using var document = PdfDocument.Open(globalsFixture);
        var decodedAny = false;
        var failures = new List<string>();

        foreach (var page in document.Pages)
        {
            var (images, _) = page.ExtractImagesWithDiagnostics();
            foreach (var image in images.Where(static i => i.Filters.Contains("JBIG2Decode")))
            {
                if (image.IsRawEncoded)
                {
                    failures.Add($"object {image.Reference?.Number}: still raw-encoded (JBIG2Decode not applied).");
                    continue;
                }

                if (image.Width <= 0 || image.Height <= 0 || image.Data.IsEmpty)
                {
                    failures.Add($"object {image.Reference?.Number}: decoded to an empty/non-positive-size result.");
                    continue;
                }

                decodedAny = true;
            }
        }

        Assert.True(decodedAny, $"Expected at least one JBIG2Decode image to decode successfully in '{globalsFixture}'.");
        Assert.True(failures.Count == 0, "JBIG2Globals end-to-end decode failures:\n" + string.Join('\n', failures));
    }

    /// <summary>
    /// Regression guard for a root cause found in a JBIG2 corpus sweep: an SDREFAGG=1 (refinement-coded)
    /// symbol dictionary's aggregate IAID reads must use SBSYMCODELEN computed from the
    /// DECLARED symbol totals (T.88 6.5.8.2.3), never the running count — the running-count
    /// read consumed too few arithmetic bits and silently desynced the whole dictionary
    /// (garbage symbols, dense-black text regions, zero diagnostics). Pre-fix this fixture
    /// decoded with PLUME3556 + a near-blank page; post-fix it decodes clean and non-blank
    /// (pixel parity with pdfium is covered by the SSIM sweep).
    /// </summary>
    [Fact]
    public void BitmapSymbolRefineOne_DecodesCleanAndNonBlank_WhenFetched()
    {
        if (!CorpusAvailable)
        {
            return;
        }

        var path = BitmapFixtureFiles.FirstOrDefault(static f => Path.GetFileName(f) == "bitmap-symbol-symbolrefineone.pdf");
        if (path is null)
        {
            return;
        }

        foreach (var jbig2Bytes in ExtractJbig2Streams(File.ReadAllBytes(path)))
        {
            var diagnostics = new DiagnosticCollection();
            var result = Jbig2Decoder.Decode(jbig2Bytes, globals: null, PdfOptions.Default, diagnostics, null);

            Assert.Empty(diagnostics);
            var painted = 0;
            for (var y = 0; y < result.Height; y++)
            {
                for (var x = 0; x < result.Width; x++)
                {
                    if (result.Page[y, x])
                    {
                        painted++;
                    }
                }
            }

            Assert.True(painted > 0, "expected the refinement-coded symbols to decode real ink (the pre-fix desync produced a blank/garbage page).");
        }
    }
}
