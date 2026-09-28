using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BenchmarkDotNet.Attributes;
using PlumePdf.Compose;
using PlumePdf.Elements;

namespace PlumePdf.Benchmarks;

/// <summary>
/// Phase 5 signing/verification throughput: PAdES B-B <c>doc.Signatures.Add</c> (RSA
/// PKCS#1 v1.5, the common case) and <see cref="PdfSignature.Verify"/>, end to end against a
/// small synthetic document — no network I/O (TSA/OCSP timing belongs to a separate,
/// network-dependent benchmark this repo deliberately does not run on shared CI runners; see
/// <c>ExtractionComparisons</c>'s own remarks on the same PlumePdf-only-macro-suite policy).
/// </summary>
[MemoryDiagnoser]
public class SigningBenchmarks
{
    private string _sourcePath = null!;
    private string _signedPath = null!;
    private string _outputPath = null!;
    private X509Certificate2 _certificate = null!;
    private PdfSignature _signatureToVerify = null!;
    private PdfDocument _openForVerify = null!;

    [GlobalSetup]
    public void Setup()
    {
        _certificate = CreateSelfSignedTestCertificate();

        _sourcePath = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-sign-source-{Guid.NewGuid():N}.pdf");
        using (var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("Signing benchmark fixture.");
        }))
        {
            document.Save(_sourcePath);
        }

        _signedPath = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-sign-signed-{Guid.NewGuid():N}.pdf");
        using (var document = PdfDocument.Open(_sourcePath))
        {
            document.Signatures.Add(_signedPath, new PdfSignOptions { Certificate = _certificate });
        }

        _openForVerify = PdfDocument.Open(_signedPath);
        _signatureToVerify = _openForVerify.Signatures[0];

        _outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-sign-output-{Guid.NewGuid():N}.pdf");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _openForVerify.Dispose();
        _certificate.Dispose();
        File.Delete(_sourcePath);
        File.Delete(_signedPath);
        if (File.Exists(_outputPath))
        {
            File.Delete(_outputPath);
        }
    }

    [Benchmark(Baseline = true)]
    public void Sign_RsaPkcs1_LevelB()
    {
        using var document = PdfDocument.Open(_sourcePath);
        document.Signatures.Add(_outputPath, new PdfSignOptions { Certificate = _certificate }); // overwrites on each iteration.
    }

    [Benchmark]
    public bool Verify_RsaPkcs1_NoTrustAnchors() => _signatureToVerify.Verify().IsValid;

    private static X509Certificate2 CreateSelfSignedTestCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=PlumePDF Signing Benchmark", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }
}
