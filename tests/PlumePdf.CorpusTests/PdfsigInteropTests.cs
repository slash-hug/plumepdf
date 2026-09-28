using PlumePdf.Compose;
using PlumePdf.Elements;
using PlumePdf.Tests.Signing;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// External-validator interop for Phase 5 signing: runs
/// <c>pdfsig</c> (poppler-utils — an independent, reference-grade implementation with no code
/// or authorship in common with PlumePDF) over a PlumePDF-signed document, with the shared
/// <see cref="TestCertificateAuthority"/> root seeded into an NSS database via <c>certutil</c>
/// so the trust verdict exercises a real chain end to end. This is the only independent oracle
/// for the signing surface — <c>CmsSignatureReader</c> validating its own sibling
/// <c>CmsSignatureBuilder</c>'s output proves internal consistency, never conformance to anyone
/// else's reading of the CMS/PDF signature spec. Skips (no-ops) when <c>pdfsig</c>/<c>certutil</c>
/// aren't installed so the hermetic lane stays green; the CI corpus lane installs
/// <c>poppler-utils</c> + <c>libnss3-tools</c> and runs this for real.
/// </summary>
/// <remarks>
/// A caveat, verified empirically: with no usable NSS database, pdfsig prints
/// "NSS_Init failed" to stderr yet still exits 0 and still prints per-signature results with an
/// untrusted certificate verdict — it silently degrades rather than failing. That is why
/// <see cref="SignedWithCaChain_UnseededNssRun_IsDetectablyDegraded"/> exists: it proves the
/// degraded mode is distinguishable from the seeded mode by exactly the assertion the gating
/// test uses, so a broken seeding step can never quietly pass.
/// </remarks>
public class PdfsigInteropTests
{
    private const string TrustedVerdict = "Certificate is Trusted";

    private static readonly bool PdfsigAvailable = ProbePdfsig();
    private static readonly bool CertutilAvailable = ProbeCertutil();

    private static bool ProbePdfsig()
    {
        // The probe inspects output rather than trusting Process.Start alone: an unrelated
        // binary that happens to be named pdfsig must not arm the lane.
        var (started, _, output) = TryRun("pdfsig", "-v");
        return started && output.Contains("pdfsig version", StringComparison.Ordinal);
    }

    private static bool ProbeCertutil()
    {
        // NSS certutil (libnss3-tools / brew nss) prints its command list for -H; the check on
        // the output text also rules out Windows' unrelated built-in certutil.exe.
        var (started, _, output) = TryRun("certutil", "-H");
        return started && output.Contains("certificate database", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignedWithCaChain_PassesPdfsigWithSeededNssTrust()
    {
        if (!PdfsigAvailable || !CertutilAvailable)
        {
            return;
        }

        using var workspace = new SignedFixtureWorkspace();

        // Seeded run: the test CA root is imported as a trusted anchor, so pdfsig must reach a
        // full positive verdict on all three axes — cryptographic validity, whole-document
        // coverage, and chain trust. Asserting the trust axis is what makes the degradation
        // failure mode (see the type remarks) impossible to miss.
        var seededNssDir = workspace.CreateNssDatabase(seedRootCertificate: true);
        var output = RunPdfsig(workspace.SignedPdfPath, seededNssDir);

        Assert.Contains("Signature is Valid.", output);
        Assert.Contains("Total document signed", output);
        Assert.Contains(TrustedVerdict, output);
    }

    [Fact]
    public void SignedWithCaChain_UnseededNssRun_IsDetectablyDegraded()
    {
        if (!PdfsigAvailable || !CertutilAvailable)
        {
            return;
        }

        using var workspace = new SignedFixtureWorkspace();

        // Unseeded (but initialized) database: pdfsig still validates the cryptography, but the
        // trust verdict must NOT read as trusted — proving the seeded test's TrustedVerdict
        // assertion actually distinguishes a working certutil -A step from a silently broken one.
        var unseededNssDir = workspace.CreateNssDatabase(seedRootCertificate: false);
        var unseededOutput = RunPdfsig(workspace.SignedPdfPath, unseededNssDir);
        Assert.Contains("Signature is Valid.", unseededOutput);
        Assert.DoesNotContain(TrustedVerdict, unseededOutput);

        // No database at all — the "NSS_Init failed" mode: pdfsig degrades further but still
        // reports; it must likewise never read as trusted.
        var missingDirOutput = RunPdfsig(workspace.SignedPdfPath, Path.Combine(workspace.Root, "no-such-nss-dir"));
        Assert.DoesNotContain(TrustedVerdict, missingDirOutput);
    }

    private static string RunPdfsig(string pdfPath, string nssDir)
    {
        var (started, exitCode, output) = TryRun("pdfsig", $"-nssdir \"{nssDir}\" \"{pdfPath}\"");
        Assert.True(started, "pdfsig failed to start after probing available.");
        // pdfsig exits 0 even for untrusted/degraded runs (verified empirically) — the OUTPUT
        // carries the verdicts, so nothing is asserted on the exit code here; including it in
        // the failure text of the callers' Contains asserts is what matters.
        _ = exitCode;
        return output;
    }

    private static (bool Started, int ExitCode, string Output) TryRun(string fileName, string arguments)
    {
        // Thin veneer over the shared deadlock-safe runner: these tests assert on the two
        // streams' combined text (pdfsig writes verdicts to stdout, NSS degradation notes to
        // stderr), so the split ExternalTool result is concatenated here.
        var (started, exitCode, stdout, stderr) = ExternalTool.TryRun(fileName, arguments, timeoutMilliseconds: 30_000);
        return (started, exitCode, stdout + stderr);
    }

    /// <summary>
    /// A per-test temp workspace holding one PlumePDF-composed document signed with the shared
    /// <see cref="TestCertificateAuthority"/>'s RSA chain, plus factory methods for seeded and
    /// unseeded NSS databases. Everything lives under one root directory deleted on dispose.
    /// </summary>
    private sealed class SignedFixtureWorkspace : IDisposable
    {
        private readonly TestCertificateAuthority _authority = new();

        public string Root { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"plumepdf-pdfsig-interop-{Guid.NewGuid():N}")).FullName;

        public string SignedPdfPath { get; }

        public SignedFixtureWorkspace()
        {
            var sourcePath = Path.Combine(Root, "source.pdf");
            SignedPdfPath = Path.Combine(Root, "signed.pdf");

            using (var document = PdfDocument.Compose(page =>
            {
                page.Size(PageSize.A4).Margin(40);
                page.Content().Text("pdfsig external-validator interop fixture.");
            }))
            {
                document.Save(sourcePath);
            }

            using var document2 = PdfDocument.Open(sourcePath);
            document2.Signatures.Add(SignedPdfPath, new PdfSignOptions
            {
                Certificate = _authority.LeafRsa,
                AdditionalCertificates = [_authority.Intermediate, _authority.Root],
                Reason = "pdfsig external-validator interop test",
            });
        }

        public string CreateNssDatabase(bool seedRootCertificate)
        {
            var directory = Directory.CreateDirectory(
                Path.Combine(Root, seedRootCertificate ? "nss-seeded" : "nss-unseeded")).FullName;

            RunCertutil($"-N -d \"sql:{directory}\" --empty-password");
            if (seedRootCertificate)
            {
                var rootPemPath = Path.Combine(Root, "test-root.pem");
                File.WriteAllText(rootPemPath, _authority.Root.ExportCertificatePem());
                RunCertutil($"-A -d \"sql:{directory}\" -n plumepdf-test-root -t \"CT,C,C\" -a -i \"{rootPemPath}\"");
            }

            return directory;
        }

        private static void RunCertutil(string arguments)
        {
            var (started, exitCode, output) = TryRun("certutil", arguments);
            Assert.True(started && exitCode == 0, $"certutil {arguments} failed ({exitCode}): {output}");
        }

        public void Dispose()
        {
            _authority.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup only.
            }
        }
    }
}
