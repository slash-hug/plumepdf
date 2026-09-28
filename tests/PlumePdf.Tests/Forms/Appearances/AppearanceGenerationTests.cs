using System.Text;
using PlumePdf.Documents;
using PlumePdf.Documents.Forms;
using PlumePdf.Documents.Forms.Appearances;
using Xunit;

namespace PlumePdf.Tests.Forms.Appearances;

/// <summary>/DA parsing, variable-text layout geometry, and end-to-end /AP generation on fill.</summary>
public class AppearanceGenerationTests
{
    [Theory]
    [InlineData("/Helv 12 Tf 0 g", "Helv", 12.0, "0 g")]
    [InlineData("0 0 1 rg /F1 0 Tf", "F1", 0.0, "0 0 1 rg")]
    [InlineData("garbage /Helv 8 Tf more garbage", "Helv", 8.0, "0 g")]
    [InlineData("no font here at all", null, 0.0, "0 g")]
    [InlineData("/Helv NaN Tf", null, 0.0, "0 g")]
    public void DefaultAppearanceParser_ExtractsFontSizeAndColor(string da, string? font, double size, string color)
    {
        var parsed = DefaultAppearanceParser.Parse(da);

        Assert.Equal(font, parsed.FontResourceName);
        Assert.Equal(size, parsed.FontSize);
        Assert.Equal(color, parsed.ColorOperators);
    }

    [Fact]
    public void VariableTextLayout_Quadding_PositionsSingleLine()
    {
        static double Measure(string s) => s.Length * 0.5; // 500/1000 units per char

        var (size, left) = VariableTextLayout.Layout("ab", Measure, 100, 20, 10, quadding: 0, multiline: false, out _);
        var (_, center) = VariableTextLayout.Layout("ab", Measure, 100, 20, 10, quadding: 1, multiline: false, out _);
        var (_, right) = VariableTextLayout.Layout("ab", Measure, 100, 20, 10, quadding: 2, multiline: false, out _);

        Assert.Equal(10, size);
        Assert.True(left[0].X < center[0].X && center[0].X < right[0].X);
        Assert.Equal(2, left[0].X); // inset
    }

    [Fact]
    public void VariableTextLayout_AutoSize_ShrinksToFitWidth()
    {
        static double Measure(string s) => s.Length * 0.5;

        var (size, _) = VariableTextLayout.Layout(new string('x', 40), Measure, 100, 50, requestedSize: 0, quadding: 0, multiline: false, out _);

        Assert.True(size * 40 * 0.5 <= 100 - 4 + 0.001, $"auto-sized text ({size}pt) must fit the available width");
        Assert.True(size >= 4, "auto-size never goes below the readable floor");
    }

    [Fact]
    public void VariableTextLayout_Multiline_WrapsAndTruncates()
    {
        static double Measure(string s) => s.Length * 0.5;

        var (_, lines) = VariableTextLayout.Layout("aaaa bbbb cccc dddd", Measure, 50, 200, 10, 0, multiline: true, out var truncated);
        Assert.True(lines.Count > 1, "narrow rect must wrap");
        Assert.False(truncated);

        var (_, few) = VariableTextLayout.Layout("aaaa bbbb cccc dddd eeee ffff", Measure, 50, 18, 10, 0, multiline: true, out var truncatedNow);
        Assert.True(truncatedNow, "a rect with room for one line must truncate");
        Assert.True(few.Count <= 2);
    }

    [Fact]
    public void Fill_TextField_GeneratesAppearanceStream()
    {
        var path = WriteTemp(FormsTestDocuments.Build());
        try
        {
            using var document = PdfDocument.Open(path);
            document.Form.Fields["Name"].Value = "Jane Q. Public";

            var model = AcroFormReader.Read(document, null).Fields.Single(f => f.ShortName == "Name");
            var widget = model.Widgets[0];
            Assert.True(widget.Dictionary.TryGetValue(AcroFormNames.AP, out var apValue));
            var ap = Assert.IsType<PdfDictionary>(apValue);
            var n = Assert.IsType<PdfReference>(ap[AcroFormNames.N]);
            var stream = Assert.IsType<PdfStream>(document.Objects[n.Target]);

            var operators = Encoding.ASCII.GetString(stream.GetDecodedBytes(PdfOptions.Default.Filters, PdfOptions.Default));
            Assert.Contains("BT", operators, StringComparison.Ordinal);
            Assert.Contains("/PlumeF0", operators, StringComparison.Ordinal);
            Assert.Contains("Tj", operators, StringComparison.Ordinal);

            // The stream is a well-formed Form XObject sized to the widget's /Rect.
            Assert.Equal("Form", ((PdfName)stream.Dictionary[PdfName.Subtype]).Value);
            Assert.True(stream.Dictionary.ContainsKey(PdfName.Get("BBox")));

            // No appearance-failure diagnostics: the real generator handled it.
            Assert.DoesNotContain(document.Diagnostics, d => d.Code == "PLUME6041");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Fill_UnencodableCharacter_FailsLoudWithCodedDiagnostic()
    {
        var path = WriteTemp(FormsTestDocuments.Build());
        try
        {
            using var document = PdfDocument.Open(path);
            document.Form.Fields["Name"].Value = "你好"; // CJK — not in Helvetica's simple encoding

            Assert.Contains(document.Diagnostics, d => d.Code == "PLUME6044");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Fill_ThenSaveIncremental_AppearanceSurvivesRoundTrip()
    {
        var path = WriteTemp(FormsTestDocuments.Build());
        var output = Path.Combine(Path.GetTempPath(), $"plumepdf-ap-roundtrip-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(path))
            {
                document.Form.Fields["Name"].Value = "Persisted";
                document.SaveIncremental(output);
            }

            using var reopened = PdfDocument.Open(output);
            Assert.Equal("Persisted", reopened.Form.Fields["Name"].Value);
            var model = AcroFormReader.Read(reopened, null).Fields.Single(f => f.ShortName == "Name");
            var widget = model.Widgets[0];
            var ap = Assert.IsType<PdfDictionary>(widget.Dictionary[AcroFormNames.AP]);
            var n = Assert.IsType<PdfReference>(ap[AcroFormNames.N]);
            var stream = Assert.IsType<PdfStream>(reopened.Objects[n.Target]);
            var operators = Encoding.ASCII.GetString(stream.GetDecodedBytes(PdfOptions.Default.Filters, PdfOptions.Default));
            Assert.Contains("Tj", operators, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(output);
        }
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-forms-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
