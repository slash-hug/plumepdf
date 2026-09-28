using PlumePdf.Documents.Redaction;
using Xunit;

namespace PlumePdf.Tests.Redaction;

/// <summary>
/// The integration half of the redaction save guard: <see cref="PdfDocument.Redact"/> (the public
/// door over <c>RedactionEngine</c>) marks the document redaction-dirty once anything was
/// actually removed, so <c>SaveIncremental</c> refuses with <c>PLUME5016</c> — and a zero-match
/// call that touched nothing does <em>not</em> arm the guard, per
/// <c>PdfDocument.MarkRedactionDirty</c>'s own contract. An earlier suite pins the guard's mechanics
/// against a manually-set flag; this suite pins the wiring from the real redaction path.
/// Also covers <c>Pdf.Redact</c>, the one-line path verb over the same engine.
/// </summary>
public class RedactionSaveGuardTests
{
    [Fact]
    public void Redact_WithMatches_MakesSaveIncrementalRefuseWithPlume5016()
    {
        var path = TempPdfPath();
        try
        {
            CreateSampleDocument(path, "The launch code is Osprey.");

            using var document = PdfDocument.Open(path);
            var result = document.Redact([RedactionTarget.Text("Osprey")]);
            Assert.Equal(1, result.MatchCount);

            var ex = Assert.Throws<PlumePdfException>(() => document.SaveIncremental(path));
            Assert.Equal("PLUME5016", ex.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_ZeroMatches_LeavesSaveIncrementalAvailable()
    {
        var path = TempPdfPath();
        try
        {
            CreateSampleDocument(path, "Nothing sensitive here.");

            using (var document = PdfDocument.Open(path))
            {
                var result = document.Redact([RedactionTarget.Text("Osprey")]);
                Assert.True(result.HadNoMatches);

                // Nothing was removed, so the document is not redaction-dirty; an unrelated
                // incremental edit is still legal.
                document.Objects.MarkDirty(document.Pages[0].Reference);
                document.SaveIncremental(path);
            }

            using var reopened = PdfDocument.Open(path);
            Assert.Single(reopened.Pages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_ThenSave_StillWorks_AndOutputIsClean()
    {
        var path = TempPdfPath();
        var outputPath = TempPdfPath();
        try
        {
            CreateSampleDocument(path, "The launch code is Osprey.");

            using (var document = PdfDocument.Open(path))
            {
                document.Redact([RedactionTarget.Text("Osprey")]);
                document.Save(outputPath); // full rewrite is the legal path after redaction
            }

            using var reopened = PdfDocument.Open(outputPath);
            Assert.DoesNotContain("Osprey", reopened.Pages[0].ExtractText().Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public void PdfRedact_PathVerb_RedactsAndReportsThroughTheResult()
    {
        var path = TempPdfPath();
        var outputPath = TempPdfPath();
        try
        {
            CreateSampleDocument(path, "Contact Jane Doe about the audit.");

            var result = Pdf.Redact(path, outputPath, [RedactionTarget.Text("Jane Doe")]);
            Assert.Equal(1, result.MatchCount);
            Assert.False(result.HadNoMatches);

            using var reopened = PdfDocument.Open(outputPath);
            var text = reopened.Pages[0].ExtractText().Text;
            Assert.DoesNotContain("Jane Doe", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public void PdfRedact_PathVerb_ZeroMatches_IsLoudInTheResult()
    {
        var path = TempPdfPath();
        var outputPath = TempPdfPath();
        try
        {
            CreateSampleDocument(path, "Nothing sensitive here.");

            var result = Pdf.Redact(path, outputPath, [RedactionTarget.Text("Osprey")]);
            Assert.True(result.HadNoMatches);
            Assert.True(File.Exists(outputPath)); // the file IS written — the result is the caller's check
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    private static void CreateSampleDocument(string path, string body)
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(Elements.PageSize.A4).Margin(40);
            page.Content().Text(body);
        });
        document.Save(path);
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-redact-guard-{Guid.NewGuid():N}.pdf");
}
