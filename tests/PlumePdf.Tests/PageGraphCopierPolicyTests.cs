using System.Text;
using PlumePdf.Documents;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <see cref="ImportPolicy"/>: what the page copier leaves out when a page is added to an opened
/// document (<see cref="ImportPolicy.PageAdd"/>) — structure-tree back-pointers, article beads and
/// signature values, none of which mean anything in the destination — and that
/// <see cref="ImportPolicy.Merge"/> keeps every one of them, as Merge always has.
/// </summary>
public class PageGraphCopierPolicyTests
{
    private static readonly PdfName StructParents = PdfName.Get("StructParents");
    private static readonly PdfName StructParent = PdfName.Get("StructParent");
    private static readonly PdfName Beads = PdfName.Get("B");
    private static readonly PdfName Annots = PdfName.Get("Annots");
    private static readonly PdfName Resources = PdfName.Get("Resources");
    private static readonly PdfName XObjectName = PdfName.Get("XObject");
    private static readonly PdfName Value = PdfName.Get("V");

    [Fact]
    public void PageAdd_StripsStructParentsFromThePage()
    {
        var copy = CopyFirstPage(ImportPolicy.PageAdd);

        Assert.False(copy.Page.ContainsKey(StructParents));
    }

    [Fact]
    public void PageAdd_StripsStructParentFromAnnotations()
    {
        var copy = CopyFirstPage(ImportPolicy.PageAdd);

        Assert.All(copy.Annotations, static annotation => Assert.False(annotation.ContainsKey(StructParent)));
    }

    [Fact]
    public void PageAdd_StripsStructParentsFromFormXObjectsAndStructParentFromImageXObjects()
    {
        var copy = CopyFirstPage(ImportPolicy.PageAdd);

        Assert.False(copy.XObject("Fm").ContainsKey(StructParents));
        Assert.False(copy.XObject("Im").ContainsKey(StructParent));
    }

    [Fact]
    public void PageAdd_DropsArticleBeads()
    {
        var copy = CopyFirstPage(ImportPolicy.PageAdd);

        Assert.False(copy.Page.ContainsKey(Beads));
    }

    [Fact]
    public void PageAdd_DropsTheValueOfACopiedSignatureField()
    {
        var copy = CopyFirstPage(ImportPolicy.PageAdd);

        var signatureField = Assert.Single(copy.Annotations, static a => a.TryGetValue(PdfName.Get("FT"), out var ft) && ft is PdfName { Value: "Sig" });
        Assert.False(signatureField.ContainsKey(Value));
    }

    [Fact]
    public void PageAdd_KeepsTheValueOfOtherFields()
    {
        var copy = CopyFirstPage(ImportPolicy.PageAdd);

        var textField = Assert.Single(copy.Annotations, static a => a.TryGetValue(PdfName.Get("FT"), out var ft) && ft is PdfName { Value: "Tx" });
        Assert.True(textField.ContainsKey(Value));
    }

    [Fact]
    public void Merge_KeepsEveryOneOfThem()
    {
        var copy = CopyFirstPage(ImportPolicy.Merge);

        Assert.True(copy.Page.ContainsKey(StructParents));
        Assert.True(copy.Page.ContainsKey(Beads));
        Assert.All(copy.Annotations, static annotation => Assert.True(annotation.ContainsKey(StructParent)));
        Assert.True(copy.XObject("Fm").ContainsKey(StructParents));
        Assert.True(copy.XObject("Im").ContainsKey(StructParent));
        var signatureField = Assert.Single(copy.Annotations, static a => a.TryGetValue(PdfName.Get("FT"), out var ft) && ft is PdfName { Value: "Sig" });
        Assert.True(signatureField.ContainsKey(Value));
    }

    private static CopiedPage CopyFirstPage(ImportPolicy policy)
    {
        using var document = PdfDocument.Open(BuildSource());
        var page = document.Pages[0];
        var source = new ImportSource(document, 0);
        var imports = new List<(ImportSource, IndirectReference, PdfDictionary)> { (source, page.Reference, page.Dictionary) };

        var target = new FreshDocumentTarget();
        var copier = new PageGraphCopier(target, PageGraphCopier.Prepare(imports), policy);
        var pageReference = copier.ReservePage(source, page.Reference);
        copier.Drain();

        return new CopiedPage(target.Objects, (PdfDictionary)target.Objects[pageReference.Number]);
    }

    private sealed record CopiedPage(Dictionary<int, PdfObject> Objects, PdfDictionary Page)
    {
        public IEnumerable<PdfDictionary> Annotations =>
            ((PdfArray)Page[Annots]).Select(entry => (PdfDictionary)Objects[((PdfReference)entry).Target.Number]);

        public PdfDictionary XObject(string name)
        {
            var xobjects = (PdfDictionary)((PdfDictionary)Page[Resources])[XObjectName];
            return ((PdfStream)Objects[((PdfReference)xobjects[PdfName.Get(name)]).Target.Number]).Dictionary;
        }
    }

    /// <summary>
    /// Two pages; the first carries every key the page-add policy strips: <c>/StructParents</c>,
    /// an article bead, a link and a text field with <c>/StructParent</c>, a signed signature field,
    /// and a Form XObject (<c>/StructParents</c>) and image XObject (<c>/StructParent</c>).
    /// </summary>
    private static byte[] BuildSource()
    {
        var objects = new SortedDictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [9 0 R 11 0 R] >> >>",
            [2] = "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /StructParents 0 /B [7 0 R] "
                + "/Resources << /XObject << /Fm 12 0 R /Im 13 0 R >> >> /Contents 5 0 R /Annots [8 0 R 9 0 R 11 0 R] >>",
            [4] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 6 0 R >>",
            [7] = "<< /Type /Bead /P 3 0 R /R [0 0 100 100] >>",
            [8] = "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /StructParent 1 /A << /S /URI /URI (https://example.com) >> >>",
            [9] = "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [0 0 50 20] /P 3 0 R /StructParent 2 /V 10 0 R >>",
            [10] = "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 10] /Contents <00> >>",
            [11] = "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [0 30 50 50] /P 3 0 R /StructParent 3 /V (Ada) >>",
        };
        var streams = new SortedDictionary<int, (string Dictionary, string Data)>
        {
            [5] = ("<<", "q /Fm Do Q q 10 0 0 10 0 0 cm /Im Do Q"),
            [6] = ("<<", "0 0 10 10 re f"),
            [12] = ("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /StructParents 4", "0 0 5 5 re f"),
            [13] = ("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /StructParent 5", "\u0080"),
        };

        var buffer = new List<byte>(Encoding.ASCII.GetBytes("%PDF-1.7\n"));
        var offsets = new Dictionary<int, int>();
        foreach (var number in objects.Keys.Concat(streams.Keys).Order())
        {
            offsets[number] = buffer.Count;
            var body = objects.TryGetValue(number, out var plain)
                ? plain
                : $"{streams[number].Dictionary} /Length {streams[number].Data.Length} >>\nstream\n{streams[number].Data}\nendstream";
            buffer.AddRange(Encoding.Latin1.GetBytes($"{number} 0 obj\n{body}\nendobj\n"));
        }

        var size = offsets.Keys.Max() + 1;
        var xref = buffer.Count;
        var table = new StringBuilder($"xref\n0 {size}\n0000000000 65535 f \n");
        for (var n = 1; n < size; n++)
        {
            table.Append(offsets.TryGetValue(n, out var offset) ? $"{offset:D10} 00000 n \n" : "0000000000 00000 f \n");
        }

        buffer.AddRange(Encoding.ASCII.GetBytes($"{table}trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF"));
        return [.. buffer];
    }
}
