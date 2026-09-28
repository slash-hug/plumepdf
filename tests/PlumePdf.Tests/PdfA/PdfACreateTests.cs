using PlumePdf.Compose;
using PlumePdf.Documents.PdfA;
using PlumePdf.Elements;
using PlumePdf.Tests.Fonts;
using Xunit;

namespace PlumePdf.Tests.PdfA;

/// <summary>
/// <see cref="OutputIntentBuilder"/>'s ICC-embedding
/// round-trip, the full PDF/A create path behind <see cref="PdfOptions.PdfAConformance"/>
/// (version knob, XMP <c>pdfaid</c> packet + DocInfo agreement, output intent, the
/// Standard-14 embedding refusal <c>PLUME8023</c>, and the never-<c>/NeedAppearances</c>
/// guarantee), and <see cref="PdfAValidator"/>'s reaction to all of it. The
/// byte-identical determinism regression lives in <c>PdfADeterministicTests</c>; the veraPDF
/// CLI oracle over this same create path lives in
/// <c>tests/PlumePdf.CorpusTests/VeraPdfInteropTests.cs</c>.
/// </summary>
/// <remarks>
/// Tests that need an embeddable TrueType font self-skip via
/// <see cref="FontFixtures.SkipUnlessAvailable"/> when <c>corpora/fonts/</c> hasn't been
/// fetched; everything else here (image-only PDF/A documents, every refusal path) runs
/// hermetically.
/// </remarks>
public class PdfACreateTests
{
    private static readonly DateTimeOffset FixedDate = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static byte[] GrayPixels()
    {
        var pixels = new byte[4 * 4 * 3];
        Array.Fill(pixels, (byte)200);
        return pixels;
    }

    private static Manuscript ImageOnlyManuscript(string title = "PDF/A image fixture") => new()
    {
        Title = title,
        CreateDate = FixedDate,
        ModifyDate = FixedDate,
        Sections = [new Section { Body = new Image(GrayPixels(), 4, 4) }],
    };

    [Fact]
    public void Render_PdfA2b_ImageOnly_ProducesASelfCheckConformantFile()
    {
        var path = TempPdfPath();
        try
        {
            using (var document = ImageOnlyManuscript().Render(new PdfOptions { PdfAConformance = PdfAConformance.A2b }))
            {
                document.Save(path);
            }

            using var reopened = PdfDocument.Open(path);
            var result = PdfAValidator.Validate(reopened);

            Assert.Equal("2", result.DeclaredPart);
            Assert.Equal("B", result.DeclaredConformance);
            Assert.True(result.IsConformant, string.Join("; ", result.Failures.Select(static f => $"{f.RuleId}: {f.Message}")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Render_PdfA1b_ImageOnly_Writes14Header_AndPassesTheSelfCheck()
    {
        var path = TempPdfPath();
        try
        {
            using (var document = ImageOnlyManuscript().Render(new PdfOptions { PdfAConformance = PdfAConformance.A1b }))
            {
                document.Save(path);
            }

            // A1b forces the version knob to 1.4 without the caller touching
            // PdfOptions.PdfVersion.
            var header = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 8);
            Assert.Equal("%PDF-1.4", header);

            using var reopened = PdfDocument.Open(path);
            var result = PdfAValidator.Validate(reopened);
            Assert.Equal("1", result.DeclaredPart);
            Assert.True(result.IsConformant, string.Join("; ", result.Failures.Select(static f => $"{f.RuleId}: {f.Message}")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Render_PdfA2b_WithEmbeddedFont_ProducesASelfCheckConformantFile()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var noto = PdfFont.FromFile(FontFixtures.NotoSansRegular);
        var manuscript = new Manuscript
        {
            Title = "Embedded-font PDF/A",
            CreateDate = FixedDate,
            ModifyDate = FixedDate,
            Sections = [new Section { Body = new Text("PDF/A body text.") { Font = noto } }],
        };

        var path = TempPdfPath();
        try
        {
            using (var document = manuscript.Render(new PdfOptions { PdfAConformance = PdfAConformance.A2b }))
            {
                document.Save(path);
            }

            using var reopened = PdfDocument.Open(path);
            Assert.Equal("Embedded-font PDF/A", reopened.GetInfo().Title);
            var xmp = reopened.GetXmpMetadataText();
            Assert.NotNull(xmp);
            Assert.Contains("pdfaid", xmp, StringComparison.Ordinal);
            Assert.Contains("Embedded-font PDF/A", xmp, StringComparison.Ordinal);

            var result = PdfAValidator.Validate(reopened);
            Assert.True(result.IsConformant, string.Join("; ", result.Failures.Select(static f => $"{f.RuleId}: {f.Message}")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Render_PdfA_WithStandard14Fonts_IsRefusedNamingEveryOffendingFontAndTheFixPath()
    {
        // Two distinct Standard-14 fonts: the default Helvetica (Text.Font unset) and an
        // explicit Times-Roman — the refusal must name EVERY offender, not just the first.
        var manuscript = new Manuscript
        {
            CreateDate = FixedDate,
            ModifyDate = FixedDate,
            Sections =
            [
                new Section
                {
                    Body = new Column(
                        new Text("Default Helvetica body."),
                        new Text("Times body.") { Font = PdfFont.TimesRoman }),
                },
            ],
        };

        var ex = Assert.Throws<PlumePdfException>(() => manuscript.Render(new PdfOptions { PdfAConformance = PdfAConformance.A2b }));

        Assert.Equal("PLUME8023", ex.Code);
        Assert.Contains("'Helvetica'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Times-Roman'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PdfFont.FromFile", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PdfA_WithWatermark_IsRefusedNamingTheWatermarkUsage()
    {
        // Watermarks/stamps always draw with Helvetica-Bold (no font surface of their own), so
        // they can never appear in a v1.0 PDF/A document — the refusal says so explicitly.
        var manuscript = new Manuscript
        {
            CreateDate = FixedDate,
            ModifyDate = FixedDate,
            Sections = [new Section { Body = new Image(GrayPixels(), 4, 4), Watermark = new Watermark { Text = "DRAFT" } }],
        };

        var ex = Assert.Throws<PlumePdfException>(() => manuscript.Render(new PdfOptions { PdfAConformance = PdfAConformance.A2b }));

        Assert.Equal("PLUME8023", ex.Code);
        Assert.Contains("'Helvetica-Bold'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Watermark \"DRAFT\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PdfA_Deterministic_WithoutDates_IsRefusedBeforeLayout()
    {
        var manuscript = new Manuscript
        {
            Sections = [new Section { Body = new Image(GrayPixels(), 4, 4) }],
        };

        var ex = Assert.Throws<PlumePdfException>(() => manuscript.Render(new PdfOptions { PdfAConformance = PdfAConformance.A2b, Deterministic = true }));

        Assert.Equal("PLUME6058", ex.Code);
        Assert.Contains("Manuscript.CreateDate", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("not-a-version")]
    public void Render_PdfA2b_WithAVersionKnobAboveTheCeilingOrUnparseable_IsRefused(string version)
    {
        var ex = Assert.Throws<PlumePdfException>(() => ImageOnlyManuscript().Render(new PdfOptions { PdfAConformance = PdfAConformance.A2b, PdfVersion = version }));

        Assert.Equal("PLUME6071", ex.Code);
    }

    [Fact]
    public void Save_WithFreshOptionsCarryingA1b_StillWritesThe14Header()
    {
        // The "consumed by ... Save" rule: the version knob is re-applied from whatever
        // options Save actually runs under, so a caller passing fresh options (losing the
        // coerced PdfVersion Render captured) still gets a PDF/A-1B-ceiling header.
        var path = TempPdfPath();
        try
        {
            using (var document = ImageOnlyManuscript().Render(new PdfOptions { PdfAConformance = PdfAConformance.A1b }))
            {
                document.Save(path, new PdfOptions { PdfAConformance = PdfAConformance.A1b });
            }

            var header = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 8);
            Assert.Equal("%PDF-1.4", header);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Render_PdfA_NeverSetsNeedAppearances()
    {
        // The create path never emits an /AcroForm at all, so /NeedAppearances can never be set —
        // asserted on the raw reopened catalog, not just via the validator's rule.
        var path = TempPdfPath();
        try
        {
            using (var document = ImageOnlyManuscript().Render(new PdfOptions { PdfAConformance = PdfAConformance.A2b }))
            {
                document.Save(path);
            }

            using var reopened = PdfDocument.Open(path);
            var catalog = reopened.Objects.Trailer[PdfName.Root] is PdfReference rootRef
                ? reopened.Objects[rootRef.Target] as PdfDictionary
                : null;
            Assert.NotNull(catalog);
            Assert.False(catalog!.ContainsKey(PdfName.Get("AcroForm")));

            var result = PdfAValidator.Validate(reopened);
            var finding = Assert.Single(result.Findings, static f => f.RuleId == "NeedAppearancesAbsent");
            Assert.Equal(PdfARuleStatus.Pass, finding.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Render_PdfA_WithACallerSuppliedOutputIntentProfile_EmbedsItUnderItsIdentifier()
    {
        // The bundled profile is only the default — a caller supplies their own via
        // PdfOptions, and the identifier they name round-trips into the written intent.
        var options = new PdfOptions
        {
            PdfAConformance = PdfAConformance.A2b,
            PdfAOutputIntentProfile = OutputIntentBuilder.SrgbProfile,
            PdfAOutputConditionIdentifier = "Custom sRGB condition",
        };

        var path = TempPdfPath();
        try
        {
            using (var document = ImageOnlyManuscript().Render(options))
            {
                document.Save(path);
            }

            using var reopened = PdfDocument.Open(path);
            var result = PdfAValidator.Validate(reopened);
            var finding = Assert.Single(result.Findings, static f => f.RuleId == "OutputIntent");
            Assert.Equal(PdfARuleStatus.Pass, finding.Status);

            var raw = File.ReadAllBytes(path);
            Assert.Contains("Custom sRGB condition", System.Text.Encoding.Latin1.GetString(raw), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SrgbProfile_Is456BytesWithMonitorDeviceClass()
    {
        var icc = OutputIntentBuilder.SrgbProfile;

        Assert.Equal(456, icc.Length);
        Assert.Equal("mntr", System.Text.Encoding.ASCII.GetString(icc, 12, 4));
        Assert.Equal("RGB ", System.Text.Encoding.ASCII.GetString(icc, 16, 4));
    }

    [Fact]
    public void SrgbProfile_ReturnsAFreshDefensiveCopyEachCall()
    {
        // The copy-on-materialize contract: two calls must be content-equal but never
        // the same array instance, so a caller mutating one copy can never corrupt every other
        // caller's view of the bundled profile (the shared backing literal in OutputIntentBuilder).
        var first = OutputIntentBuilder.SrgbProfile;
        var second = OutputIntentBuilder.SrgbProfile;

        Assert.NotSame(first, second);
        Assert.Equal(first, second);

        first[0] = 255;
        Assert.NotEqual(first, OutputIntentBuilder.SrgbProfile);
    }

    [Fact]
    public void AddSrgbOutputIntent_RoundTripsThroughSaveAndReopen_AndPassesTheOutputIntentSelfCheck()
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("PDF/A output intent round-trip fixture.");
        });

        OutputIntentBuilder.AddSrgbOutputIntent(document);

        var path = TempPdfPath();
        try
        {
            document.Save(path);

            using var reopened = PdfDocument.Open(path);
            Assert.Empty(reopened.Diagnostics);

            var result = PdfAValidator.Validate(reopened);
            var outputIntentFinding = Assert.Single(result.Findings, f => f.RuleId == "OutputIntent");
            Assert.Equal(PdfARuleStatus.Pass, outputIntentFinding.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddOutputIntent_CustomProfile_AppendsToExistingOutputIntentsArrayRatherThanReplacingIt()
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("Two output intents.");
        });

        OutputIntentBuilder.AddSrgbOutputIntent(document);
        OutputIntentBuilder.AddOutputIntent(document, OutputIntentBuilder.SrgbProfile, "sRGB IEC61966-2.1 (second)");

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path);

            var catalog = reopened.Objects.Trailer[PdfName.Root] is PdfReference rootRef
                ? reopened.Objects[rootRef.Target] as PdfDictionary
                : null;
            Assert.NotNull(catalog);
            var intents = catalog!.TryGetValue(PdfName.Get("OutputIntents"), out var value) && value is PdfReference intentsRef
                ? reopened.Objects[intentsRef.Target] as PdfArray
                : value as PdfArray;
            Assert.NotNull(intents);
            Assert.Equal(2, intents!.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddOutputIntent_NullDocument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => OutputIntentBuilder.AddOutputIntent(null!, OutputIntentBuilder.SrgbProfile, "sRGB IEC61966-2.1"));
    }

    [Fact]
    public void AddOutputIntent_NullIccProfile_Throws()
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("x");
        });

        Assert.Throws<ArgumentNullException>(() => OutputIntentBuilder.AddOutputIntent(document, null!, "sRGB IEC61966-2.1"));
    }

    [Fact]
    public void AddOutputIntent_EmptyConditionIdentifier_Throws()
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("x");
        });

        Assert.Throws<ArgumentException>(() => OutputIntentBuilder.AddOutputIntent(document, OutputIntentBuilder.SrgbProfile, ""));
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-pdfa-create-{Guid.NewGuid():N}.pdf");
}
