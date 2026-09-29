using System.Text;
using PlumePdf.Documents;
using PlumePdf.Documents.PageRemoval;
using PlumePdf.Objects;
using PlumePdf.Tests.PageRemoval;
using PlumePdf.Tests.TestSupport;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// The full-rewrite exclusion seam under the removed-page guarantee (issue #16): a reference the
/// translate callback maps to <see langword="null"/> is written as the null object; the writers'
/// invariants on the catalog and the retained pages; objects a save-time clean-up allocates for
/// one save only; and the two ways the exclusion set must not over-reach (a shared resource named
/// by a malformed <c>/Annots</c>, and a page-tree number that never resolved).
/// </summary>
public class RemovedSetWriterTests
{
    [Fact]
    public void NullTranslation_WritesTheNullObject_InDictionariesAndArrays()
    {
        var excludedRef = new PdfReference(new IndirectReference(7, 0));
        var keptRef = new PdfReference(new IndirectReference(8, 0));
        var array = new PdfArray();
        array.Add(excludedRef);
        array.Add(keptRef);
        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Get("Dest"), excludedRef);
        dictionary.Set(PdfName.Get("Kids"), array);

        using var output = new MemoryStream();
        ObjectSerializer.WriteValue(output, dictionary, reference => reference.Target.Number == 7 ? null : new IndirectReference(1, 0));
        var text = Encoding.ASCII.GetString(output.ToArray());

        Assert.Contains("/Dest null", text);
        Assert.Contains("[null 1 0 R]", text);
        Assert.DoesNotContain("7 0 R", text);
    }

    [Fact]
    public void Writer_RefusesToExcludeARetainedPage()
    {
        using var document = PdfDocument.Open(WriterTestDocuments.BuildDocument(pageCount: 2));
        var pages = document.Pages.Select(static p => (p.Reference, p.Dictionary)).ToList();
        var excluded = new HashSet<int> { pages[1].Reference.Number };

        var ex = Assert.Throws<PlumePdfException>(() =>
            FullRewriteWriter.Write(Stream.Null, document.Objects, document.Catalog!.Reference, document.Catalog.Dictionary, pages, PdfOptions.Default, excluded));
        Assert.Equal("PLUME5010", ex.Code);
    }

    [Fact]
    public void Writer_RefusesAReplacementForTheCatalog()
    {
        using var document = PdfDocument.Open(WriterTestDocuments.BuildDocument(pageCount: 1));
        var pages = document.Pages.Select(static p => (p.Reference, p.Dictionary)).ToList();
        var replacements = new Dictionary<int, PdfObject> { [document.Catalog!.Reference.Number] = new PdfDictionary() };

        var ex = Assert.Throws<PlumePdfException>(() =>
            FullRewriteWriter.Write(Stream.Null, document.Objects, document.Catalog.Reference, document.Catalog.Dictionary, pages, PdfOptions.Default, replacements: replacements));
        Assert.Equal("PLUME5010", ex.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllocatedObject_IsWrittenWhenReferenced_AndGarbageCollectedOtherwise(bool linearize)
    {
        using var document = PdfDocument.Open(WriterTestDocuments.BuildDocument(pageCount: 1, includeInfo: true));
        var pages = document.Pages.Select(static p => (p.Reference, p.Dictionary)).ToList();
        var context = new SaveCleanupContext(document.Objects, document.Catalog!.Reference, document.Catalog.Dictionary, pages, [], [], PdfOptions.Default);

        var referenced = context.Allocate(PdfString.FromLiteral(Encoding.ASCII.GetBytes("ALLOCATED-REFERENCED-MARK")));
        context.Allocate(PdfString.FromLiteral(Encoding.ASCII.GetBytes("ALLOCATED-ORPHAN-MARK")));

        // Point the (replaced) /Info at the referenced allocation; the registry is never touched.
        var infoNumber = ((PdfReference)document.Objects.Trailer[PdfName.Info]).Target.Number;
        var info = new PdfDictionary();
        info.Set(PdfName.Get("Extra"), new PdfReference(referenced));
        context.Replacements[infoNumber] = info;
        var nextBefore = document.Objects.NextFreshObjectNumber;

        using var output = new MemoryStream();
        var options = PdfOptions.Default with { Deterministic = true };
        if (linearize)
        {
            Linearizer.Write(output, document.Objects, context.CatalogReference, context.Catalog, context.Pages, options, context.Excluded, context.Replacements);
        }
        else
        {
            FullRewriteWriter.Write(output, document.Objects, context.CatalogReference, context.Catalog, context.Pages, options, context.Excluded, context.Replacements);
        }

        var text = Encoding.Latin1.GetString(output.ToArray());
        Assert.Contains("ALLOCATED-REFERENCED-MARK", text);
        Assert.DoesNotContain("ALLOCATED-ORPHAN-MARK", text);
        Assert.Equal(nextBefore, document.Objects.NextFreshObjectNumber);
    }

    [Fact]
    public void SharedFontNamedByARemovedPagesAnnots_IsNotExcluded()
    {
        // Pages 3 and 4 share font 9; the removed page 4 (malformed) lists the font in /Annots.
        var bytes = BuildPdf(new SortedDictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R /Resources << /Font << /F1 9 0 R >> >> >>",
            [4] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R /Resources << /Font << /F1 9 0 R >> >> /Annots [9 0 R] >>",
            [5] = ContentStream("BT /F1 12 Tf 20 100 Td (KEPT-PAGE-MARK) Tj ET"),
            [6] = ContentStream("BT /F1 12 Tf 20 100 Td (REMOVED-PAGE-MARK) Tj ET"),
            [9] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        });

        using var document = PdfDocument.Open(bytes);
        document.Pages.RemoveAt(1);
        var saved = SaveToBytes(document);

        var text = Encoding.Latin1.GetString(saved);
        Assert.Contains("/BaseFont /Helvetica", text);
        Assert.DoesNotContain("REMOVED-PAGE-MARK", text);
        using var reopened = PdfDocument.Open(saved);
        Assert.Contains("KEPT-PAGE-MARK", reopened.Pages[0].ExtractText().Text);
    }

    [Fact]
    public void PageTreeNodes_OnlyRecordResolvedNodes()
    {
        // /Kids names object 99, which the file does not contain (it resolves to null): a number
        // the registry may later hand out for a new object must never be excluded.
        var bytes = BuildPdf(new SortedDictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R 99 0 R] /Count 2 >>",
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        using var document = PdfDocument.Open(bytes);

        Assert.Contains(2, document.OpenTimePageTree);
        Assert.Contains(3, document.OpenTimePageTree);
        Assert.DoesNotContain(99, document.OpenTimePageTree);
    }

    // Two pages; page 3 is kept, page 4 is removed. Extra objects are merged in.
    private static SortedDictionary<int, string> TwoPages(string catalogExtras, string keptAnnots, string removedAnnots, SortedDictionary<int, string> extra)
    {
        var objects = new SortedDictionary<int, string>
        {
            [1] = $"<< /Type /Catalog /Pages 2 0 R {catalogExtras} >>",
            [2] = "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            [3] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R /Resources << /Font << /F1 9 0 R >> >> {keptAnnots} >>",
            [4] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R /Resources << /Font << /F1 9 0 R >> >> {removedAnnots} >>",
            [5] = ContentStream("BT /F1 12 Tf 20 100 Td (KEPT-PAGE-MARK) Tj ET"),
            [6] = ContentStream("BT /F1 12 Tf 20 100 Td (REMOVED-PAGE-MARK) Tj ET"),
            [9] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        foreach (var (number, body) in extra)
        {
            objects[number] = body;
        }

        return objects;
    }

    private static byte[] RemoveSecondPageAndSave(byte[] bytes, SaveLayout layout)
    {
        using var document = PdfDocument.Open(bytes);
        document.Pages.RemoveAt(1);
        return RemovedPageFixtures.SaveToBytes(document, layout);
    }

    public static TheoryData<SaveLayout> Layouts => new(SaveLayout.Save, SaveLayout.Optimize, SaveLayout.Linearize);

    [Theory]
    [MemberData(nameof(Layouts))]
    public void FieldValueSharedWithAKeptField_Survives(SaveLayout layout)
    {
        // Fields 20 (widget on the removed page) and 21 (widget on the kept page) share value 30.
        var bytes = BuildPdf(TwoPages("/AcroForm << /Fields [20 0 R 21 0 R] >>", "/Annots [21 0 R]", "/Annots [20 0 R]", new()
        {
            [20] = "<< /FT /Tx /T (A) /V 30 0 R /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 4 0 R >>",
            [21] = "<< /FT /Tx /T (B) /V 30 0 R /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 3 0 R >>",
            [30] = "(SHARED-VALUE-TEXT)",
        }));

        var saved = RemoveSecondPageAndSave(bytes, layout);

        Assert.True(RecoveredBytes.AnyRecoveredObjectContains(saved, "SHARED-VALUE-TEXT"));
        Assert.False(RecoveredBytes.AnyRecoveredObjectContains(saved, "REMOVED-PAGE-MARK"));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void WidgetWithKidsOnARemovedPage_KeepsTheFieldOfItsKeptKid(SaveLayout layout)
    {
        // Widget 20 is listed on the removed page but is also a field whose kid 21 is on the kept page.
        var bytes = BuildPdf(TwoPages("/AcroForm << /Fields [20 0 R] >>", "/Annots [21 0 R]", "/Annots [20 0 R]", new()
        {
            [20] = "<< /FT /Tx /T (A) /V (PARENT-WIDGET-VALUE) /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 4 0 R /Kids [21 0 R] >>",
            [21] = "<< /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 3 0 R /Parent 20 0 R >>",
        }));

        var saved = RemoveSecondPageAndSave(bytes, layout);

        Assert.True(RecoveredBytes.AnyRecoveredObjectContains(saved, "PARENT-WIDGET-VALUE"));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void AnnotationReachedOnlyThroughAnObjectReference_OnARemovedPage_IsExcluded(SaveLayout layout)
    {
        // Annotation 50 is on the removed page but listed in no /Annots; only a structure element's
        // object reference (/Pg 4 0 R) reaches it.
        var bytes = BuildPdf(TwoPages("/StructTreeRoot 40 0 R /MarkInfo << /Marked true >>", "", "", new()
        {
            [40] = "<< /Type /StructTreeRoot /K [41 0 R] >>",
            [41] = "<< /Type /StructElem /S /Link /P 40 0 R /ActualText (STRUCT-ON-REMOVED) /K << /Type /OBJR /Obj 50 0 R /Pg 4 0 R >> >>",
            [50] = "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /Contents (ORPHAN-ANNOT-SECRET) /P 4 0 R >>",
        }));

        var saved = RemoveSecondPageAndSave(bytes, layout);

        Assert.False(RecoveredBytes.AnyRecoveredObjectContains(saved, "ORPHAN-ANNOT-SECRET"));
        Assert.False(RecoveredBytes.AnyRecoveredObjectContains(saved, "STRUCT-ON-REMOVED"));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void StructureElementWithNoContent_WhosePgIsARemovedPage_IsExcluded(SaveLayout layout)
    {
        var bytes = BuildPdf(TwoPages("/StructTreeRoot 40 0 R /MarkInfo << /Marked true >>", "", "", new()
        {
            [40] = "<< /Type /StructTreeRoot /K [41 0 R 42 0 R] >>",
            [41] = "<< /Type /StructElem /S /Figure /P 40 0 R /Alt (NOKIDS-ON-REMOVED) /Pg 4 0 R >>",
            [42] = "<< /Type /StructElem /S /P /P 40 0 R /ActualText (KEPT-STRUCT-TEXT) /Pg 3 0 R /K 0 >>",
        }));

        var saved = RemoveSecondPageAndSave(bytes, layout);

        Assert.False(RecoveredBytes.AnyRecoveredObjectContains(saved, "NOKIDS-ON-REMOVED"));
        Assert.True(RecoveredBytes.AnyRecoveredObjectContains(saved, "KEPT-STRUCT-TEXT"));
    }

    [Fact]
    public void SignatureFieldRemovedWithItsPage_IsNotCountedAsInvalidated()
    {
        var bytes = BuildPdf(TwoPages("/AcroForm << /Fields [20 0 R] /SigFlags 3 >>", "", "/Annots [20 0 R]", new()
        {
            [20] = "<< /FT /Sig /T (Sig1) /V 60 0 R /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 4 0 R >>",
            [60] = "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 30] /Contents <00> >>",
        }));
        var strict = PdfOptions.Default with { Strict = true };

        // The signature is recognised: kept, a full rewrite refuses under Strict.
        using (var intact = PdfDocument.Open(bytes))
        {
            var path = Path.Combine(Path.GetTempPath(), $"plumepdf-sig-{Guid.NewGuid():N}.pdf");
            var refused = Assert.Throws<PlumePdfException>(() => intact.Save(path, strict));
            Assert.Equal("PLUME5014", refused.Code);
        }

        // Removed with its page, it is not written at all, so it is not "invalidated".
        using var document = PdfDocument.Open(bytes);
        document.Pages.RemoveAt(1);
        var output = Path.Combine(Path.GetTempPath(), $"plumepdf-sig-{Guid.NewGuid():N}.pdf");
        try
        {
            document.Save(output, strict);
            Assert.DoesNotContain(document.Diagnostics, static d => d.Code == "PLUME5014");
            Assert.False(RecoveredBytes.AnyRecoveredObjectContains(File.ReadAllBytes(output), "ByteRange"));
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static string ContentStream(string content) => $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";

    private static byte[] SaveToBytes(PdfDocument document)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-removed-set-{Guid.NewGuid():N}.pdf");
        try
        {
            document.Save(path, PdfOptions.Default with { Deterministic = true });
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildPdf(SortedDictionary<int, string> objects)
    {
        var output = new StringBuilder("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();
        foreach (var (number, body) in objects)
        {
            offsets[number] = output.Length;
            output.Append($"{number} 0 obj\n{body}\nendobj\n");
        }

        var size = objects.Keys.Max() + 1;
        var xref = output.Length;
        output.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        for (var number = 1; number < size; number++)
        {
            output.Append(offsets.TryGetValue(number, out var offset) ? $"{offset:D10} 00000 n \n" : "0000000000 65535 f \n");
        }

        output.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(output.ToString());
    }
}
