using System.Text;
using PlumePdf.Documents.Compression;
using PlumePdf.Documents.Metadata;
using Xunit;
using static PlumePdf.Tests.Compression.CompressFixtures;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// What <c>Pdf.Compress</c> checks before it plans anything: argument validation, the effective
/// save options (the version raise for packing, PDF/A-1 sources left unpacked), and the
/// refusals that come before any work (linearized output, an encrypted source).
/// </summary>
public class CompressOptionsTests
{
    public static TheoryData<double> BadMaxDpi => new() { 0, -1, double.NaN, double.PositiveInfinity };

    [Theory]
    [MemberData(nameof(BadMaxDpi))]
    public void MaxDpi_MustBePositiveAndFinite(double maxDpi)
    {
        var options = new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = maxDpi } };

        Assert.Throws<ArgumentOutOfRangeException>(() => Pdf.Compress("unused.pdf", "unused-out.pdf", options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public void JpegQuality_MustBeOneToOneHundred(int quality)
    {
        var options = new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = quality } };

        Assert.Throws<ArgumentOutOfRangeException>(() => Pdf.Compress("unused.pdf", "unused-out.pdf", options));
    }

    [Fact]
    public void Paths_AreRequired()
    {
        Assert.Throws<ArgumentNullException>(() => Pdf.Compress(null!, "out.pdf"));
        Assert.Throws<ArgumentException>(() => Pdf.Compress("in.pdf", string.Empty));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void JpegQuality_AcceptsTheEnds(int quality)
    {
        var (result, _) = Compress(OneImage(Image(), FlateSamples()), new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = quality } });

        Assert.Single(result.Images);
    }

    [Fact]
    public void CallerVersionBelowOnePointFive_IsRaisedForPacking()
    {
        var (result, output) = Compress(OneImage(Image(), FlateSamples()), options: PdfOptions.Default with { PdfVersion = "1.4" });

        Assert.StartsWith("%PDF-1.5", Encoding.ASCII.GetString(output, 0, 8), StringComparison.Ordinal);
        Assert.True(HasObjectStreams(output));
        var note = Assert.Single(result.Diagnostics, static d => d.Code == "PLUME6088");
        Assert.Equal(DiagnosticSeverity.Info, note.Severity);
        Assert.Contains("1.4", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultVersion_IsLeftAlone()
    {
        var (result, output) = Compress(OneImage(Image(), FlateSamples()));

        Assert.StartsWith("%PDF-1.7", Encoding.ASCII.GetString(output, 0, 8), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, static d => d.Code == "PLUME6088");
    }

    [Fact]
    public void LosslessOff_KeepsTheCallersVersion()
    {
        var (result, output) = Compress(OneImage(Image(), FlateSamples()), new PdfCompressOptions { Lossless = false }, PdfOptions.Default with { PdfVersion = "1.4" });

        Assert.StartsWith("%PDF-1.4", Encoding.ASCII.GetString(output, 0, 8), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, static d => d.Code == "PLUME6088");
    }

    [Fact]
    public void PdfA1Source_IsNotPacked()
    {
        var source = TempPath("source");
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(OneImage(Image(), FlateSamples()).Build()))
            {
                document.SetXmpMetadata(new XmpPacket { Conformance = PdfAConformance.A1b });
                document.Save(source);
            }

            var result = Pdf.Compress(source, output);

            Assert.False(HasObjectStreams(File.ReadAllBytes(output)));
            var note = Assert.Single(result.Diagnostics, static d => d.Code == "PLUME6089");
            Assert.Equal(DiagnosticSeverity.Info, note.Severity);
            Assert.DoesNotContain(result.Diagnostics, static d => d.Code == "PLUME6088");
        }
        finally
        {
            Delete(source, output);
        }
    }

    [Fact]
    public void CallerPdfA1Conformance_IsNotPacked()
    {
        var (result, output) = Compress(OneImage(Image(), FlateSamples()), options: PdfOptions.Default with { PdfAConformance = PdfAConformance.A1b });

        Assert.False(HasObjectStreams(output));
        Assert.Contains(result.Diagnostics, static d => d.Code == "PLUME6089");
    }

    [Fact]
    public void PdfA2Source_IsPacked()
    {
        var source = TempPath("source");
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(OneImage(Image(), FlateSamples()).Build()))
            {
                document.SetXmpMetadata(new XmpPacket { Conformance = PdfAConformance.A2b });
                document.Save(source);
            }

            var result = Pdf.Compress(source, output);

            Assert.True(HasObjectStreams(File.ReadAllBytes(output)));
            Assert.DoesNotContain(result.Diagnostics, static d => d.Code == "PLUME6089");
        }
        finally
        {
            Delete(source, output);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PdfA1bSource_StaysSelfCheckConformant(bool lossless)
    {
        var source = TempPath("source");
        var output = TempPath();
        try
        {
            var manuscript = new Manuscript
            {
                Title = "PDF/A-1b compress fixture",
                CreateDate = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero),
                ModifyDate = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero),
                Sections = [new PlumePdf.Elements.Section { Body = new PlumePdf.Elements.Image(Enumerable.Range(0, 48).Select(static i => (byte)(i * 5)).ToArray(), 4, 4) }],
            };
            using (var document = manuscript.Render(new PdfOptions { PdfAConformance = PdfAConformance.A1b }))
            {
                document.Save(source);
            }

            Assert.True(Pdf.ValidatePdfA(source).IsConformant);

            Pdf.Compress(source, output, new PdfCompressOptions { Lossless = lossless });

            Assert.StartsWith("%PDF-1.4", Encoding.ASCII.GetString(File.ReadAllBytes(output), 0, 8), StringComparison.Ordinal);
            var check = Pdf.ValidatePdfA(output);
            Assert.True(check.IsConformant, string.Join("; ", check.Failures.Select(static f => $"{f.RuleId}: {f.Message}")));
        }
        finally
        {
            Delete(source, output);
        }
    }

    [Fact]
    public void PdfA1Source_KeepsAnExplicitlyChosenVersion()
    {
        var source = TempPath("source");
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(OneImage(Image(), FlateSamples()).Build()))
            {
                document.SetXmpMetadata(new XmpPacket { Conformance = PdfAConformance.A1b });
                document.Save(source);
            }

            Pdf.Compress(source, output, options: PdfOptions.Default with { PdfVersion = "1.6" });

            Assert.StartsWith("%PDF-1.6", Encoding.ASCII.GetString(File.ReadAllBytes(output), 0, 8), StringComparison.Ordinal);
        }
        finally
        {
            Delete(source, output);
        }
    }

    [Fact]
    public void Linearize_IsRefusedBeforeAnythingIsRead()
    {
        var output = TempPath();

        var ex = Assert.Throws<PlumePdfException>(() => Pdf.Compress("does-not-exist.pdf", output, options: PdfOptions.Default with { Linearize = true }));

        Assert.Equal("PLUME5018", ex.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void EncryptedSource_IsRefusedBeforePlanning()
    {
        var source = Path.Combine(RepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "AES-128.pdf");
        var output = TempPath();
        var planned = false;
        var hooks = new CompressionHooks { Planning = () => planned = true };

        var ex = Assert.Throws<PlumePdfException>(() => CompressionEngine.Compress(source, output, PdfCompressOptions.Default, null, CancellationToken.None, hooks));

        Assert.Equal("PLUME5001", ex.Code);
        Assert.False(planned);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PlanningHook_RunsForAnOrdinarySource()
    {
        // The counterpart of the refusal tests: the hook they rely on does fire.
        var source = Write(OneImage(Image(), FlateSamples()));
        var output = TempPath();
        var planned = false;
        try
        {
            CompressionEngine.Compress(source, output, PdfCompressOptions.Default, null, CancellationToken.None, new CompressionHooks { Planning = () => planned = true });

            Assert.True(planned);
        }
        finally
        {
            Delete(source, output);
        }
    }
}
