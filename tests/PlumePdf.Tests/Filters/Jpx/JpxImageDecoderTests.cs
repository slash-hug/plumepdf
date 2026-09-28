using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// <see cref="JpxImageDecoder.Decode"/>'s
/// <c>PdfOptions.MaxImagePixels</c> guard — specifically that it bounds the actual
/// allocation (one full-grid plane PER COMPONENT), not just the single-plane reference grid.
/// </summary>
public class JpxImageDecoderTests
{
    [Fact]
    public void Decode_ManyComponentsOnASmallGrid_ThrowsImageTooLarge_BeforeAllocating()
    {
        // A regression test for the finding that the guard checked only the
        // reference grid (Xsiz-XOsiz)x(Ysiz-YOsiz), while the actual allocation just below it is
        // one full-grid plane PER COMPONENT, and Csiz is bounded only by SIZ's own 16-bit segment
        // length (up to ~21,832 components) -- a small reference grid with many components passed
        // the old check and then allocated gigabytes. 100x100 pixels x 20,000 components x 1
        // byte/sample = ~200,000,000 samples, comfortably over the 2^27 default
        // MaxImagePixels, while the single-plane grid (10,000 pixels) is nowhere close on its
        // own.
        const int componentCount = 20_000;
        var bytes = BuildManyComponentCodestream(width: 100, height: 100, componentCount);

        var ex = Assert.Throws<PlumePdfException>(() => JpxImageDecoder.Decode(bytes, PdfOptions.Default, null));

        Assert.Equal(JpxDiagnosticCodes.ImageTooLarge, ex.Code);
    }

    /// <summary>A minimal raw J2K codestream (SIZ + COD + QCD + EOC, no tile-parts needed since the guard runs before any tile is decoded) declaring <paramref name="componentCount"/> 8-bit unsigned, unsubsampled components on a <paramref name="width"/> x <paramref name="height"/> reference grid.</summary>
    private static byte[] BuildManyComponentCodestream(int width, int height, int componentCount)
    {
        var bytes = new List<byte> { 0xFF, 0x4F }; // SOC

        // SIZ (A.5.1).
        var sizPayload = new List<byte>();
        AddU16(sizPayload, 0); // Rsiz
        AddU32(sizPayload, (uint)width); // Xsiz
        AddU32(sizPayload, (uint)height); // Ysiz
        AddU32(sizPayload, 0); // XOsiz
        AddU32(sizPayload, 0); // YOsiz
        AddU32(sizPayload, (uint)width); // XTsiz
        AddU32(sizPayload, (uint)height); // YTsiz
        AddU32(sizPayload, 0); // XTOsiz
        AddU32(sizPayload, 0); // YTOsiz
        AddU16(sizPayload, (ushort)componentCount);
        for (var c = 0; c < componentCount; c++)
        {
            sizPayload.Add(7); // Ssiz: unsigned, precision 8 (7 + 1)
            sizPayload.Add(1); // XRsiz
            sizPayload.Add(1); // YRsiz
        }

        WriteSegment(bytes, 0xFF51, sizPayload);

        // COD (A.6.1): no precincts (default 15/15), 5/3 reversible, 1 layer, LRCP.
        var codPayload = new List<byte> { 0x00, 0x00 };
        AddU16(codPayload, 1); // layers
        codPayload.Add(0); // MCT off
        codPayload.Add(0); // NL = 0 decomposition levels
        codPayload.Add(4); // xcb
        codPayload.Add(4); // ycb
        codPayload.Add(0); // code-block style
        codPayload.Add(1); // wavelet: 5/3
        WriteSegment(bytes, 0xFF52, codPayload);

        // QCD (A.6.4): style 0 (none/reversible), one SPqcd byte.
        var qcdPayload = new List<byte> { 0x00, 0x08 };
        WriteSegment(bytes, 0xFF5C, qcdPayload);

        bytes.Add(0xFF);
        bytes.Add(0xD9); // EOC -- no tile-parts; JpxImageDecoder's guard runs before any tile loop.
        return [.. bytes];
    }

    private static void WriteSegment(List<byte> output, ushort marker, List<byte> payload)
    {
        output.Add((byte)(marker >> 8));
        output.Add((byte)marker);
        var length = payload.Count + 2;
        output.Add((byte)(length >> 8));
        output.Add((byte)length);
        output.AddRange(payload);
    }

    private static void AddU16(List<byte> list, ushort value)
    {
        list.Add((byte)(value >> 8));
        list.Add((byte)value);
    }

    private static void AddU32(List<byte> list, uint value)
    {
        list.Add((byte)(value >> 24));
        list.Add((byte)(value >> 16));
        list.Add((byte)(value >> 8));
        list.Add((byte)value);
    }
}
