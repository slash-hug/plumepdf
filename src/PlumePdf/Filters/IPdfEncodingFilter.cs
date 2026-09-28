namespace PlumePdf;

/// <summary>
/// A single named PDF stream filter <em>encoder</em> (ISO 32000-1 §7.4) — the write-side
/// counterpart of <see cref="IPdfFilter"/>. Deliberately a separate interface rather than
/// an <c>Encode</c> member added to <see cref="IPdfFilter"/>: <see cref="IPdfFilter"/> is decode-only and
/// already public in v1, so every existing community codec that implements it keeps
/// compiling unchanged, and a codec that can only decode (many can — e.g. reading a vendor
/// image format without ever needing to re-encode one) never has to implement a member it
/// cannot honor. <see cref="PdfFilterRegistry.TryGetEncoder"/> is the seam a writer path
/// probes to find one.
/// </summary>
/// <example>
/// <code>
/// // PdfFilterRegistry.Default already registers PlumePDF's built-in Flate encoder:
/// if (PdfFilterRegistry.Default.TryGetEncoder("FlateDecode", out var encoder))
/// {
///     byte[] encoded = encoder!.Encode(contentStreamBytes, PdfOptions.Default);
/// }
///
/// // Or register your own codec's encoder on a registry you construct:
/// // registry.RegisterEncoder("AcmeCompress", new MyAcmeCompressEncoder()); // : IPdfEncodingFilter
/// </code>
/// </example>
public interface IPdfEncodingFilter
{
    /// <summary>
    /// Encodes <paramref name="data"/> into this filter's on-disk representation — the
    /// inverse of the matching <see cref="IPdfFilter.Decode"/>. Implementations that need
    /// deterministic output (<see cref="PdfOptions.Deterministic"/> or otherwise) must
    /// produce byte-identical output for byte-identical input on every call: no
    /// timestamps, no randomized dictionary/window choices, no machine-dependent behavior.
    /// </summary>
    /// <param name="data">The raw, not-yet-encoded payload bytes.</param>
    /// <param name="options">The active options.</param>
    /// <returns>The encoded bytes, ready to be written as a stream's raw payload.</returns>
    byte[] Encode(ReadOnlyMemory<byte> data, PdfOptions options);
}
