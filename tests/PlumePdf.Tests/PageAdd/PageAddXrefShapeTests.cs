using Xunit;
using static PlumePdf.Tests.PageAdd.PageAddFixtures;

namespace PlumePdf.Tests.PageAdd;

/// <summary>
/// Page-add followed by <c>SaveIncremental</c> on every cross-reference shape a destination can
/// have: a classic table, an xref stream with object streams, a linearized file, a hybrid file,
/// and a file that already has a <c>/Prev</c> chain. The output reopens with the added page and
/// nothing to recover.
/// </summary>
public class PageAddXrefShapeTests
{
    public static TheoryData<string> Shapes() => ["classic", "xref-stream", "linearized", "prev-chain", "hybrid"];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void AnIncrementalAdd_ReopensCleanly(string shape)
    {
        var input = Destination(shape);
        var output = TempPath();
        try
        {
            int before;
            using (var document = PdfDocument.Open(input))
            using (var source = PdfDocument.Open(Text(["New"])))
            {
                before = document.Pages.Count;
                document.Pages.Insert(1, source.Pages[0]);
                document.Pages.Add(source.Pages[0]);
                document.SaveIncremental(output);
            }

            using var reopened = PdfDocument.Open(output);
            Assert.DoesNotContain(reopened.Diagnostics, static d => d.Severity >= DiagnosticSeverity.Warning);
            Assert.Equal(before + 2, reopened.Pages.Count);
            Assert.Equal("New", reopened.Pages[1].ExtractText().Text.Trim());
            Assert.Equal("New", reopened.Pages[^1].ExtractText().Text.Trim());
        }
        finally
        {
            if (shape != "hybrid")
            {
                File.Delete(input);
            }

            File.Delete(output);
        }
    }

    private static string Destination(string shape)
    {
        if (shape == "hybrid")
        {
            return Fixture("hybrid.pdf");
        }

        var plain = WriteTemp(Text(["One", "Two"]));
        if (shape == "classic")
        {
            return plain;
        }

        var output = TempPath();
        try
        {
            using var document = PdfDocument.Open(plain);
            switch (shape)
            {
                case "xref-stream":
                    document.Save(output, new PdfOptions { Optimize = true });
                    break;
                case "linearized":
                    document.Save(output, new PdfOptions { Linearize = true });
                    break;
                default: // prev-chain: one incremental revision already appended
                    document.Pages.Move(1, 0);
                    document.SaveIncremental(output);
                    break;
            }
        }
        finally
        {
            File.Delete(plain);
        }

        return output;
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
