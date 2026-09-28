using System.Text;
using PlumePdf.Documents.Redaction;
using Xunit;

namespace PlumePdf.Tests.Redaction;

/// <summary>
/// Per-surface regressions for <see cref="MetadataScrubber"/>'s previously-missing coverage
/// (all under-redaction bugs): a <c>/Kids</c>-branched <c>/EmbeddedFiles</c> name tree (the
/// flat-only walk used to scrub NOTHING behind a branch node), the embedded <c>/EF</c> stream
/// bytes themselves (the attachment payload used to survive a "scrubbed" filespec verbatim),
/// page-level <c>/PieceInfo</c> (only the catalog's was removed), and the source document's
/// structure-tree <c>/ActualText</c>/<c>/Alt</c> values (a ratified scrub surface that had
/// no implementation).
/// </summary>
public class MetadataScrubberTests
{
    [Fact]
    public void Scrub_EmbeddedFilesNameTreeWithKidsBranch_ScrubsFilespecAndDropsAttachmentBytes()
    {
        const string payload = "ATTACHMENTPAYLOADSENTINEL77";
        using var document = RedactionTargetTests.ComposeSample("Body text, nothing special.");

        // Attachment stream + filespec.
        var streamReference = document.Objects.AllocateNumber();
        document.Objects.RegisterNew(streamReference, new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(payload)));

        var efDictionary = new PdfDictionary();
        efDictionary.Set(PdfName.Get("F"), new PdfReference(streamReference));
        var fileSpec = new PdfDictionary();
        fileSpec.Set(PdfName.Type, PdfName.Get("Filespec"));
        fileSpec.Set(PdfName.Get("F"), PdfString.FromLiteral("secret-roster.txt"u8.ToArray()));
        fileSpec.Set(PdfName.Get("UF"), PdfString.FromLiteral("secret-roster.txt"u8.ToArray()));
        fileSpec.Set(PdfName.Get("EF"), efDictionary);
        var fileSpecReference = document.Objects.AllocateNumber();
        document.Objects.RegisterNew(fileSpecReference, fileSpec);

        // A BRANCHED name tree: root carries only /Kids; the leaf node carries /Names+/Limits.
        var leaf = new PdfDictionary();
        leaf.Set(PdfName.Get("Names"), new PdfArray { PdfString.FromLiteral("secret-roster.txt"u8.ToArray()), new PdfReference(fileSpecReference) });
        leaf.Set(PdfName.Get("Limits"), new PdfArray { PdfString.FromLiteral("secret-roster.txt"u8.ToArray()), PdfString.FromLiteral("secret-roster.txt"u8.ToArray()) });
        var leafReference = document.Objects.AllocateNumber();
        document.Objects.RegisterNew(leafReference, leaf);

        var embeddedFiles = new PdfDictionary();
        embeddedFiles.Set(PdfName.Get("Kids"), new PdfArray { new PdfReference(leafReference) });
        var names = new PdfDictionary();
        names.Set(PdfName.Get("EmbeddedFiles"), embeddedFiles);

        Assert.NotNull(document.Catalog);
        document.Catalog!.Dictionary.Set(PdfName.Get("Names"), names);
        document.Objects.MarkDirty(document.Catalog.Reference);

        var result = RedactionEngine.Redact(document, [RedactionTarget.Text("secret-roster")], null);

        Assert.True(result.ScrubbedSurfaces.TryGetValue("EmbeddedFileNames", out var scrubbed));
        Assert.Equal(1, scrubbed);

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            var wholeFile = Encoding.Latin1.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("secret-roster", wholeFile, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(payload, wholeFile, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Scrub_PageLevelPieceInfo_IsRemovedAlongsideCatalogs()
    {
        using var document = RedactionTargetTests.ComposeSample("Body text, nothing special.");

        // The opportunistic /PieceInfo policy triggers once any other surface matched — give
        // it a matching DocInfo title.
        var infoReference = document.Objects.AllocateNumber();
        var info = new PdfDictionary();
        info.Set(PdfName.Get("Title"), PdfString.FromLiteral("Codename Nightjar"u8.ToArray()));
        document.Objects.RegisterNew(infoReference, info);
        document.Objects.Trailer.Set(PdfName.Info, new PdfReference(infoReference));

        var catalogPiece = new PdfDictionary();
        catalogPiece.Set(PdfName.Get("SomeApp"), new PdfDictionary());
        Assert.NotNull(document.Catalog);
        document.Catalog!.Dictionary.Set(PdfName.Get("PieceInfo"), catalogPiece);
        document.Objects.MarkDirty(document.Catalog.Reference);

        var pagePiece = new PdfDictionary();
        pagePiece.Set(PdfName.Get("SomeApp"), new PdfDictionary());
        var page = document.Pages[0];
        page.Dictionary.Set(PdfName.Get("PieceInfo"), pagePiece);
        document.Objects.MarkDirty(page.Reference);

        var result = RedactionEngine.Redact(document, [RedactionTarget.Text("Nightjar")], null);

        Assert.True(result.ScrubbedSurfaces.TryGetValue("PieceInfo", out var removed));
        Assert.Equal(2, removed); // catalog-level AND page-level
        Assert.False(document.Catalog.Dictionary.ContainsKey(PdfName.Get("PieceInfo")));
        Assert.False(page.Dictionary.ContainsKey(PdfName.Get("PieceInfo")));
    }

    [Fact]
    public void Scrub_StructureTreeActualTextAndAlt_AreScrubbedAcrossNestedElements()
    {
        const string secretActual = "SecretActualTextSentinel";
        const string secretAlt = "SecretAltTextSentinel";
        using var document = RedactionTargetTests.ComposeSample("Body text, nothing special.");

        var childReference = document.Objects.AllocateNumber();
        var child = new PdfDictionary();
        child.Set(PdfName.Get("S"), PdfName.Get("Figure"));
        child.Set(PdfName.Get("Alt"), PdfString.FromLiteral(Encoding.ASCII.GetBytes(secretAlt)));
        document.Objects.RegisterNew(childReference, child);

        var elementReference = document.Objects.AllocateNumber();
        var element = new PdfDictionary();
        element.Set(PdfName.Get("S"), PdfName.Get("P"));
        element.Set(PdfName.Get("ActualText"), PdfString.FromLiteral(Encoding.ASCII.GetBytes(secretActual)));
        element.Set(PdfName.Get("K"), new PdfArray { new PdfReference(childReference) });
        document.Objects.RegisterNew(elementReference, element);

        var rootReference = document.Objects.AllocateNumber();
        var root = new PdfDictionary();
        root.Set(PdfName.Type, PdfName.Get("StructTreeRoot"));
        root.Set(PdfName.Get("K"), new PdfReference(elementReference));
        document.Objects.RegisterNew(rootReference, root);

        Assert.NotNull(document.Catalog);
        document.Catalog!.Dictionary.Set(PdfName.Get("StructTreeRoot"), new PdfReference(rootReference));
        document.Objects.MarkDirty(document.Catalog.Reference);

        var result = RedactionEngine.Redact(document, [RedactionTarget.Text("Sentinel")], null);

        Assert.True(result.ScrubbedSurfaces.TryGetValue("StructureTree", out var scrubbed));
        Assert.Equal(2, scrubbed);

        var path = TempPdfPath();
        try
        {
            document.Save(path);
            var wholeFile = Encoding.Latin1.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain(secretActual, wholeFile, StringComparison.Ordinal);
            Assert.DoesNotContain(secretAlt, wholeFile, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Scrub_StructureTreeBeyondElementCountCap_ThrowsPlume6076()
    {
        using var document = RedactionTargetTests.ComposeSample("Body text, nothing special.");

        // A chain of struct elements longer than the cap — the scrub must refuse loudly, never
        // stop silently with part of the tree unscrubbed.
        var options = document.Options with { MaxStructureElementCount = 5 };
        var childValue = (PdfObject)PdfNumber.Get(0);
        for (var i = 0; i < 10; i++)
        {
            var reference = document.Objects.AllocateNumber();
            var element = new PdfDictionary();
            element.Set(PdfName.Get("S"), PdfName.Get("P"));
            element.Set(PdfName.Get("K"), childValue);
            document.Objects.RegisterNew(reference, element);
            childValue = new PdfReference(reference);
        }

        var rootReference = document.Objects.AllocateNumber();
        var root = new PdfDictionary();
        root.Set(PdfName.Type, PdfName.Get("StructTreeRoot"));
        root.Set(PdfName.Get("K"), childValue);
        document.Objects.RegisterNew(rootReference, root);
        Assert.NotNull(document.Catalog);
        document.Catalog!.Dictionary.Set(PdfName.Get("StructTreeRoot"), new PdfReference(rootReference));

        // Exercise the scrubber directly through a reopened document carrying the tightened
        // options (Redact's own text-target extraction would consult the structure tree first
        // under the reading-order default, muddying which cap fires).
        var path = TempPdfPath();
        try
        {
            document.Save(path);
            using var reopened = PdfDocument.Open(path, options);
            var ex = Assert.Throws<PlumePdfException>(() => MetadataScrubber.Scrub(reopened, [RedactionTarget.Text("Body")]));
            Assert.Equal("PLUME6076", ex.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-scrub-{Guid.NewGuid():N}.pdf");
}
