using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// Verifies "<c>ccitt_EndOfBlock_false.pdf</c> corpus fixture
/// decodes" plus two JBIG2 edge-case fixtures — three pdf.js conformance fixtures
/// <c>scripts/fetch-corpora.sh</c>'s <c>PDFJS_CODEC_FILES</c> array fetches but which, before
/// this test existed, gated nothing: they added fetch time and CI surface without a single
/// assertion consuming them. <c>ccitt_EndOfBlock_false.pdf</c> exercises a CCITT stream whose
/// <c>/DecodeParms</c> declares <c>/EndOfBlock false</c> (no <c>EOL</c>/RTC terminator to rely
/// on — the row count alone bounds the decode); <c>jbig2_file_header.pdf</c> and
/// <c>jbig2_symbol_offset.pdf</c> are two more pdf.js JBIG2 conformance fixtures outside the
/// <c>bitmap-*</c> glob <see cref="Jbig2BitmapMatrixTests"/> already covers, named individually
/// rather than swept up by a wildcard.
/// </summary>
public class CcittJbig2EdgeCaseTests
{
    private static string? FindFixture(string fileName) => CorpusFixture.Phase1PdfJsSubsetRoot is { } root
        ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault()
        : null;

    [Fact]
    public void CcittEndOfBlockFalse_DecodesAtLeastOneImage()
    {
        var path = FindFixture("ccitt_EndOfBlock_false.pdf");
        if (path is null)
        {
            return; // Hermetic lane: corpus not fetched.
        }

        using var document = PdfDocument.Open(path);
        var decoded = false;
        foreach (var page in document.Pages)
        {
            foreach (var image in page.ExtractImages().Where(static i => i.Filters.Contains("CCITTFaxDecode") || i.Filters.Contains("CCF")))
            {
                Assert.False(image.IsRawEncoded, $"object {image.Reference?.Number}: CCITTFaxDecode image with /EndOfBlock false was not decoded.");
                Assert.True(image.Width > 0 && image.Height > 0 && !image.Data.IsEmpty);
                decoded = true;
            }
        }

        Assert.True(decoded, $"Expected at least one CCITTFaxDecode image to decode in '{path}'.");
    }

    [Theory]
    [InlineData("jbig2_file_header.pdf")]
    [InlineData("jbig2_symbol_offset.pdf")]
    public void JbigEdgeCaseFixture_DecodesAtLeastOneImage(string fileName)
    {
        var path = FindFixture(fileName);
        if (path is null)
        {
            return; // Hermetic lane: corpus not fetched.
        }

        using var document = PdfDocument.Open(path);
        var decoded = false;
        foreach (var page in document.Pages)
        {
            foreach (var image in page.ExtractImages().Where(static i => i.Filters.Contains("JBIG2Decode")))
            {
                Assert.False(image.IsRawEncoded, $"object {image.Reference?.Number}: JBIG2Decode image in '{fileName}' was not decoded.");
                Assert.True(image.Width > 0 && image.Height > 0 && !image.Data.IsEmpty);
                decoded = true;
            }
        }

        Assert.True(decoded, $"Expected at least one JBIG2Decode image to decode in '{path}'.");
    }
}
