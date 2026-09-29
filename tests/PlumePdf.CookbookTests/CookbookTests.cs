using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using PlumePdf.Documents;
using PlumePdf.Documents.PdfA;
using PlumePdf.Documents.Redaction;
using PlumePdf.Elements;
using VerifyXunit;
using Xunit;

namespace PlumePdf.CookbookTests;

/// <summary>
/// One snapshot-verified test per cookbook recipe. The code between
/// <c>begin-snippet</c>/<c>end-snippet</c> markers IS the recipe: MarkdownSnippets embeds it
/// verbatim into <c>docs/cookbook/*.md</c> (run <c>dotnet mdsnippets</c>; CI fails on drift),
/// and the <c>.verified.txt</c> snapshot next to this file is the recipe's expected output —
/// an agent can run the backing test and diff <c>.received</c> vs <c>.verified</c> as
/// objective proof its usage is correct (docs/agent-forward.md).
/// The <c>samples/</c> PDFs are the self-authored corpus fixtures, copied at build time.
/// </summary>
public partial class CookbookTests
{
    [Fact]
    public Task OpenAndInspect()
    {
        var report = new StringBuilder();

        // begin-snippet: open-and-inspect
        using var document = PdfDocument.Open("samples/classic-xref.pdf");

        report.AppendLine($"Pages: {document.Pages.Count}");
        report.AppendLine($"Recovery diagnostics: {document.Diagnostics.Count()}");

        // The escape hatch: the raw object graph is always reachable.
        var trailer = document.Objects.Trailer;
        report.AppendLine($"Trailer /Size: {trailer[PdfName.Get("Size")]}");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task MergeDocuments()
    {
        var report = new StringBuilder();

        // begin-snippet: merge
        using var merged = Pdf.Merge("samples/classic-xref.pdf", "samples/hybrid.pdf");
        merged.Save("output/merged.pdf");

        report.AppendLine($"Merged page count: {merged.Pages.Count}");
        // end-snippet

        using var reopened = PdfDocument.Open("output/merged.pdf");
        report.AppendLine($"Reopened page count: {reopened.Pages.Count}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task SplitDocument()
    {
        var report = new StringBuilder();

        // begin-snippet: split
        using var document = PdfDocument.Open("samples/three-pages.pdf");
        using var split = Pdf.Split(document);

        // One single-page document per source page; {n} in the pattern becomes 1, 2, …
        split.SaveAll("output/part-{n}.pdf");

        report.AppendLine($"Parts: {split.Documents.Count}");
        // end-snippet

        for (var i = 1; i <= split.Documents.Count; i++)
        {
            using var part = PdfDocument.Open($"output/part-{i}.pdf");
            report.AppendLine($"part-{i}.pdf pages: {part.Pages.Count}");
        }

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task ReorderAndRemovePages()
    {
        var report = new StringBuilder();

        // begin-snippet: reorder-pages
        using var document = PdfDocument.Open("samples/three-pages.pdf");

        document.Pages.Move(fromIndex: 0, toIndex: document.Pages.Count - 1); // first page to the back
        document.Pages.RemoveAt(1);                                           // drop what is now page 2

        document.Save("output/reordered.pdf");
        // end-snippet

        using var reopened = PdfDocument.Open("output/reordered.pdf");
        report.AppendLine($"Pages after reorder+remove: {reopened.Pages.Count}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task RemovePagesAndProveIt()
    {
        var report = new StringBuilder();
        using (var merged = Pdf.Merge("samples/simple-form.pdf", "samples/three-pages.pdf"))
        {
            merged.Save("output/form-then-pages.pdf");
        }

        // begin-snippet: remove-pages-prove-it
        using var document = PdfDocument.Open("output/form-then-pages.pdf");
        document.Pages.RemoveAt(0); // the page that carries the form's widgets
        document.Save("output/without-form-page.pdf");

        // Save reports what it left out; the open document itself is unchanged.
        foreach (var diagnostic in document.Diagnostics.Where(static d => d.Code == "PLUME5021"))
        {
            report.AppendLine(diagnostic.Message);
        }

        // Reopen the saved file to see the result.
        using var saved = PdfDocument.Open("output/without-form-page.pdf");
        report.AppendLine($"Pages: {saved.Pages.Count}, form fields: {saved.Form.Fields.Count}");
        // end-snippet

        report.AppendLine($"Open document still has {document.Form.Fields.Count} form field(s)");
        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task SaveIncremental()
    {
        var report = new StringBuilder();

        // begin-snippet: save-incremental
        using var document = PdfDocument.Open("samples/three-pages.pdf");
        document.Pages.RemoveAt(0);

        // Incremental save appends the change to a copy of the original bytes — the
        // original revision stays intact inside the file (required for signed documents).
        // Prefer it over Save unless you want a rewritten, garbage-collected file.
        document.SaveIncremental("output/incremental.pdf");
        // end-snippet

        var originalLength = new FileInfo("samples/three-pages.pdf").Length;
        var updatedLength = new FileInfo("output/incremental.pdf").Length;
        report.AppendLine($"Output begins with the original revision: {updatedLength > originalLength}");

        using var reopened = PdfDocument.Open("output/incremental.pdf");
        report.AppendLine($"Pages after incremental update: {reopened.Pages.Count}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task DeterministicOutput()
    {
        var report = new StringBuilder();

        // begin-snippet: deterministic-output
        var deterministic = PdfOptions.Default with { Deterministic = true };

        using (var document = PdfDocument.Open("samples/classic-xref.pdf"))
        {
            document.Save("output/run1.pdf", deterministic);
        }

        using (var document = PdfDocument.Open("samples/classic-xref.pdf"))
        {
            document.Save("output/run2.pdf", deterministic);
        }

        // Byte-identical run to run — the basis for snapshot-testing your own PDF output.
        var identical = File.ReadAllBytes("output/run1.pdf").SequenceEqual(File.ReadAllBytes("output/run2.pdf"));
        // end-snippet

        report.AppendLine($"Two deterministic saves byte-identical: {identical}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task OptimizeAndLinearize()
    {
        var report = new StringBuilder();

        // begin-snippet: optimize-linearize
        using var document = PdfDocument.Open("samples/three-pages.pdf");

        // Optimize: pack every compressible object into object streams and close with a
        // cross-reference stream (ISO 32000-1 §7.5.7/§7.5.8) — a smaller file. PDF 1.5+
        // only: combining it with PdfVersion "1.4" (or PdfAConformance.A1b, which forces
        // that header) is a coded refusal, PLUME5017.
        document.Save("output/optimized.pdf", PdfOptions.Default with { Optimize = true });

        // Linearize: "fast web view" — first-page objects first plus hint tables
        // (ISO 32000-1 Annex F), so a byte-range-capable viewer can show page one before
        // the download finishes. Uses classic cross-reference tables; Optimize + Linearize
        // together is a coded refusal (PLUME5018) — pick one per output.
        document.Save("output/linearized.pdf", PdfOptions.Default with { Linearize = true });
        // end-snippet

        var optimizedBytes = File.ReadAllBytes("output/optimized.pdf");
        var linearizedBytes = File.ReadAllBytes("output/linearized.pdf");
        report.AppendLine($"Optimized output uses object streams: {Encoding.Latin1.GetString(optimizedBytes).Contains("/ObjStm", StringComparison.Ordinal)}");
        report.AppendLine($"Linearization dictionary inside the first kilobyte: {Encoding.Latin1.GetString(linearizedBytes, 0, 1024).Contains("/Linearized", StringComparison.Ordinal)}");

        using (var optimized = PdfDocument.Open("output/optimized.pdf"))
        using (var linearized = PdfDocument.Open("output/linearized.pdf"))
        {
            report.AppendLine($"Optimized reopens with pages: {optimized.Pages.Count}");
            report.AppendLine($"Linearized reopens with pages: {linearized.Pages.Count}");
        }

        // begin-snippet: delinearization-diagnostic
        // Linearization is a Save-time layout, destroyed by construction the moment an
        // incremental update appends onto the file. A later SaveIncremental on this document
        // still succeeds — the output is valid, merely no longer linearized — and records a
        // PLUME5019 diagnostic so the de-linearization is a recorded fact, not a surprise.
        document.SaveIncremental("output/updated.pdf");
        var delinearized = document.Diagnostics.Any(d => d.Code == "PLUME5019");

        // Under PdfOptions.Strict the same call refuses instead:
        PlumePdfException? refusal = null;
        try
        {
            document.SaveIncremental("output/updated.pdf", PdfOptions.Default with { Strict = true });
        }
        catch (PlumePdfException ex)
        {
            refusal = ex; // PLUME5019 — re-run Save with Linearize for a linearized result
        }
        // end-snippet

        report.AppendLine($"SaveIncremental after a linearized save records PLUME5019: {delinearized}");
        report.AppendLine($"Strict refusal code: {refusal?.Code}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task StampDocument()
    {
        var report = new StringBuilder();

        // begin-snippet: stamp-document
        using (var document = PdfDocument.Open("samples/three-pages.pdf"))
        {
            // Stamping an opened document is purely additive — it appends a per-page stamp
            // content stream and never rewrites existing bytes — so it works through
            // SaveIncremental, the signature-preserving path: prior revisions, including any
            // existing signature's signed bytes, stay intact (unlike redaction, which must
            // route through Save's full rewrite).
            document.Stamp(new Stamp { Text = "CONFIDENTIAL", Position = StampPosition.TopRight });
            document.SaveIncremental("output/stamped.pdf");
        }

        using (var stamped = PdfDocument.Open("output/stamped.pdf"))
        {
            report.AppendLine($"Pages: {stamped.Pages.Count}");
            report.AppendLine($"Page 1 carries the stamp: {stamped.Pages[0].ExtractText().Text.Contains("CONFIDENTIAL", StringComparison.Ordinal)}");
        }
        // end-snippet

        // begin-snippet: stamp-document-verb
        // The one-line verb: open, stamp every page, SaveIncremental to the output path.
        Pdf.Stamp("samples/three-pages.pdf", "output/stamped-verb.pdf", "APPROVED");
        // end-snippet

        using (var stampedByVerb = PdfDocument.Open("output/stamped-verb.pdf"))
        {
            report.AppendLine($"Verb-stamped page 3 carries the stamp: {stampedByVerb.Pages[2].ExtractText().Text.Contains("APPROVED", StringComparison.Ordinal)}");
        }

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task Redact()
    {
        var report = new StringBuilder();

        // A source with something worth redacting.
        using (var source = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("Employee: Jane Doe, SSN 123-45-6789, cleared for review.");
        }))
        {
            source.Save("output/contract.pdf");
        }

        // begin-snippet: redact
        using var document = PdfDocument.Open("output/contract.pdf");

        var result = document.Redact(
        [
            RedactionTarget.Text("Jane Doe"),
            RedactionTarget.Pattern(new Regex(@"\d{3}-\d{2}-\d{4}")), // SSN-shaped numbers
        ]);

        if (result.HadNoMatches)
        {
            throw new InvalidOperationException("Expected at least one redaction match — nothing was redacted.");
        }

        report.AppendLine($"{result.MatchCount} match(es); {result.TextOperatorsRemoved} operator(s) removed; " +
            $"{result.ImagesRemoved} image(s) removed.");

        document.Save("output/contract-redacted.pdf"); // Save only — SaveIncremental refuses (PLUME5016).
        // end-snippet

        // begin-snippet: redact-verify
        using var reopened = PdfDocument.Open("output/contract-redacted.pdf");
        var text = reopened.Pages[0].ExtractText().Text;
        report.AppendLine($"'Jane Doe' still extractable: {text.Contains("Jane Doe", StringComparison.Ordinal)}");
        report.AppendLine($"SSN still extractable: {text.Contains("123-45-6789", StringComparison.Ordinal)}");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task CreateTaggedPdf()
    {
        var report = new StringBuilder();
        var logoPixels = new byte[4 * 4 * 3]; // stand-in logo bitmap

        // begin-snippet: create-tagged-pdf
        var manuscript = new Manuscript
        {
            Language = "en-US", // the opt-in: setting Language is what turns tagging on at all
            Title = "Quarterly Report",
            Sections =
            [
                new Section
                {
                    Body = new Column(
                        new Text("Quarterly Report") { HeadingLevel = 1, Bold = true, FontSize = 24 },
                        new Text("Revenue grew 12% quarter over quarter."),
                        new Image(logoPixels, pixelWidth: 4, pixelHeight: 4) { AltText = "Acme Corp logo" })
                    {
                        Spacing = 12,
                    },
                },
            ],
        };

        using (var document = manuscript.Render())
        {
            document.Save("output/tagged-report.pdf");
        }

        // Read the structure tree back through the same model extraction's reading order uses.
        using var reopened = PdfDocument.Open("output/tagged-report.pdf");
        var structure = PdfStructureInfo.For(reopened);
        report.AppendLine($"Tagged: {structure.IsTagged}, language: {structure.Language}");
        if (structure.Root is PdfStructureElement root)
        {
            report.AppendLine($"Structure: {root.Role} → {string.Join(", ", root.Children.OfType<PdfStructureElement>().Select(static c => c.Role))}");
        }
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task CreateInvoice()
    {
        var report = new StringBuilder();

        // begin-snippet: create-invoice
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Header().Text("INVOICE #1042").Bold().FontSize(20);
            page.Content().Column(col =>
            {
                col.Spacing(12);
                col.Item().Text("Bill to: Acme Corp");
                col.Item().Text("Total: $500.00").Bold();
            });
            page.Footer().AlignCenter().Text(text =>
            {
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPageCount();
            });
        });

        document.Save("output/invoice.pdf");
        report.AppendLine($"Pages: {document.Pages.Count}");
        // end-snippet

        using var reopened = PdfDocument.Open("output/invoice.pdf");
        report.AppendLine($"Reopened pages: {reopened.Pages.Count}");
        report.AppendLine($"Reopened diagnostics: {reopened.Diagnostics.Count()}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task CreateArabicDocument()
    {
        // Self-skip (loudly) when the Arabic OFL font fixture hasn't been fetched: this recipe
        // needs a font whose OpenType Layout tables actually implement Arabic shaping (the
        // "arab" GSUB joining-form substitutions plus mark-attachment GPOS features) — neither
        // Standard-14 Helvetica nor the Latin-only NotoSans fixture the PDF/A recipe uses have
        // that. The fonts/ directory is copied from corpora/fonts at build (see the csproj).
        if (!File.Exists("fonts/NotoNaskhArabic-Regular.ttf"))
        {
            Console.WriteLine("SKIPPED (Arabic font corpus not fetched — run scripts/fetch-corpora.sh, then rebuild so fonts/ is repopulated)");
            return Task.CompletedTask;
        }

        var report = new StringBuilder();

        // begin-snippet: create-arabic-document
        // An Arabic-capable font: PlumePDF checks this at shaping time and refuses (PLUME8025)
        // naming the missing capability rather than silently drawing unjoined isolated forms —
        // see docs/errors/PLUME8025.md for exactly which OpenType features are required.
        var arabicFont = PdfFont.FromFile("fonts/NotoNaskhArabic-Regular.ttf");

        var manuscript = new Manuscript
        {
            Sections =
            [
                new Section
                {
                    Body = new Column(
                        // Direction.Auto (the default) applies UAX#9's first-strong heuristic
                        // to pick the paragraph's base direction; set it explicitly when you
                        // already know it. Align = Start resolves to the physical right edge
                        // for a right-to-left paragraph — Left/Right stay physical forever.
                        // "مرحباً بكم" ("Welcome") stays pure Arabic deliberately: NotoNaskhArabic-Regular
                        // is an Arabic-only face (no Latin glyphs at all), so mixing in a Latin brand
                        // name here would need a second Text/font pairing, not this one font — the
                        // PLUME8009 fail-fast coverage policy applies to embedded fonts exactly like
                        // any other — that policy still holds post-shaping; shaping doesn't waive it.
                        new Text("مرحباً بكم") { Font = arabicFont, FontSize = 16, Direction = TextDirection.RightToLeft, Align = HorizontalAlign.Start },
                        // Mixed Arabic + digits on one line: UAX#9 bidi keeps "500" reading
                        // left-to-right inside the right-to-left line, exactly like a real
                        // invoice total — the headline reason bidi (not just glyph shaping) is
                        // in scope for this recipe to work at all.
                        new Text("الإجمالي: 500 دولار") { Font = arabicFont, Direction = TextDirection.RightToLeft, Align = HorizontalAlign.Start })
                    {
                        Spacing = 12,
                    },
                },
            ],
        };

        using (var document = manuscript.Render())
        {
            document.Save("output/arabic-welcome.pdf");
        }
        // end-snippet

        using var reopened = PdfDocument.Open("output/arabic-welcome.pdf");
        report.AppendLine($"Pages: {reopened.Pages.Count}");
        report.AppendLine($"Diagnostics: {reopened.Diagnostics.Count()}");
        // "مرحباً" carries a fatha + tanwin (ً) — a GPOS mark-attachment glyph this recipe's
        // headline claim ("harakat position correctly") depends on actually being painted at
        // its own offset, not the pen position. Ts (text rise) is how ManuscriptRenderer
        // reproduces a mark's GPOS YOffset — its presence is the objective, content-stream-level
        // proof this recipe's claim holds, not just an assertion that rendering didn't throw.
        report.AppendLine($"Emits per-glyph GPOS mark placement (a Ts operator is in the content stream): {ContentStreamContainsTextRiseOperator(reopened)}");

        // begin-snippet: shaping-budget-cap
        // A cumulative shaping work budget guards against a hostile or pathological
        // caller-supplied font encoding a runaway contextual/chaining lookup graph. It applies
        // to every shaped run, not just complex scripts — lower it when embedding a font you
        // do not fully trust; exceeding it refuses with PLUME8024 rather than hanging.
        var tightBudget = PdfOptions.Default with { MaxShapingLookupApplications = 10_000 };
        using var tightlyBudgeted = manuscript.Render(tightBudget);
        // end-snippet

        report.AppendLine($"Renders under a tighter shaping budget too: {tightlyBudgeted.Pages.Count == reopened.Pages.Count}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task ExtractText()
    {
        var report = new StringBuilder();

        using (var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("Bill to: Acme Corp");
        }))
        {
            document.Save("output/extract-text-source.pdf");
        }

        // begin-snippet: extract-text
        using var source = PdfDocument.Open("output/extract-text-source.pdf");

        // The "quick door": Pdf.ExtractText(path) flattens every page's text into one string.
        var wholeDocumentText = Pdf.ExtractText("output/extract-text-source.pdf");

        // The "rich door": PdfPage.ExtractText() gives positions, words/lines, and the raw
        // Letters escape hatch — call it per page so a context-budgeted agent can pull only
        // the pages it needs instead of the whole document at once.
        ExtractedText page1 = source.Pages[0].ExtractText();

        report.AppendLine($"Whole-document text: {wholeDocumentText}");
        report.AppendLine($"Page 1 text: {page1.Text}");
        report.AppendLine($"Page 1 words: {page1.Words.Count}");
        report.AppendLine($"First letter: '{page1.Letters[0].Value}' at ({page1.Letters[0].X:F1}, {page1.Letters[0].Y:F1})");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task ExtractText_ImageOnlyPage()
    {
        var report = new StringBuilder();

        byte[] pixels = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]; // 2x2 RGB, no text at all
        using (var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Image(new Image(pixels, pixelWidth: 2, pixelHeight: 2));
        }))
        {
            document.Save("output/extract-text-scanned-source.pdf");
        }

        // begin-snippet: extract-text-scanned
        // A scanned page (or any image-only page, with no text-showing operators at all) has
        // no glyphs for PlumePDF to decode. PlumePDF does not perform OCR (out of scope) — an
        // empty Text/Letters with zero Diagnostics is the correct, SUCCESSFUL answer for this
        // kind of page, not a failure to detect or retry.
        using var source = PdfDocument.Open("output/extract-text-scanned-source.pdf");
        ExtractedText page1 = source.Pages[0].ExtractText();

        var emptyResultIsSuccess = page1.Text.Length == 0 && page1.Letters.Count == 0 && page1.Diagnostics.Count == 0;
        // end-snippet

        report.AppendLine($"Image-only page: empty result, zero diagnostics (success, not an error): {emptyResultIsSuccess}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task ExtractImages()
    {
        var report = new StringBuilder();

        byte[] pixels = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]; // 2x2 RGB
        using (var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Image(new Image(pixels, pixelWidth: 2, pixelHeight: 2));
        }))
        {
            document.Save("output/extract-images-source.pdf");
        }

        // begin-snippet: extract-images
        using var source = PdfDocument.Open("output/extract-images-source.pdf");

        // ExtractImages() discards this call's diagnostics (unsupported filters, skipped
        // inline images); ExtractImagesWithDiagnostics() returns them alongside the images.
        var (images, diagnostics) = source.Pages[0].ExtractImagesWithDiagnostics();

        foreach (var image in images)
        {
            var kind = image.IsJpeg ? "JPEG (pass-through)" : image.IsRawEncoded ? "still-encoded (unsupported filter)" : "decoded samples";
            report.AppendLine($"{image.Width}x{image.Height}, {image.Data.Length} bytes, {kind}");
        }

        report.AppendLine($"Diagnostics: {diagnostics.Count}");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task ExtractImages_UnsupportedFilter()
    {
        var report = new StringBuilder();

        var sourcePath = "output/extract-images-unsupported-filter-source.pdf";
        File.WriteAllBytes(sourcePath, BuildUnregisteredFilterImageDocument());

        // begin-snippet: extract-images-unsupported-filter
        // PlumePDF ships a decoder for every filter ISO 32000-1 §7.4 defines, JPEG 2000
        // included. By default, an image using a filter
        // name PlumePDF has never registered a decoder for degrades to its still-encoded
        // bytes plus a PLUME6023 diagnostic, rather than failing the whole page.
        using (var document = PdfDocument.Open(sourcePath))
        {
            var image = document.Pages[0].ExtractImages()[0];
            report.AppendLine($"No registered filter: IsRawEncoded={image.IsRawEncoded}");
        }

        // Register your own IPdfFilter under the filter's name (PdfOptions.Filters, the
        // extension seam) to decode it for real instead. Start from a fresh copy of the
        // built-in registry so FlateDecode and every other shipped filter stay registered
        // alongside yours -- `new PdfFilterRegistry()` is EMPTY, and a document whose
        // cross-reference or object streams are Flate-compressed would not even open.
        var registry = PdfFilterRegistry.CreateDefault();
        registry.Register("PlumeVendorTestDecode", new FixedOutputFilter([0x01, 0x02, 0x03]));
        var withVendorFilter = PdfOptions.Default with { Filters = registry };

        using (var document = PdfDocument.Open(sourcePath, withVendorFilter))
        {
            var image = document.Pages[0].ExtractImages()[0];
            report.AppendLine($"With a registered filter: IsRawEncoded={image.IsRawEncoded}, bytes={image.Data.Length}");
        }
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task ReadMetadata()
    {
        var report = new StringBuilder();

        using (var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("Report body");
        }))
        {
            document.Save("output/read-metadata-source.pdf");
        }

        // begin-snippet: read-metadata
        using var source = PdfDocument.Open("output/read-metadata-source.pdf");

        PdfDocumentInfo info = source.GetInfo();
        var xmpBytes = source.GetXmpMetadataBytes(); // raw, filter-decoded XMP packet, or null

        report.AppendLine($"Title: {info.Title ?? "(none)"}");
        report.AppendLine($"Producer: {info.Producer ?? "(none)"}");
        report.AppendLine($"XMP present: {xmpBytes is not null}");
        report.AppendLine($"Permissions: {source.Permissions}");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    // A minimal single-page, single-image raw PDF (ISO 32000-1 grammar directly, an approach
    // the clean-room policy in AGENTS.md allows; never PlumePdf's own writer, which has no way to emit an unsupported
    // filter name) whose one image XObject declares /Filter /PlumeVendorTestDecode over a
    // garbage payload — PlumePDF never has to actually decode it correctly for this recipe;
    // the point is exercising the "unsupported filter" degrade path and the custom-filter
    // override seam. /JBIG2Decode was this fixture's filter through Phase 6, then /JPXDecode
    // through Phase 7 while CCITT/JBIG2/DCT were becoming
    // PlumePDF's own registered built-in codecs and JPX was the one remaining community-seam
    // gap; JPX was later registered in-house too, so this fixture now uses a
    // vendor filter name PlumePDF will never recognize, to keep demonstrating the degrade path
    // and override seam honestly.
    /// <summary>Whether <paramref name="document"/>'s first page's content stream contains a <c>Ts</c> (text rise) operator — the objective, decoded-bytes proof that GPOS mark placement (a nonzero <c>ShapedGlyph.YOffset</c>) actually reached the painted output, backing the Arabic recipe's "harakat position correctly" claim.</summary>
    private static bool ContentStreamContainsTextRiseOperator(PdfDocument document)
    {
        var page = document.Pages[0];
        if (!page.Dictionary.TryGetValue(PdfName.Get("Contents"), out var contentsValue)
            || contentsValue is not PdfReference reference
            || document.Objects[reference.Target] is not PdfStream stream)
        {
            return false;
        }

        var bytes = stream.GetDecodedBytes(PdfFilterRegistry.Default, PdfOptions.Default);
        var text = Encoding.Latin1.GetString(bytes);
        return Regex.IsMatch(text, @"(?m)^-?[\d.]+ Ts$");
    }

    private static byte[] BuildUnregisteredFilterImageDocument()
    {
        const int catalogNum = 1;
        const int pagesNum = 2;
        const int pageNum = 3;
        const int imageNum = 4;
        const int contentNum = 5;
        const int totalObjects = 6;

        var buffer = new List<byte>();
        buffer.AddRange(Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"));
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R >>");
        WriteObject(pagesNum, $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 >>");
        WriteObject(
            pageNum,
            $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 100 100] " +
            $"/Resources << /XObject << /Img {imageNum} 0 R >> >> /Contents {contentNum} 0 R >>");

        byte[] garbagePayload = [0xDE, 0xAD, 0xBE, 0xEF];
        offsets[imageNum] = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"{imageNum} 0 obj\n<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 1 " +
            $"/ColorSpace /DeviceGray /Filter /PlumeVendorTestDecode /Length {garbagePayload.Length} >>\nstream\n"));
        buffer.AddRange(garbagePayload);
        buffer.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));

        const string content = "q 100 0 0 100 0 0 cm /Img Do Q";
        offsets[contentNum] = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"{contentNum} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root {catalogNum} 0 R >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    private sealed class FixedOutputFilter(byte[] output) : IPdfFilter
    {
        public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) => output;
    }

    [Fact]
    public Task HandleDamagedPdf()
    {
        var report = new StringBuilder();

        // begin-snippet: handle-damaged-pdf
        // broken-xref.pdf declares cross-reference offsets that are wrong; lenient reading
        // (the default) repairs what it can and records HOW on document.Diagnostics.
        using var document = PdfDocument.Open("samples/broken-xref.pdf");

        foreach (var diagnostic in document.Diagnostics)
        {
            report.AppendLine($"{diagnostic.Code} [{diagnostic.Severity}]");
        }

        report.AppendLine($"Recovered pages: {document.Pages.Count}");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task CreatePdfA()
    {
        // Self-skip (loudly) when the OFL font fixtures haven't been fetched: PDF/A requires
        // every font embedded, so this recipe cannot run on the Standard-14 fonts alone. The
        // fonts/ directory is copied from corpora/fonts at build (see the csproj).
        if (!File.Exists("fonts/NotoSans-Regular.ttf"))
        {
            Console.WriteLine("SKIPPED (font corpus not fetched — run scripts/fetch-corpora.sh, then rebuild so fonts/ is repopulated)");
            return Task.CompletedTask;
        }

        var report = new StringBuilder();

        // begin-snippet: create-pdfa
        // PDF/A requires every font embedded — load a TrueType/OpenType file instead of a
        // Standard-14 font (PdfFont.Helvetica and friends embed nothing by design).
        var font = PdfFont.FromFile("fonts/NotoSans-Regular.ttf");

        var manuscript = new Manuscript
        {
            Title = "Invoice #1042",
            Sections =
            [
                new Section
                {
                    Body = new Column(
                        new Text("INVOICE #1042") { Font = font, Bold = true, FontSize = 20 },
                        new Text("Bill to: Acme Corp") { Font = font })
                    {
                        Spacing = 12,
                    },
                },
            ],
        };

        // One switch does the rest: the header version ceiling, the XMP pdfaid:part/
        // pdfaid:conformance identification (agreeing with /Info), and a GTS_PDFA1 output
        // intent carrying the bundled CC0 sRGB profile.
        var options = PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b };

        using (var document = manuscript.Render(options))
        {
            document.Save("output/invoice-a2b.pdf");
        }

        // Prove it with the in-process self-check (the veraPDF CLI is the full oracle).
        using var reopened = PdfDocument.Open("output/invoice-a2b.pdf");
        var result = PdfAValidator.Validate(reopened);
        // end-snippet

        report.AppendLine($"Declared: PDF/A-{result.DeclaredPart}{result.DeclaredConformance}");
        report.AppendLine($"Conformant (self-check): {result.IsConformant}");
        report.AppendLine($"Title round-trips: {reopened.GetInfo().Title}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task CreatePdfA_Standard14Refusal()
    {
        var report = new StringBuilder();

        // begin-snippet: create-pdfa-standard14-refusal
        // A Standard-14 font under a PDF/A conformance is a coded refusal naming every
        // offending font and the fix path — never a silent substitution.
        var manuscript = new Manuscript
        {
            Sections = [new Section { Body = new Text("Bill to: Acme Corp") }], // default font: Helvetica
        };

        try
        {
            manuscript.Render(PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b });
        }
        catch (PlumePdfException ex)
        {
            report.AppendLine($"{ex.Code}: {ex.Message}");
        }
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task ValidatePdfA()
    {
        var report = new StringBuilder();

        // An image-only PDF/A-2b candidate (no fonts needed, so this recipe runs everywhere).
        byte[] pixels = [200, 200, 200, 200, 200, 200, 200, 200, 200, 200, 200, 200]; // 2x2 gray RGB
        using (var created = new Manuscript
        {
            Title = "PDF/A candidate",
            Sections = [new Section { Body = new Image(pixels, pixelWidth: 2, pixelHeight: 2) }],
        }.Render(PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b }))
        {
            created.Save("output/pdfa-candidate.pdf");
        }

        // begin-snippet: validate-pdfa
        using var candidate = PdfDocument.Open("output/pdfa-candidate.pdf");
        var result = PdfAValidator.Validate(candidate);

        report.AppendLine($"Declared: PDF/A-{result.DeclaredPart}{result.DeclaredConformance}");
        report.AppendLine($"Conformant (per this self-check): {result.IsConformant}");

        foreach (var failure in result.Failures)
        {
            report.AppendLine($"FAIL [{failure.RuleId}] {failure.Message}");
        }

        // NotChecked rules are honest coverage gaps — never treated as passing. Use the
        // veraPDF CLI for a full-coverage conformance proof.
        foreach (var notChecked in result.NotCheckedRules)
        {
            report.AppendLine($"NOT CHECKED [{notChecked.RuleId}]");
        }
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task FillForm()
    {
        var report = new StringBuilder();
        File.Copy("samples/simple-form.pdf", "output/form-to-fill.pdf", overwrite: true);

        // begin-snippet: fill-form
        Pdf.FillForm("output/form-to-fill.pdf", new Dictionary<string, string>
        {
            ["FullName"] = "Jane Q. Public",
            ["Subscribe"] = "Yes", // a checkbox's on-state, discovered from its /AP (never assumed)
        });

        using var filled = PdfDocument.Open("output/form-to-fill.pdf");
        report.AppendLine($"FullName = {filled.Form.Fields["FullName"].Value}");
        report.AppendLine($"Subscribe = {filled.Form.Fields["Subscribe"].Value}");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task FlattenForm()
    {
        var report = new StringBuilder();
        File.Copy("samples/simple-form.pdf", "output/form-to-flatten.pdf", overwrite: true);

        // begin-snippet: flatten-form
        // Fill first (fill regenerates each widget's appearance), then flatten: the fields
        // become ordinary page content and the interactive form is gone.
        Pdf.FillForm("output/form-to-flatten.pdf", new Dictionary<string, string> { ["FullName"] = "Baked In" });
        Pdf.FlattenForm("output/form-to-flatten.pdf", "output/flattened.pdf");

        using var flattened = PdfDocument.Open("output/flattened.pdf");
        report.AppendLine($"Fields after flatten: {flattened.Form.Fields.Count}");
        report.AppendLine($"Pages: {flattened.Pages.Count}");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task SignDocument()
    {
        var report = new StringBuilder();
        using (var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("Contract terms go here.");
        }))
        {
            document.Save("output/contract.pdf");
        }

        // A real caller loads a certificate they already have (a PFX file, a platform key
        // store, an HSM); this test generates a throwaway self-signed one purely so the recipe
        // runs offline and deterministically.
        File.WriteAllBytes("output/signing-cert.pfx", CreateTestCertificatePfx("Cookbook Sign Test"));

        // begin-snippet: sign-document
        using var certificate = LoadPfx(File.ReadAllBytes("output/signing-cert.pfx"));

        Pdf.Sign("output/contract.pdf", "output/contract-signed.pdf", new PdfSignOptions
        {
            Certificate = certificate,
            Reason = "I approve this document",
        });

        using var signed = PdfDocument.Open("output/contract-signed.pdf");
        report.AppendLine($"Signatures: {signed.Signatures.Count}");
        report.AppendLine($"Field name: {signed.Signatures[0].FieldName}");
        report.AppendLine($"Reason: {signed.Signatures[0].Reason}");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task VerifySignature()
    {
        var report = new StringBuilder();
        using (var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("Agreement terms go here.");
        }))
        {
            document.Save("output/agreement.pdf");
        }

        using (var certificate = LoadPfx(CreateTestCertificatePfx("Cookbook Verify Test")))
        {
            Pdf.Sign("output/agreement.pdf", "output/agreement-signed.pdf", new PdfSignOptions { Certificate = certificate });
        }

        // begin-snippet: verify-signatures
        using var document2 = PdfDocument.Open("output/agreement-signed.pdf");
        var result = document2.Signatures[0].Verify();

        report.AppendLine($"CryptographicStatus: {result.CryptographicStatus}");
        report.AppendLine($"CoversWholeDocument: {result.CoversWholeDocument}");
        report.AppendLine($"IsValid: {result.IsValid}");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public async Task TimestampAndLtv()
    {
        var report = new StringBuilder();
        using (var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("Long-term-valid agreement terms go here.");
        }))
        {
            document.Save("output/lta-agreement.pdf");
        }

        using var certificate = LoadPfx(CreateTestCertificatePfx("Cookbook LTV Test"));

        // begin-snippet: timestamp-and-ltv
        // IO.Http.HttpTimestampAuthority / HttpRevocationFetcher are the in-box HTTP-backed
        // choice for a real TSA/OCSP responder; this recipe implements the same
        // ITimestampAuthority/IRevocationFetcher seams against minimal in-process stand-ins so
        // it runs offline and deterministically (mirroring PlumePdf.Tests.Signing.FakeTimestampAuthority).
        await Pdf.SignAsync("output/lta-agreement.pdf", "output/lta-agreement-signed.pdf", new PdfSignOptions
        {
            Certificate = certificate,
            Level = PdfSignatureLevel.T,
            TimestampAuthority = new CookbookTimestampAuthority(),
        });

        using (var withTimestamp = PdfDocument.Open("output/lta-agreement-signed.pdf"))
        {
            await withTimestamp.Signatures.AddLtvAsync("output/lta-agreement-ltv.pdf", new CookbookRevocationFetcher());
        }

        using var withLtv = PdfDocument.Open("output/lta-agreement-ltv.pdf");
        var result = withLtv.Signatures[0].Verify();
        var catalog = withLtv.Objects.Trailer[PdfName.Root] is PdfReference rootRef ? withLtv.Objects[rootRef.Target] as PdfDictionary : null;
        report.AppendLine($"HasTimestamp: {result.HasTimestamp}");
        report.AppendLine($"Has /DSS (LTV material embedded): {catalog?.ContainsKey(PdfName.DSS) == true}");
        // end-snippet

        await Verifier.Verify(report.ToString());
    }

    // begin-snippet: load-pfx
    // .NET 9+ obsoletes the X509Certificate2 byte-array constructors (SYSLIB0057); net8.0 has no
    // X509CertificateLoader.
    private static X509Certificate2 LoadPfx(byte[] pfx) =>
#if NET9_0_OR_GREATER
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
#else
        new(pfx, (string?)null, X509KeyStorageFlags.Exportable);
#endif
    // end-snippet

    private static byte[] CreateTestCertificatePfx(string commonName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return ephemeral.Export(X509ContentType.Pfx);
    }

    /// <summary>
    /// A minimal in-process <see cref="ITimestampAuthority"/> for the timestamp-and-ltv
    /// cookbook recipe — see that snippet's own remark. Mirrors
    /// <c>PlumePdf.Tests.Signing.FakeTimestampAuthority</c>'s shape but is self-contained here
    /// (this project has no InternalsVisibleTo access to PlumePdf's own ESS-attribute helper).
    /// </summary>
    private sealed class CookbookTimestampAuthority : ITimestampAuthority
    {
        public Task<byte[]> GetTimestampAsync(ReadOnlyMemory<byte> messageImprint, HashAlgorithmName hashAlgorithm, CancellationToken cancellationToken = default)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Cookbook Test TSA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], critical: true)); // RFC 3161 §2.3: id-kp-timeStamping.
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
            using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            using var tsaCertificate = LoadPfx(ephemeral.Export(X509ContentType.Pfx));

            var tokenInfo = new Rfc3161TimestampTokenInfo(
                new Oid("1.2.3.4.5.6"),
                new Oid("2.16.840.1.101.3.4.2.1"),
                messageImprint,
                new byte[] { 1 },
                DateTimeOffset.UtcNow,
                null,
                false,
                null,
                null,
                null);

            var contentInfo = new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), tokenInfo.Encode());
            var signedCms = new SignedCms(contentInfo);
            var cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, tsaCertificate) { IncludeOption = X509IncludeOption.EndCertOnly };
            // id-aa-signingCertificateV2, hashAlgorithm omitted (RFC 5035 DEFAULT id-sha256).
            cmsSigner.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", EncodeEssCertIdV2(tsaCertificate)));
            signedCms.ComputeSignature(cmsSigner, silent: true);

            return Task.FromResult(signedCms.Encode());
        }

        private static byte[] EncodeEssCertIdV2(X509Certificate2 certificate)
        {
            var certHash = SHA256.HashData(certificate.RawData);
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence()) // SigningCertificateV2
            using (writer.PushSequence()) // certs SEQUENCE OF ESSCertIDv2
            using (writer.PushSequence()) // ESSCertIDv2
            {
                writer.WriteOctetString(certHash);
            }

            return writer.Encode();
        }
    }

    /// <summary>A minimal in-process <see cref="IRevocationFetcher"/> for the timestamp-and-ltv cookbook recipe — no OCSP/CRL locations to actually fetch for a self-signed test certificate.</summary>
    private sealed class CookbookRevocationFetcher : IRevocationFetcher
    {
        public Task<byte[]?> FetchOcspAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<byte[]?> FetchCrlAsync(X509Certificate2 certificate, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);
    }
}
