using System.Diagnostics;
using System.Text;
using PlumePdf.Documents.PdfA;
using PlumePdf.Elements;
using Xunit;

namespace PlumePdf.Tests.PdfA;

/// <summary>
/// Regression coverage for the hostile-XMP hang class of bug: <c>Pdf.ValidatePdfA</c>'s
/// packet scans once ran uncapped, backtracking regexes over an unbounded
/// <c>GetXmpMetadataText()</c>, so a small compressed packet full of repeated unclosed start
/// tags drove minutes of CPU. Two independent guards now exist, each pinned here:
/// <see cref="PdfOptions.MaxXmpPacketReadBytes"/> refuses an oversized decoded packet
/// (<c>PLUME6080</c>) before any scan runs, and every validator regex is
/// <see cref="System.Text.RegularExpressions.RegexOptions.NonBacktracking"/> so an in-cap
/// hostile packet still scans in linear time.
/// </summary>
public class HostileXmpTests
{
    [Fact]
    public void Validate_HostileRepeatedUnclosedStartTags_ReturnsVerdictInBoundedTime()
    {
        // ~2 MiB (under the 8 MiB read cap) of start tags that carry the pdfaid namespace
        // declaration but are never closed with '>' — the exact shape that drove the old lazy
        // [^>]*? scan quadratic (every candidate position re-scanned to end-of-packet looking
        // for the '>' that never comes).
        var fragment = "<r:d xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" ";
        var builder = new StringBuilder(2 * 1024 * 1024 + fragment.Length);
        while (builder.Length < 2 * 1024 * 1024)
        {
            builder.Append(fragment);
        }

        using var document = OneImagePageDocument(PdfOptions.Default);
        InjectRawXmp(document, Encoding.UTF8.GetBytes(builder.ToString()));

        var stopwatch = Stopwatch.StartNew();
        var result = PdfAValidator.Validate(document);
        stopwatch.Stop();

        // Generous bound: linear scans over 2 MiB complete in milliseconds; the pre-fix
        // behavior ran 7+ minutes. A minute of headroom keeps this stable on slow CI runners.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMinutes(1), $"Validation took {stopwatch.Elapsed} — the hostile-XMP linear-time guarantee has regressed.");
        Assert.False(result.IsConformant); // no parseable pdfaid declaration → identification fails, honestly.
    }

    [Fact]
    public void Validate_XmpPacketOverReadCap_RefusesWithPlume6080BeforeAnyScan()
    {
        var options = PdfOptions.Default with { MaxXmpPacketReadBytes = 1024 };
        using var document = OneImagePageDocument(options);
        InjectRawXmp(document, Encoding.UTF8.GetBytes(new string('x', 4096)));

        var ex = Assert.Throws<PlumePdfException>(() => PdfAValidator.Validate(document));
        Assert.Equal("PLUME6080", ex.Code);
    }

    [Fact]
    public void GetXmpMetadataText_PacketOverReadCap_RefusesWithPlume6080()
    {
        var options = PdfOptions.Default with { MaxXmpPacketReadBytes = 1024 };
        using var document = OneImagePageDocument(options);
        InjectRawXmp(document, Encoding.UTF8.GetBytes(new string('x', 4096)));

        var ex = Assert.Throws<PlumePdfException>(() => document.GetXmpMetadataText());
        Assert.Equal("PLUME6080", ex.Code);
    }

    [Fact]
    public void GetXmpMetadataText_PacketAtDefaultSizes_StillReads()
    {
        using var document = OneImagePageDocument(PdfOptions.Default);
        InjectRawXmp(document, "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta>"u8.ToArray());

        Assert.Contains("xmpmeta", document.GetXmpMetadataText());
    }

    private static PdfDocument OneImagePageDocument(PdfOptions options)
    {
        byte[] pixels = new byte[2 * 2 * 3];
        Array.Fill(pixels, (byte)180);
        return new Manuscript
        {
            Sections = [new Section { Body = new Image(pixels, pixelWidth: 2, pixelHeight: 2) }],
        }.Render(options);
    }

    // Bypasses SetXmpMetadata/XmpWriter deliberately: the write path could never produce these
    // packets (that is the point), so the raw stream is registered through the public
    // doc.Objects escape hatch, exactly the way a hostile file would present it on open.
    private static void InjectRawXmp(PdfDocument document, byte[] xmpBytes)
    {
        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Type, PdfName.Get("Metadata"));
        dictionary.Set(PdfName.Subtype, PdfName.Get("XML"));
        dictionary.Set(PdfName.Length, PdfNumber.Get(xmpBytes.Length));
        var stream = new PdfStream(dictionary, xmpBytes);

        var reference = document.Objects.AllocateNumber();
        document.Objects.RegisterNew(reference, stream);
        document.Catalog!.Dictionary.Set(PdfName.Get("Metadata"), new PdfReference(reference));
    }
}
