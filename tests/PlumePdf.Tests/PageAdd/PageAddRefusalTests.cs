using System.Text;
using PlumePdf.Objects;
using PlumePdf.Tests.Signing;
using Xunit;
using static PlumePdf.Tests.PageAdd.PageAddFixtures;

namespace PlumePdf.Tests.PageAdd;

/// <summary>
/// What page-add refuses before it changes anything, and the diagnostics it reports about the
/// destination: an encrypted source into an unencrypted destination (<c>PLUME6012</c>), a
/// DocMDP-certified destination (<c>PLUME6092</c>), a catalog without an indirect <c>/Pages</c>
/// (<c>PLUME6097</c>), and a destination whose permissions don't allow assembly (<c>PLUME6098</c>).
/// </summary>
public class PageAddRefusalTests
{
    private static readonly byte[] FileId = Encoding.ASCII.GetBytes("0123456789ABCDEF");

    [Fact]
    public void AnEncryptedSource_IntoAnUnencryptedDestination_IsRefused()
    {
        using var document = PdfDocument.Open(Text(["One"]));
        using var source = PdfDocument.Open(Fixture("RC4-128.pdf"));

        var ex = Assert.Throws<PlumePdfException>(() => document.Pages.Add(source.Pages[0]));

        Assert.Equal("PLUME6012", ex.Code);
        Assert.Contains("not encrypted", ex.Message, StringComparison.Ordinal);
        Assert.Single(document.Pages);
        Assert.Empty(document.Objects.DirtyObjects);
    }

    [Fact]
    public void ADirectPagesDictionary_IsRefused()
    {
        using var document = PdfDocument.Open(PdfFixtureBuilder.BuildClassicXrefPdf(
            [(1, "<< /Type /Catalog /Pages << /Type /Pages /Kids [3 0 R] /Count 1 >> >>"), (3, "<< /Type /Page /MediaBox [0 0 100 100] >>")],
            rootObjectNumber: 1));
        using var source = PdfDocument.Open(Text(["New"]));

        var ex = Assert.Throws<PlumePdfException>(() => document.Pages.Add(source.Pages[0]));

        Assert.Equal("PLUME6097", ex.Code);
        Assert.Empty(document.Objects.DirtyObjects);
    }

    [Fact]
    public void ACertifiedDestination_IsReported_AndThePageIsAdded()
    {
        using var ca = new TestCertificateAuthority();
        var certified = Certified(ca);
        try
        {
            using var document = PdfDocument.Open(certified);
            using var source = PdfDocument.Open(Text(["New"]));

            document.Pages.Add(source.Pages[0]);
            document.Pages.Add(source.Pages[0]);

            var diagnostic = Assert.Single(document.Diagnostics, static d => d.Code == "PLUME6092");
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.Contains("P=1", diagnostic.Message, StringComparison.Ordinal);
            Assert.Equal(3, document.Pages.Count);
        }
        finally
        {
            File.Delete(certified);
        }
    }

    [Fact]
    public void ACertifiedDestination_UnderStrict_IsRefused_AndNothingChanges()
    {
        using var ca = new TestCertificateAuthority();
        var certified = Certified(ca);
        try
        {
            using var document = PdfDocument.Open(certified, new PdfOptions { Strict = true });
            using var source = PdfDocument.Open(Text(["New"]));

            var ex = Assert.Throws<PlumePdfException>(() => document.Pages.Add(source.Pages[0]));

            Assert.Equal("PLUME6092", ex.Code);
            Assert.Single(document.Pages);
            Assert.Empty(document.Objects.DirtyObjects);
        }
        finally
        {
            File.Delete(certified);
        }
    }

    [Fact]
    public void ACertifiedDestinationWithUsageRights_NamesThemInTheDiagnostic()
    {
        using var ca = new TestCertificateAuthority();
        var certified = Certified(ca);
        try
        {
            using var withRights = PdfDocument.Open(certified);
            using var source = PdfDocument.Open(Text(["New"]));
            withRights.Catalog!.Dictionary.Set(PdfName.Get("Perms"), DictionaryWithUr3());

            withRights.Pages.Add(source.Pages[0]);

            Assert.Contains("/UR3", Assert.Single(withRights.Diagnostics, static d => d.Code == "PLUME6092").Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(certified);
        }
    }

    [Fact]
    public void ADestinationWithoutAssemblyPermission_GetsAnAdvisory()
    {
        using var document = PdfDocument.Open(EncryptedWithPermissions(-4 & ~(1 << 10)));
        using var source = PdfDocument.Open(Fixture("RC4-40.pdf"));

        document.Pages.Add(source.Pages[0]);

        var diagnostic = Assert.Single(document.Diagnostics, static d => d.Code == "PLUME6098");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Equal(2, document.Pages.Count);
    }

    [Fact]
    public void ADestinationThatAllowsAssembly_GetsNoAdvisory()
    {
        using var document = PdfDocument.Open(EncryptedWithPermissions(-4));
        using var source = PdfDocument.Open(Fixture("RC4-40.pdf"));

        document.Pages.Add(source.Pages[0]);

        Assert.DoesNotContain(document.Diagnostics, static d => d.Code == "PLUME6098");
    }

    private static PdfDictionary DictionaryWithUr3()
    {
        var perms = new PdfDictionary();
        perms.Set(PdfName.Get("UR3"), new PdfDictionary());
        return perms;
    }

    // A one-page document certified with DocMDP P=1, written to a temp file.
    private static string Certified(TestCertificateAuthority ca)
    {
        var source = SignVerifyRoundTripTests.TempPdfPath();
        var certified = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(source);
            using var document = PdfDocument.Open(source);
            document.Signatures.Add(certified, new PdfSignOptions { Certificate = ca.LeafRsa, CertifyNoChanges = true });
            return certified;
        }
        finally
        {
            File.Delete(source);
        }
    }

    // A one-page RC4-40 document (empty user password) with /P = permissions. It has no strings
    // or streams, so nothing but the /Encrypt dictionary needs the key.
    private static byte[] EncryptedWithPermissions(int permissions)
    {
        var (o, u, _) = EncryptionFixtureBuilder.BuildRc4OrAesCredentials(userPassword: "", ownerPassword: "owner", permissions, FileId, keyLengthBytes: 5, revision: 2);
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            $"<< /Filter /Standard /V 1 /R 2 /Length 40 /O <{Convert.ToHexString(o)}> /U <{Convert.ToHexString(u)}> /P {permissions} >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }

        var id = Convert.ToHexString(FileId);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Encrypt 4 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Fixture(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlumePdf.sln")))
            {
                return Path.Combine(dir.FullName, "tests", "PlumePdf.CorpusTests", "Fixtures", name);
            }
        }

        throw new InvalidOperationException("PlumePdf.sln not found.");
    }
}
