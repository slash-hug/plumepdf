using System.Text;
using System.Xml.Linq;
using PlumePdf.Documents.Metadata;
using PlumePdf.Elements;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Metadata;

/// <summary>
/// <see cref="XmpWriter"/>'s RDF/XML serialization round trip, its
/// <see cref="PdfOptions.MaxXmpPacketWriteBytes"/> cap, and the
/// PDF/A+Deterministic-without-dates refusal.
/// </summary>
public class XmpWriteTests
{
    [Fact]
    public void ToBytes_FullPacket_ParsesBackToTheSameFields()
    {
        var createDate = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.FromHours(2));
        var modifyDate = new DateTimeOffset(2026, 8, 16, 9, 30, 0, TimeSpan.FromHours(2));
        var packet = new XmpPacket
        {
            Title = "Q3 Report",
            Creator = "Acme Corp",
            Subject = "Quarterly numbers",
            Keywords = "finance, q3",
            Producer = "PlumePDF",
            CreateDate = createDate,
            ModifyDate = modifyDate,
            Conformance = PdfAConformance.A2b,
            DeclarePdfUa = true,
        };

        var bytes = XmpWriter.ToBytes(packet, PdfOptions.Default);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.StartsWith("<?xpacket begin=", text, StringComparison.Ordinal);
        Assert.EndsWith("<?xpacket end=\"w\"?>", text, StringComparison.Ordinal);

        var doc = XDocument.Parse(text);
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace xmp = "http://ns.adobe.com/xap/1.0/";
        XNamespace pdf = "http://ns.adobe.com/pdf/1.3/";
        XNamespace pdfaid = "http://www.aiim.org/pdfa/ns/id/";
        XNamespace pdfuaid = "http://www.aiim.org/pdfua/ns/id/";
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

        var description = doc.Descendants(rdf + "Description").Single();

        Assert.Equal("Q3 Report", description.Element(dc + "title")!.Descendants(rdf + "li").Single().Value);
        Assert.Equal("Acme Corp", description.Element(dc + "creator")!.Descendants(rdf + "li").Single().Value);
        Assert.Equal("Quarterly numbers", description.Element(dc + "description")!.Descendants(rdf + "li").Single().Value);
        Assert.Equal("finance, q3", description.Element(pdf + "Keywords")!.Value);
        Assert.Equal("PlumePDF", description.Element(pdf + "Producer")!.Value);
        Assert.Equal("2", description.Element(pdfaid + "part")!.Value);
        Assert.Equal("B", description.Element(pdfaid + "conformance")!.Value);
        Assert.Equal("1", description.Element(pdfuaid + "part")!.Value);

        var parsedCreate = DateTimeOffset.Parse(description.Element(xmp + "CreateDate")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        var parsedModify = DateTimeOffset.Parse(description.Element(xmp + "ModifyDate")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(createDate, parsedCreate);
        Assert.Equal(modifyDate, parsedModify);
    }

    [Fact]
    public void ToBytes_EmptyPacket_ProducesParseableXmlWithNoOptionalFields()
    {
        var bytes = XmpWriter.ToBytes(new XmpPacket(), PdfOptions.Default);
        var text = Encoding.UTF8.GetString(bytes);
        var doc = XDocument.Parse(text);

        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        var description = doc.Descendants(rdf + "Description").Single();
        Assert.Empty(description.Elements());
    }

    [Fact]
    public void ToBytes_ExceedsMaxXmpPacketWriteBytes_ThrowsPlume6056()
    {
        var packet = new XmpPacket { Keywords = new string('a', 10_000) };
        var tight = PdfOptions.Default with { MaxXmpPacketWriteBytes = 100 };

        var ex = Assert.Throws<PlumePdfException>(() => XmpWriter.ToBytes(packet, tight));
        Assert.Equal("PLUME6056", ex.Code);
    }

    [Fact]
    public void ToBytes_ControlCharacterInTitle_ThrowsPlume6081NamingTheField()
    {
        var packet = new XmpPacket { Title = "Q3\u0001Report" };

        var ex = Assert.Throws<PlumePdfException>(() => XmpWriter.ToBytes(packet, PdfOptions.Default));
        Assert.Equal("PLUME6081", ex.Code);
        Assert.Contains("Title", ex.Message, StringComparison.Ordinal);
        Assert.Contains("U+0001", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToBytes_UnpairedSurrogateInKeywords_ThrowsPlume6081NamingTheField()
    {
        var packet = new XmpPacket { Keywords = "broken \ud83d surrogate" };

        var ex = Assert.Throws<PlumePdfException>(() => XmpWriter.ToBytes(packet, PdfOptions.Default));
        Assert.Equal("PLUME6081", ex.Code);
        Assert.Contains("Keywords", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToBytes_AstralPlanePair_IsRepresentableAndRoundTrips()
    {
        var packet = new XmpPacket { Title = "emoji \U0001F600 title" };

        var bytes = XmpWriter.ToBytes(packet, PdfOptions.Default);
        Assert.Contains("emoji \U0001F600 title", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void ToBytes_PdfAConformanceDeterministicWithoutDates_ThrowsPlume6058()
    {
        var packet = new XmpPacket { Conformance = PdfAConformance.A2b };
        var deterministic = PdfOptions.Default with { Deterministic = true };

        var ex = Assert.Throws<PlumePdfException>(() => XmpWriter.ToBytes(packet, deterministic));
        Assert.Equal("PLUME6058", ex.Code);
    }

    [Fact]
    public void ToBytes_PdfAConformanceDeterministicWithBothDates_DoesNotThrow()
    {
        var now = DateTimeOffset.UtcNow;
        var packet = new XmpPacket { Conformance = PdfAConformance.A2b, CreateDate = now, ModifyDate = now };
        var deterministic = PdfOptions.Default with { Deterministic = true };

        var bytes = XmpWriter.ToBytes(packet, deterministic);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void ToBytes_NonPdfADeterministicWithoutDates_DoesNotThrow()
    {
        // The refusal is scoped to PDF/A metadata specifically (Conformance != None) —
        // an ordinary deterministic document with no PDF/A claim never needs xmp:CreateDate/
        // ModifyDate at all, so omitting them is not a violation of anything.
        var packet = new XmpPacket { Title = "Plain document" };
        var deterministic = PdfOptions.Default with { Deterministic = true };

        var bytes = XmpWriter.ToBytes(packet, deterministic);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void ToBytes_Deterministic_TwoCallsProduceByteIdenticalOutput()
    {
        var fixedDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var packet = new XmpPacket
        {
            Title = "Report",
            Conformance = PdfAConformance.A1b,
            CreateDate = fixedDate,
            ModifyDate = fixedDate,
        };
        var deterministic = PdfOptions.Default with { Deterministic = true };

        var first = XmpWriter.ToBytes(packet, deterministic);
        var second = XmpWriter.ToBytes(packet, deterministic);

        Assert.Equal(first, second);
    }
}

/// <summary><see cref="DocInfoWriter"/> builds a well-formed <c>/Info</c> dictionary from a <see cref="DocInfoMetadata"/>.</summary>
public class DocInfoWriteTests
{
    [Fact]
    public void BuildDictionary_EveryField_RoundTripsThroughPdfStringAndPdfDate()
    {
        var creationDate = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.FromHours(2));
        var info = new DocInfoMetadata
        {
            Title = "Q3 Report",
            Author = "Jane Doe",
            Subject = "Quarterly numbers",
            Keywords = "finance, q3",
            Creator = "PlumePDF.Tests",
            Producer = "PlumePDF",
            CreationDate = creationDate,
        };

        var dict = DocInfoWriter.BuildDictionary(info);

        Assert.Equal("Q3 Report", ((PdfString)dict[PdfName.Get("Title")]).GetText());
        Assert.Equal("Jane Doe", ((PdfString)dict[PdfName.Get("Author")]).GetText());
        Assert.Equal("Quarterly numbers", ((PdfString)dict[PdfName.Get("Subject")]).GetText());
        Assert.Equal("finance, q3", ((PdfString)dict[PdfName.Get("Keywords")]).GetText());
        Assert.Equal("PlumePDF.Tests", ((PdfString)dict[PdfName.Get("Creator")]).GetText());
        Assert.Equal("PlumePDF", ((PdfString)dict[PdfName.Get("Producer")]).GetText());

        var creationDateText = ((PdfString)dict[PdfName.Get("CreationDate")]).GetText();
        Assert.Equal(creationDate, PlumePdf.Documents.MetadataReader.ParsePdfDate(creationDateText));
        Assert.False(dict.ContainsKey(PdfName.Get("ModDate")));
    }

    [Fact]
    public void BuildDictionary_NoFieldsSet_ProducesEmptyDictionary()
    {
        var dict = DocInfoWriter.BuildDictionary(new DocInfoMetadata());
        Assert.Empty(dict);
    }
}

/// <summary><see cref="PdfDocument.SetInfo"/>/<see cref="PdfDocument.SetXmpMetadata"/> — write → read-back round trip, and the XMP/DocInfo agreement rule.</summary>
public class PdfDocumentMetadataAccessorTests
{
    [Fact]
    public void SetXmpMetadata_ThenGetXmpMetadataText_RoundTripsBeforeAnySave()
    {
        using var document = ComposeSimpleDocument();
        var packet = new XmpPacket { Title = "Q3 Report", Creator = "Acme Corp" };

        document.SetXmpMetadata(packet);
        var text = document.GetXmpMetadataText();

        Assert.NotNull(text);
        var doc = XDocument.Parse(text);
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        var description = doc.Descendants(rdf + "Description").Single();
        Assert.Equal("Q3 Report", description.Element(dc + "title")!.Descendants(rdf + "li").Single().Value);
    }

    [Fact]
    public void SetInfo_ThenGetInfo_RoundTripsBeforeAnySave()
    {
        using var document = ComposeSimpleDocument();
        document.SetInfo(new DocInfoMetadata { Title = "Q3 Report", Producer = "PlumePDF" });

        var info = document.GetInfo();

        Assert.Equal("Q3 Report", info.Title);
        Assert.Equal("PlumePDF", info.Producer);
    }

    [Fact]
    public void SetInfo_ThenSetXmpMetadata_AgreeingTitles_DoesNotThrow()
    {
        using var document = ComposeSimpleDocument();
        document.SetInfo(new DocInfoMetadata { Title = "Q3 Report" });
        document.SetXmpMetadata(new XmpPacket { Title = "Q3 Report" });

        Assert.Equal("Q3 Report", document.GetInfo().Title);
    }

    [Fact]
    public void SetInfo_ThenSetXmpMetadata_DisagreeingTitles_ThrowsPlume6057()
    {
        using var document = ComposeSimpleDocument();
        document.SetInfo(new DocInfoMetadata { Title = "Q3 Report" });

        var ex = Assert.Throws<PlumePdfException>(() => document.SetXmpMetadata(new XmpPacket { Title = "Annual Report" }));
        Assert.Equal("PLUME6057", ex.Code);
    }

    [Fact]
    public void SetXmpMetadata_ThenSetInfo_DisagreeingDates_ThrowsPlume6057()
    {
        using var document = ComposeSimpleDocument();
        var dateA = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var dateB = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        document.SetXmpMetadata(new XmpPacket { CreateDate = dateA });

        var ex = Assert.Throws<PlumePdfException>(() => document.SetInfo(new DocInfoMetadata { CreationDate = dateB }));
        Assert.Equal("PLUME6057", ex.Code);
    }

    [Fact]
    public void SetXmpMetadata_NoResolvableCatalog_ThrowsPlume6059()
    {
        var trailer = new PdfDictionary(); // deliberately no /Root
        var source = new InMemoryObjectSource(trailer, new Dictionary<int, PdfObject>());
        using var document = PdfDocument.CreateSynthetic(source);

        var ex = Assert.Throws<PlumePdfException>(() => document.SetXmpMetadata(new XmpPacket { Title = "x" }));
        Assert.Equal("PLUME6059", ex.Code);
    }

    private static PdfDocument ComposeSimpleDocument() =>
        PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("Hello");
        });
}

/// <summary>The PDF version knob (<c>PdfOptions.PdfVersion</c>) unhardcoding <c>%PDF-1.7</c> in <c>FullRewriteWriter</c>.</summary>
public class PdfVersionKnobTests
{
    [Fact]
    public void Save_DefaultOptions_WritesPdf17Header()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var outputPath = TempPath("v17");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Save(outputPath);
            }

            var header = Encoding.ASCII.GetString(File.ReadAllBytes(outputPath), 0, 9);
            Assert.Equal("%PDF-1.7\n", header);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void Save_PdfVersion14_WritesPdf14HeaderAndReopens()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var outputPath = TempPath("v14");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Save(outputPath, PdfOptions.Default with { PdfVersion = "1.4" });
            }

            var header = Encoding.ASCII.GetString(File.ReadAllBytes(outputPath), 0, 9);
            Assert.Equal("%PDF-1.4\n", header);

            using var reopened = PdfDocument.Open(outputPath);
            Assert.Single(reopened.Pages);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    private static string TempPath(string tag) => Path.Combine(Path.GetTempPath(), $"plumepdf-{tag}-{Guid.NewGuid():N}.pdf");
}

/// <summary><see cref="ObjectRegistry.Replace"/> — the "re-point /Contents" escape hatch for immutable <see cref="PdfStream"/> objects.</summary>
public class ObjectRegistryReplaceTests
{
    [Fact]
    public void Replace_ExistingContentsStream_RepointsWithoutMintingNewObjectNumber_AndPersistsThroughSave()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-replace-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                var page = document.Pages[0];
                Assert.True(page.Dictionary.TryGetValue(PdfName.Get("Contents"), out var contentsValue));
                var contentsRef = (PdfReference)contentsValue;

                var replacement = new PdfStream(new PdfDictionary(), "BT /F1 12 Tf 20 50 Td (REDACTED) Tj ET"u8.ToArray());
                document.Objects.Replace(contentsRef.Target, replacement);

                // The replacement kept the same object identity — no fresh number consumed.
                Assert.True(document.Objects.IsDirty(contentsRef.Target));
                Assert.Same(replacement, document.Objects[contentsRef.Target]);

                document.Save(outputPath);
            }

            var text = Encoding.Latin1.GetString(File.ReadAllBytes(outputPath));
            Assert.Contains("REDACTED", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Page 1 of 1", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void Replace_NeverAllocatedObjectNumber_ThrowsArgumentException()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        try
        {
            using var document = PdfDocument.Open(sourcePath);
            var farFutureNumber = new IndirectReference(999_999, 0);

            Assert.Throws<ArgumentException>(() => document.Objects.Replace(farFutureNumber, new PdfDictionary()));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public void Replace_ObjectJustAllocated_Succeeds()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        try
        {
            using var document = PdfDocument.Open(sourcePath);
            var reference = document.Objects.AllocateNumber();
            document.Objects.RegisterNew(reference, new PdfDictionary());

            var replacement = new PdfDictionary();
            document.Objects.Replace(reference, replacement);

            Assert.Same(replacement, document.Objects[reference]);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }
}

/// <summary>The internal redaction-dirty flag/guard seam — <c>SaveIncremental</c> refuses once <c>MarkRedactionDirty</c> has been called.</summary>
public class RedactionDirtyGuardTests
{
    [Fact]
    public void SaveIncremental_AfterMarkRedactionDirty_ThrowsPlume5016()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        try
        {
            using var document = PdfDocument.Open(sourcePath);
            Assert.False(document.IsRedactionDirty);

            document.MarkRedactionDirty();

            Assert.True(document.IsRedactionDirty);
            var ex = Assert.Throws<PlumePdfException>(() => document.SaveIncremental(sourcePath));
            Assert.Equal("PLUME5016", ex.Code);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public void Save_AfterMarkRedactionDirty_StillSucceeds()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-redact-save-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.MarkRedactionDirty();
                document.Save(outputPath);
            }

            using var reopened = PdfDocument.Open(outputPath);
            Assert.Single(reopened.Pages);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }
}
