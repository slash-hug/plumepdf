using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The shim the lane arms must advertise the flagged render
/// verbs. An earlier shim build passes <c>--self-test</c> but lacks them; on the armed lane
/// that is a failure (a stale oracle), not a skip.
/// </summary>
public class PdfiumShimCapsTests
{
    [Fact]
    public void ArmedShim_AdvertisesRenderFlagVerbs()
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires the pdfium_shim binary at {PdfiumOracle.ShimPath})");
            return;
        }

        Assert.True(PdfiumOracle.SupportsRenderFlags,
            $"pdfium_shim at {PdfiumOracle.ShimPath} passes --self-test but does not print 'PDFIUM_SHIM_CAPS: render-aliased render-nosmooth-image' — " +
            "rebuild it with ./scripts/install-pdfium-oracle.sh.");
    }
}
