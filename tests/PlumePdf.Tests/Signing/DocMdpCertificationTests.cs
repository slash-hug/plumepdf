using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Signing;

/// <summary>
/// <c>PdfSignOptions.CertifyNoChanges</c> (DocMDP certification) and the
/// diagnostic upgrade it drives in <c>FormFiller</c> (naming the certifying field and its
/// P-value, refusing under <see cref="PdfOptions.Strict"/>).
/// </summary>
public class DocMdpCertificationTests
{
    [Fact]
    public void CertifyNoChanges_SecondSignature_ThrowsPlume6051()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var certifiedPath = SignVerifyRoundTripTests.TempPdfPath();
        var secondPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(certifiedPath, new PdfSignOptions { Certificate = ca.LeafRsa, CertifyNoChanges = true });
            }

            using var certified = PdfDocument.Open(certifiedPath);
            var ex = Assert.Throws<PlumePdfException>(() => certified.Signatures.Add(secondPath, new PdfSignOptions { Certificate = ca.LeafEcdsa, CertifyNoChanges = true }));
            Assert.Equal("PLUME6051", ex.Code);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(certifiedPath);
            if (File.Exists(secondPath))
            {
                File.Delete(secondPath);
            }
        }
    }

    [Fact]
    public void Fill_CertifiedNoChangesDocument_Strict_ThrowsPlume6038NamingTheField()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = WriteTempFile(BuildFillableDocument());
        var certifiedPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(certifiedPath, new PdfSignOptions { Certificate = ca.LeafRsa, CertifyNoChanges = true });
            }

            using var certified = PdfDocument.Open(certifiedPath, new PdfOptions { Strict = true });
            var form = PdfForm.For(certified);
            var ex = Assert.Throws<PlumePdfException>(() => form.Fill(new Dictionary<string, string> { ["Name"] = "Jane" }));
            Assert.Equal("PLUME6038", ex.Code);
            Assert.Contains("Signature1", ex.Message, StringComparison.Ordinal);
            Assert.Contains("P=1", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(certifiedPath);
        }
    }

    [Fact]
    public void Fill_CertifiedNoChangesDocument_NonStrict_RecordsNamedDiagnosticAndProceeds()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = WriteTempFile(BuildFillableDocument());
        var certifiedPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(certifiedPath, new PdfSignOptions { Certificate = ca.LeafRsa, CertifyNoChanges = true });
            }

            using var certified = PdfDocument.Open(certifiedPath);
            var form = PdfForm.For(certified);
            form.Fill(new Dictionary<string, string> { ["Name"] = "Jane" });

            var diagnostic = Assert.Single(certified.Diagnostics, d => d.Code == "PLUME6038");
            Assert.Contains("Signature1", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("P=1", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(certifiedPath);
        }
    }

    // A minimal fillable single-text-field document (one page, one merged field/widget) — the
    // same hand-rolled-ISO-grammar approach WriterTestDocuments/SignatureSurvivalTests already
    // use (allowed under the clean-room policy in AGENTS.md) rather than depending on
    // network-fetched corpora.
    private static byte[] BuildFillableDocument()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"));
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        const int catalogNum = 1;
        const int pagesNum = 2;
        const int pageNum = 3;
        const int acroFormNum = 4;
        const int fieldNum = 5;
        const int totalObjects = 6;

        WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R /AcroForm {acroFormNum} 0 R >>");
        WriteObject(pagesNum, $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 >>");
        WriteObject(pageNum, $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 200 100] /Annots [{fieldNum} 0 R] >>");
        WriteObject(acroFormNum, $"<< /Fields [{fieldNum} 0 R] >>");
        WriteObject(fieldNum, $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [0 0 100 20] /P {pageNum} 0 R >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root {catalogNum} 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    private static string WriteTempFile(byte[] bytes)
    {
        var path = SignVerifyRoundTripTests.TempPdfPath();
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
