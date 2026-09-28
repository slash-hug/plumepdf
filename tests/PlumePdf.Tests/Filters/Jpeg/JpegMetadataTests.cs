using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpeg;

public class JpegMetadataTests
{
    [Fact]
    public void ParseSof_ReadsDimensionsPrecisionAndComponents()
    {
        byte[] payload =
        [
            8, // precision
            0, 32, // height = 32
            0, 16, // width = 16
            3, // component count
            1, 0x22, 0, // id, 2h2v, quant table 0
            2, 0x11, 1, // id, 1h1v, quant table 1
            3, 0x11, 1, // id, 1h1v, quant table 1
        ];

        var frame = JpegMetadata.ParseSof(payload, JpegMarkers.Sof0);

        Assert.Equal(8, frame.Precision);
        Assert.Equal(32, frame.Height);
        Assert.Equal(16, frame.Width);
        Assert.False(frame.IsProgressive);
        Assert.Equal(3, frame.Components.Count);
        Assert.Equal(2, frame.Components[0].HorizontalSampling);
        Assert.Equal(2, frame.Components[0].VerticalSampling);
        Assert.Equal(1, frame.Components[1].QuantTableId);
    }

    [Fact]
    public void ParseSof_Sof2_IsMarkedProgressive()
    {
        byte[] payload = [8, 0, 8, 0, 8, 1, 1, 0x11, 0];
        var frame = JpegMetadata.ParseSof(payload, JpegMarkers.Sof2);
        Assert.True(frame.IsProgressive);
    }

    [Fact]
    public void ParseSof_ZeroWidth_Throws()
    {
        byte[] payload = [8, 0, 8, 0, 0, 1, 1, 0x11, 0];
        var ex = Assert.Throws<PlumePdfException>(() => JpegMetadata.ParseSof(payload, JpegMarkers.Sof0));
        Assert.Equal("PLUME3202", ex.Code);
    }

    [Fact]
    public void ParseSof_TruncatedComponentList_Throws()
    {
        byte[] payload = [8, 0, 8, 0, 8, 2, 1, 0x11, 0]; // declares 2 components, only 1 present.
        var ex = Assert.Throws<PlumePdfException>(() => JpegMetadata.ParseSof(payload, JpegMarkers.Sof0));
        Assert.Equal("PLUME3203", ex.Code);
    }

    [Fact]
    public void ParseDqt_DezigzagsIntoNaturalOrder()
    {
        var payload = new byte[65];
        payload[0] = 0x00; // precision 0, id 0.
        for (var z = 0; z < 64; z++)
        {
            payload[1 + z] = (byte)z; // value equals its own zigzag index.
        }

        var tables = JpegMetadata.ParseDqt(payload);
        Assert.Single(tables);
        Assert.Equal(0, tables[0].Id);
        // Zigzag index 1 belongs at natural index 1 (JpegHuffmanTable.ZigzagToNatural[1] == 1).
        Assert.Equal(1, tables[0].NaturalOrderValues[1]);
        // Zigzag index 2 belongs at natural index 8.
        Assert.Equal(2, tables[0].NaturalOrderValues[8]);
    }

    [Fact]
    public void ParseDqt_MultipleTablesInOneSegment()
    {
        var payload = new byte[130];
        payload[0] = 0; // table id 0.
        payload[65] = 1; // table id 1.
        var tables = JpegMetadata.ParseDqt(payload);
        Assert.Equal(2, tables.Count);
        Assert.Equal(0, tables[0].Id);
        Assert.Equal(1, tables[1].Id);
    }

    [Fact]
    public void ParseDht_ReadsBitsAndValues()
    {
        var payload = new byte[19];
        payload[0] = 0x10; // class 1 (AC), id 0.
        payload[1] = 2; // two codes of length 1.
        payload[17] = 0xAA;
        payload[18] = 0xBB;

        var tables = JpegMetadata.ParseDht(payload);
        Assert.Single(tables);
        Assert.Equal(1, tables[0].TableClass);
        Assert.Equal(0, tables[0].Id);
        Assert.Equal(2, tables[0].Bits[1]);
        Assert.Equal([0xAA, 0xBB], tables[0].Values);
    }

    [Fact]
    public void ParseDri_ReadsRestartInterval()
    {
        Assert.Equal(0x0102, JpegMetadata.ParseDri([0x01, 0x02]));
    }

    [Fact]
    public void ParseSos_ReadsComponentsAndSpectralRange()
    {
        byte[] payload = [2, 1, 0x00, 2, 0x11, 1, 63, 0x00];
        var scan = JpegMetadata.ParseSos(payload);
        Assert.Equal(2, scan.Components.Count);
        Assert.Equal(1, scan.Components[0].ComponentSelector);
        Assert.Equal(0, scan.Components[0].DcTableId);
        Assert.Equal(2, scan.Components[1].ComponentSelector);
        Assert.Equal(1, scan.Components[1].DcTableId);
        Assert.Equal(1, scan.Components[1].AcTableId);
        Assert.Equal(1, scan.SpectralStart);
        Assert.Equal(63, scan.SpectralEnd);
    }

    [Fact]
    public void ParseApp0Jfif_DotsPerInch_ReturnsDeclaredDensity()
    {
        byte[] payload = [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 1, 0, 150, 0, 96, 0, 0];
        var dpi = JpegMetadata.ParseApp0Jfif(payload);
        Assert.NotNull(dpi);
        Assert.Equal(150, dpi.Value.XDpi);
        Assert.Equal(96, dpi.Value.YDpi);
    }

    [Fact]
    public void ParseApp0Jfif_DotsPerCm_ConvertsToDpi()
    {
        byte[] payload = [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 2, 0, 40, 0, 40, 0, 0];
        var dpi = JpegMetadata.ParseApp0Jfif(payload);
        Assert.NotNull(dpi);
        Assert.Equal(40 * 2.54, dpi.Value.XDpi, 3);
    }

    [Fact]
    public void ParseApp0Jfif_AspectRatioOnly_ReturnsNull()
    {
        byte[] payload = [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 0, 0, 1, 0, 1, 0, 0];
        Assert.Null(JpegMetadata.ParseApp0Jfif(payload));
    }

    [Fact]
    public void ParseApp0Jfif_NonJfifIdentifier_ReturnsNull()
    {
        byte[] payload = [(byte)'X', (byte)'X', (byte)'X', (byte)'X', 0, 1, 2, 1, 0, 96, 0, 96, 0, 0];
        Assert.Null(JpegMetadata.ParseApp0Jfif(payload));
    }

    [Fact]
    public void ParseApp1Exif_ReadsResolutionTagsFromLittleEndianTiff()
    {
        var payload = BuildExifPayload(littleEndian: true, xRes: 300, yRes: 300, unit: 2);
        var dpi = JpegMetadata.ParseApp1Exif(payload);
        Assert.NotNull(dpi);
        Assert.Equal(300, dpi.Value.XDpi, 3);
        Assert.Equal(300, dpi.Value.YDpi, 3);
    }

    [Fact]
    public void ParseApp1Exif_ReadsResolutionTagsFromBigEndianTiff()
    {
        var payload = BuildExifPayload(littleEndian: false, xRes: 72, yRes: 72, unit: 2);
        var dpi = JpegMetadata.ParseApp1Exif(payload);
        Assert.NotNull(dpi);
        Assert.Equal(72, dpi.Value.XDpi, 3);
    }

    [Fact]
    public void ParseApp1Exif_NoAbsoluteUnit_ReturnsNull()
    {
        var payload = BuildExifPayload(littleEndian: true, xRes: 300, yRes: 300, unit: 1); // 1 = no absolute unit.
        Assert.Null(JpegMetadata.ParseApp1Exif(payload));
    }

    [Fact]
    public void ParseApp14Adobe_ReadsTransformByte()
    {
        byte[] ycck = [(byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 100, 0, 0, 0, 0, 0, 2];
        Assert.Equal(JpegAdobeTransform.Ycck, JpegMetadata.ParseApp14Adobe(ycck));

        byte[] ycbcr = [(byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 100, 0, 0, 0, 0, 0, 1];
        Assert.Equal(JpegAdobeTransform.YCbCr, JpegMetadata.ParseApp14Adobe(ycbcr));

        byte[] unknown = [(byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 100, 0, 0, 0, 0, 0, 0];
        Assert.Equal(JpegAdobeTransform.Unknown, JpegMetadata.ParseApp14Adobe(unknown));
    }

    [Fact]
    public void ParseApp14Adobe_NonAdobeIdentifier_ReturnsNull()
    {
        byte[] payload = new byte[12];
        Assert.Null(JpegMetadata.ParseApp14Adobe(payload));
    }

    [Fact]
    public void Markers_ClassifyArithmeticAndUnsupportedSofCorrectly()
    {
        Assert.True(JpegMarkers.IsArithmeticSof(JpegMarkers.Sof9));
        Assert.True(JpegMarkers.IsArithmeticSof(JpegMarkers.Sof13));
        Assert.False(JpegMarkers.IsArithmeticSof(JpegMarkers.Sof0));

        Assert.True(JpegMarkers.IsUnsupportedSof(JpegMarkers.Sof3));
        Assert.False(JpegMarkers.IsUnsupportedSof(JpegMarkers.Sof1));

        Assert.True(JpegMarkers.IsSupportedSof(JpegMarkers.Sof0));
        Assert.True(JpegMarkers.IsSupportedSof(JpegMarkers.Sof1));
        Assert.True(JpegMarkers.IsSupportedSof(JpegMarkers.Sof2));

        Assert.True(JpegMarkers.IsRestart(0xD0));
        Assert.True(JpegMarkers.IsRestart(0xD7));
        Assert.False(JpegMarkers.IsRestart(0xD8));
    }

    private static byte[] BuildExifPayload(bool littleEndian, double xRes, double yRes, ushort unit)
    {
        // "Exif\0\0" + a minimal single-IFD TIFF: header(8) + entry-count(2) + 3 IFD entries
        // (XResolution, YResolution, ResolutionUnit)(12 each) + next-IFD-offset(4) + 2 RATIONAL value pairs(8 each).
        var tiff = new byte[8 + 2 + (3 * 12) + 4 + 16];
        void WriteU16(int offset, ushort value)
        {
            if (littleEndian)
            {
                tiff[offset] = (byte)value;
                tiff[offset + 1] = (byte)(value >> 8);
            }
            else
            {
                tiff[offset] = (byte)(value >> 8);
                tiff[offset + 1] = (byte)value;
            }
        }

        void WriteU32(int offset, uint value)
        {
            if (littleEndian)
            {
                tiff[offset] = (byte)value;
                tiff[offset + 1] = (byte)(value >> 8);
                tiff[offset + 2] = (byte)(value >> 16);
                tiff[offset + 3] = (byte)(value >> 24);
            }
            else
            {
                tiff[offset] = (byte)(value >> 24);
                tiff[offset + 1] = (byte)(value >> 16);
                tiff[offset + 2] = (byte)(value >> 8);
                tiff[offset + 3] = (byte)value;
            }
        }

        if (littleEndian)
        {
            tiff[0] = (byte)'I';
            tiff[1] = (byte)'I';
        }
        else
        {
            tiff[0] = (byte)'M';
            tiff[1] = (byte)'M';
        }

        WriteU16(2, 42);
        WriteU32(4, 8); // IFD0 offset.
        WriteU16(8, 3); // 3 entries.

        var xResValueOffset = 8 + 2 + (3 * 12) + 4;
        var yResValueOffset = xResValueOffset + 8;

        // Entry 0: XResolution (0x011A), type RATIONAL(5), count 1, value offset.
        WriteU16(10, 0x011A);
        WriteU16(12, 5);
        WriteU32(14, 1);
        WriteU32(18, (uint)xResValueOffset);

        // Entry 1: YResolution (0x011B).
        WriteU16(22, 0x011B);
        WriteU16(24, 5);
        WriteU32(26, 1);
        WriteU32(30, (uint)yResValueOffset);

        // Entry 2: ResolutionUnit (0x0128), type SHORT(3).
        WriteU16(34, 0x0128);
        WriteU16(36, 3);
        WriteU32(38, 1);
        WriteU16(42, unit);

        WriteU32(46, 0); // next IFD offset: none.

        WriteU32(xResValueOffset, (uint)(xRes * 10));
        WriteU32(xResValueOffset + 4, 10);
        WriteU32(yResValueOffset, (uint)(yRes * 10));
        WriteU32(yResValueOffset + 4, 10);

        var payload = new byte[6 + tiff.Length];
        "Exif\0\0"u8.CopyTo(payload);
        tiff.CopyTo(payload, 6);
        return payload;
    }
}
