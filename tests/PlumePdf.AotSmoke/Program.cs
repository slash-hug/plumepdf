// NativeAOT publish smoke test.
//
// Not a unit test: this is a tiny real consumer app, published with
// `dotnet publish -p:PublishAot=true` (see .github/workflows/ci.yml's
// `aot-smoke` job), that exercises Phase 1's three headline verbs — Open,
// Merge, Save — against a real fixture file, plus Phase 2's creation path
// (Manuscript/Compose/Fonts/Layout): a Standard-14 Text
// element and, when the caller supplies a TrueType/OpenType font file, an
// embedded/subsetted PdfFont.FromFile text element too. If PlumePdf ever
// grows a reflection-based code path, this is what catches it: a library's
// own `dotnet build` cannot see downstream AOT/trimming reachability the way
// an actual native-compiled consumer can.
//
// Written against the Phase 1 public API contract — compiles once the
// PdfDocument/Pdf surface merges. The creation-path call (below) is written
// against Phase 2's Manuscript/PdfFont surface.
//
// Phase 4 forms path: appearance
// generation landed on main, so the FillForm + Flatten
// exercise below runs the real appearance-regeneration path — not the /NeedAppearances
// escape hatch — against the committed simple-form.pdf fixture, and fails loudly if the
// filled value doesn't round-trip.
//
// Phase 5 signing path: PdfSignOptions/CertificateSigner/Pdf.Sign/doc.Signatures/SignatureVerifier — and
// in particular the runtime's first-ever PackageReference, System.Security.Cryptography.Pkcs
// (SignedCms, X509Chain, Rfc3161TimestampToken) — must survive NativeAOT
// trimming/reachability exactly like every reflection-free path exercised above. The block
// below generates a self-signed test certificate, signs the fixture file, reopens the result,
// and asserts doc.Signatures[0].Verify() reports a valid signature.
//
// Phase 6 compliance path: the final block exercises Pdf.Redact, Pdf.ValidatePdfA, Pdf.Linearize, and
// Pdf.Stamp end to end — asserting non-trivial output for each, failing loudly otherwise.
using PlumePdf;
using PlumePdf.Elements;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: PlumePdf.AotSmoke <fixture.pdf>");
    return 1;
}

var fixturePath = args[0];

using (var document = PdfDocument.Open(fixturePath))
{
    Console.WriteLine($"Open: {fixturePath} -> {document.Objects.Trailer}");
}

using var merged = Pdf.Merge(fixturePath, fixturePath);
var mergedPath = Path.Combine(Path.GetTempPath(), $"plumepdf-aotsmoke-{Guid.NewGuid():N}.pdf");
try
{
    merged.Save(mergedPath, new PdfOptions { Deterministic = true });
    Console.WriteLine($"Merge + Save: {mergedPath}");

    using var reopened = PdfDocument.Open(mergedPath);
    Console.WriteLine($"Reopen merged output: {reopened.Objects.Trailer}");
}
finally
{
    File.Delete(mergedPath);
}

// Phase 2 creation path: Manuscript -> Fonts (shaping + Standard-14 encoding) -> Layout ->
// Content -> a real synthetic PdfDocument, reopened to confirm the written file is valid.
// An optional second arg names a TrueType/OpenType font file to embed and subset, so the
// AOT-published binary also exercises FontObjectBuilder/FontSubsetter's reflection-free path
// when the corpus font fixtures are available (they are gitignored, so this stays optional).
var font = args.Length > 1 && File.Exists(args[1]) ? PdfFont.FromFile(args[1]) : null;

var manuscript = new Manuscript
{
    Sections =
    [
        new Section
        {
            Header = new Text("PlumePDF AOT smoke") { Bold = true, FontSize = 16 },
            Body = font is null
                ? new Text("Standard-14 creation path OK.")
                : new Text($"Embedded font creation path OK: {font.Name}") { Font = font },
            Footer = new Text("Page {page} of {pages}") { Align = HorizontalAlign.Center },
        },
    ],
};

var renderedPath = Path.Combine(Path.GetTempPath(), $"plumepdf-aotsmoke-render-{Guid.NewGuid():N}.pdf");
try
{
    using (var rendered = manuscript.Render())
    {
        rendered.Save(renderedPath, new PdfOptions { Deterministic = true });
    }

    using var reopenedRendered = PdfDocument.Open(renderedPath);
    Console.WriteLine($"Manuscript render + Save + reopen: {reopenedRendered.Pages.Count} page(s), {reopenedRendered.Diagnostics.Count} diagnostic(s)");

    // Phase 3 extraction path under NativeAOT: positioned text (content-stream reader, the
    // read-side font family, word/line assembly, reading order), images, and metadata — the
    // gate must keep biting on the new public surface, not just the read/create paths.
    var extractionPage = reopenedRendered.Pages[0];
    var extractedText = extractionPage.ExtractText();
    var extractedImages = extractionPage.ExtractImages();
    var info = reopenedRendered.GetInfo();
    Console.WriteLine($"Extraction: {extractedText.Letters.Count} letter(s), {extractedText.Words.Count} word(s), \"{extractedText.Text.Trim()}\", {extractedImages.Count} image(s), Producer='{info.Producer}'");
    if (extractedText.Letters.Count == 0)
    {
        Console.Error.WriteLine("AOT smoke FAILED: extraction returned no letters from the rendered page.");
        return 1;
    }
}
finally
{
    File.Delete(renderedPath);
}

// Phase 4 forms path under NativeAOT: fill (appearance generation through
// the Fonts/Content layers, incremental save) and flatten, against the committed sample form.
var formFixture = Path.Combine(Path.GetDirectoryName(fixturePath)!, "simple-form.pdf");
if (File.Exists(formFixture))
{
    var formCopy = Path.Combine(Path.GetTempPath(), $"plumepdf-aotsmoke-form-{Guid.NewGuid():N}.pdf");
    var flatCopy = Path.Combine(Path.GetTempPath(), $"plumepdf-aotsmoke-flat-{Guid.NewGuid():N}.pdf");
    try
    {
        File.Copy(formFixture, formCopy);
        Pdf.FillForm(formCopy, new Dictionary<string, string> { ["FullName"] = "AOT Smoke", ["Subscribe"] = "Yes" });
        using (var filled = PdfDocument.Open(formCopy))
        {
            Console.WriteLine($"FillForm: FullName='{filled.Form.Fields["FullName"].Value}', Subscribe='{filled.Form.Fields["Subscribe"].Value}'");
            if (filled.Form.Fields["FullName"].Value != "AOT Smoke")
            {
                Console.Error.WriteLine("AOT smoke FAILED: filled value did not round-trip.");
                return 1;
            }
        }

        Pdf.FlattenForm(formCopy, flatCopy);
        using var flattened = PdfDocument.Open(flatCopy);
        Console.WriteLine($"FlattenForm: {flattened.Form.Fields.Count} field(s) remain, {flattened.Pages.Count} page(s)");
    }
    finally
    {
        File.Delete(formCopy);
        if (File.Exists(flatCopy))
        {
            File.Delete(flatCopy);
        }
    }
}

// Phase 5 signing path under NativeAOT: generate a self-signed test
// certificate (System.Security.Cryptography.X509Certificates.CertificateRequest — no
// reflection), sign the fixture, reopen the signed output, and verify it.
using var signingKey = RSA.Create(2048);
var certificateRequest = new CertificateRequest("CN=PlumePDF AOT Smoke Test", signingKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var ephemeralSigningCertificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
using var signingCertificate = new X509Certificate2(ephemeralSigningCertificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);

var signedPath = Path.Combine(Path.GetTempPath(), $"plumepdf-aotsmoke-signed-{Guid.NewGuid():N}.pdf");
try
{
    Pdf.Sign(fixturePath, signedPath, new PdfSignOptions { Certificate = signingCertificate, Reason = "AOT smoke test" });

    using var signedDocument = PdfDocument.Open(signedPath);
    if (signedDocument.Signatures.Count != 1)
    {
        Console.Error.WriteLine($"AOT smoke FAILED: expected 1 signature after Pdf.Sign, found {signedDocument.Signatures.Count}.");
        return 1;
    }

    var verifyResult = signedDocument.Signatures[0].Verify();
    Console.WriteLine($"Sign + Verify: CryptographicStatus={verifyResult.CryptographicStatus}, CoversWholeDocument={verifyResult.CoversWholeDocument}, IsValid={verifyResult.IsValid}");
    if (!verifyResult.IsValid)
    {
        Console.Error.WriteLine($"AOT smoke FAILED: signature did not verify (CryptographicStatus={verifyResult.CryptographicStatus}, Detail={verifyResult.Detail}).");
        return 1;
    }
}
finally
{
    if (File.Exists(signedPath))
    {
        File.Delete(signedPath);
    }
}

// Phase 6 compliance path under NativeAOT: the four new public verbs —
// Pdf.Redact (content-level removal through the full-rewrite GC), Pdf.ValidatePdfA (the
// bounded in-process self-check over the XMP/OutputIntent/filter rules), Pdf.Linearize
// (the Annex F two-pass writer), and Pdf.Stamp/doc.Stamp (additive opened-document stamping)
// — all reflection-free by construction, proven reachable here the same
// way every prior phase's surface is.
const string aotSecret = "AotRedactSecret9412"; // gitleaks:allow — deliberate redaction sentinel, proven unrecoverable below
var complianceSourcePath = Path.Combine(Path.GetTempPath(), $"plumepdf-aotsmoke-compliance-{Guid.NewGuid():N}.pdf");
var redactedPath = Path.Combine(Path.GetTempPath(), $"plumepdf-aotsmoke-redacted-{Guid.NewGuid():N}.pdf");
var linearizedPath = Path.Combine(Path.GetTempPath(), $"plumepdf-aotsmoke-linearized-{Guid.NewGuid():N}.pdf");
var stampedPath = Path.Combine(Path.GetTempPath(), $"plumepdf-aotsmoke-stamped-{Guid.NewGuid():N}.pdf");
var pdfaPath = Path.Combine(Path.GetTempPath(), $"plumepdf-aotsmoke-pdfa-{Guid.NewGuid():N}.pdf");
try
{
    using (var complianceSource = PdfDocument.Compose(page =>
    {
        page.Size(PageSize.A4).Margin(40);
        page.Content().Text($"Briefing: {aotSecret}. End.");
    }))
    {
        complianceSource.Save(complianceSourcePath);
    }

    // Redact: content-level removal, loud rich result, output written by full-rewrite Save.
    var redactResult = Pdf.Redact(complianceSourcePath, redactedPath, [PlumePdf.Documents.Redaction.RedactionTarget.Text(aotSecret)]);
    using (var redacted = PdfDocument.Open(redactedPath))
    {
        var redactedText = redacted.Pages[0].ExtractText().Text;
        Console.WriteLine($"Redact: {redactResult.MatchCount} match(es), {redactResult.TextOperatorsRemoved} operator(s) removed");
        if (redactResult.MatchCount != 1 || redactedText.Contains(aotSecret, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("AOT smoke FAILED: redacted text survived (or nothing matched).");
            return 1;
        }
    }

    // ValidatePdfA: an image-only PDF/A-2b candidate needs no embedded font, so this runs
    // whether or not the optional font fixture was supplied.
    byte[] aotPixels = [200, 200, 200, 200, 200, 200, 200, 200, 200, 200, 200, 200]; // 2x2 gray RGB
    using (var pdfa = new Manuscript
    {
        Title = "AOT smoke PDF/A candidate",
        Sections = [new Section { Body = new Image(aotPixels, pixelWidth: 2, pixelHeight: 2) }],
    }.Render(PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b }))
    {
        pdfa.Save(pdfaPath);
    }

    var validation = Pdf.ValidatePdfA(pdfaPath);
    Console.WriteLine($"ValidatePdfA: declared PDF/A-{validation.DeclaredPart}{validation.DeclaredConformance}, IsConformant={validation.IsConformant}, {validation.Findings.Count} finding(s)");
    if (!validation.IsConformant || validation.DeclaredPart != "2")
    {
        Console.Error.WriteLine("AOT smoke FAILED: PlumePDF's own PDF/A-2b output failed its own self-check.");
        return 1;
    }

    // Linearize: Annex F fast-web-view layout — the parameter dictionary must sit in the
    // first kilobyte, and the output must reopen clean.
    Pdf.Linearize(complianceSourcePath, linearizedPath);
    var linearizedBytes = File.ReadAllBytes(linearizedPath);
    var linearizedHead = System.Text.Encoding.Latin1.GetString(linearizedBytes, 0, Math.Min(1024, linearizedBytes.Length));
    using (var linearized = PdfDocument.Open(linearizedPath))
    {
        Console.WriteLine($"Linearize: /Linearized in first KB={linearizedHead.Contains("/Linearized", StringComparison.Ordinal)}, pages={linearized.Pages.Count}");
        if (!linearizedHead.Contains("/Linearized", StringComparison.Ordinal) || linearized.Pages.Count != 1)
        {
            Console.Error.WriteLine("AOT smoke FAILED: linearized output missing its Annex F parameter dictionary or pages.");
            return 1;
        }
    }

    // Stamp: additive opened-document stamping through the signature-preserving incremental path.
    Pdf.Stamp(complianceSourcePath, stampedPath, "AOT SMOKE");
    using (var stamped = PdfDocument.Open(stampedPath))
    {
        var stampedText = stamped.Pages[0].ExtractText().Text;
        Console.WriteLine($"Stamp: found stamp text={stampedText.Contains("AOT SMOKE", StringComparison.Ordinal)}");
        if (!stampedText.Contains("AOT SMOKE", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("AOT smoke FAILED: stamp text did not survive the stamp + incremental save + reopen round trip.");
            return 1;
        }
    }
}
finally
{
    foreach (var tempPath in new[] { complianceSourcePath, redactedPath, linearizedPath, stampedPath, pdfaPath })
    {
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }
}

// Phase 7 raster codecs / image-to-PDF path under NativeAOT: RasterImage.Decode
// (PNG and JPEG dispatch — the JPEG codec's DCT/Huffman-table machinery is the phase's biggest
// AOT-reachability risk) and Pdf.FromImages, the same reflection-free proof every prior
// phase's surface gets. No external fixture needed: both source images are built in-memory
// through the same public API (RasterImageFrame + EncodePng/EncodeJpeg) this block then
// decodes back.
byte[] aotPngPixels = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120]; // 2x2 RGB
var aotPngBytes = new RasterImageFrame(aotPngPixels, 2, 2, RasterPixelFormat.Rgb24).EncodePng();
var aotJpegBytes = new RasterImageFrame(aotPngPixels, 2, 2, RasterPixelFormat.Rgb24).EncodeJpeg(quality: 90);

var pngImage = RasterImage.Decode(aotPngBytes);
var jpegImage = RasterImage.Decode(aotJpegBytes);
Console.WriteLine($"RasterImage.Decode: PNG {pngImage.Frames[0].Width}x{pngImage.Frames[0].Height} {pngImage.Frames[0].Format}, JPEG {jpegImage.Frames[0].Width}x{jpegImage.Frames[0].Height} {jpegImage.Frames[0].Format}");
if (pngImage.Frames[0].Width != 2 || jpegImage.Frames[0].Width != 2)
{
    Console.Error.WriteLine("AOT smoke FAILED: RasterImage.Decode did not round-trip the in-memory PNG/JPEG fixtures.");
    return 1;
}

var fromImagesPath = Path.Combine(Path.GetTempPath(), $"plumepdf-aotsmoke-fromimages-{Guid.NewGuid():N}.pdf");
try
{
    using (var fromImages = Pdf.FromImages([(ReadOnlyMemory<byte>)aotPngBytes, aotJpegBytes]))
    {
        fromImages.Save(fromImagesPath);
    }

    using var fromImagesDocument = PdfDocument.Open(fromImagesPath);
    Console.WriteLine($"Pdf.FromImages: {fromImagesDocument.Pages.Count} page(s)");
    if (fromImagesDocument.Pages.Count != 2)
    {
        Console.Error.WriteLine("AOT smoke FAILED: Pdf.FromImages did not produce one page per source image.");
        return 1;
    }
}
finally
{
    if (File.Exists(fromImagesPath))
    {
        File.Delete(fromImagesPath);
    }
}

// Phase 8 rasterization path under NativeAOT: Pdf.Rasterize end to end
// (RasterSurface/RasterInterpreter/AGG23 scan conversion — the phase's biggest new
// AOT-reachability surface after Phase 7's JPEG codec), plus SubstituteFontStore's own
// documented AOT-smoke hook — enumerate every bundled face (12 Liberation TrueType +
// 2 Foxit CFF) and actually parse it, proving the compiled-in FieldRVA font-blob bundle survives
// ahead-of-time compilation for both program formats, not merely that TrimmerRootAssembly kept
// its bytes present.
var rasterizedImage = Pdf.Rasterize(fixturePath);
var rasterizedFrame = rasterizedImage.Frames[0];
var rasterizedPng = rasterizedFrame.EncodePng();
Console.WriteLine($"Rasterize: {rasterizedFrame.Width}x{rasterizedFrame.Height} {rasterizedFrame.Format}, {rasterizedPng.Length} PNG byte(s)");
if (rasterizedFrame.Width <= 0 || rasterizedFrame.Height <= 0 || rasterizedPng.Length == 0)
{
    Console.Error.WriteLine("AOT smoke FAILED: Pdf.Rasterize produced an empty or zero-sized frame.");
    return 1;
}

var substituteFaceKeys = PlumePdf.Fonts.Substitute.SubstituteFontStore.AvailableFaceKeys;
if (substituteFaceKeys.Count == 0)
{
    Console.Error.WriteLine("AOT smoke FAILED: SubstituteFontStore.AvailableFaceKeys is empty — the bundled substitute-font set did not compile in.");
    return 1;
}

foreach (var faceKey in substituteFaceKeys)
{
    switch (PlumePdf.Fonts.Substitute.SubstituteFontStore.GetFaceKind(faceKey))
    {
        case PlumePdf.Fonts.Substitute.SubstituteFaceKind.TrueType:
            if (!PlumePdf.Fonts.Substitute.SubstituteFontStore.TryGetFont(faceKey, PlumePdf.Fonts.FontReadLimits.Default, out var substituteFont) || substituteFont.Maxp.NumGlyphs == 0)
            {
                Console.Error.WriteLine($"AOT smoke FAILED: bundled substitute face '{faceKey}' failed to parse under NativeAOT.");
                return 1;
            }

            break;

        case PlumePdf.Fonts.Substitute.SubstituteFaceKind.Cff:
            if (!PlumePdf.Fonts.Substitute.SubstituteFontStore.TryGetCffFont(faceKey, PlumePdf.Fonts.FontReadLimits.Default, out var substituteCff) || substituteCff.GlyphCount == 0)
            {
                Console.Error.WriteLine($"AOT smoke FAILED: bundled substitute face '{faceKey}' failed to parse under NativeAOT.");
                return 1;
            }

            break;
    }
}

Console.WriteLine($"SubstituteFontStore: {substituteFaceKeys.Count} bundled face(s) parsed OK");

Console.WriteLine("AOT smoke OK");
return 0;
