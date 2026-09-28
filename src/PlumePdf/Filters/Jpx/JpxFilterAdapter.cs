namespace PlumePdf.Filters.Jpx;

/// <summary>
/// Bridges <see cref="JpxImageDecoder"/> to the public <see cref="IPdfFilter"/> seam as
/// <c>JPXDecode</c> (registered in <see cref="PdfFilterRegistry.Default"/> — the same
/// registry-behaviour-change shape already used for CCITT/JBIG2). Stateless:
/// decodes, packs the colour channels interleaved row-major at 8 or 16 bits per sample,
/// and discards the <see cref="JpxImage"/> metadata (colour space, DPI, palette) — exactly what
/// <c>DctFilterAdapter</c> does for its decoder's result. A caller that needs that metadata reads
/// it through <see cref="JpxImageDecoder.Decode"/> directly, or through the PDF resolver seam
/// (<c>ImageXObjectResolver</c>/<c>ImageExtractor</c>), neither of which goes through this
/// byte-only contract.
/// </summary>
internal sealed class JpxFilterAdapter : IPdfFilter
{
    /// <summary>
    /// Decodes a JP2 file or raw J2K codestream and returns its colour channels interleaved
    /// row-major, alpha dropped (this filter's byte contract carries pixel samples only, the
    /// same shape a PDF image XObject's own <c>/BitsPerComponent</c>-driven unpack expects — a
    /// soft mask, when present, is a separate stream). 8-bit-or-narrower components pack one
    /// byte per sample; anything wider (9-16 bits, per <c>SIZ</c>'s declared precision) packs two
    /// bytes per sample, big-endian — the signed level shift and full-scale rescale
    /// both happen inside <see cref="JpxImage.ToInterleaved8Bit"/>/<see cref="JpxImage.ToInterleaved16BitBigEndian"/>,
    /// not here.
    /// </summary>
    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var image = JpxImageDecoder.Decode(data, options, diagnostics);
        var maxPrecision = 0;

        // This filter drops alpha (dropAlpha: true below), so
        // an opacity-only plane wider than 8 bits must not push the returned COLOUR samples to
        // 16-bit width - ImageExtractor's own equivalent loop already skips AlphaChannelIndex for
        // exactly this reason (both call sites share this same rule and must never disagree, per
        // this method's own doc comment above).
        for (var p = 0; p < image.Planes.Length; p++)
        {
            if (p == image.Colour.AlphaChannelIndex)
            {
                continue;
            }

            maxPrecision = Math.Max(maxPrecision, image.Planes[p].Precision);
        }

        return maxPrecision <= 8
            ? image.ToInterleaved8Bit(dropAlpha: true)
            : image.ToInterleaved16BitBigEndian(dropAlpha: true);
    }
}
