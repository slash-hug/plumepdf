using Xunit;
using Xunit.Abstractions;

namespace PlumePdf.CorpusTests;

/// <summary>
/// Spot-checks <c>Pdf.EstimateCompression</c> against <c>Pdf.Compress</c> on corpus files a
/// review found it mis-estimating; skipped when the corpora haven't been fetched.
/// </summary>
public class CompressEstimateCorpusTests(ITestOutputHelper output)
{
    // A group of re-encoded Flate images whose samples all lose to never-larger: estimated from
    // the samples' originals, the rest were shrunk on paper and the estimate came out 8.3% under.
    [Fact]
    public void ArlingtonModel_KeptSamplesNoLongerPullTheEstimateUnder()
    {
        var path = Path.Combine(CorpusFixture.CorporaRoot, "arlington-pdf-model-master", "PDF-Days-2021-Arlington-PDF-model.pdf");
        if (!File.Exists(path))
        {
            Console.WriteLine($"SKIPPED: requires {path} — run scripts/fetch-corpora.sh");
            return;
        }

        var levers = new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = 150, JpegQuality = 70 } };
        var estimate = Pdf.EstimateCompression(path, levers);
        using var input = File.OpenRead(path);
        var actual = Pdf.Compress(input, Stream.Null, levers);

        var error = (double)(estimate.EstimatedOutputBytes - actual.OutputBytes) / actual.OutputBytes;
        output.WriteLine($"estimated {estimate.EstimatedOutputBytes}, actual {actual.OutputBytes}, error {error:P2}");
        Assert.InRange(error, -0.01, 0.05);
        Assert.True(estimate.EstimatedOutputBytes + estimate.ExtrapolatedSavedBytes >= actual.OutputBytes);
    }
}
