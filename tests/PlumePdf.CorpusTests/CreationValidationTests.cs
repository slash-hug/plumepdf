using System.Diagnostics;
using PlumePdf.Elements;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// Phase 2's own reading of the corpus-conformance gate: a creation phase has no
/// input corpus to validate against, so its gate is external-
/// oracle validation of <em>generated</em> output — <c>qpdf --check</c> clean, plus a
/// round-trip re-open through PlumePDF's own reader with zero diagnostics — over documents
/// <see cref="Manuscript.Render(PdfOptions?)"/> actually produces (Standard-14 text, an
/// embedded/subsetted TrueType font when the font corpus is fetched, pagination, watermark,
/// and stamp). Follows <see cref="QpdfInteropTests"/>'s self-skip shape: qpdf absent means
/// this lane no-ops rather than failing, so the default hermetic CI lane stays green; the
/// dedicated corpus CI job installs qpdf and runs these for real.
/// </summary>
public class CreationValidationTests
{
    private static readonly bool QpdfAvailable = ProbeQpdf();

    private static bool ProbeQpdf()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("qpdf", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            probe!.WaitForExit(10_000);
            return probe.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static void AssertQpdfClean(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("qpdf", $"--check \"{path}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        Assert.True(process.ExitCode == 0, $"qpdf --check reported problems (exit {process.ExitCode}):\n{stdout}{stderr}");
    }

    private static string TempPdfPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"plumepdf-creation-validation-{label}-{Guid.NewGuid():N}.pdf");

    /// <summary>Renders <paramref name="manuscript"/>, saves it, asserts qpdf accepts it, then reopens it through PlumePDF's own reader and asserts zero recovered diagnostics.</summary>
    private static void AssertGeneratedOutputIsClean(Manuscript manuscript, string label, PdfOptions? options = null)
    {
        if (!QpdfAvailable)
        {
            return;
        }

        var path = TempPdfPath(label);
        try
        {
            using (var document = manuscript.Render(options))
            {
                document.Save(path, options ?? PdfOptions.Default);
            }

            AssertQpdfClean(path);

            using var reopened = PdfDocument.Open(path);
            Assert.Empty(reopened.Diagnostics);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Standard14Invoice_QpdfCheckClean_AndReopensWithZeroDiagnostics()
    {
        var manuscript = new Manuscript
        {
            Sections =
            [
                new Section
                {
                    Header = new Text("INVOICE #1042") { Bold = true, FontSize = 20 },
                    Body = new Column(
                        new Text("Bill to: Acme Corp"),
                        new Text("Total: $19,300.00") { Bold = true }),
                    Footer = new Text("Page {page} of {pages}") { Align = HorizontalAlign.Center },
                    Watermark = new Watermark { Text = "DRAFT" },
                    Stamps = [new Stamp { Text = "CONFIDENTIAL" }],
                },
            ],
        };

        AssertGeneratedOutputIsClean(manuscript, "standard14");
    }

    [Fact]
    public void HundredLineItemInvoice_PaginatedOutput_QpdfCheckClean_AndReopensWithZeroDiagnostics()
    {
        var rows = new List<IReadOnlyList<Element>>();
        for (var i = 0; i < 100; i++)
        {
            rows.Add([new Text($"Line item {i + 1}"), new Text("1"), new Text("$10.00")]);
        }

        var table = new Table
        {
            Columns = [TableColumn.Relative(3), TableColumn.Relative(1), TableColumn.Relative(1)],
            HeaderRow = [new Text("Item") { Bold = true }, new Text("Qty") { Bold = true }, new Text("Price") { Bold = true }],
            Rows = rows,
            RowSpacing = 4,
        };

        var manuscript = new Manuscript
        {
            Sections =
            [
                new Section
                {
                    Header = new Text("INVOICE #2099") { Bold = true, FontSize = 18 },
                    Body = table,
                    Footer = new Text("Page {page} of {pages}") { Align = HorizontalAlign.Center },
                },
            ],
        };

        AssertGeneratedOutputIsClean(manuscript, "paginated", new PdfOptions { Deterministic = true });
    }

    [Fact]
    public void EmbeddedTrueTypeFont_QpdfCheckClean_AndReopensWithZeroDiagnostics()
    {
        var fontPath = Path.Combine(CorpusFixture.RepoRoot, "corpora", "fonts", "NotoSans-Regular.ttf");
        if (!File.Exists(fontPath))
        {
            // Hermetic lane: font corpus not fetched (scripts/fetch-corpora.sh); nothing to assert yet.
            return;
        }

        var font = PdfFont.FromFile(fontPath);
        var manuscript = new Manuscript
        {
            Sections =
            [
                new Section
                {
                    Body = new Text("Embedded font: the quick brown fox jumps over the lazy dog.") { Font = font },
                },
            ],
        };

        AssertGeneratedOutputIsClean(manuscript, "embedded-truetype");
    }
}
