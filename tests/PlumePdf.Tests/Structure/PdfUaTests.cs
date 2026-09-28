using PlumePdf.Elements;
using Xunit;

namespace PlumePdf.Tests.Structure;

/// <summary>
/// The explicit PDF/UA-1 opt-in: <see cref="Manuscript.PdfUa"/> implies
/// tagging, enforces the caller-required semantics (language <c>PLUME9011</c>, title
/// <c>PLUME9012</c>, alt text <c>PLUME9010</c>) as coded refusals before/during render, and
/// declares <c>pdfuaid:part = 1</c> — while <see cref="Manuscript.Language"/> alone keeps
/// meaning plain tagged PDF with no conformance claim and no title requirement.
/// </summary>
public class PdfUaTests
{
    [Fact]
    public void Render_PdfUaWithLanguageAndTitle_DeclaresPdfuaidPart1AndTags()
    {
        var manuscript = new Manuscript
        {
            PdfUa = true,
            Language = "en-US",
            Title = "Accessible Report",
            Sections = [new Section { Body = new Text("Body paragraph.") }],
        };

        using var document = manuscript.Render();

        var structure = PdfStructureInfo.For(document);
        Assert.True(structure.IsTagged);
        Assert.Equal("en-US", structure.Language);

        var xmp = document.GetXmpMetadataText();
        Assert.NotNull(xmp);
        Assert.Contains("pdfuaid:part", xmp, StringComparison.Ordinal);
        Assert.Contains("Accessible Report", xmp, StringComparison.Ordinal);
        Assert.DoesNotContain("pdfaid:part", xmp, StringComparison.Ordinal); // no PDF/A claim was requested.

        Assert.Equal("Accessible Report", document.GetInfo().Title);
    }

    [Fact]
    public void Render_PdfUaWithoutLanguage_ThrowsPlume9011()
    {
        var manuscript = new Manuscript
        {
            PdfUa = true,
            Title = "Accessible Report",
            Sections = [new Section { Body = new Text("Body.") }],
        };

        var ex = Assert.Throws<PlumePdfException>(() => manuscript.Render());
        Assert.Equal("PLUME9011", ex.Code);
        Assert.Contains("Manuscript.Language", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PdfUaWithoutTitle_ThrowsPlume9012()
    {
        var manuscript = new Manuscript
        {
            PdfUa = true,
            Language = "en-US",
            Sections = [new Section { Body = new Text("Body.") }],
        };

        var ex = Assert.Throws<PlumePdfException>(() => manuscript.Render());
        Assert.Equal("PLUME9012", ex.Code);
        Assert.Contains("Manuscript.Title", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PdfUaImageWithoutAltText_ThrowsPlume9010()
    {
        var pixels = new byte[2 * 2 * 3];
        var manuscript = new Manuscript
        {
            PdfUa = true,
            Language = "en-US",
            Title = "Accessible Report",
            Sections = [new Section { Body = new Image(pixels, 2, 2) }],
        };

        var ex = Assert.Throws<PlumePdfException>(() => manuscript.Render());
        Assert.Equal("PLUME9010", ex.Code);
    }

    [Fact]
    public void Render_LanguageOnly_StaysPlainTaggedPdf_NoPdfuaidNoTitleRefusal()
    {
        // The distinction: Language alone is plain tagged PDF — no title required, no
        // conformance claim written.
        var manuscript = new Manuscript
        {
            Language = "en-US",
            Sections = [new Section { Body = new Text("Body.") }],
        };

        using var document = manuscript.Render(); // no PLUME9012 despite the missing Title.

        Assert.True(PdfStructureInfo.For(document).IsTagged);
        var xmp = document.GetXmpMetadataText();
        Assert.True(xmp is null || !xmp.Contains("pdfuaid:part", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_PdfUaCombinedWithPdfA2b_DeclaresBothSchemasInOnePacket()
    {
        var fixedDate = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        byte[] pixels = new byte[2 * 2 * 3];
        Array.Fill(pixels, (byte)200);

        var manuscript = new Manuscript
        {
            PdfUa = true,
            Language = "en-US",
            Title = "Archival and accessible",
            CreateDate = fixedDate,
            ModifyDate = fixedDate,
            Sections = [new Section { Body = new Image(pixels, 2, 2) { AltText = "A gray swatch" } }],
        };

        using var document = manuscript.Render(PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b, Deterministic = true });

        var xmp = document.GetXmpMetadataText();
        Assert.NotNull(xmp);
        Assert.Contains("pdfaid:part", xmp, StringComparison.Ordinal);
        Assert.Contains("pdfuaid:part", xmp, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PdfUa_SavedAndReopened_KeepsClaimAndStructure()
    {
        // The exit-demo shape the corpus lane's veraPDF UA-1 interop check drives: compose →
        // PdfUa = true → save.
        var manuscript = new Manuscript
        {
            PdfUa = true,
            Language = "en-US",
            Title = "Round-trip UA",
            Sections = [new Section { Body = new Text("Heading") { HeadingLevel = 1 } }],
        };

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-ua-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = manuscript.Render())
            {
                document.Save(path);
            }

            using var reopened = PdfDocument.Open(path);
            Assert.Contains("pdfuaid:part", reopened.GetXmpMetadataText(), StringComparison.Ordinal);
            Assert.True(PdfStructureInfo.For(reopened).IsTagged);
            Assert.Equal("Round-trip UA", reopened.GetInfo().Title);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
