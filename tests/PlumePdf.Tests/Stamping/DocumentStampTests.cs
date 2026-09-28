using System.Text;
using PlumePdf.Elements;
using PlumePdf.Tests.Signing;
using Xunit;

namespace PlumePdf.Tests.Stamping;

/// <summary>
/// Opened-document stamping (Phase 6 — closing a v1.0 commitment deferred earlier). The suite
/// proves the three properties the design names:
/// the stamp is really painted (reopen + extraction finds it), the operation is purely
/// additive (every prior-revision byte survives <c>SaveIncremental</c> untouched — the
/// <see cref="Writing.SignatureSurvivalTests"/> pattern), and a signed source's signature stays
/// cryptographically valid after stamp + incremental save (the whole point of stamping being
/// additive where redaction is full-rewrite-only).
/// </summary>
public class DocumentStampTests
{
    [Fact]
    public void Stamp_SaveIncremental_Reopen_ExtractionFindsStampAndOriginalText()
    {
        var path = TempPdfPath();
        try
        {
            CreateSampleDocument(path, "Original body text.");

            using (var document = PdfDocument.Open(path))
            {
                document.Stamp(new Stamp { Text = "CONFIDENTIAL" });
                document.SaveIncremental(path);
            }

            using var reopened = PdfDocument.Open(path);
            var text = reopened.Pages[0].ExtractText().Text;
            Assert.Contains("CONFIDENTIAL", text, StringComparison.Ordinal);
            Assert.Contains("Original body text.", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stamp_SaveIncremental_PriorRevisionBytesUntouched()
    {
        var path = TempPdfPath();
        try
        {
            CreateSampleDocument(path, "Body.");
            var sourceBytes = File.ReadAllBytes(path);

            using (var document = PdfDocument.Open(path))
            {
                document.Stamp(new Stamp { Text = "DRAFT" });
                document.SaveIncremental(path);
            }

            var afterBytes = File.ReadAllBytes(path);
            Assert.True(afterBytes.Length > sourceBytes.Length, "expected the stamp to append a new revision.");
            Assert.Equal(sourceBytes, afterBytes[..sourceBytes.Length]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stamp_SignedSource_SaveIncremental_SignatureStaysCryptographicallyValid()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = TempPdfPath();
        var signedPath = TempPdfPath();
        var stampedPath = TempPdfPath();
        try
        {
            CreateSampleDocument(sourcePath, "Sample content for signing.");
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(signedPath, new PdfSignOptions { Certificate = ca.LeafRsa });
            }

            var signedBytes = File.ReadAllBytes(signedPath);

            using (var signed = PdfDocument.Open(signedPath))
            {
                signed.Stamp(new Stamp { Text = "RECEIVED" });
                signed.SaveIncremental(stampedPath);
            }

            // Additive: the signed revision's bytes — including the signature's whole signed
            // range — survive verbatim as the stamped file's prefix.
            var stampedBytes = File.ReadAllBytes(stampedPath);
            Assert.Equal(signedBytes, stampedBytes[..signedBytes.Length]);

            // The signature still verifies cryptographically; it (correctly) no longer covers
            // the WHOLE document, since the stamp appended a newer revision — exactly the
            // verdict shape SignVerifyRoundTripTests pins for a sign-then-append workflow.
            using var reopened = PdfDocument.Open(stampedPath);
            Assert.Single(reopened.Signatures);
            var result = reopened.Signatures[0].Verify();
            Assert.Equal(SignatureCryptographicStatus.Valid, result.CryptographicStatus);
            Assert.False(result.CoversWholeDocument);

            // And the stamp is really there.
            Assert.Contains("RECEIVED", reopened.Pages[0].ExtractText().Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
            if (File.Exists(stampedPath))
            {
                File.Delete(stampedPath);
            }
        }
    }

    [Fact]
    public void Stamp_FullRewriteSave_AlsoCarriesTheStamp()
    {
        var path = TempPdfPath();
        var outputPath = TempPdfPath();
        try
        {
            CreateSampleDocument(path, "Body.");

            using (var document = PdfDocument.Open(path))
            {
                document.Stamp(new Stamp { Text = "APPROVED", Position = StampPosition.BottomLeft });
                document.Save(outputPath);
            }

            using var reopened = PdfDocument.Open(outputPath);
            Assert.Contains("APPROVED", reopened.Pages[0].ExtractText().Text, StringComparison.Ordinal);
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
    public void Stamp_SubsetOfPages_LeavesOtherPagesUnstamped()
    {
        var path = TempPdfPath();
        try
        {
            CreateThreePageDocument(path);

            using (var document = PdfDocument.Open(path))
            {
                document.Stamp(new Stamp { Text = "COPY" }, pageIndexes: [1]);
                document.SaveIncremental(path);
            }

            using var reopened = PdfDocument.Open(path);
            Assert.DoesNotContain("COPY", reopened.Pages[0].ExtractText().Text, StringComparison.Ordinal);
            Assert.Contains("COPY", reopened.Pages[1].ExtractText().Text, StringComparison.Ordinal);
            Assert.DoesNotContain("COPY", reopened.Pages[2].ExtractText().Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stamp_RotatedPage_ReadsUprightAtTheVisualCorner()
    {
        var path = TempPdfPath();
        try
        {
            File.WriteAllBytes(path, BuildRotatedSinglePageDocument(rotate: 90));

            using (var document = PdfDocument.Open(path))
            {
                document.Stamp(new Stamp { Text = "ROTATED", Position = StampPosition.TopRight, FontSize = 12 });
                document.SaveIncremental(path);
            }

            using var reopened = PdfDocument.Open(path);
            var extracted = reopened.Pages[0].ExtractText();
            Assert.Contains("ROTATED", extracted.Text, StringComparison.Ordinal);

            // Extraction reports post-/Rotate visual coordinates: a 90-rotated 612x792 page
            // displays as 792x612, so a TopRight stamp's letters must sit near the top of the
            // 612-high visual space (y ≈ 612 - 36 - 12) and read left-to-right (upright).
            var stampLetters = extracted.Letters.Where(static l => "ROTATED".Contains(l.Value, StringComparison.Ordinal) && l.Y > 500).ToList();
            Assert.NotEmpty(stampLetters);
            Assert.All(stampLetters, l => Assert.InRange(l.Y, 540, 600));
            Assert.All(stampLetters, l => Assert.InRange(l.X, 600, 792));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stamp_UnbalancedOriginalContent_DanglingSave_StampStaysOnPage()
    {
        // Traced repro: original content ending `3 0 0 3 0 0 cm q` — a single suffix `Q` only
        // pops that dangling q, restoring the 3x-scaled state, and the stamp silently lands
        // off-page. The wrap must be sized to the original's actual net q/Q balance.
        var path = TempPdfPath();
        try
        {
            File.WriteAllBytes(path, BuildUnbalancedSinglePageDocument("BT /F0 12 Tf 100 100 Td (existing) Tj ET 3 0 0 3 0 0 cm q"));

            using (var document = PdfDocument.Open(path))
            {
                document.Stamp(new Stamp { Text = "ONPAGE", Position = StampPosition.TopRight, FontSize = 12 });
                document.SaveIncremental(path);
            }

            using var reopened = PdfDocument.Open(path);
            AssertStampLettersOnPage(reopened, "ONPAGE");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stamp_UnbalancedOriginalContent_StrayRestore_StampStaysOnPage()
    {
        // The other underflow direction: a stray leading `Q` pops the wrap's own protective
        // save, then a dangling transform pollutes the unprotected state — the prefix must
        // carry enough saves that a stray Q can never underflow past the initial state.
        var path = TempPdfPath();
        try
        {
            File.WriteAllBytes(path, BuildUnbalancedSinglePageDocument("Q 3 0 0 3 0 0 cm BT /F0 12 Tf 100 100 Td (existing) Tj ET"));

            using (var document = PdfDocument.Open(path))
            {
                document.Stamp(new Stamp { Text = "ONPAGE", Position = StampPosition.TopRight, FontSize = 12 });
                document.SaveIncremental(path);
            }

            using var reopened = PdfDocument.Open(path);
            AssertStampLettersOnPage(reopened, "ONPAGE");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Extraction reports page-space coordinates: on the 612x792 US Letter fixture a TopRight
    // 12pt stamp anchors at y = 792 - 36 - 12 = 744, x ending at 612 - 36. A stamp displaced
    // by the original's dangling 3x transform would report y ≈ 3x that, far outside the page.
    private static void AssertStampLettersOnPage(PdfDocument document, string stampText)
    {
        var extracted = document.Pages[0].ExtractText();
        Assert.Contains(stampText, extracted.Text, StringComparison.Ordinal);

        var stampLetters = extracted.Letters.Where(l => stampText.Contains(l.Value, StringComparison.Ordinal) && l.Y > 200).ToList();
        Assert.NotEmpty(stampLetters);
        Assert.All(stampLetters, l => Assert.InRange(l.Y, 700, 792));
        Assert.All(stampLetters, l => Assert.InRange(l.X, 400, 612));
    }

    private static byte[] BuildUnbalancedSinglePageDocument(string content)
    {
        var buffer = new List<byte>();
        buffer.AddRange(Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"));
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteObject(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F0 5 0 R >> >> >>");

        offsets[4] = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));

        WriteObject(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes("xref\n0 6\n0000000000 65535 f \n"));
        for (var n = 1; n <= 5; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF"));
        return [.. buffer];
    }

    [Fact]
    public void Stamp_ZeroPages_ThrowsPlume6073()
    {
        var path = TempPdfPath();
        try
        {
            CreateSampleDocument(path, "Body.");
            using var document = PdfDocument.Open(path);
            document.Pages.RemoveAt(0);

            var ex = Assert.Throws<PlumePdfException>(() => document.Stamp(new Stamp { Text = "X" }));
            Assert.Equal("PLUME6073", ex.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stamp_NonWinAnsiText_ThrowsPlume6072()
    {
        var path = TempPdfPath();
        try
        {
            CreateSampleDocument(path, "Body.");
            using var document = PdfDocument.Open(path);

            var ex = Assert.Throws<PlumePdfException>(() => document.Stamp(new Stamp { Text = "机密" }));
            Assert.Equal("PLUME6072", ex.Code);
            Assert.Contains("U+673A", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stamp_PageIndexOutOfRange_ThrowsArgumentOutOfRange()
    {
        var path = TempPdfPath();
        try
        {
            CreateSampleDocument(path, "Body.");
            using var document = PdfDocument.Open(path);

            Assert.Throws<ArgumentOutOfRangeException>(() => document.Stamp(new Stamp { Text = "X" }, pageIndexes: [1]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PdfStamp_PathVerb_StampsEveryPageThroughSaveIncremental()
    {
        var path = TempPdfPath();
        var outputPath = TempPdfPath();
        try
        {
            CreateThreePageDocument(path);
            var sourceBytes = File.ReadAllBytes(path);

            Pdf.Stamp(path, outputPath, "APPROVED");

            var outputBytes = File.ReadAllBytes(outputPath);
            Assert.Equal(sourceBytes, outputBytes[..sourceBytes.Length]); // additive — the verb goes through SaveIncremental

            using var reopened = PdfDocument.Open(outputPath);
            for (var i = 0; i < reopened.Pages.Count; i++)
            {
                Assert.Contains("APPROVED", reopened.Pages[i].ExtractText().Text, StringComparison.Ordinal);
            }
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
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text(body);
        });
        document.Save(path);
    }

    private static void CreateThreePageDocument(string path)
    {
        using var document = new Manuscript
        {
            Sections =
            [
                new Section { Body = new Column(new Text("Page one."), new PageBreak(), new Text("Page two."), new PageBreak(), new Text("Page three.")) },
            ],
        }.Render();
        document.Save(path);
    }

    /// <summary>
    /// A minimal, well-formed classic-xref single-page document with <c>/Rotate 90</c> — the
    /// hand-rolled ISO 32000-1 grammar approach <see cref="Writing.SignatureSurvivalTests"/>
    /// uses (Compose has no rotation surface, so a rotated fixture must be authored directly).
    /// US Letter MediaBox (612x792), displayed landscape (792x612).
    /// </summary>
    private static byte[] BuildRotatedSinglePageDocument(int rotate)
    {
        var buffer = new List<byte>();
        buffer.AddRange(Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"));
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteObject(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate {rotate} /Contents 4 0 R /Resources << /Font << /F0 5 0 R >> >> >>");

        const string content = "BT /F0 12 Tf 100 100 Td (existing) Tj ET";
        offsets[4] = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));

        WriteObject(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes("xref\n0 6\n0000000000 65535 f \n"));
        for (var n = 1; n <= 5; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF"));
        return [.. buffer];
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-stamp-{Guid.NewGuid():N}.pdf");

    [Fact]
    public void Stamp_ContentsAsIndirectReferenceToArray_SplicesStreamsInsteadOfNestingTheArray()
    {
        // A page shape found through the stamp writer: an existing /Contents that is an
        // indirect reference to an ARRAY must be spliced element-by-element into the new
        // [prefix ... suffix] array — keeping the reference whole nested an array inside
        // /Contents, which §7.7.3.3 does not allow (and pre-fix ComputeWrapBalance also read
        // such a page as empty, mis-balancing the wrap).
        var path = TempPdfPath();
        try
        {
            File.WriteAllBytes(path, WriterTestDocuments.BuildDocumentWithIndirectContentsArray());

            using (var document = PdfDocument.Open(path))
            {
                document.Stamp(new Stamp { Text = "RECEIVED" });
                document.SaveIncremental(path);
            }

            using var reopened = PdfDocument.Open(path);
            var contents = reopened.Pages[0].Dictionary[PdfName.Get("Contents")];
            var contentsArray = Assert.IsType<PdfArray>(contents is PdfReference reference ? reopened.Objects[reference.Target] : contents);
            Assert.All(contentsArray, item => Assert.IsType<PdfStream>(item is PdfReference itemReference ? reopened.Objects[itemReference.Target] : item));

            var text = reopened.Pages[0].ExtractText().Text;
            Assert.Contains("RECEIVED", text, StringComparison.Ordinal);
            Assert.Contains("Hello", text, StringComparison.Ordinal); // The original array streams survived the splice.
        }
        finally
        {
            File.Delete(path);
        }
    }
}
