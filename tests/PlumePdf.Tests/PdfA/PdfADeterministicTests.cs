using PlumePdf.Elements;
using PlumePdf.Tests.Fonts;
using Xunit;

namespace PlumePdf.Tests.PdfA;

/// <summary>
/// Byte-identical double-render of a PDF/A-2b
/// document under <see cref="PdfOptions.Deterministic"/> with caller-supplied
/// <see cref="Manuscript.CreateDate"/>/<see cref="Manuscript.ModifyDate"/> — the positive
/// half of the scoping whose refusal half (<c>PLUME6058</c>, PDF/A + Deterministic without
/// supplied dates) <c>PdfACreateTests</c> and <c>XmpWriteTests</c> already pin.
/// Proven end to end through the full create pipeline: pagination, the XMP/DocInfo/output
/// intent finishing pass, font subsetting (when the font corpus is fetched), and
/// <c>Save</c>'s full rewrite.
/// </summary>
public class PdfADeterministicTests
{
    private static readonly DateTimeOffset FixedDate = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly PdfOptions Deterministic2b = new()
    {
        PdfAConformance = PdfAConformance.A2b,
        Deterministic = true,
    };

    [Fact]
    public void Render_PdfA2b_TwiceWithCallerSuppliedDates_ProducesByteIdenticalOutput()
    {
        AssertDoubleRenderIsByteIdentical(BuildImageOnlyManuscript, Deterministic2b);
    }

    [Fact]
    public void Render_PdfA2b_NonDeterministic_TwiceProducesDifferentOutput()
    {
        // The control case (the OptimizationTests pattern): proves the byte-identical test
        // above isn't passing vacuously — without Deterministic, the same manuscript with the
        // same caller-supplied dates still varies run to run (the fresh random trailer /ID).
        var nonDeterministic2b = PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b };
        var pathA = RenderAndSave(BuildImageOnlyManuscript, nonDeterministic2b);
        var pathB = RenderAndSave(BuildImageOnlyManuscript, nonDeterministic2b);
        try
        {
            Assert.NotEqual(File.ReadAllBytes(pathA), File.ReadAllBytes(pathB));
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    private static Manuscript BuildImageOnlyManuscript()
    {
        var pixels = new byte[4 * 4 * 3];
        Array.Fill(pixels, (byte)200);
        return new Manuscript
        {
            Title = "PDF/A determinism check",
            CreateDate = FixedDate,
            ModifyDate = FixedDate,
            Sections = [new Section { Body = new Image(pixels, 4, 4) }],
        };
    }

    [Fact]
    public void Render_PdfA2b_WithAnEmbeddedFont_TwiceWithCallerSuppliedDates_ProducesByteIdenticalOutput()
    {
        // The realistic PDF/A document carries text in an embedded, subsetted font — this
        // variant proves the subsetter/CIDSet/ToUnicode path is deterministic too, not just
        // the image-only metadata plumbing above. Self-skips when corpora/fonts/ isn't fetched.
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        Manuscript Build()
        {
            var noto = PdfFont.FromFile(FontFixtures.NotoSansRegular);
            return new Manuscript
            {
                Title = "PDF/A determinism check (embedded font)",
                CreateDate = FixedDate,
                ModifyDate = FixedDate,
                Sections =
                [
                    new Section
                    {
                        Body = new Column(
                            new Text("First deterministic paragraph.") { Font = noto },
                            new Text("Second, with ünïcode käse.") { Font = noto })
                        {
                            Spacing = 8,
                        },
                    },
                ],
            };
        }

        AssertDoubleRenderIsByteIdentical(Build, Deterministic2b);
    }

    private static void AssertDoubleRenderIsByteIdentical(Func<Manuscript> build, PdfOptions options)
    {
        var pathA = RenderAndSave(build, options);
        var pathB = RenderAndSave(build, options);
        try
        {
            Assert.Equal(File.ReadAllBytes(pathA), File.ReadAllBytes(pathB));
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    private static string RenderAndSave(Func<Manuscript> build, PdfOptions options)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-pdfa-det-{Guid.NewGuid():N}.pdf");
        using var document = build().Render(options);
        document.Save(path, options);
        return path;
    }
}
