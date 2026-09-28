using System.Text;
using PlumePdf.Content;
using PlumePdf.Elements;
using Xunit;

namespace PlumePdf.Tests.Structure;

/// <summary><see cref="ContentStreamBuilder"/>'s <c>BDC</c>/<c>BMC</c>/<c>EMC</c> marked-content emission and its round trip through <see cref="ContentStreamReader"/>.</summary>
public class MarkedContentTests
{
    [Fact]
    public void BeginMarkedContent_EndMarkedContent_IsByteExact()
    {
        var bytes = new ContentStreamBuilder().BeginMarkedContent("Span").EndMarkedContent().Build();
        Assert.Equal("/Span BMC\nEMC\n", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void BeginTaggedContent_EndMarkedContent_IsByteExact()
    {
        var bytes = new ContentStreamBuilder().BeginTaggedContent("P", 0).EndMarkedContent().Build();
        Assert.Equal("/P <</MCID 0>> BDC\nEMC\n", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void BeginArtifact_EndMarkedContent_IsByteExact()
    {
        var bytes = new ContentStreamBuilder().BeginArtifact().EndMarkedContent().Build();
        Assert.Equal("/Artifact BMC\nEMC\n", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void MarkedContent_NestsAroundOrdinaryOperators()
    {
        var bytes = new ContentStreamBuilder()
            .BeginTaggedContent("P", 3)
            .BeginText()
            .ShowText("Hi"u8)
            .EndText()
            .EndMarkedContent()
            .Build();

        Assert.Equal("/P <</MCID 3>> BDC\nBT\n(Hi) Tj\nET\nEMC\n", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void EndMarkedContent_WithNoOpenSequence_ThrowsPlume7016()
    {
        var ex = Assert.Throws<PlumePdfException>(() => new ContentStreamBuilder().EndMarkedContent());
        Assert.Equal("PLUME7016", ex.Code);
    }

    [Fact]
    public void Build_UnclosedMarkedContent_ThrowsPlume7017()
    {
        var ex = Assert.Throws<PlumePdfException>(() => new ContentStreamBuilder().BeginTaggedContent("P", 0).Build());
        Assert.Equal("PLUME7017", ex.Code);
    }

    [Fact]
    public void BeginTaggedContent_NegativeMcid_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentStreamBuilder().BeginTaggedContent("P", -1));
    }

    [Fact]
    public void RoundTrip_ThroughContentStreamReader_RecoversTagAndMcid()
    {
        var bytes = new ContentStreamBuilder()
            .BeginTaggedContent("H1", 7)
            .BeginText()
            .ShowText("Title"u8)
            .EndText()
            .EndMarkedContent()
            .Build();

        var ops = ContentStreamReader.Read(bytes, PdfOptions.Default, diagnostics: null);

        Assert.Equal("BDC", ops[0].Operator);
        Assert.Equal(2, ops[0].Operands.Count);
        var tag = Assert.IsType<PdfName>(ops[0].Operands[0]);
        Assert.Equal("H1", tag.Value);
        var properties = Assert.IsType<PdfDictionary>(ops[0].Operands[1]);
        var mcid = Assert.IsType<PdfNumber>(properties[PdfName.Get("MCID")]);
        Assert.Equal(7, mcid.Value);

        Assert.Equal("BT", ops[1].Operator);
        Assert.Equal("Tj", ops[2].Operator);
        Assert.Equal("ET", ops[3].Operator);
        Assert.Equal("EMC", ops[4].Operator);
    }

    [Fact]
    public void RoundTrip_Artifact_RecoversBmcTag()
    {
        var bytes = new ContentStreamBuilder().BeginArtifact().EndMarkedContent().Build();
        var ops = ContentStreamReader.Read(bytes, PdfOptions.Default, diagnostics: null);

        Assert.Equal("BMC", ops[0].Operator);
        var tag = Assert.IsType<PdfName>(ops[0].Operands[0]);
        Assert.Equal("Artifact", tag.Value);
        Assert.Equal("EMC", ops[1].Operator);
    }
}

/// <summary><see cref="Layout.ManuscriptRenderer"/> walking element roles into marked content and feeding <see cref="PlumePdf.Documents.Structure.StructureTreeBuilder"/>.</summary>
public class TaggedOutputTests
{
    [Fact]
    public void Render_LanguageUnset_ProducesNoTaggingAtAll()
    {
        var manuscript = new Manuscript
        {
            Sections = [new Section { Body = new Text("Hello, untagged world.") }],
        };

        using var document = manuscript.Render();
        var structure = PdfStructureInfo.For(document);

        Assert.False(structure.IsTagged);
        Assert.Null(structure.Root);

        var content = PdfContentTestHelper.GetPageText(document, 0);
        Assert.DoesNotContain("BDC", content);
        Assert.DoesNotContain("MCID", content);
    }

    [Fact]
    public void Render_LanguageSet_ProducesTaggedTreeMatchingElements()
    {
        var manuscript = new Manuscript
        {
            Language = "en-US",
            Title = "Structure Report",
            Sections =
            [
                new Section
                {
                    Body = new Column(
                        new Text("Quarterly Report") { HeadingLevel = 1 },
                        new Text("A plain paragraph.")),
                },
            ],
        };

        using var document = manuscript.Render();
        var structure = PdfStructureInfo.For(document);

        Assert.True(structure.IsTagged);
        Assert.Equal("en-US", structure.Language);
        var root = Assert.IsType<PdfStructureElement>(structure.Root);
        Assert.Equal("Document", root.Role);
        Assert.Equal(2, root.Children.Count);

        var heading = Assert.IsType<PdfStructureElement>(root.Children[0]);
        Assert.Equal("H1", heading.Role);
        Assert.IsType<PdfMarkedContentReference>(heading.Children[0]);

        var paragraph = Assert.IsType<PdfStructureElement>(root.Children[1]);
        Assert.Equal("P", paragraph.Role);

        Assert.Equal("Structure Report", document.GetInfo().Title);

        var content = PdfContentTestHelper.GetPageText(document, 0);
        Assert.Contains("/H1 <</MCID 0>> BDC", content);
        Assert.Contains("/P <</MCID 1>> BDC", content);
    }

    [Fact]
    public void Render_ImageWithAltText_TagsAsFigure()
    {
        var manuscript = new Manuscript
        {
            Language = "en-US",
            Sections = [new Section { Body = new Image(SmallRgb(), 2, 2) { AltText = "A tiny swatch" } }],
        };

        using var document = manuscript.Render();
        var structure = PdfStructureInfo.For(document);

        var root = Assert.IsType<PdfStructureElement>(structure.Root);
        var figure = Assert.IsType<PdfStructureElement>(root.Children[0]);
        Assert.Equal("Figure", figure.Role);
        Assert.Equal("A tiny swatch", figure.AlternateText);
    }

    [Fact]
    public void Render_ImageWithoutAltTextWhileTagged_ThrowsPlume9010()
    {
        var manuscript = new Manuscript
        {
            Language = "en-US",
            Sections = [new Section { Body = new Image(SmallRgb(), 2, 2) }],
        };

        var ex = Assert.Throws<PlumePdfException>(() => manuscript.Render());
        Assert.Equal("PLUME9010", ex.Code);
        Assert.Contains("Figure", ex.Message);
    }

    [Fact]
    public void Render_ImageMarkedArtifact_SkipsAltTextRequirement()
    {
        var manuscript = new Manuscript
        {
            Language = "en-US",
            Sections = [new Section { Body = new Image(SmallRgb(), 2, 2) { Role = "Artifact" } }],
        };

        using var document = manuscript.Render();
        var structure = PdfStructureInfo.For(document);

        // The image is excluded from the structure tree entirely (marked /Artifact instead).
        var root = Assert.IsType<PdfStructureElement>(structure.Root);
        Assert.Empty(root.Children);

        var content = PdfContentTestHelper.GetPageText(document, 0);
        Assert.Contains("/Artifact BMC", content);
    }

    [Fact]
    public void Render_TableWithHeaderRow_TagsTableTrThThTd()
    {
        var manuscript = new Manuscript
        {
            Language = "en-US",
            Sections =
            [
                new Section
                {
                    Body = new Table
                    {
                        Columns = [TableColumn.Relative(1), TableColumn.Relative(1)],
                        HeaderRow = [new Text("Item"), new Text("Qty")],
                        Rows = [[new Text("Widget"), new Text("3")]],
                    },
                },
            ],
        };

        using var document = manuscript.Render();
        var structure = PdfStructureInfo.For(document);

        var root = Assert.IsType<PdfStructureElement>(structure.Root);
        var table = Assert.IsType<PdfStructureElement>(root.Children[0]);
        Assert.Equal("Table", table.Role);
        Assert.Equal(2, table.Children.Count); // header TR + one body TR

        var headerRow = Assert.IsType<PdfStructureElement>(table.Children[0]);
        Assert.Equal("TR", headerRow.Role);
        var th = Assert.IsType<PdfStructureElement>(headerRow.Children[0]);
        Assert.Equal("TH", th.Role);
        Assert.Equal("Column", th.TableHeaderScope);

        var bodyRow = Assert.IsType<PdfStructureElement>(table.Children[1]);
        var td = Assert.IsType<PdfStructureElement>(bodyRow.Children[0]);
        Assert.Equal("TD", td.Role);
    }

    [Fact]
    public void Render_WatermarkAndStamp_AreArtifactsExcludedFromStructureTree()
    {
        var manuscript = new Manuscript
        {
            Language = "en-US",
            Sections =
            [
                new Section
                {
                    Body = new Text("Body content."),
                    Watermark = new Watermark { Text = "DRAFT" },
                    Stamps = [new Stamp { Text = "CONFIDENTIAL" }],
                },
            ],
        };

        using var document = manuscript.Render();
        var structure = PdfStructureInfo.For(document);

        // Only the body Text becomes a structure element - the watermark/stamp never do.
        var root = Assert.IsType<PdfStructureElement>(structure.Root);
        Assert.Single(root.Children);
        Assert.Equal("P", ((PdfStructureElement)root.Children[0]).Role);

        var content = PdfContentTestHelper.GetPageText(document, 0);
        Assert.Contains("/Artifact BMC", content);
    }

    [Fact]
    public void Render_RowWithExplicitRole_WrapsChildrenUnderContainerNode()
    {
        var manuscript = new Manuscript
        {
            Language = "en-US",
            Sections = [new Section { Body = new Row(new Text("Item one")) { Role = "L" } }],
        };

        using var document = manuscript.Render();
        var structure = PdfStructureInfo.For(document);

        var root = Assert.IsType<PdfStructureElement>(structure.Root);
        var list = Assert.IsType<PdfStructureElement>(root.Children[0]);
        Assert.Equal("L", list.Role);
        Assert.Single(list.Children);
        Assert.Equal("P", ((PdfStructureElement)list.Children[0]).Role);
    }

    [Fact]
    public void Render_SavedAndReopened_StructureTreeStillReadsBack()
    {
        var manuscript = new Manuscript
        {
            Language = "en-US",
            Sections = [new Section { Body = new Text("Persisted heading") { HeadingLevel = 2 } }],
        };

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-tagged-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = manuscript.Render())
            {
                document.Save(path);
            }

            using var reopened = PdfDocument.Open(path);
            var structure = PdfStructureInfo.For(reopened);

            Assert.True(structure.IsTagged);
            var root = Assert.IsType<PdfStructureElement>(structure.Root);
            Assert.Equal("H2", ((PdfStructureElement)root.Children[0]).Role);
            Assert.Empty(structure.Diagnostics);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] SmallRgb()
    {
        var pixels = new byte[2 * 2 * 3];
        Array.Fill(pixels, (byte)128);
        return pixels;
    }
}

/// <summary>Byte-identical double-render of a tagged document.</summary>
public class TaggedDocumentDeterminismTests
{
    [Fact]
    public void Render_TaggedManuscript_TwiceWithDeterministicOption_ProducesByteIdenticalOutput()
    {
        Manuscript BuildManuscript() => new()
        {
            Language = "en-US",
            Title = "Determinism Check",
            Sections =
            [
                new Section
                {
                    Header = new Text("Report") { HeadingLevel = 1 },
                    Body = new Column(
                        new Text("First paragraph."),
                        new Table
                        {
                            Columns = [TableColumn.Relative(1), TableColumn.Relative(1)],
                            HeaderRow = [new Text("A"), new Text("B")],
                            Rows = [[new Text("1"), new Text("2")]],
                        }),
                    Footer = new Text("Page {page} of {pages}"),
                },
            ],
        };

        var options = new PdfOptions { Deterministic = true };
        var pathA = Path.Combine(Path.GetTempPath(), $"plumepdf-tagged-det-a-{Guid.NewGuid():N}.pdf");
        var pathB = Path.Combine(Path.GetTempPath(), $"plumepdf-tagged-det-b-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var a = BuildManuscript().Render(options))
            {
                a.Save(pathA, options);
            }

            using (var b = BuildManuscript().Render(options))
            {
                b.Save(pathB, options);
            }

            Assert.Equal(File.ReadAllBytes(pathA), File.ReadAllBytes(pathB));
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }
}
