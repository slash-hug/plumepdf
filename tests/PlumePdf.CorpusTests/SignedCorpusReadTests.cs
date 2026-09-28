using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// Third-party-signed fixture sweep: PlumePDF's signature READ surface
/// (<c>doc.Signatures</c>, per-signature <c>Verify()</c>) over real signatures produced by
/// other implementations — the veraPDF Permissions conformance files (2017-era
/// <c>/adbe.pkcs7.detached</c>) and the pinned demo forms (f1040/i-9, whose usage-rights
/// signatures were produced by Adobe tooling; i-9 is additionally encrypted, exercising the
/// <c>/Contents</c> decrypt carve-out end to end on a real file). These prove the reader
/// handles wild-caught producers' structures without throwing — the strong verdict assertions
/// live in <see cref="PdfsigInteropTests"/> where PlumePDF is the producer.
/// </summary>
public class SignedCorpusReadTests
{
    private static readonly string VeraPdfSignedFixture = Path.Combine(
        CorpusFixture.CorporaRoot, "veraPDF-corpus-master", "PDF_A-2b", "6.1 File structure",
        "6.1.12 Permissions", "veraPDF test suite 6-1-12-t01-pass-a.pdf");

    public static TheoryData<string> DemoFormPaths => new()
    {
        Path.Combine(CorpusFixture.CorporaRoot, "demo-forms", "f1040-2022.pdf"),
        Path.Combine(CorpusFixture.CorporaRoot, "demo-forms", "i-9.pdf"),
    };

    [Fact]
    public void VeraPdfPermissionsFixture_SignatureIsReadAndCoverageComputed()
    {
        if (!File.Exists(VeraPdfSignedFixture))
        {
            return; // corpus not fetched — the corpus CI lane runs this for real.
        }

        using var document = PdfDocument.Open(VeraPdfSignedFixture);
        Assert.True(document.Signatures.Count >= 1, "Expected at least one signature field.");

        var result = document.Signatures[0].Verify();

        // Independently confirmed by pdfsig ("Not total document signed", plus "Illegal values
        // in ByteRange array"): this fixture's /ByteRange second segment [4862, 6654] claims
        // bytes past the file's actual 6706-byte end. Whole-document coverage MUST come back
        // false — the un-hideable coverage verdict (shadow-attack literature) on a
        // real third-party file — and the impossible segment maps to a Malformed VERDICT, never
        // an exception (read-leniently/verify-strictly split).
        Assert.False(result.CoversWholeDocument);
        Assert.Equal(SignatureCryptographicStatus.Malformed, result.CryptographicStatus);

        // No trust anchors supplied — chain trust stays opt-in.
        Assert.Equal(SignatureChainStatus.NotEvaluated, result.ChainStatus);
    }

    [Theory]
    [MemberData(nameof(DemoFormPaths))]
    public void DemoForms_UsageRightsSignatures_ReadAndVerifyWithoutThrowing(string path)
    {
        if (!File.Exists(path))
        {
            return; // demo forms are a separate non-gating fetch.
        }

        using var document = PdfDocument.Open(path);

        // Both demo forms carry a usage-rights signature. The point of this sweep is that a
        // wild-caught producer's signature dictionary — including i-9's encrypted case, where
        // the encryption carve-out must hand Verify() the raw CMS bytes — flows through the read and
        // verify paths as an ordinary result, never an exception.
        Assert.True(document.Signatures.Count >= 1, $"Expected at least one signature in {Path.GetFileName(path)}.");
        foreach (var signature in document.Signatures)
        {
            var result = signature.Verify();
            Assert.Equal(SignatureChainStatus.NotEvaluated, result.ChainStatus);
        }
    }
}
