namespace PlumePdf.Tests.PageAdd;

/// <summary>Small hand-built PDFs for the page-add tests: each page shows one line of text, so a copied page can be recognised by extracting it.</summary>
internal static class PageAddFixtures
{
    /// <summary>
    /// A document with one page per entry of <paramref name="texts"/>. With
    /// <paramref name="inheritResources"/> the font lives on the <c>/Pages</c> node and the pages
    /// inherit it; with <paramref name="catalogExtra"/> more catalog entries are added.
    /// </summary>
    public static byte[] Text(string[] texts, bool inheritResources = false, string catalogExtra = "", (int Number, string Body)[]? extra = null)
    {
        const string Font = "<< /Font << /F1 3 0 R >> >>";
        var objects = new List<(int, string)>
        {
            (1, $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>"),
            (3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
        };

        var kids = new List<string>();
        for (var i = 0; i < texts.Length; i++)
        {
            var pageNumber = 10 + (2 * i);
            var contentNumber = pageNumber + 1;
            var content = $"BT /F1 12 Tf 20 50 Td ({texts[i]}) Tj ET";
            objects.Add((pageNumber, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] {(inheritResources ? "" : "/Resources " + Font)} /Contents {contentNumber} 0 R >>"));
            objects.Add((contentNumber, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"));
            kids.Add($"{pageNumber} 0 R");
        }

        objects.Add((2, $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {texts.Length} {(inheritResources ? "/Resources " + Font : "")} >>"));
        objects.AddRange(extra ?? []);
        return PdfFixtureBuilder.BuildClassicXrefPdf([.. objects.OrderBy(static o => o.Item1)], rootObjectNumber: 1);
    }

    /// <summary>The text on every page of <paramref name="document"/>, in order.</summary>
    public static List<string> PageTexts(PdfDocument document) => [.. document.Pages.Select(static p => p.ExtractText().Text.Trim())];

    /// <summary>A fresh temp path for a saved PDF; delete it when done.</summary>
    public static string TempPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-pageadd-{Guid.NewGuid():N}.pdf");

    /// <summary>Writes <paramref name="bytes"/> to a fresh temp path and returns it.</summary>
    public static string WriteTemp(byte[] bytes)
    {
        var path = TempPath();
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
