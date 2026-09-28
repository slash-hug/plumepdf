using System.Text;
using PlumePdf.Content;
using Xunit;

namespace PlumePdf.Tests.Content;

/// <summary><see cref="ContentStreamBuilder"/> operator emission and q/Q, BT/ET balance enforcement.</summary>
public class ContentStreamBuilderTests
{
    [Fact]
    public void KnownOperatorSequence_PathPainting_IsByteExact()
    {
        var bytes = new ContentStreamBuilder()
            .SaveState()
            .Transform(1, 0, 0, 1, 72, 720)
            .Rectangle(0, 0, 100, 20)
            .Fill()
            .RestoreState()
            .Build();

        var expected = "q\n1 0 0 1 72 720 cm\n0 0 100 20 re\nf\nQ\n";
        Assert.Equal(expected, Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void KnownOperatorSequence_Text_IsByteExact()
    {
        var bytes = new ContentStreamBuilder()
            .BeginText()
            .SetFont("F1", 12)
            .MoveText(20, 50)
            .ShowText("Hello"u8)
            .EndText()
            .Build();

        var expected = "BT\n/F1 12 Tf\n20 50 Td\n(Hello) Tj\nET\n";
        Assert.Equal(expected, Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void ShowTextWithAdjustments_EmitsTJArray_IsByteExact()
    {
        var bytes = new ContentStreamBuilder()
            .BeginText()
            .ShowTextWithAdjustments([
                TextShowElement.ForText("A"u8.ToArray()),
                TextShowElement.ForAdjustment(-120),
                TextShowElement.ForText("V"u8.ToArray()),
            ])
            .EndText()
            .Build();

        var expected = "BT\n[(A) -120 (V) ] TJ\nET\n";
        Assert.Equal(expected, Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void ColorAndXObjectOperators_AreByteExact()
    {
        var bytes = new ContentStreamBuilder()
            .SetFillGray(0)
            .SetStrokeGray(1)
            .SetFillRgb(1, 0, 0)
            .SetStrokeRgb(0, 0, 1)
            .Stroke()
            .PaintXObject("X1")
            .Build();

        var expected = "0 g\n1 G\n1 0 0 rg\n0 0 1 RG\nS\n/X1 Do\n";
        Assert.Equal(expected, Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void LiteralString_EscapesParensAndBackslash()
    {
        var bytes = new ContentStreamBuilder()
            .BeginText()
            .ShowText("a(b)c\\d"u8)
            .EndText()
            .Build();

        var expected = "BT\n(a\\(b\\)c\\\\d) Tj\nET\n";
        Assert.Equal(expected, Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void NumberFormatting_UsesInvariantCultureMinimalDigits()
    {
        var bytes = new ContentStreamBuilder()
            .Transform(1.5, -0.25, 0, 1, 100, 0)
            .Build();

        Assert.Equal("1.5 -0.25 0 1 100 0 cm\n", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void SameOperatorSequence_ProducesIdenticalBytes()
    {
        byte[] Build() => new ContentStreamBuilder().SaveState().Rectangle(0, 0, 10, 10).Fill().RestoreState().Build();

        Assert.Equal(Build(), Build());
    }

    [Fact]
    public void RestoreState_WithoutSaveState_ThrowsCodedException()
    {
        var builder = new ContentStreamBuilder();

        var ex = Assert.Throws<PlumePdfException>(() => builder.RestoreState());
        Assert.Equal("PLUME7001", ex.Code);
    }

    [Fact]
    public void BeginText_WhileAlreadyInTextObject_ThrowsCodedException()
    {
        var builder = new ContentStreamBuilder().BeginText();

        var ex = Assert.Throws<PlumePdfException>(() => builder.BeginText());
        Assert.Equal("PLUME7002", ex.Code);
    }

    [Fact]
    public void EndText_WithoutBeginText_ThrowsCodedException()
    {
        var builder = new ContentStreamBuilder();

        var ex = Assert.Throws<PlumePdfException>(() => builder.EndText());
        Assert.Equal("PLUME7003", ex.Code);
    }

    [Fact]
    public void Build_WithUnmatchedSaveState_ThrowsCodedException()
    {
        var builder = new ContentStreamBuilder().SaveState();

        var ex = Assert.Throws<PlumePdfException>(() => builder.Build());
        Assert.Equal("PLUME7004", ex.Code);
    }

    [Fact]
    public void Build_WithUnclosedTextObject_ThrowsCodedException()
    {
        var builder = new ContentStreamBuilder().BeginText();

        var ex = Assert.Throws<PlumePdfException>(() => builder.Build());
        Assert.Equal("PLUME7005", ex.Code);
    }

    [Fact]
    public void Build_CalledTwice_Throws()
    {
        var builder = new ContentStreamBuilder().Fill();
        builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void NestedSaveState_BalancesCorrectly()
    {
        var bytes = new ContentStreamBuilder()
            .SaveState()
            .SaveState()
            .RestoreState()
            .RestoreState()
            .Build();

        Assert.Equal("q\nq\nQ\nQ\n", Encoding.ASCII.GetString(bytes));
    }
}
