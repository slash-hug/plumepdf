using System.Text.RegularExpressions;
using PlumePdf.Compose;
using PlumePdf.Documents;
using PlumePdf.Documents.Redaction;
using PlumePdf.Elements;
using PlumePdf.Tests.Signing;
using Xunit;

namespace PlumePdf.Tests.Redaction;

/// <summary><see cref="RedactionTarget"/>/<see cref="RedactionTargetResolver"/> — targeting resolves to concrete page regions with correct match counts.</summary>
public class RedactionTargetTests
{
    [Fact]
    public void Resolve_TextTarget_FindsExactOccurrenceCount()
    {
        using var document = ComposeSample("Alpha Beta Alpha Gamma Alpha");

        var (regions, matchCount) = RedactionTargetResolver.Resolve([RedactionTarget.Text("Alpha")], document, PdfRedactOptions.Default);

        Assert.Equal(3, matchCount);
        Assert.Equal(3, regions.Count);
    }

    [Fact]
    public void Resolve_TextTarget_IsCaseInsensitiveByDefault()
    {
        using var document = ComposeSample("Confidential Data");

        var (_, matchCount) = RedactionTargetResolver.Resolve([RedactionTarget.Text("confidential")], document, PdfRedactOptions.Default);

        Assert.Equal(1, matchCount);
    }

    [Fact]
    public void Resolve_TextTarget_CaseSensitiveOptionRefusesLowercaseMismatch()
    {
        using var document = ComposeSample("Confidential Data");

        var (_, matchCount) = RedactionTargetResolver.Resolve([RedactionTarget.Text("confidential")], document, new PdfRedactOptions { CaseSensitiveText = true });

        Assert.Equal(0, matchCount);
    }

    [Fact]
    public void Resolve_PatternTarget_FindsRegexMatches()
    {
        using var document = ComposeSample("Call 555-01-2345 or 555-99-8888 for details.");

        var (_, matchCount) = RedactionTargetResolver.Resolve([RedactionTarget.Pattern(new Regex(@"\d{3}-\d{2}-\d{4}"))], document, PdfRedactOptions.Default);

        Assert.Equal(2, matchCount);
    }

    [Fact]
    public void Resolve_RegionTarget_PassesThroughUnchanged()
    {
        using var document = ComposeSample("Anything");
        var rect = new PdfRectangle(0, 0, 100, 100);

        var (regions, matchCount) = RedactionTargetResolver.Resolve([RedactionTarget.Region(0, rect)], document, PdfRedactOptions.Default);

        Assert.Equal(1, matchCount);
        Assert.Equal(0, regions[0].PageIndex);
        Assert.Equal(rect, regions[0].Rect);
    }

    [Fact]
    public void Resolve_MaxMatchesExceeded_ThrowsPlume6056()
    {
        using var document = ComposeSample("A A A A A A A A A A");

        var ex = Assert.Throws<PlumePdfException>(() =>
            RedactionTargetResolver.Resolve([RedactionTarget.Text("A")], document, new PdfRedactOptions { MaxMatches = 3 }));

        Assert.Equal("PLUME6060", ex.Code);
    }

    [Fact]
    public void Resolve_PageIndexOutOfRange_Throws()
    {
        using var document = ComposeSample("Anything");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RedactionTargetResolver.Resolve([RedactionTarget.Text("Anything", pageIndex: 5)], document, PdfRedactOptions.Default));
    }

    internal static PdfDocument ComposeSample(string text) => PdfDocument.Compose(page =>
    {
        page.Size(PageSize.A4).Margin(40);
        page.Content().Text(text);
    });
}

/// <summary><see cref="RedactionEngine"/> orchestration, guards, and metadata scrubbing.</summary>
public class RedactionTests
{
    [Fact]
    public void Redact_LiteralTextTarget_RemovedFromReopenedExtraction()
    {
        // Two separate paragraphs so each becomes its own content-stream text-showing
        // operator — ContentStreamEditor removes at operator granularity (this file's own
        // class remarks), so the surviving-text assertion below needs the
        // target isolated to its own operator, not merely a substring of a shared one.
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Column(col =>
            {
                col.Item().Text("Jane Doe");
                col.Item().Text("Public: nothing else here.");
            });
        });

        var result = RedactionEngine.Redact(document, [RedactionTarget.Text("Jane Doe")], null);

        Assert.Equal(1, result.MatchCount);
        Assert.False(result.HadNoMatches);
        Assert.True(result.TextOperatorsRemoved > 0);

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path);
            var text = reopened.Pages[0].ExtractText().Text;
            Assert.DoesNotContain("Jane Doe", text, StringComparison.Ordinal);
            Assert.Contains("Public", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_PatternTarget_RemovesRegexMatchOnly()
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Column(col =>
            {
                col.Item().Text("999-88-7777");
                col.Item().Text("Reference number stays confidential otherwise visible.");
            });
        });

        var result = RedactionEngine.Redact(document, [RedactionTarget.Pattern(new Regex(@"\d{3}-\d{2}-\d{4}"))], null);
        Assert.Equal(1, result.MatchCount);

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path);
            var text = reopened.Pages[0].ExtractText().Text;
            Assert.DoesNotContain("999-88-7777", text, StringComparison.Ordinal);
            Assert.Contains("Reference number", text, StringComparison.Ordinal);
            Assert.Contains("visible", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_RegionCoveringWholePage_RemovesAllPageText()
    {
        using var document = RedactionTargetTests.ComposeSample("Everything on this page must go away.");
        var mediaBox = new PdfRectangle(0, 0, PageSize.A4.Width, PageSize.A4.Height);

        var result = RedactionEngine.Redact(document, [RedactionTarget.Region(0, mediaBox)], null);
        Assert.Equal(1, result.MatchCount);
        Assert.True(result.TextOperatorsRemoved > 0);

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path);
            var text = reopened.Pages[0].ExtractText().Text;
            Assert.DoesNotContain("Everything", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_NoMatch_ReportsZeroLoudlyAndLeavesContentUntouched()
    {
        using var document = RedactionTargetTests.ComposeSample("Nothing sensitive here at all.");

        var result = RedactionEngine.Redact(document, [RedactionTarget.Text("Nonexistent Phrase")], null);

        Assert.Equal(0, result.MatchCount);
        Assert.True(result.HadNoMatches);
        Assert.Equal(0, result.TextOperatorsRemoved);

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path);
            Assert.Contains("Nothing sensitive", reopened.Pages[0].ExtractText().Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_EncryptedSource_ThrowsPlume6057()
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "AES-128.pdf");
        using var document = PdfDocument.Open(path);

        var ex = Assert.Throws<PlumePdfException>(() => RedactionEngine.Redact(document, [RedactionTarget.Text("Hello")], null));
        Assert.Equal("PLUME6061", ex.Code);
    }

    [Fact]
    public void Redact_SignedSource_DefaultOptions_ThrowsPlume6058()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(signedPath, new PdfSignOptions { Certificate = ca.LeafRsa });
            }

            using var signed = PdfDocument.Open(signedPath);
            var ex = Assert.Throws<PlumePdfException>(() => RedactionEngine.Redact(signed, [RedactionTarget.Text("Sample")], null));
            Assert.Equal("PLUME6062", ex.Code);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
        }
    }

    [Fact]
    public void Redact_SignedSource_AllowInvalidatingSignatures_StripsAndSucceeds()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        var outputPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(signedPath, new PdfSignOptions { Certificate = ca.LeafRsa });
            }

            using var signed = PdfDocument.Open(signedPath);
            var result = RedactionEngine.Redact(signed, [RedactionTarget.Text("Sample")], new PdfRedactOptions { AllowInvalidatingSignatures = true });

            Assert.True(result.SignaturesStripped > 0);
            signed.Save(outputPath);

            using var reopened = PdfDocument.Open(outputPath);
            Assert.Empty(reopened.Signatures);

            // The ratified wording is "strips signature fields" — the field and its
            // widget must be GONE, not merely emptied: no /AcroForm survives (the signature
            // field was its only field), and no page /Annots entry is a signature widget.
            Assert.NotNull(reopened.Catalog);
            Assert.False(reopened.Catalog!.Dictionary.ContainsKey(PdfName.AcroForm));
            foreach (var page in reopened.Pages)
            {
                if (!page.Dictionary.TryGetValue(PdfName.Get("Annots"), out var annotsValue))
                {
                    continue;
                }

                var annots = annotsValue is PdfReference annotsRef ? reopened.Objects[annotsRef.Target] as PdfArray : annotsValue as PdfArray;
                if (annots is null)
                {
                    continue;
                }

                foreach (var entry in annots)
                {
                    var annot = entry is PdfReference entryRef ? reopened.Objects[entryRef.Target] as PdfDictionary : entry as PdfDictionary;
                    if (annot is not null && annot.TryGetValue(PdfName.FT, out var ft))
                    {
                        Assert.False(PdfName.Get("Sig").Equals(ft), "a signature widget survived in page /Annots after AllowInvalidatingSignatures stripping.");
                    }
                }
            }
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public void Redact_ImageIntersectingRegion_RemovesImageAndItIsNoLongerExtractable()
    {
        var pixels = new byte[64 * 64 * 3];
        Array.Fill(pixels, (byte)200);
        var image = new Image(pixels, pixelWidth: 64, pixelHeight: 64) { Width = 100, Height = 100 };

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
            using var reopened = PdfDocument.Open(path);
            var images = reopened.Pages[0].ExtractImages();
            Assert.All(images, img => Assert.True(img.Data.Length < pixels.Length));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_TextTargetMatchingDocInfoTitle_ScrubsDocInfo()
    {
        using var document = RedactionTargetTests.ComposeSample("Body text, nothing special.");

        // The object graph is mutated directly through the public escape hatch (doc.Objects)
        // rather than the public DocInfo-write surface, to exercise the scrubber against a raw
        // /Info dictionary regardless of how it was created.
        var infoReference = document.Objects.AllocateNumber();
        var infoDictionary = new PdfDictionary();
        infoDictionary.Set(PdfName.Get("Title"), PdfString.FromLiteral("Project Nightingale - Confidential"u8.ToArray()));
        document.Objects.RegisterNew(infoReference, infoDictionary);
        document.Objects.Trailer.Set(PdfName.Info, new PdfReference(infoReference));

        var result = RedactionEngine.Redact(document, [RedactionTarget.Text("Nightingale")], null);
        Assert.True(result.ScrubbedSurfaces.ContainsKey("DocInfo"));

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path);
            Assert.DoesNotContain("Nightingale", reopened.GetInfo().Title ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_TextTargetMatchingXmpPacket_ScrubsWholePacket()
    {
        using var document = RedactionTargetTests.ComposeSample("Body text, nothing special.");

        var catalogReference = document.Objects.Trailer[PdfName.Root] is PdfReference rootRef
            ? rootRef.Target
            : throw new InvalidOperationException("Composed document has no /Root.");
        var catalog = (PdfDictionary)document.Objects[catalogReference];

        var xmpBytes = "<?xpacket begin=''?><x:xmpmeta><dc:title>Redact Me Please</dc:title></x:xmpmeta><?xpacket end='w'?>"u8.ToArray();
        var xmpReference = document.Objects.AllocateNumber();
        var xmpDictionary = new PdfDictionary();
        xmpDictionary.Set(PdfName.Type, PdfName.Get("Metadata"));
        xmpDictionary.Set(PdfName.Subtype, PdfName.Get("XML"));
        document.Objects.RegisterNew(xmpReference, new PdfStream(xmpDictionary, xmpBytes));
        catalog.Set(PdfName.Metadata, new PdfReference(xmpReference));
        document.Objects.MarkDirty(catalogReference);

        var result = RedactionEngine.Redact(document, [RedactionTarget.Text("Redact Me Please")], null);
        Assert.True(result.ScrubbedSurfaces.ContainsKey("Xmp"));

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path);
            var text = reopened.GetXmpMetadataText();
            Assert.NotNull(text);
            Assert.DoesNotContain("Redact Me Please", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_TextTargetMatchingAnnotationContents_ScrubsAnnotation()
    {
        using var document = RedactionTargetTests.ComposeSample("Body text, nothing special.");

        var page = document.Pages[0];
        var annotReference = document.Objects.AllocateNumber();
        var annotDictionary = new PdfDictionary();
        annotDictionary.Set(PdfName.Type, PdfName.Get("Annot"));
        annotDictionary.Set(PdfName.Subtype, PdfName.Get("Text"));
        annotDictionary.Set(PdfName.Get("Contents"), PdfString.FromLiteral("Reviewer note: leaked internal codename Falcon"u8.ToArray()));
        document.Objects.RegisterNew(annotReference, annotDictionary);

        var annots = new PdfArray { new PdfReference(annotReference) };

        // MetadataScrubber (like ContentStreamEditor) reads PdfPage.Dictionary — the "effective"
        // dictionary with inherited page-tree attributes resolved onto it — not
        // doc.Objects[page.Reference]'s untouched original (PdfPage's own remarks), so the test
        // fixture has to mutate the same one production code reads.
        page.Dictionary.Set(PdfName.Get("Annots"), annots);
        document.Objects.MarkDirty(page.Reference);

        var result = RedactionEngine.Redact(document, [RedactionTarget.Text("Falcon")], null);
        Assert.True(result.ScrubbedSurfaces.ContainsKey("AnnotationContents"));

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path);

            // Save renumbers every object (full rewrite) — find the annotation by walking the
            // reopened page's /Annots rather than assuming annotReference's number survived.
            var reopenedPage = reopened.Pages[0];
            Assert.True(reopenedPage.Dictionary.TryGetValue(PdfName.Get("Annots"), out var annotsValue));
            var reopenedAnnots = (PdfArray)(annotsValue is PdfReference annotsRef ? reopened.Objects[annotsRef.Target] : annotsValue);
            Assert.Single(reopenedAnnots);
            var reopenedAnnotRef = (PdfReference)reopenedAnnots[0];
            var reopenedAnnot = (PdfDictionary)reopened.Objects[reopenedAnnotRef.Target];
            Assert.False(reopenedAnnot.ContainsKey(PdfName.Get("Contents")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_ImageIntersectingRegion_RefuseOnImageRemoval_ThrowsPlume6074()
    {
        var pixels = new byte[16 * 16 * 3];
        Array.Fill(pixels, (byte)10);
        var image = new Image(pixels, pixelWidth: 16, pixelHeight: 16) { Width = 100, Height = 100 };

        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(0);
            page.Content().Image(image);
        });

        var mediaBox = new PdfRectangle(0, 0, PageSize.A4.Width, PageSize.A4.Height);
        var options = new PdfRedactOptions { RefuseOnImageRemoval = true };

        var ex = Assert.Throws<PlumePdfException>(() => RedactionEngine.Redact(document, [RedactionTarget.Region(0, mediaBox)], options));

        Assert.Equal("PLUME6074", ex.Code);
        Assert.Contains("image XObject", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RegionIntersectingAnnotation_WipesAppearanceStreamsAndContents()
    {
        const string hidden = "HIDDENNOTECALLSIGN42";
        using var document = RedactionTargetTests.ComposeSample("Body text, nothing special.");

        // A FreeText annotation whose /AP /N appearance paints text the page content stream
        // never carries — the executed repro: without appearance wiping, this text fully inside
        // the region survives redaction and still renders.
        var apBytes = System.Text.Encoding.ASCII.GetBytes($"BT /Helv 10 Tf 2 2 Td ({hidden}) Tj ET");
        var apDictionary = new PdfDictionary();
        apDictionary.Set(PdfName.Type, PdfName.Get("XObject"));
        apDictionary.Set(PdfName.Subtype, PdfName.Get("Form"));
        apDictionary.Set(PdfName.Get("BBox"), new PdfArray { PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(200), PdfNumber.Get(40) });
        var apReference = document.Objects.AllocateNumber();
        document.Objects.RegisterNew(apReference, new PdfStream(apDictionary, apBytes));

        var annotReference = document.Objects.AllocateNumber();
        var annot = new PdfDictionary();
        annot.Set(PdfName.Type, PdfName.Get("Annot"));
        annot.Set(PdfName.Subtype, PdfName.Get("FreeText"));
        annot.Set(PdfName.Get("Rect"), new PdfArray { PdfNumber.Get(100), PdfNumber.Get(600), PdfNumber.Get(300), PdfNumber.Get(640) });
        annot.Set(PdfName.Get("Contents"), PdfString.FromLiteral(System.Text.Encoding.ASCII.GetBytes(hidden)));
        var ap = new PdfDictionary();
        ap.Set(PdfName.N, new PdfReference(apReference));
        annot.Set(PdfName.AP, ap);
        document.Objects.RegisterNew(annotReference, annot);

        var page = document.Pages[0];
        page.Dictionary.Set(PdfName.Get("Annots"), new PdfArray { new PdfReference(annotReference) });
        document.Objects.MarkDirty(page.Reference);

        var mediaBox = new PdfRectangle(0, 0, PageSize.A4.Width, PageSize.A4.Height);
        var result = RedactionEngine.Redact(document, [RedactionTarget.Region(0, mediaBox)], null);

        Assert.Equal(1, result.AnnotationAppearancesWiped);
        Assert.Contains(document.Diagnostics, static d => d.Code == "PLUME6075");

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            var wholeFile = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain(hidden, wholeFile, StringComparison.Ordinal);

            using var reopened = PdfDocument.Open(path);
            var reopenedPage = reopened.Pages[0];
            Assert.True(reopenedPage.Dictionary.TryGetValue(PdfName.Get("Annots"), out var annotsValue));
            var annots = (PdfArray)(annotsValue is PdfReference annotsRef ? reopened.Objects[annotsRef.Target] : annotsValue);
            var reopenedAnnot = (PdfDictionary)reopened.Objects[((PdfReference)annots[0]).Target];
            Assert.False(reopenedAnnot.ContainsKey(PdfName.Get("Contents")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redact_AnnotationOutsideEveryRegion_IsLeftUntouched()
    {
        using var document = RedactionTargetTests.ComposeSample("Body text, nothing special.");

        var annotReference = document.Objects.AllocateNumber();
        var annot = new PdfDictionary();
        annot.Set(PdfName.Type, PdfName.Get("Annot"));
        annot.Set(PdfName.Subtype, PdfName.Get("Text"));
        annot.Set(PdfName.Get("Rect"), new PdfArray { PdfNumber.Get(500), PdfNumber.Get(700), PdfNumber.Get(520), PdfNumber.Get(720) });
        annot.Set(PdfName.Get("Contents"), PdfString.FromLiteral("survives"u8.ToArray()));
        document.Objects.RegisterNew(annotReference, annot);
        var page = document.Pages[0];
        page.Dictionary.Set(PdfName.Get("Annots"), new PdfArray { new PdfReference(annotReference) });
        document.Objects.MarkDirty(page.Reference);

        var result = RedactionEngine.Redact(document, [RedactionTarget.Region(0, new PdfRectangle(0, 0, 50, 50))], null);

        Assert.Equal(0, result.AnnotationAppearancesWiped);
        Assert.True(((PdfDictionary)document.Objects[annotReference]).ContainsKey(PdfName.Get("Contents")));
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-redact-{Guid.NewGuid():N}.pdf");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlumePdf.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }
}
