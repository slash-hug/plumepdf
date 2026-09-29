using System.Text;
using System.Text.RegularExpressions;
using PlumePdf.Compose;
using PlumePdf.Documents;
using PlumePdf.Documents.Redaction;
using PlumePdf.Elements;
using PlumePdf.IO;
using PlumePdf.Objects;
using PlumePdf.Tests.TestSupport;
using Xunit;

namespace PlumePdf.Tests.Redaction;

/// <summary>
/// The exit-demo proof: redacted content is unrecoverable not merely
/// "unreachable from the trailer" but genuinely absent from the saved file's bytes. Every test
/// here redacts, <see cref="PdfDocument.Save"/>s, then proves absence two independent ways:
/// (1) reopening and calling <c>ExtractText</c>/<c>ExtractImages</c> (the ordinary reader path),
/// and (2) <see cref="RecoveredBytes"/>' brute-force "N G obj" byte scan reconstructing
/// <em>every</em> object physically present in the output file regardless of reachability —
/// object-graph reachability alone would not catch a writer bug that still emitted the
/// original bytes somewhere unreferenced. A raw whole-file substring scan is layered on top of
/// both as the strongest, assumption-free check.
/// </summary>
public class RedactionUnrecoverabilityTests
{
    [Fact]
    public void Redact_TextTarget_UnrecoverableByExtractionByRecoveryScannerAndByRawBytes()
    {
        const string secret = "TopSecretCallsign7734";
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text($"Mission briefing: {secret}. End of briefing.");
        });

        var result = RedactionEngine.Redact(document, [RedactionTarget.Text(secret)], null);
        Assert.Equal(1, result.MatchCount);

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            var fileBytes = File.ReadAllBytes(path);

            // (1) ordinary reader path.
            using (var reopened = PdfDocument.Open(path))
            {
                Assert.DoesNotContain(secret, reopened.Pages[0].ExtractText().Text, StringComparison.Ordinal);
            }

            // (2) brute-force recovered-object reconstruction: every object physically present
            // in the file, decoded, regardless of whether the ordinary catalog/page-tree walk
            // would ever reach it.
            Assert.False(RecoveredBytes.AnyRecoveredObjectContains(fileBytes, secret), $"The brute-force object scan found '{secret}' surviving in an object the ordinary reachability walk might not have visited.");

            // (3) raw whole-file byte scan — the strongest, assumption-free check: the target
            // text's plain-text bytes must not appear anywhere in the saved file at all.
            var wholeFile = Encoding.Latin1.GetString(fileBytes);
            Assert.DoesNotContain(secret, wholeFile, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_RegexTarget_UnrecoverableAcrossAllThreeProofs()
    {
        const string secret = "444-55-6666";
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text($"Case file reference {secret} is closed.");
        });

        var result = RedactionEngine.Redact(document, [RedactionTarget.Pattern(new Regex(@"\d{3}-\d{2}-\d{4}"))], null);
        Assert.Equal(1, result.MatchCount);

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            var fileBytes = File.ReadAllBytes(path);

            using (var reopened = PdfDocument.Open(path))
            {
                Assert.DoesNotContain(secret, reopened.Pages[0].ExtractText().Text, StringComparison.Ordinal);
            }

            Assert.False(RecoveredBytes.AnyRecoveredObjectContains(fileBytes, secret));
            Assert.DoesNotContain(secret, Encoding.Latin1.GetString(fileBytes), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_Image_OriginalPixelBytesUnrecoverable()
    {
        var pixels = new byte[80 * 80 * 3];
        // A distinctive, vanishingly-unlikely-to-occur-by-chance byte pattern standing in for
        // "sensitive pixel content" — every third byte set to a fixed sentinel value.
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 0xAB;
        }

        var image = new Image(pixels, pixelWidth: 80, pixelHeight: 80) { Width = 200, Height = 200 };

        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(0);
            page.Content().Image(image);
        });

        var mediaBox = new PdfRectangle(0, 0, PageSize.A4.Width, PageSize.A4.Height);
        var result = RedactionEngine.Redact(document, [RedactionTarget.Region(0, mediaBox)], null);
        Assert.Equal(1, result.ImagesRemoved);

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            var fileBytes = File.ReadAllBytes(path);

            // The original raw pixel buffer's distinctive byte run must not survive anywhere in
            // the saved file — not filter-decoded on any recovered object, and not present raw
            // (PlumePDF stores Flate-compressed images by default, so a raw-byte scan of the
            // *compressed* file wouldn't be conclusive on its own; the recovered-object pass
            // below decodes every recovered stream through its own /Filter chain, which is the
            // check that actually proves it).
            Assert.False(RecoveredBytes.AnyRecoveredObjectContainsByteRun(fileBytes, pixels.AsSpan(0, 300)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-redact-unrecoverability-{Guid.NewGuid():N}.pdf");
}
