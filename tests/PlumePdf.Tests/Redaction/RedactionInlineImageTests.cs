using System.Diagnostics;
using System.Text;
using PlumePdf.Documents;
using PlumePdf.Documents.Redaction;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Redaction;

/// <summary>
/// Regressions for the inline-image (<c>BI</c>…<c>ID</c>…<c>EI</c>) redaction paths and the
/// cumulative operator budget: the reader-recorded raw span keeps a non-matching inline image
/// byte-identical through a rebuild (never re-derived by offset guesswork — the bug that once
/// re-emitted a removed operator's original tail verbatim), an intersecting inline image is
/// removed whole, <c>PdfRedactOptions.RefuseOnImageRemoval</c> refuses instead
/// (<c>PLUME6074</c>), and a fanout^depth Form XObject graph is refused by the
/// cumulative budget (<c>PLUME7018</c>) rather than traversed exponentially.
/// </summary>
public class RedactionInlineImageTests
{
    private const string Secret = "TOPSECRETXYZ";

    [Fact]
    public void Redact_TextOperatorBeforeInlineImage_RemovedTextDoesNotResurrectInRebuiltStream()
    {
        // The executed repro this guards against: BI's recorded offset pointing before its
        // leading whitespace made span re-derivation fail, and the failure path copied the
        // ENTIRE original tail verbatim — including the just-removed text operator — while
        // also emitting the rebuilt list, so the removed text extracted again after save.
        var bytes = BuildInlineImageDocument();
        using var document = PdfDocument.Open(bytes);

        var result = RedactionEngine.Redact(document, [RedactionTarget.Text(Secret)], null);

        Assert.Equal(1, result.MatchCount);
        Assert.Equal(1, result.TextOperatorsRemoved);
        Assert.Equal(0, result.InlineImagesRemoved);
        Assert.Equal(0, result.InlineImagesSkipped);

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path);

            // (1) via extraction.
            var text = reopened.Pages[0].ExtractText().Text;
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
            Assert.Contains("public", text, StringComparison.Ordinal);

            // (2) in the rebuilt content stream itself — the non-matching inline image must
            // survive byte-identically, and no verbatim tail splice may resurrect the secret.
            var rebuilt = Encoding.Latin1.GetString(DecodedPageContent(reopened));
            Assert.DoesNotContain(Secret, rebuilt, StringComparison.Ordinal);
            Assert.Contains("BI", rebuilt, StringComparison.Ordinal);
            Assert.Contains("EI", rebuilt, StringComparison.Ordinal);
            Assert.Contains("public", rebuilt, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_InlineImageIntersectingRegion_IsRemovedWhole()
    {
        var bytes = BuildInlineImageDocument();
        using var document = PdfDocument.Open(bytes);

        // The inline image paints the CTM's unit square at (0,0)-(1,1); this region covers it
        // but neither text operator.
        var result = RedactionEngine.Redact(document, [RedactionTarget.Region(0, new PdfRectangle(0, 0, 10, 10))], null);

        Assert.Equal(1, result.InlineImagesRemoved);
        Assert.Equal(0, result.InlineImagesSkipped);

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path);
            var rebuilt = Encoding.Latin1.GetString(DecodedPageContent(reopened));
            Assert.DoesNotContain("BI", rebuilt, StringComparison.Ordinal);
            Assert.Contains(Secret, rebuilt, StringComparison.Ordinal); // untargeted text survives
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_InlineImageIntersectingRegion_RefuseOnImageRemoval_ThrowsPlume6074()
    {
        var bytes = BuildInlineImageDocument();
        using var document = PdfDocument.Open(bytes);

        var options = new PdfRedactOptions { RefuseOnImageRemoval = true };
        var ex = Assert.Throws<PlumePdfException>(() =>
            RedactionEngine.Redact(document, [RedactionTarget.Region(0, new PdfRectangle(0, 0, 10, 10))], options));

        Assert.Equal("PLUME6074", ex.Code);
        Assert.Contains("inline image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_FanOutFormXObjectGraph_TightBudget_ThrowsPlume7018Fast()
    {
        // Depth 6, fanout 4, ~54 operators per leaf: > 200,000 cumulative operators if fully
        // traversed — a shape that once took tens of seconds from a 13KB file at depth 25
        // because only per-stream caps existed. The cumulative budget must refuse quickly.
        var bytes = BuildFanOutDocument(depth: 6, fanout: 4);
        using var document = PdfDocument.Open(bytes, new PdfOptions { MaxContentStreamOperators = 10_000 });

        var stopwatch = Stopwatch.StartNew();
        var ex = Assert.Throws<PlumePdfException>(() =>
            RedactionEngine.Redact(document, [RedactionTarget.Region(0, new PdfRectangle(0, 0, 612, 792))], null));
        stopwatch.Stop();

        Assert.Equal("PLUME7018", ex.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"cumulative budget refusal took {stopwatch.Elapsed} — the budget is not bounding traversal.");
    }

    private static byte[] DecodedPageContent(PdfDocument document)
    {
        var page = document.Pages[0];
        Assert.True(page.Dictionary.TryGetValue(PdfName.Get("Contents"), out var contentsValue));
        var stream = contentsValue switch
        {
            PdfReference reference => document.Objects[reference.Target] as PdfStream,
            _ => null,
        };
        Assert.NotNull(stream);
        return stream!.GetDecodedBytes(PdfFilterRegistry.Default, PdfOptions.Default);
    }

    // A minimal, well-formed classic-xref single-page document (the hand-rolled ISO 32000-1
    // grammar approach DocumentStampTests uses) whose content stream holds a text operator,
    // then an inline image with a real binary payload, then a second text operator — the exact
    // shape the fail-open span bug re-emitted verbatim.
    private static byte[] BuildInlineImageDocument()
    {
        var content = new List<byte>();
        content.AddRange(Encoding.ASCII.GetBytes($"BT /F0 12 Tf 100 700 Td ({Secret}) Tj ET\n"));
        content.AddRange(Encoding.ASCII.GetBytes("BI /W 1 /H 1 /CS /G /BPC 8 ID "));
        content.Add(0x7A); // the 1x1 image's single sample byte
        content.AddRange(Encoding.ASCII.GetBytes(" EI\n"));
        content.AddRange(Encoding.ASCII.GetBytes("BT /F0 12 Tf 100 100 Td (public) Tj ET"));

        return BuildSinglePageDocument([.. content], extraObjects: null, pageExtras: string.Empty);
    }

    private static byte[] BuildFanOutDocument(int depth, int fanout)
    {
        // Objects: 1 catalog, 2 pages, 3 page, 4 page content, 5 font, 6.. forms F1..Fdepth
        // where F1 is the leaf (plain operators) and Fk invokes F(k-1) `fanout` times.
        var leafOps = string.Concat(Enumerable.Repeat("1 0 0 1 0 0 cm\n", 54));
        var extraObjects = new List<(int Number, string Body, string? StreamContent)>();

        for (var level = 1; level <= depth; level++)
        {
            var number = 5 + level;
            string streamContent;
            string resources;
            if (level == 1)
            {
                streamContent = leafOps;
                resources = string.Empty;
            }
            else
            {
                streamContent = string.Concat(Enumerable.Repeat("/F Do\n", fanout));
                resources = $" /Resources << /XObject << /F {number - 1} 0 R >> >>";
            }

            extraObjects.Add((number, $"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792]{resources} /Length {streamContent.Length} >>", streamContent));
        }

        return BuildSinglePageDocument(
            Encoding.ASCII.GetBytes("/FTop Do\n"),
            extraObjects,
            pageExtras: $" /Resources << /Font << /F0 5 0 R >> /XObject << /FTop {5 + depth} 0 R >> >>",
            includeDefaultResources: false);
    }

    private static byte[] BuildSinglePageDocument(
        byte[] content,
        List<(int Number, string Body, string? StreamContent)>? extraObjects,
        string pageExtras,
        bool includeDefaultResources = true)
    {
        var buffer = new List<byte>();
        buffer.AddRange(Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"));
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        void WriteStreamObject(int num, string dictionary, byte[] streamBytes)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{dictionary}\nstream\n"));
            buffer.AddRange(streamBytes);
            buffer.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        }

        var resources = includeDefaultResources ? " /Resources << /Font << /F0 5 0 R >> >>" : string.Empty;

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteObject(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R{resources}{pageExtras} >>");
        WriteStreamObject(4, $"<< /Length {content.Length} >>", content);
        WriteObject(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        var maxNumber = 5;
        if (extraObjects is not null)
        {
            foreach (var (number, body, streamContent) in extraObjects)
            {
                if (streamContent is null)
                {
                    WriteObject(number, body);
                }
                else
                {
                    WriteStreamObject(number, body, Encoding.ASCII.GetBytes(streamContent));
                }

                maxNumber = Math.Max(maxNumber, number);
            }
        }

        var size = maxNumber + 1;
        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {size}\n0000000000 65535 f \n"));
        for (var n = 1; n <= maxNumber; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF"));
        return [.. buffer];
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-redact-inline-{Guid.NewGuid():N}.pdf");
}
