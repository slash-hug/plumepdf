using System.IO.Compression;

namespace PlumePdf.Filters;

/// <summary>
/// The built-in <c>FlateDecode</c> encoder (ISO 32000-1 §7.4.4, RFC 1950 zlib-wrapped
/// DEFLATE) — the write-side counterpart of <see cref="FlateFilterAdapter"/>. Always
/// compresses at <see cref="CompressionLevel.Optimal"/>: a <em>fixed</em> level regardless
/// of input, never adapted to size or content, so the same input byte-for-byte always
/// produces the same output byte-for-byte <em>on the same platform, architecture, and
/// .NET runtime version</em> — the determinism contract (<see cref="PdfOptions.Deterministic"/>
/// and beyond: content-stream generation must be reproducible even when the option is off,
/// since <c>PdfFilterRegistry.Default</c> is a shared singleton other callers rely on behaving
/// the same way every time). "On any machine" was this doc's original, overbroad claim;
/// corrected once it became clear that <see cref="ZLibStream"/> emits bytes that are a property of the runtime's own zlib
/// implementation, not of PlumePDF, and CI already runs both the 8.0.x and 10.0.x SDKs across
/// macOS/Linux/Windows — this filter's determinism promise has only ever actually held within
/// one such combination, never universally.
/// </summary>
internal sealed class FlateEncodingFilter : IPdfEncodingFilter
{
    /// <inheritdoc/>
    public byte[] Encode(ReadOnlyMemory<byte> data, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(data.Span);
        }

        return output.ToArray();
    }
}
