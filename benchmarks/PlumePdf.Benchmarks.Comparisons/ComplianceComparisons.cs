using BenchmarkDotNet.Attributes;
using PlumePdf;
using PlumePdf.Documents;
using PlumePdf.Documents.Redaction;
using PlumePdf.Elements;

namespace PlumePdf.Benchmarks.Comparisons;

// =============================================================================
// COMPETITOR COVERAGE:
//
// iText7 is the ONLY comparison target for this phase's three scenarios — it is
// the only permitted competitor that implements any of them:
//
//   - PdfPig:   NOT APPLICABLE — read/extract-oriented; implements no PDF/A
//               creation, no tagged-PDF authoring, and no redaction.
//   - PDFsharp: NOT APPLICABLE — implements none of the three either.
//   - QuestPDF: still excluded entirely pending license confirmation (standing
//               constraint, unchanged by this phase).
//
// Both rows are recorded here ("recorded as not-applicable in the
// benchmark docs, not silently omitted") rather than left to be inferred from
// their absence. Optimization/linearization scenarios are deliberately NOT in
// this project: there is no third-party "optimize an existing PDF" comparison
// shape that fits the harness, so they run PlumePDF-only against stored
// baselines in benchmarks/PlumePdf.Benchmarks/OptimizationBenchmarks.cs.
//
// The redaction scenario calls iText's pdfSweep add-on (NuGet `itext.pdfsweep`)
// — redaction is not in the iText7 core packages. Same vendor, same AGPL
// license family, same isolation wall: referenced only from this project in
// bench.sln, binary API only, never from PlumePdf.sln.
// =============================================================================

/// <summary>
/// Phase 6 compliance scenarios, informational competitor comparison:
/// PDF/A-2b creation, tagged-PDF creation, and region redaction — PlumePDF vs iText7's
/// published binary API (never its source). Per the AGPL isolation wall (the clean-room
/// policy in AGENTS.md) this project, inside bench.sln, is the one place iText packages are ever
/// referenced; manual/per-phase, not CI-gated.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ComplianceComparisons
{
    private const string RedactionFixtureFileName = "basicapi.pdf";

    private string _fontPath = null!;
    private byte[] _iccProfile = null!;
    private string _redactionFixturePath = null!;
    private string _plumeOutputPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        // The same OFL font fixture the cookbook's PDF/A recipe uses (PDF/A requires every
        // font embedded — Standard-14 fonts are refused), and the same CC0
        // sRGB profile PlumePDF bundles as its default output intent so both libraries
        // embed identical assets.
        _fontPath = ResolveRepoFile("corpora", "fonts", "NotoSans-Regular.ttf");
        _iccProfile = File.ReadAllBytes(ResolveRepoFile("src", "PlumePdf", "Assets", "sRGB-v2-micro.icc"));
        _redactionFixturePath = ComparisonCorpora.ResolvePdfJsSubsetFile(RedactionFixtureFileName);
        _plumeOutputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-{Guid.NewGuid():N}-compliance.pdf");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (File.Exists(_plumeOutputPath))
        {
            File.Delete(_plumeOutputPath);
        }
    }

    private Manuscript BuildManuscript(PdfFont? font, bool tagged) => new()
    {
        Title = "Compliance benchmark",
        Language = tagged ? "en-US" : null,
        Sections =
        [
            new Section
            {
                Body = new Column(
                    new Text("Quarterly Compliance Report") { Font = font, HeadingLevel = tagged ? 1 : null, FontSize = 18 },
                    new Text("A paragraph of body text long enough to exercise layout, shaping, and content-stream generation.") { Font = font })
                {
                    Spacing = 10,
                },
            },
        ],
    };

    /// <summary>Creates a PDF/A-2b document — embedded font, XMP `pdfaid` identification, bundled sRGB output intent — via PlumePDF's `PdfAConformance` switch.</summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("PdfACreate")]
    public long PlumePdf_CreatePdfA2b()
    {
        var font = PdfFont.FromFile(_fontPath);
        var options = PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b };
        using var document = BuildManuscript(font, tagged: false).Render(options);
        document.Save(_plumeOutputPath, options);
        return new FileInfo(_plumeOutputPath).Length;
    }

    /// <summary>Creates a PDF/A-2b document via iText7's published binary API (`PdfADocument` + layout), embedding the same font and ICC profile.</summary>
    [Benchmark]
    [BenchmarkCategory("PdfACreate")]
    public long ITextSeven_CreatePdfA2b()
    {
        using var output = new MemoryStream();
        using (var icc = new MemoryStream(_iccProfile))
        {
            var intent = new iText.Kernel.Pdf.PdfOutputIntent("Custom", string.Empty, null, "sRGB IEC61966-2.1", icc);
            var writer = new iText.Kernel.Pdf.PdfWriter(output);
            writer.SetCloseStream(false); // keep the MemoryStream readable for the size measurement below
            using var pdf = new iText.Pdfa.PdfADocument(
                writer,
                iText.Kernel.Pdf.PdfAConformance.PDF_A_2B,
                intent);
            var font = iText.Kernel.Font.PdfFontFactory.CreateFont(
                _fontPath,
                iText.IO.Font.PdfEncodings.IDENTITY_H,
                iText.Kernel.Font.PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
            using var document = new iText.Layout.Document(pdf);
            document.Add(new iText.Layout.Element.Paragraph("Quarterly Compliance Report").SetFont(font).SetFontSize(18));
            document.Add(new iText.Layout.Element.Paragraph("A paragraph of body text long enough to exercise layout, shaping, and content-stream generation.").SetFont(font));
        }

        return output.Length;
    }

    /// <summary>Creates a tagged (structure-tree-bearing) document via PlumePDF's opt-in semantics (`Manuscript.Language` + element roles).</summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("TaggedCreate")]
    public long PlumePdf_CreateTagged()
    {
        using var document = BuildManuscript(font: null, tagged: true).Render();
        document.Save(_plumeOutputPath);
        return new FileInfo(_plumeOutputPath).Length;
    }

    /// <summary>Creates a tagged document via iText7's published binary API (`PdfDocument.SetTagged()` + layout's automatic role assignment).</summary>
    [Benchmark]
    [BenchmarkCategory("TaggedCreate")]
    public long ITextSeven_CreateTagged()
    {
        using var output = new MemoryStream();
        var taggedWriter = new iText.Kernel.Pdf.PdfWriter(output);
        taggedWriter.SetCloseStream(false); // keep the MemoryStream readable for the size measurement below
        using (var pdf = new iText.Kernel.Pdf.PdfDocument(taggedWriter))
        {
            pdf.SetTagged();
            using var document = new iText.Layout.Document(pdf);
            document.Add(new iText.Layout.Element.Paragraph("Quarterly Compliance Report").SetFontSize(18));
            document.Add(new iText.Layout.Element.Paragraph("A paragraph of body text long enough to exercise layout, shaping, and content-stream generation."));
        }

        return output.Length;
    }

    /// <summary>Redacts a fixed region of a real-world document's first page via the public `PdfDocument.Redact` and full-rewrite save (redaction always routes through `Save`).</summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Redact")]
    public long PlumePdf_RedactRegion()
    {
        using var document = PdfDocument.Open(_redactionFixturePath);
        var result = document.Redact([RedactionTarget.Region(0, new PdfRectangle(0, 700, 595, 842))]);
        document.Save(_plumeOutputPath);
        return result.RegionsRedacted + new FileInfo(_plumeOutputPath).Length;
    }

    /// <summary>Redacts the same region via iText's pdfSweep add-on (`PdfCleaner.CleanUp`) — the one iText package that implements true content removal.</summary>
    [Benchmark]
    [BenchmarkCategory("Redact")]
    public long ITextSeven_RedactRegion()
    {
        using var output = new MemoryStream();
        var redactWriter = new iText.Kernel.Pdf.PdfWriter(output);
        redactWriter.SetCloseStream(false); // keep the MemoryStream readable for the size measurement below
        using (var reader = new iText.Kernel.Pdf.PdfReader(_redactionFixturePath))
        using (var pdf = new iText.Kernel.Pdf.PdfDocument(reader, redactWriter))
        {
            iText.PdfCleanup.PdfCleaner.CleanUp(
                pdf,
                [new iText.PdfCleanup.PdfCleanUpLocation(1, new iText.Kernel.Geom.Rectangle(0, 700, 595, 142))]);
        }

        return output.Length;
    }

    // Local copy of ComparisonCorpora's repo-root walk for assets outside the pdf.js subset
    // (the OFL font fixtures and PlumePDF's bundled ICC profile).
    private static string ResolveRepoFile(params string[] segments)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "bench.sln")) && dir.Parent is { } repoRoot)
            {
                var path = Path.Combine([repoRoot.FullName, .. segments]);
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException($"Expected repo file not found: {path} (run ./scripts/fetch-corpora.sh for corpora assets).");
                }

                return path;
            }
        }

        throw new InvalidOperationException("Could not locate bench.sln walking up from the benchmark's base directory.");
    }
}
