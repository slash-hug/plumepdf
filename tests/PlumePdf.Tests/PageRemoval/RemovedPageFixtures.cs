using System.Globalization;
using System.Text;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>What a <see cref="FixtureMarker"/> stands for inside a removed-page fixture.</summary>
public enum FixtureMarkerKind
{
    /// <summary>Text in a page's own (uncompressed) content stream.</summary>
    PageContent,

    /// <summary>An annotation's <c>/Contents</c> string.</summary>
    AnnotationText,

    /// <summary>A form field's value (<c>/V</c>), or a signature dictionary reached through one.</summary>
    FieldValue,

    /// <summary>A structure element's <c>/ActualText</c>.</summary>
    StructureText,

    /// <summary>An outline item's <c>/Title</c>.</summary>
    OutlineTitle,

    /// <summary>The <c>/Contents</c> of a link annotation on a kept page whose destination is another page.</summary>
    LinkText,
}

/// <summary>
/// One unique ASCII marker planted in a fixture. <see cref="OwnerPages"/> are the zero-based
/// page indexes the marker belongs to: it is removed content exactly when <em>every</em> owner
/// page is removed, and an empty list means document-level content that is never removed.
/// </summary>
public sealed record FixtureMarker(string Text, FixtureMarkerKind Kind, IReadOnlyList<int> OwnerPages)
{
    /// <summary>Whether removing the pages at <paramref name="removedPageIndexes"/> makes this marker removed content.</summary>
    public bool IsRemovedBy(IReadOnlyCollection<int> removedPageIndexes) =>
        OwnerPages.Count > 0 && OwnerPages.All(removedPageIndexes.Contains);
}

/// <summary>The three full-rewrite save layouts every page-removal suite runs.</summary>
public enum SaveLayout
{
    /// <summary>A plain <see cref="PdfDocument.Save"/> (classic cross-reference table).</summary>
    Save,

    /// <summary><see cref="PdfOptions.Optimize"/>: object streams plus a cross-reference stream.</summary>
    Optimize,

    /// <summary><see cref="PdfOptions.Linearize"/>: ISO 32000-1 Annex F fast web view.</summary>
    Linearize,
}

/// <summary>A built fixture: its shape letter, its bytes and the markers planted in it.</summary>
public sealed record RemovedPageFixture(string Shape, byte[] Bytes, IReadOnlyList<FixtureMarker> Markers, bool IsFormShape);

/// <summary>
/// Byte-composed three-page documents for the page-removal confidentiality suites, one per
/// shape of cross-reference into a removed page. Written directly from ISO 32000-1's
/// object/xref-table grammar like <see cref="WriterTestDocuments"/>, independent of PlumePDF's
/// own writer (the thing under test).
/// </summary>
/// <remarks>
/// <para>
/// Every fixture has page objects 3, 4 and 5 (page indexes 0, 1 and 2), each with its own
/// uncompressed content stream carrying one marker (<see cref="PageMarkers"/>), plus a shared
/// Helvetica font (object 9). The default removal is page index 1 (object 4); the second
/// removal set is indexes 1 and 2, which includes the last page. Field values, annotation
/// text, <c>/ActualText</c> and outline titles carry their own markers, listed on
/// <see cref="RemovedPageFixture.Markers"/>.
/// </para>
/// <para>
/// Shapes: <c>none</c> (no cross-references); <c>A</c> outline <c>/Dest</c> → removed page;
/// <c>G</c> outline <c>/A</c> GoTo; <c>D</c> named destination via <c>/Dests</c> plus a kept-page
/// link using it; <c>M</c> named destination via the <c>/Names /Dests</c> tree with
/// <c>/Kids</c> and <c>/Limits</c>; <c>B</c> merged field/widget on the removed page; <c>F</c>
/// separate field and widget, widget on the removed page; <c>K</c> one field with widgets on a
/// kept and a removed page; <c>W</c> widget with <c>/P</c> → removed page listed in no
/// <c>/Annots</c>; <c>R</c> radio group with <c>/Opt</c> split across a kept and a removed page;
/// <c>L</c> kept-page link → removed page; <c>O</c> catalog <c>/OpenAction</c> → removed page;
/// <c>P</c> <c>/Popup</c> from a kept-page annotation into a removed page's annotation;
/// <c>S</c> one link listed in the <c>/Annots</c> of a kept and a removed page; <c>X</c> an
/// indirect <c>/Annots</c> array on the removed page, whose annotation a kept-page reply
/// reaches through <c>/IRT</c>; <c>T</c> <c>/ActualText</c> on a structure element of the
/// removed page; <c>N</c> non-flat page tree with inherited <c>/Resources</c>/<c>/MediaBox</c>;
/// <c>I</c> an integer-index destination; <c>Q</c> a signature field on the removed page plus
/// <c>/Perms /DocMDP</c> → its <c>/V</c>; <c>E1</c>/<c>E2</c>/<c>E3</c> an outline, named-
/// destination tree and structure tree whose every entry targets the removed page; <c>C</c>
/// K + R + A + T + L in one document.
/// </para>
/// </remarks>
public static class RemovedPageFixtures
{
    /// <summary>Every shape, in a stable order.</summary>
    public static IReadOnlyList<string> AllShapes { get; } =
        ["none", "A", "G", "D", "M", "B", "F", "K", "W", "R", "L", "O", "P", "S", "X", "T", "N", "I", "Q", "E1", "E2", "E3", "C"];

    /// <summary>The shapes that carry an AcroForm.</summary>
    public static IReadOnlyList<string> FormShapes { get; } = ["B", "F", "K", "W", "R", "Q", "C"];

    /// <summary>Every shape without an AcroForm.</summary>
    public static IReadOnlyList<string> NonFormShapes { get; } = [.. AllShapes.Where(static s => !FormShapes.Contains(s))];

    /// <summary>
    /// The two removal sets, as one-based page numbers readable in test names: the middle page
    /// (<c>"2"</c>), and the middle plus the last page (<c>"2+3"</c>).
    /// </summary>
    public static IReadOnlyList<string> Removals { get; } = ["2", "2+3"];

    /// <summary>The content-stream marker of each page, by zero-based page index.</summary>
    public static IReadOnlyList<string> PageMarkers { get; } = ["PAGE-ONE-MARK", "PAGE-TWO-MARK", "PAGE-THREE-MARK"];

    /// <summary>The zero-based page indexes a <see cref="Removals"/> entry names.</summary>
    public static int[] PageIndexes(string removal) =>
        [.. removal.Split('+').Select(static p => int.Parse(p, CultureInfo.InvariantCulture) - 1)];

    /// <summary>Removes the pages at <paramref name="pageIndexes"/> (indexes into the original order).</summary>
    public static void RemovePages(PdfDocument document, IEnumerable<int> pageIndexes)
    {
        foreach (var index in pageIndexes.OrderByDescending(static i => i))
        {
            document.Pages.RemoveAt(index);
        }
    }

    /// <summary>Deterministic options for <paramref name="layout"/>.</summary>
    public static PdfOptions OptionsFor(SaveLayout layout) => layout switch
    {
        SaveLayout.Save => PdfOptions.Default with { Deterministic = true },
        SaveLayout.Optimize => PdfOptions.Default with { Deterministic = true, Optimize = true },
        SaveLayout.Linearize => PdfOptions.Default with { Deterministic = true, Linearize = true },
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    /// <summary>Saves <paramref name="document"/> in <paramref name="layout"/> through a temp file and returns the bytes.</summary>
    public static byte[] SaveToBytes(PdfDocument document, SaveLayout layout)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-removed-page-{Guid.NewGuid():N}.pdf");
        try
        {
            document.Save(path, OptionsFor(layout));
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Builds the fixture for <paramref name="shape"/> (one of <see cref="AllShapes"/>).</summary>
    public static RemovedPageFixture Build(string shape)
    {
        var c = new Composer();
        switch (shape)
        {
            case "none":
                break;

            case "A":
                Outline(c, 20, [(21, "OUTLINE-TO-TWO-MARK", 1, "/Dest [4 0 R /Fit]"), (22, "OUTLINE-TO-ONE-MARK", 0, "/Dest [3 0 R /Fit]")]);
                break;

            case "G":
                Outline(c, 20, [(21, "GOTO-OUTLINE-TWO-MARK", 1, "/A << /S /GoTo /D [4 0 R /Fit] >>"), (22, "GOTO-OUTLINE-ONE-MARK", 0, "/A << /S /GoTo /D [3 0 R /Fit] >>")]);
                break;

            case "D":
                c.CatalogEntries.Add("/Dests 60 0 R");
                c.Objects[60] = "<< /two [4 0 R /Fit] /one [3 0 R /Fit] >>";
                Outline(c, 20, [(21, "NAMED-OUTLINE-TWO-MARK", 1, "/Dest /two")]);
                Link(c, 40, "/Dest /two", "NAMED-LINK-TWO-MARK", 1);
                break;

            case "M":
                c.CatalogEntries.Add("/Names << /Dests 61 0 R >>");
                c.Objects[61] = "<< /Kids [62 0 R 63 0 R] >>";
                c.Objects[62] = "<< /Names [(one) [3 0 R /Fit]] /Limits [(one) (one)] >>";
                c.Objects[63] = "<< /Names [(two) << /D [4 0 R /Fit] >>] /Limits [(two) (two)] >>";
                Link(c, 40, "/Dest (two)", "NAMETREE-LINK-TWO-MARK", 1);
                break;

            case "B":
                c.Fields.Add(30);
                c.PageAnnots[1].Add(30);
                c.Objects[30] = "<< /FT /Tx /T (secret) /V (FIELD-VALUE-MARK) /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /P 4 0 R >>";
                c.Mark("FIELD-VALUE-MARK", FixtureMarkerKind.FieldValue, 1);
                break;

            case "F":
                c.Fields.Add(30);
                c.PageAnnots[1].Add(31);
                c.Objects[30] = "<< /FT /Tx /T (secret) /V (FIELD-VALUE-MARK) /Kids [31 0 R] >>";
                c.Objects[31] = "<< /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /P 4 0 R /Parent 30 0 R >>";
                c.Mark("FIELD-VALUE-MARK", FixtureMarkerKind.FieldValue, 1);
                break;

            case "K":
                FieldAcrossPages(c, 30, 31, 32);
                break;

            case "W":
                c.Fields.Add(30);
                c.Objects[30] = "<< /FT /Tx /T (secret) /V (FIELD-VALUE-MARK) /Kids [31 0 R] >>";
                c.Objects[31] = "<< /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /P 4 0 R /Parent 30 0 R >>";
                c.Mark("FIELD-VALUE-MARK", FixtureMarkerKind.FieldValue, 1);
                break;

            case "R":
                RadioGroup(c, 30, 31, 32, 13);
                break;

            case "L":
                Link(c, 40, "/Dest [4 0 R /Fit]", "LINK-TO-TWO-MARK", 1);
                break;

            case "O":
                c.CatalogEntries.Add("/OpenAction [4 0 R /Fit]");
                break;

            case "P":
                c.PageAnnots[0].Add(41);
                c.PageAnnots[1].Add(42);
                c.Objects[41] = "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /Contents (KEPT-NOTE-MARK) /Popup 42 0 R /P 3 0 R >>";
                c.Objects[42] = "<< /Type /Annot /Subtype /Popup /Rect [0 0 100 100] /Parent 41 0 R /P 4 0 R /Open true /Contents (REMOVED-POPUP-MARK) >>";
                c.Mark("KEPT-NOTE-MARK", FixtureMarkerKind.AnnotationText, 0);
                c.Mark("REMOVED-POPUP-MARK", FixtureMarkerKind.AnnotationText, 1);
                break;

            case "S":
                c.PageAnnots[0].Add(40);
                c.PageAnnots[1].Add(40);
                c.Objects[40] = "<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /Dest [3 0 R /Fit] /Contents (SHARED-LINK-MARK) >>";
                c.Mark("SHARED-LINK-MARK", FixtureMarkerKind.AnnotationText, 0, 1);
                break;

            case "X":
                c.IndirectAnnots[1] = 43;
                c.Objects[43] = "[44 0 R]";
                c.Objects[44] = "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /Contents (INDIRECT-ANNOT-MARK) /P 4 0 R >>";
                c.PageAnnots[0].Add(41);
                c.Objects[41] = "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /Contents (KEPT-REPLY-MARK) /IRT 44 0 R /P 3 0 R >>";
                c.Mark("INDIRECT-ANNOT-MARK", FixtureMarkerKind.AnnotationText, 1);
                c.Mark("KEPT-REPLY-MARK", FixtureMarkerKind.AnnotationText, 0);
                break;

            case "T":
                StructureTree(c, 50, [(51, 1, "PAGE-TWO-ACTUALTEXT-MARK"), (52, 0, "PAGE-ONE-ACTUALTEXT-MARK")]);
                break;

            case "N":
                c.NonFlat = true;
                break;

            case "I":
                Outline(c, 20, [(21, "INDEX-OUTLINE-MARK", -1, "/Dest [1 /Fit]")]);
                break;

            case "Q":
                c.Fields.Add(30);
                c.AcroFormExtras = " /SigFlags 3";
                c.CatalogEntries.Add("/Perms << /DocMDP 33 0 R >>");
                c.PageAnnots[1].Add(30);
                c.Objects[30] = "<< /FT /Sig /T (signature) /V 33 0 R /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /F 132 /P 4 0 R >>";
                c.Objects[33] =
                    "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /Name (SIGNATURE-VALUE-MARK) " +
                    "/M (D:20260101000000Z) /ByteRange [0 0 0 0] /Contents <0000000000000000> " +
                    "/Reference [<< /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams /P 2 /V /1.2 >> >>] >>";
                c.Mark("SIGNATURE-VALUE-MARK", FixtureMarkerKind.FieldValue, 1);
                break;

            case "E1":
                Outline(c, 20, [(21, "EMPTIED-OUTLINE-A-MARK", 1, "/Dest [4 0 R /Fit]"), (22, "EMPTIED-OUTLINE-B-MARK", 1, "/A << /S /GoTo /D [4 0 R /XYZ 0 200 0] >>")]);
                break;

            case "E2":
                c.CatalogEntries.Add("/Names << /Dests 61 0 R >>");
                c.Objects[61] = "<< /Kids [62 0 R] >>";
                c.Objects[62] = "<< /Names [(alpha) [4 0 R /Fit] (beta) << /D [4 0 R /XYZ 0 200 0] >>] /Limits [(alpha) (beta)] >>";
                break;

            case "E3":
                StructureTree(c, 50, [(51, 1, "EMPTIED-STRUCT-A-MARK"), (52, 1, "EMPTIED-STRUCT-B-MARK")]);
                break;

            case "C":
                FieldAcrossPages(c, 30, 31, 32);
                RadioGroup(c, 33, 34, 35, 13);
                Outline(c, 20, [(21, "OUTLINE-TO-TWO-MARK", 1, "/Dest [4 0 R /Fit]"), (22, "OUTLINE-TO-ONE-MARK", 0, "/Dest [3 0 R /Fit]")]);
                StructureTree(c, 50, [(51, 1, "PAGE-TWO-ACTUALTEXT-MARK"), (52, 0, "PAGE-ONE-ACTUALTEXT-MARK")]);
                Link(c, 40, "/Dest [4 0 R /Fit]", "LINK-TO-TWO-MARK", 1);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown removed-page fixture shape.");
        }

        return new RemovedPageFixture(shape, c.Compose(), c.Markers, FormShapes.Contains(shape));
    }

    // Outline items, chained in order under root `root`. `page` is the owner page index of the
    // title marker, or -1 for a title that is never removed content.
    private static void Outline(Composer c, int root, (int Number, string Title, int Page, string Destination)[] items)
    {
        c.CatalogEntries.Add($"/Outlines {root} 0 R");
        c.Objects[root] = $"<< /Type /Outlines /First {items[0].Number} 0 R /Last {items[^1].Number} 0 R /Count {items.Length} >>";
        for (var i = 0; i < items.Length; i++)
        {
            var prev = i > 0 ? $" /Prev {items[i - 1].Number} 0 R" : string.Empty;
            var next = i < items.Length - 1 ? $" /Next {items[i + 1].Number} 0 R" : string.Empty;
            c.Objects[items[i].Number] = $"<< /Title ({items[i].Title}) /Parent {root} 0 R{prev}{next} {items[i].Destination} >>";
            if (items[i].Page >= 0)
            {
                c.Mark(items[i].Title, FixtureMarkerKind.OutlineTitle, items[i].Page);
            }
            else
            {
                c.Mark(items[i].Title, FixtureMarkerKind.OutlineTitle);
            }
        }
    }

    // A link on kept page index 0 whose destination is page `targetPage`.
    private static void Link(Composer c, int number, string destination, string marker, int targetPage)
    {
        c.PageAnnots[0].Add(number);
        c.Objects[number] = $"<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] {destination} /Contents ({marker}) >>";
        c.Mark(marker, FixtureMarkerKind.LinkText, targetPage);
    }

    // One text field whose widgets sit on page index 0 (kept) and page index 1 (removed).
    private static void FieldAcrossPages(Composer c, int field, int keptWidget, int removedWidget)
    {
        c.Fields.Add(field);
        c.PageAnnots[0].Add(keptWidget);
        c.PageAnnots[1].Add(removedWidget);
        c.Objects[field] = $"<< /FT /Tx /T (shared) /V (KEPT-FIELD-VALUE-MARK) /Kids [{keptWidget} 0 R {removedWidget} 0 R] >>";
        c.Objects[keptWidget] = $"<< /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /P 3 0 R /Parent {field} 0 R >>";
        c.Objects[removedWidget] = $"<< /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /P 4 0 R /Parent {field} 0 R /Contents (REMOVED-WIDGET-MARK) >>";
        c.Mark("KEPT-FIELD-VALUE-MARK", FixtureMarkerKind.FieldValue, 0, 1);
        c.Mark("REMOVED-WIDGET-MARK", FixtureMarkerKind.AnnotationText, 1);
    }

    // A radio group: `/Opt` aligned with `/Kids`, the kept widget on page index 0, the removed
    // widget (holding the group's current on-state `/1`) on page index 1.
    private static void RadioGroup(Composer c, int field, int keptWidget, int removedWidget, int appearance)
    {
        c.Fields.Add(field);
        c.PageAnnots[0].Add(keptWidget);
        c.PageAnnots[1].Add(removedWidget);
        c.Objects[field] = $"<< /FT /Btn /Ff 32768 /T (radio) /V /1 /Kids [{keptWidget} 0 R {removedWidget} 0 R] /Opt [(alpha) (beta)] >>";
        c.Objects[keptWidget] = $"<< /Type /Annot /Subtype /Widget /Rect [10 10 30 30] /P 3 0 R /Parent {field} 0 R /AS /Off /AP << /N << /0 {appearance} 0 R /Off {appearance} 0 R >> >> >>";
        c.Objects[removedWidget] = $"<< /Type /Annot /Subtype /Widget /Rect [10 10 30 30] /P 4 0 R /Parent {field} 0 R /AS /1 /AP << /N << /1 {appearance} 0 R /Off {appearance} 0 R >> >> >>";
        c.Objects[appearance] = "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 0 >>\nstream\n\nendstream";
    }

    // A structure tree whose elements each mark one page's MCID 0 and carry an /ActualText marker.
    private static void StructureTree(Composer c, int root, (int Number, int Page, string ActualText)[] elements)
    {
        c.CatalogEntries.Add($"/StructTreeRoot {root} 0 R /MarkInfo << /Marked true >>");
        var pages = elements.Select(static e => e.Page).Distinct().Order().ToList();
        var nums = new StringBuilder();
        foreach (var page in pages)
        {
            c.StructParents[page] = page;
            c.MarkedContent[page] = true;
            var onPage = string.Join(" ", elements.Where(e => e.Page == page).Select(static e => $"{e.Number} 0 R"));
            nums.Append($" {page} [{onPage}]");
        }

        var kids = string.Join(" ", elements.Select(static e => $"{e.Number} 0 R"));
        c.Objects[root] = $"<< /Type /StructTreeRoot /K [{kids}] /ParentTree << /Nums [{nums.ToString().Trim()}] >> /ParentTreeNextKey 3 >>";
        foreach (var (number, page, actualText) in elements)
        {
            c.Objects[number] = $"<< /Type /StructElem /S /P /P {root} 0 R /Pg {3 + page} 0 R /ActualText ({actualText}) /K [<< /Type /MCR /Pg {3 + page} 0 R /MCID 0 >>] >>";
            c.Mark(actualText, FixtureMarkerKind.StructureText, page);
        }
    }

    private sealed class Composer
    {
        private static readonly byte[] Header = Encoding.Latin1.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");

        public SortedDictionary<int, string> Objects { get; } = [];

        public List<string> CatalogEntries { get; } = [];

        public List<int> Fields { get; } = [];

        public string AcroFormExtras { get; set; } = string.Empty;

        public List<int>[] PageAnnots { get; } = [[], [], []];

        // A page index → the number of an indirect /Annots array object for it.
        public Dictionary<int, int> IndirectAnnots { get; } = [];

        public int?[] StructParents { get; } = new int?[3];

        public bool[] MarkedContent { get; } = new bool[3];

        public bool NonFlat { get; set; }

        public List<FixtureMarker> Markers { get; } = [];

        public void Mark(string text, FixtureMarkerKind kind, params int[] ownerPages)
        {
            if (!Markers.Exists(m => m.Text == text))
            {
                Markers.Add(new FixtureMarker(text, kind, ownerPages));
            }
        }

        public byte[] Compose()
        {
            for (var i = 0; i < 3; i++)
            {
                Mark(PageMarkers[i], FixtureMarkerKind.PageContent, i);
            }

            var catalog = new StringBuilder("<< /Type /Catalog /Pages 2 0 R");
            foreach (var entry in CatalogEntries)
            {
                catalog.Append(' ').Append(entry);
            }

            if (Fields.Count > 0)
            {
                catalog.Append($" /AcroForm << /Fields [{References(Fields)}]{AcroFormExtras} >>");
            }

            catalog.Append(" >>");
            Objects[1] = catalog.ToString();

            if (NonFlat)
            {
                // Pages 3 and 4 sit under intermediate node 6, which carries the inherited
                // /Resources; the root carries the inherited /MediaBox.
                Objects[2] = "<< /Type /Pages /Kids [6 0 R 5 0 R] /Count 3 /MediaBox [0 0 300 300] >>";
                Objects[6] = "<< /Type /Pages /Parent 2 0 R /Kids [3 0 R 4 0 R] /Count 2 /Resources << /Font << /F1 9 0 R >> >> >>";
            }
            else
            {
                Objects[2] = "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>";
            }

            for (var i = 0; i < 3; i++)
            {
                var number = 3 + i;
                var extras = new StringBuilder();
                if (IndirectAnnots.TryGetValue(i, out var annotsArray))
                {
                    extras.Append($" /Annots {annotsArray} 0 R");
                }
                else if (PageAnnots[i].Count > 0)
                {
                    extras.Append($" /Annots [{References(PageAnnots[i])}]");
                }

                if (StructParents[i] is int key)
                {
                    extras.Append($" /StructParents {key}");
                }

                Objects[number] = NonFlat && i < 2
                    ? $"<< /Type /Page /Parent 6 0 R /Contents {10 + i} 0 R{extras} >>"
                    : $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents {10 + i} 0 R /Resources << /Font << /F1 9 0 R >> >>{extras} >>";

                var text = $"BT /F1 12 Tf 20 100 Td ({PageMarkers[i]}) Tj ET";
                var content = MarkedContent[i] ? $"/P << /MCID 0 >> BDC {text} EMC" : text;
                Objects[10 + i] = $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
            }

            Objects[9] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

            var buffer = new List<byte>(Header);
            var offsets = new Dictionary<int, int>();
            foreach (var (number, body) in Objects)
            {
                offsets[number] = buffer.Count;
                buffer.AddRange(Encoding.Latin1.GetBytes($"{number} 0 obj\n{body}\nendobj\n"));
            }

            var size = Objects.Keys.Max() + 1;
            var xrefOffset = buffer.Count;
            var xref = new StringBuilder($"xref\n0 {size}\n0000000000 65535 f \n");
            for (var n = 1; n < size; n++)
            {
                xref.Append(offsets.TryGetValue(n, out var offset) ? $"{offset:D10} 00000 n \n" : "0000000000 65535 f \n");
            }

            xref.Append($"trailer\n<< /Size {size} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF\n");
            buffer.AddRange(Encoding.Latin1.GetBytes(xref.ToString()));
            return [.. buffer];
        }

        private static string References(IEnumerable<int> numbers) => string.Join(" ", numbers.Select(static n => $"{n} 0 R"));
    }
}
