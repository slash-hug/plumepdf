using PlumePdf.Fonts;
using PlumePdf.Fonts.Outlines;
using Xunit;

namespace PlumePdf.Tests.Fonts.Outlines;

/// <summary>
/// <see cref="SfntFont.ParseForRender"/>'s render-path routing — a CFF-flavored
/// ('OTTO') font with no 'glyf' table is accepted for render (unlike the ordinary
/// <see cref="SfntFont.Parse"/> write/embed path, which must keep throwing <c>PLUME8006</c>
/// exactly as before), and <see cref="SfntFont.GetGlyphOutline"/> dispatches to
/// <see cref="CffParser"/> for such a font.
/// </summary>
public class SfntRenderRoutingTests
{
    [Fact]
    public void OttoCffFont_OrdinaryParse_StillThrowsPlume8006()
    {
        var otto = BuildOttoCffFont();

        var ex = Assert.Throws<PlumePdfException>(() => SfntFont.Parse(otto, FontReadLimits.Default));

        Assert.Equal("PLUME8006", ex.Code);
    }

    [Fact]
    public void OttoCffFont_ParseForRender_SucceedsAndDispatchesToCff()
    {
        var otto = BuildOttoCffFont();

        var sfnt = SfntFont.ParseForRender(otto, FontReadLimits.Default);
        var outline = sfnt.GetGlyphOutline(1, FontReadLimits.Default);

        Assert.Collection(
            outline.Commands,
            c => Assert.Equal(GlyphPathCommandKind.MoveTo, c.Kind),
            c => Assert.Equal(GlyphPathCommandKind.LineTo, c.Kind),
            c => Assert.Equal(GlyphPathCommandKind.LineTo, c.Kind),
            c => Assert.Equal(GlyphPathCommandKind.LineTo, c.Kind),
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void OttoCffFont_GetGlyphOutline_CachesParserAcrossCalls()
    {
        var otto = BuildOttoCffFont();
        var sfnt = SfntFont.ParseForRender(otto, FontReadLimits.Default);

        var first = sfnt.GetGlyphOutline(1, FontReadLimits.Default);
        var second = sfnt.GetGlyphOutline(1, FontReadLimits.Default);

        Assert.Equal(first.Commands, second.Commands);
    }

    /// <summary>A minimal but structurally real 'OTTO' SFNT wrapper (table directory only — no 'glyf') around a one-glyph CFF table from <see cref="CffTestBuilder"/>.</summary>
    private static byte[] BuildOttoCffFont()
    {
        byte[] cff = CffTestBuilder.BuildSingleFdCff(
            [],
            [],
            [
                [14], // glyph 0 (.notdef)
                [
                    .. CffTestBuilder.Number(50), .. CffTestBuilder.Number(50), 21, // rmoveto
                    .. CffTestBuilder.Number(100), .. CffTestBuilder.Number(0), 5,   // rlineto
                    .. CffTestBuilder.Number(0), .. CffTestBuilder.Number(100), 5,   // rlineto
                    14,
                ],
            ]);

        const int directoryStart = 12;
        const int tableCount = 1;
        var dataStart = directoryStart + (tableCount * 16);

        var result = new byte[dataStart + cff.Length];
        WriteTag(result, 0, "OTTO");
        WriteU16(result, 4, tableCount);

        WriteTag(result, directoryStart, "CFF ");
        WriteU32(result, directoryStart + 8, (uint)dataStart);
        WriteU32(result, directoryStart + 12, (uint)cff.Length);
        cff.CopyTo(result.AsSpan(dataStart));

        return result;
    }

    private static void WriteTag(byte[] data, int offset, string tag)
    {
        for (var i = 0; i < 4; i++)
        {
            data[offset + i] = (byte)tag[i];
        }
    }

    private static void WriteU16(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static void WriteU32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
