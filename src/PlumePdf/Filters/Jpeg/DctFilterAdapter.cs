namespace PlumePdf.Filters;

/// <summary>
/// Bridges <see cref="JpegDecoder"/> to the public <see cref="IPdfFilter"/> seam for the
/// <c>DCTDecode</c>/<c>DCT</c> filter name (ISO 32000-1 §7.4.8) - decode-only:
/// <see cref="PlumePdf.Documents.ImageExtractor"/>'s own hand-rolled DCT pass-through
/// predates this and is untouched, dispatching before <see cref="PdfFilterRegistry"/> is
/// ever consulted, so Phase 3 extraction behavior is unaffected either way. This adapter
/// exists for callers that go through the registry directly - <c>RasterImage.Decode</c>
/// chief among them - and reads the stream's own
/// <c>/ColorTransform</c> DecodeParms entry (Table 13) as a fallback for the rare 3-component
/// JPEG that carries neither an Adobe <c>APP14</c> marker nor <c>R</c>/<c>G</c>/<c>B</c>
/// component IDs. Registration into <see cref="PdfFilterRegistry.Default"/> happens
/// separately - a decode-only adapter type is
/// useful (and independently testable via a caller-constructed registry) whether or not
/// anything has registered it yet.
/// </summary>
internal sealed class DctFilterAdapter : IPdfFilterWithDecodeParms
{
    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
        JpegDecoder.Decode(data, options, diagnostics, subject).Pixels;

    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfDictionary? decodeParms, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject, Func<IndirectReference, object?>? resolver = null) =>
        JpegDecoder.Decode(data, options, diagnostics, subject, ReadColorTransform(decodeParms)).Pixels;

    // Internal (was private) so Documents.ImageXObjectResolver's terminal-DCT direct-decode
    // path applies the exact same /DecodeParms /ColorTransform reading as this registry path —
    // the two must never disagree.
    internal static JpegAdobeTransform? ReadColorTransform(PdfDictionary? decodeParms)
    {
        if (decodeParms is null || !decodeParms.TryGetValue(PdfName.Get("ColorTransform"), out var value))
        {
            return null; // ISO 32000-1 Table 13's default (1, i.e. YCbCr) is already JpegDecoder's own default heuristic when no APP14 marker is present.
        }

        if (value is PdfNumber { IsInteger: true } number && number.TryToInt32(out var result))
        {
            return result switch
            {
                0 => JpegAdobeTransform.Unknown,
                _ => JpegAdobeTransform.YCbCr,
            };
        }

        return null;
    }
}
