using System.Collections;
using PlumePdf.Documents.Structure;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Structure;

/// <summary><see cref="NumberTreeBuilder"/> and the <see cref="StructureTreeBuilder"/>/<see cref="StructureTreeReader"/> write→read round trip.</summary>
public class StructureTreeTests
{
    [Fact]
    public void NumberTreeBuilder_Build_SortsKeysAscending_AndSetsLimits()
    {
        var entries = new List<(int Key, PdfObject Value)>
        {
            (5, PdfNumber.Get(500)),
            (1, PdfNumber.Get(100)),
            (3, PdfNumber.Get(300)),
        };

        var objects = new Dictionary<int, PdfObject>();
        var next = 1;
        var reference = NumberTreeBuilder.Build(entries, value =>
        {
            var r = new IndirectReference(next++, 0);
            objects[r.Number] = value;
            return r;
        });

        var dictionary = Assert.IsType<PdfDictionary>(objects[reference.Number]);
        var nums = Assert.IsType<PdfArray>(dictionary[PdfName.Get("Nums")]);

        // [1, 100, 3, 300, 5, 500] - ascending by key, per ISO 32000-1 §7.9.7.
        Assert.Equal(6, nums.Count);
        Assert.Equal(1, ((PdfNumber)nums[0]).Value);
        Assert.Equal(100, ((PdfNumber)nums[1]).Value);
        Assert.Equal(3, ((PdfNumber)nums[2]).Value);
        Assert.Equal(5, ((PdfNumber)nums[4]).Value);

        var limits = Assert.IsType<PdfArray>(dictionary[PdfName.Get("Limits")]);
        Assert.Equal(1, ((PdfNumber)limits[0]).Value);
        Assert.Equal(5, ((PdfNumber)limits[1]).Value);
    }

    [Fact]
    public void NumberTreeBuilder_Build_EmptyEntries_NoLimits()
    {
        var objects = new Dictionary<int, PdfObject>();
        var next = 1;
        var reference = NumberTreeBuilder.Build([], value =>
        {
            var r = new IndirectReference(next++, 0);
            objects[r.Number] = value;
            return r;
        });

        var dictionary = Assert.IsType<PdfDictionary>(objects[reference.Number]);
        Assert.Empty((PdfArray)dictionary[PdfName.Get("Nums")]);
        Assert.False(dictionary.ContainsKey(PdfName.Get("Limits")));
    }

    [Fact]
    public void NumberTreeBuilder_Build_OverMaxEntries_ThrowsPlume6064()
    {
        // A fake IReadOnlyList whose Count alone reports over the cap: proves the guard fires
        // off Count before any enumeration, without actually allocating a million-plus tuples.
        var ex = Assert.Throws<PlumePdfException>(() => NumberTreeBuilder.Build(new OversizedFakeList(), static v => throw new InvalidOperationException("should never allocate")));
        Assert.Equal("PLUME6064", ex.Code);
    }

    private sealed class OversizedFakeList : IReadOnlyList<(int Key, PdfObject Value)>
    {
        public int Count => NumberTreeBuilder.MaxEntries + 1;

        public (int Key, PdfObject Value) this[int index] => throw new NotSupportedException();

        public IEnumerator<(int Key, PdfObject Value)> GetEnumerator() => throw new NotSupportedException();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Build_ThenRead_RoundTripsRoleLanguageAltActualTextAndMarkedContent()
    {
        var mcr = new MarkedContentReference { PageIndex = 0, Mcid = 0 };
        var paragraph = new StructureElement { Role = "P", Language = "en-US", ActualText = "replacement text" };
        paragraph.Children.Add(mcr);

        var figure = new MarkedContentReference { PageIndex = 0, Mcid = 1 };
        var image = new StructureElement { Role = "Figure", AlternateText = "A small red square" };
        image.Children.Add(figure);

        var root = new StructureElement { Role = "Document" };
        root.Children.Add(paragraph);
        root.Children.Add(image);

        using var document = BuildOnePageDocument(root, out var expectedPageRef);

        var read = StructureTreeReader.Read(document, PdfOptions.Default, new DiagnosticCollection());

        Assert.True(read.IsMarked);
        Assert.NotNull(read.Root);
        Assert.Equal("Document", read.Root!.Role);
        Assert.Equal(2, read.Root.Children.Count);

        var readParagraph = Assert.IsType<StructureElement>(read.Root.Children[0]);
        Assert.Equal("P", readParagraph.Role);
        Assert.Equal("en-US", readParagraph.Language);
        Assert.Equal("replacement text", readParagraph.ActualText);
        var readMcr = Assert.IsType<MarkedContentReference>(readParagraph.Children[0]);
        Assert.Equal(0, readMcr.PageIndex);
        Assert.Equal(0, readMcr.Mcid);

        var readImage = Assert.IsType<StructureElement>(read.Root.Children[1]);
        Assert.Equal("Figure", readImage.Role);
        Assert.Equal("A small red square", readImage.AlternateText);
        var readFigureMcr = Assert.IsType<MarkedContentReference>(readImage.Children[0]);
        Assert.Equal(1, readFigureMcr.Mcid);

        Assert.Equal(expectedPageRef, document.Pages[0].Reference);
    }

    [Fact]
    public void Build_TableHeaderCell_RoundTripsScopeAttribute()
    {
        var mcr = new MarkedContentReference { PageIndex = 0, Mcid = 0 };
        var th = new StructureElement { Role = "TH", TableHeaderScope = "Column" };
        th.Children.Add(mcr);
        var tr = new StructureElement { Role = "TR" };
        tr.Children.Add(th);
        var table = new StructureElement { Role = "Table" };
        table.Children.Add(tr);
        var root = new StructureElement { Role = "Document" };
        root.Children.Add(table);

        using var document = BuildOnePageDocument(root, out _);
        var read = StructureTreeReader.Read(document, PdfOptions.Default, new DiagnosticCollection());

        var readTable = Assert.IsType<StructureElement>(read.Root!.Children[0]);
        var readTr = Assert.IsType<StructureElement>(readTable.Children[0]);
        var readTh = Assert.IsType<StructureElement>(readTr.Children[0]);
        Assert.Equal("TH", readTh.Role);
        Assert.Equal("Column", readTh.TableHeaderScope);
    }

    [Fact]
    public void Build_MarkedContentReferencesOutOfPageRange_ThrowsPlume6067()
    {
        var mcr = new MarkedContentReference { PageIndex = 5, Mcid = 0 };
        var element = new StructureElement { Role = "P" };
        element.Children.Add(mcr);
        var root = new StructureElement { Role = "Document" };
        root.Children.Add(element);

        var objects = new Dictionary<int, PdfObject>();
        var next = 1;
        IndirectReference Reserve() => new(next++, 0);
        void SetObj(IndirectReference r, PdfObject v) => objects[r.Number] = v;

        var pageRef = Reserve();
        var ex = Assert.Throws<PlumePdfException>(() => StructureTreeBuilder.Build(root, [pageRef], PdfOptions.Default, Reserve, SetObj));
        Assert.Equal("PLUME6067", ex.Code);
    }

    [Fact]
    public void Build_ExceedsMaxDepth_ThrowsPlume6066()
    {
        var leaf = new StructureElement { Role = "P" };
        leaf.Children.Add(new MarkedContentReference { PageIndex = 0, Mcid = 0 });
        StructureElement current = leaf;
        for (var i = 0; i < 10; i++)
        {
            var wrapper = new StructureElement { Role = "Div" };
            wrapper.Children.Add(current);
            current = wrapper;
        }

        var objects = new Dictionary<int, PdfObject>();
        var next = 1;
        IndirectReference Reserve() => new(next++, 0);
        void SetObj(IndirectReference r, PdfObject v) => objects[r.Number] = v;
        var pageRef = Reserve();

        var shallow = PdfOptions.Default with { MaxStructureTreeDepth = 3 };
        var ex = Assert.Throws<PlumePdfException>(() => StructureTreeBuilder.Build(current, [pageRef], shallow, Reserve, SetObj));
        Assert.Equal("PLUME6066", ex.Code);
        Assert.Contains("MaxStructureTreeDepth", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ExceedsMaxStructureElementCount_ThrowsPlume6065()
    {
        var root = new StructureElement { Role = "Document" };
        for (var i = 0; i < 5; i++)
        {
            var p = new StructureElement { Role = "P" };
            p.Children.Add(new MarkedContentReference { PageIndex = 0, Mcid = i });
            root.Children.Add(p);
        }

        var objects = new Dictionary<int, PdfObject>();
        var next = 1;
        IndirectReference Reserve() => new(next++, 0);
        void SetObj(IndirectReference r, PdfObject v) => objects[r.Number] = v;
        var pageRef = Reserve();

        var tight = PdfOptions.Default with { MaxStructureElementCount = 3 };
        var ex = Assert.Throws<PlumePdfException>(() => StructureTreeBuilder.Build(root, [pageRef], tight, Reserve, SetObj));
        Assert.Equal("PLUME6065", ex.Code);
        Assert.Contains("MaxStructureElementCount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ExceedsMaxStructureElementCount_TruncatesWithPlume6069()
    {
        var root = new StructureElement { Role = "Document" };
        for (var i = 0; i < 6; i++)
        {
            var p = new StructureElement { Role = "P" };
            p.Children.Add(new MarkedContentReference { PageIndex = 0, Mcid = i });
            root.Children.Add(p);
        }

        using var document = BuildOnePageDocument(root, out _);
        var diagnostics = new DiagnosticCollection();
        var read = StructureTreeReader.Read(document, PdfOptions.Default with { MaxStructureElementCount = 3 }, diagnostics);

        Assert.NotNull(read.Root);
        Assert.Contains(diagnostics, static d => d.Code == "PLUME6069" && d.Message.Contains("MaxStructureElementCount", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_ExceedsMaxStructureTreeDepth_TruncatesWithPlume6069()
    {
        var leaf = new StructureElement { Role = "P" };
        leaf.Children.Add(new MarkedContentReference { PageIndex = 0, Mcid = 0 });
        StructureElement current = leaf;
        for (var i = 0; i < 8; i++)
        {
            var wrapper = new StructureElement { Role = "Div" };
            wrapper.Children.Add(current);
            current = wrapper;
        }

        using var document = BuildOnePageDocument(current, out _);
        var diagnostics = new DiagnosticCollection();
        var read = StructureTreeReader.Read(document, PdfOptions.Default with { MaxStructureTreeDepth = 3 }, diagnostics);

        Assert.Contains(diagnostics, static d => d.Code == "PLUME6069" && d.Message.Contains("MaxStructureTreeDepth", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_HostileOutOfRangeMcrMcid_SkipsWithPlume6068()
    {
        // /MCR /MCID 1e20 once raw-cast to int.MinValue silently; the reader now skips it
        // leniently with a diagnostic (PdfNumber.TryToInt32, the read-side convention).
        var objects = new Dictionary<int, PdfObject>();
        var next = 1;
        IndirectReference Reserve() => new(next++, 0);
        void SetObj(IndirectReference r, PdfObject v) => objects[r.Number] = v;

        var pageRef = Reserve();
        var pagesRef = Reserve();
        SetObj(pageRef, MinimalPageDictionary(pagesRef));

        var pagesDict = new PdfDictionary();
        pagesDict.Set(PdfName.Type, PdfName.Get("Pages"));
        pagesDict.Set(PdfName.Get("Kids"), new PdfArray([new PdfReference(pageRef)]));
        pagesDict.Set(PdfName.Get("Count"), PdfNumber.Get(1));
        SetObj(pagesRef, pagesDict);

        var mcrDict = new PdfDictionary();
        mcrDict.Set(PdfName.Type, PdfName.Get("MCR"));
        mcrDict.Set(PdfName.Get("Pg"), new PdfReference(pageRef));
        mcrDict.Set(PdfName.Get("MCID"), PdfNumber.Get(1e20));

        var elementDict = new PdfDictionary();
        elementDict.Set(PdfName.Type, PdfName.Get("StructElem"));
        elementDict.Set(PdfName.Get("S"), PdfName.Get("P"));
        elementDict.Set(PdfName.Get("K"), mcrDict);
        var elementRef = Reserve();
        SetObj(elementRef, elementDict);

        var rootDict = new PdfDictionary();
        rootDict.Set(PdfName.Type, PdfName.Get("StructTreeRoot"));
        rootDict.Set(PdfName.Get("K"), new PdfReference(elementRef));
        var rootRef = Reserve();
        SetObj(rootRef, rootDict);

        var catalogRef = Reserve();
        var catalogDict = new PdfDictionary();
        catalogDict.Set(PdfName.Type, PdfName.Get("Catalog"));
        catalogDict.Set(PdfName.Get("Pages"), new PdfReference(pagesRef));
        catalogDict.Set(PdfName.Get("StructTreeRoot"), new PdfReference(rootRef));
        SetObj(catalogRef, catalogDict);

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(next));
        trailer.Set(PdfName.Root, new PdfReference(catalogRef));

        using var document = PdfDocument.CreateSynthetic(new InMemoryObjectSource(trailer, objects));
        var diagnostics = new DiagnosticCollection();
        var read = StructureTreeReader.Read(document, PdfOptions.Default, diagnostics);

        var paragraph = Assert.IsType<StructureElement>(read.Root);
        Assert.Empty(paragraph.Children); // the hostile MCR was skipped, never wrapped to int.MinValue
        Assert.Contains(diagnostics, static d => d.Code == "PLUME6068");
    }

    [Fact]
    public void Read_NoStructTreeRoot_ReturnsNullRootButPreservesMarkInfo()
    {
        var objects = new Dictionary<int, PdfObject>();
        var next = 1;
        IndirectReference Reserve() => new(next++, 0);
        void SetObj(IndirectReference r, PdfObject v) => objects[r.Number] = v;

        var pageRef = Reserve();
        var pagesRef = Reserve();
        SetObj(pageRef, MinimalPageDictionary(pagesRef));

        var pagesDict = new PdfDictionary();
        pagesDict.Set(PdfName.Type, PdfName.Get("Pages"));
        pagesDict.Set(PdfName.Get("Kids"), new PdfArray([new PdfReference(pageRef)]));
        pagesDict.Set(PdfName.Get("Count"), PdfNumber.Get(1));
        SetObj(pagesRef, pagesDict);

        var catalogRef = Reserve();
        var catalogDict = new PdfDictionary();
        catalogDict.Set(PdfName.Type, PdfName.Get("Catalog"));
        catalogDict.Set(PdfName.Get("Pages"), new PdfReference(pagesRef));
        SetObj(catalogRef, catalogDict);

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(next));
        trailer.Set(PdfName.Root, new PdfReference(catalogRef));

        using var document = PdfDocument.CreateSynthetic(new InMemoryObjectSource(trailer, objects));
        var read = StructureTreeReader.Read(document, PdfOptions.Default, new DiagnosticCollection());

        Assert.False(read.IsMarked);
        Assert.Null(read.Root);
    }

    private static PdfDocument BuildOnePageDocument(StructureElement documentRoot, out IndirectReference pageRef)
    {
        var objects = new Dictionary<int, PdfObject>();
        var next = 1;
        IndirectReference Reserve() => new(next++, 0);
        void SetObj(IndirectReference r, PdfObject v) => objects[r.Number] = v;

        var localPageRef = Reserve();
        var pagesRef = Reserve();
        SetObj(localPageRef, MinimalPageDictionary(pagesRef));

        var result = StructureTreeBuilder.Build(documentRoot, [localPageRef], PdfOptions.Default, Reserve, SetObj);

        var pagesDict = new PdfDictionary();
        pagesDict.Set(PdfName.Type, PdfName.Get("Pages"));
        pagesDict.Set(PdfName.Get("Kids"), new PdfArray([new PdfReference(localPageRef)]));
        pagesDict.Set(PdfName.Get("Count"), PdfNumber.Get(1));
        SetObj(pagesRef, pagesDict);

        var catalogRef = Reserve();
        var catalogDict = new PdfDictionary();
        catalogDict.Set(PdfName.Type, PdfName.Get("Catalog"));
        catalogDict.Set(PdfName.Get("Pages"), new PdfReference(pagesRef));
        catalogDict.Set(PdfName.Get("StructTreeRoot"), new PdfReference(result.StructTreeRootReference));
        catalogDict.Set(PdfName.Get("MarkInfo"), result.MarkInfoDictionary);
        SetObj(catalogRef, catalogDict);

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(next));
        trailer.Set(PdfName.Root, new PdfReference(catalogRef));

        pageRef = localPageRef;
        return PdfDocument.CreateSynthetic(new InMemoryObjectSource(trailer, objects));
    }

    private static PdfDictionary MinimalPageDictionary(IndirectReference parentPagesRef)
    {
        var pageDict = new PdfDictionary();
        pageDict.Set(PdfName.Type, PdfName.Get("Page"));
        pageDict.Set(PdfName.Get("Parent"), new PdfReference(parentPagesRef));
        pageDict.Set(PdfName.Get("MediaBox"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(612), PdfNumber.Get(792)]));
        return pageDict;
    }
}
