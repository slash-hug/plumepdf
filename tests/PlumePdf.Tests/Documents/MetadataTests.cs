using System.Text;
using PlumePdf.Documents;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Documents;

/// <summary>
/// <see cref="MetadataReader"/>/<see cref="PdfDocumentInfo"/> round-trip, XMP
/// accessor, and <see cref="PdfPermissions"/> surfacing (advisory only).
/// </summary>
public class MetadataTests
{
    [Fact]
    public void GetInfo_TypedFieldsAndDate_RoundTripFromInfoDictionary()
    {
        var infoDict = new PdfDictionary();
        infoDict.Set(PdfName.Get("Title"), PdfString.FromLiteral("PlumePDF Test Document"u8.ToArray()));
        infoDict.Set(PdfName.Get("Author"), PdfString.FromLiteral("The PlumePDF Contributors"u8.ToArray()));
        infoDict.Set(PdfName.Get("Subject"), PdfString.FromLiteral("Metadata extraction"u8.ToArray()));
        infoDict.Set(PdfName.Get("Keywords"), PdfString.FromLiteral("pdf, metadata, test"u8.ToArray()));
        infoDict.Set(PdfName.Get("Creator"), PdfString.FromLiteral("PlumePDF.Tests"u8.ToArray()));
        infoDict.Set(PdfName.Get("Producer"), PdfString.FromLiteral("PlumePDF"u8.ToArray()));
        infoDict.Set(PdfName.Get("CreationDate"), PdfString.FromLiteral("D:20260815120000+02'00'"u8.ToArray()));

        using var document = BuildSyntheticDocument(infoDict, metadataStream: null);

        var info = document.GetInfo();

        Assert.Equal("PlumePDF Test Document", info.Title);
        Assert.Equal("The PlumePDF Contributors", info.Author);
        Assert.Equal("Metadata extraction", info.Subject);
        Assert.Equal("pdf, metadata, test", info.Keywords);
        Assert.Equal("PlumePDF.Tests", info.Creator);
        Assert.Equal("PlumePDF", info.Producer);
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.FromHours(2)), info.CreationDate);
        Assert.Null(info.ModDate);
    }

    [Fact]
    public void GetInfo_NoInfoDictionary_EveryFieldIsNull()
    {
        using var document = BuildSyntheticDocument(infoDict: null, metadataStream: null);

        var info = document.GetInfo();

        Assert.Null(info.Title);
        Assert.Null(info.Author);
        Assert.Null(info.CreationDate);
    }

    [Theory]
    [InlineData("D:20260101", 2026, 1, 1, 0, 0, 0)]
    [InlineData("D:20260615093012", 2026, 6, 15, 9, 30, 12)]
    [InlineData("20260101000000Z", 2026, 1, 1, 0, 0, 0)] // missing "D:" prefix, tolerated
    public void ParsePdfDate_VariousPrecisions_ParseLeniently(string raw, int year, int month, int day, int hour, int minute, int second)
    {
        var parsed = MetadataReader.ParsePdfDate(raw);

        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(year, month, day, hour, minute, second), parsed!.Value.DateTime);
    }

    [Fact]
    public void ParsePdfDate_Garbage_ReturnsNullRatherThanThrowing()
    {
        Assert.Null(MetadataReader.ParsePdfDate("not a date"));
        Assert.Null(MetadataReader.ParsePdfDate(""));
        Assert.Null(MetadataReader.ParsePdfDate(null));
    }

    [Fact]
    public void GetXmpMetadataBytes_MetadataStreamPresent_ReturnsDecodedBytes()
    {
        var xml = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta>"u8.ToArray();
        var metadataDict = new PdfDictionary();
        metadataDict.Set(PdfName.Type, PdfName.Get("Metadata"));
        metadataDict.Set(PdfName.Subtype, PdfName.Get("XML"));
        var metadataStream = new PdfStream(metadataDict, xml);

        using var document = BuildSyntheticDocument(infoDict: null, metadataStream);

        var bytes = document.GetXmpMetadataBytes();
        var text = document.GetXmpMetadataText();

        Assert.Equal(xml, bytes);
        Assert.Equal(Encoding.UTF8.GetString(xml), text);
    }

    [Fact]
    public void GetXmpMetadataBytes_NoMetadataStream_ReturnsNull()
    {
        using var document = BuildSyntheticDocument(infoDict: null, metadataStream: null);

        Assert.Null(document.GetXmpMetadataBytes());
        Assert.Null(document.GetXmpMetadataText());
    }

    [Theory]
    [InlineData(-4, true)] // conventional "everything granted" value - bit 5 (0x10) is set
    [InlineData(~0x10, false)] // every bit set except bit 5 (extract content) cleared
    public void ReadPermissions_MapsPBitsToExtractContentFlag(int rawP, bool expectExtractGranted)
    {
        var permissions = MetadataReader.ReadPermissions(rawP);
        Assert.Equal(expectExtractGranted, permissions.HasFlag(PdfPermissions.ExtractContent));
    }

    [Fact]
    public void ReadPermissions_NullRawPermissions_ReturnsAll()
    {
        Assert.Equal(PdfPermissions.All, MetadataReader.ReadPermissions(null));
    }

    [Fact]
    public void StandardSecurityHandler_ExposesRawPermissions_MatchingEncryptDictP()
    {
        var fileId = Encoding.ASCII.GetBytes("0123456789ABCDEF");
        var restrictedP = unchecked((int)0xFFFFFFEF); // every bit set except bit 5 (0x10, extract content)
        var (o, u, _) = EncryptionFixtureBuilder.BuildRc4OrAesCredentials(userPassword: "", ownerPassword: "owner-secret", permissions: restrictedP, fileId, keyLengthBytes: 5, revision: 2);

        var encryptDict = new PdfDictionary();
        encryptDict.Set(PdfName.V, PdfNumber.Get(1));
        encryptDict.Set(PdfName.R, PdfNumber.Get(2));
        encryptDict.Set(PdfName.O, PdfString.FromLiteral(o));
        encryptDict.Set(PdfName.U, PdfString.FromLiteral(u));
        encryptDict.Set(PdfName.P, PdfNumber.Get(restrictedP));
        encryptDict.Set(PdfName.Length, PdfNumber.Get(40));

        var handler = new StandardSecurityHandler(encryptDict, fileId, PdfOptions.Default);

        Assert.Equal(restrictedP, handler.Permissions);
        var permissions = MetadataReader.ReadPermissions(handler.Permissions);
        Assert.False(permissions.HasFlag(PdfPermissions.ExtractContent));
        Assert.True(permissions.HasFlag(PdfPermissions.Modify));
    }

    [Fact]
    public void Open_UnrestrictedEncryptedFixture_PermissionsReportsAllAndNoAdvisoryDiagnostic()
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "RC4-40.pdf");
        using var document = PdfDocument.Open(path);

        Assert.Equal(PdfPermissions.All, document.Permissions);
        Assert.True(document.Permissions.HasFlag(PdfPermissions.ExtractContent));

        var extracted = document.Pages[0].ExtractText();
        Assert.DoesNotContain(extracted.Diagnostics, d => d.Code == "PLUME6024");
    }

    [Fact]
    public void Open_UnencryptedDocument_PermissionsReportsAll()
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PlumePdf.Elements.PageSize.A4).Margin(40);
            page.Content().Text("No encryption here");
        });

        Assert.Equal(PdfPermissions.All, document.Permissions);
    }

    private static PdfDocument BuildSyntheticDocument(PdfDictionary? infoDict, PdfStream? metadataStream)
    {
        var catalogDict = new PdfDictionary();
        catalogDict.Set(PdfName.Type, PdfName.Get("Catalog"));
        catalogDict.Set(PdfName.Get("Pages"), new PdfReference(new IndirectReference(2, 0)));

        var pagesDict = new PdfDictionary();
        pagesDict.Set(PdfName.Type, PdfName.Get("Pages"));
        pagesDict.Set(PdfName.Get("Kids"), new PdfArray());
        pagesDict.Set(PdfName.Get("Count"), PdfNumber.Get(0));

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(new IndirectReference(1, 0)));

        var objects = new Dictionary<int, PdfObject>
        {
            [1] = catalogDict,
            [2] = pagesDict,
        };

        if (infoDict is not null)
        {
            objects[3] = infoDict;
            trailer.Set(PdfName.Info, new PdfReference(new IndirectReference(3, 0)));
        }

        if (metadataStream is not null)
        {
            objects[4] = metadataStream;
            catalogDict.Set(PdfName.Get("Metadata"), new PdfReference(new IndirectReference(4, 0)));
        }

        var source = new InMemoryObjectSource(trailer, objects);
        return PdfDocument.CreateSynthetic(source);
    }

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
