using PlumePdf.Documents.Compression;
using PlumePdf.Tests.Signing;
using Xunit;
using static PlumePdf.Tests.Compression.CompressFixtures;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// <c>Pdf.Compress</c> on a signed source: refused by default (a full rewrite would leave
/// signatures that no longer verify), stripped to an honestly unsigned output when the caller
/// allows it — for an ordinary signature, a certifying (DocMDP) one, and a document
/// timestamp — and never with the full-rewrite save's own signature warning on top.
/// </summary>
public class CompressSignatureTests
{
    public enum Signing
    {
        Approval,
        Certification,
        DocumentTimestamp,
    }

    [Theory]
    [InlineData(Signing.Approval)]
    [InlineData(Signing.Certification)]
    [InlineData(Signing.DocumentTimestamp)]
    public async Task SignedSource_IsRefused_AndNothingIsWritten(Signing signing)
    {
        var signed = await SignedSource(signing);
        var output = TempPath();
        try
        {
            var ex = Assert.Throws<PlumePdfException>(() => Pdf.Compress(signed, output));

            Assert.Equal("PLUME6084", ex.Code);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Delete(signed, output);
        }
    }

    [Theory]
    [InlineData(Signing.Approval)]
    [InlineData(Signing.Certification)]
    [InlineData(Signing.DocumentTimestamp)]
    public async Task AllowInvalidatingSignatures_StripsThem(Signing signing)
    {
        var signed = await SignedSource(signing);
        var output = TempPath();
        try
        {
            using var document = PdfDocument.Open(signed);

            var result = CompressionEngine.Run(document, signed, output, new PdfCompressOptions { AllowInvalidatingSignatures = true }, PdfOptions.Default, CancellationToken.None);

            using var compressed = PdfDocument.Open(output);
            Assert.Empty(compressed.Signatures);
            Assert.False(compressed.Catalog!.Dictionary.ContainsKey(PdfName.Perms));
            Assert.False(compressed.Catalog!.Dictionary.ContainsKey(PdfName.DSS));
            Assert.DoesNotContain(result.Diagnostics, static d => d.Code == "PLUME5014");
            Assert.DoesNotContain(document.Diagnostics, static d => d.Code == "PLUME5014");
        }
        finally
        {
            Delete(signed, output);
        }
    }

    [Fact]
    public async Task AllowInvalidatingSignatures_WorksUnderStrict()
    {
        // Strict refuses a full rewrite of a signed source; stripping first means there is none left to refuse.
        var signed = await SignedSource(Signing.Approval);
        var output = TempPath();
        try
        {
            Pdf.Compress(signed, output, new PdfCompressOptions { AllowInvalidatingSignatures = true }, PdfOptions.Default with { Strict = true });

            using var compressed = PdfDocument.Open(output);
            Assert.Empty(compressed.Signatures);
        }
        finally
        {
            Delete(signed, output);
        }
    }

    private static async Task<string> SignedSource(Signing signing)
    {
        using var ca = new TestCertificateAuthority();
        var source = TempPath("unsigned");
        var signed = TempPath("signed");
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(source);
            using var document = PdfDocument.Open(source);
            switch (signing)
            {
                case Signing.Approval:
                    document.Signatures.Add(signed, new PdfSignOptions { Certificate = ca.LeafRsa });
                    break;
                case Signing.Certification:
                    document.Signatures.Add(signed, new PdfSignOptions { Certificate = ca.LeafRsa, CertifyNoChanges = true });
                    break;
                case Signing.DocumentTimestamp:
                    using (var tsa = new FakeTimestampAuthority(ca.Intermediate))
                    {
                        await document.Signatures.AddDocumentTimestampAsync(signed, tsa);
                    }

                    break;
            }

            return signed;
        }
        finally
        {
            Delete(source);
        }
    }
}
