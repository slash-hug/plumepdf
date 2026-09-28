using System.Security.Cryptography;

namespace PlumePdf.Objects;

/// <summary>
/// Supplies the fixed values <see cref="PdfOptions.Deterministic"/> substitutes for what
/// would otherwise vary run-to-run: a fixed 16-byte document
/// identifier instead of a fresh random one. Both writers use this for the trailer's
/// <c>/ID</c> entry; neither writer emits a creation/modification timestamp of its own in
/// Phase 1 (there is no document-information-dictionary authoring API yet), so no
/// timestamp-omission logic is needed beyond simply never writing one.
/// </summary>
internal static class DeterministicContext
{
    // 16 zero bytes: a fixed, obviously-synthetic identifier used only when
    // PdfOptions.Deterministic is set, so two runs over the same input produce byte-identical
    // output (docs/architecture.md "Writing pipeline").
    private static readonly byte[] FixedId = new byte[16];

    /// <summary>
    /// Returns a fresh 16-byte document identifier: <see cref="FixedId"/> when
    /// <paramref name="deterministic"/>, otherwise a new random value each call.
    /// </summary>
    /// <remarks>
    /// The non-deterministic path draws from
    /// <see cref="RandomNumberGenerator"/> rather than <see cref="Guid.NewGuid"/> —
    /// <c>/ID</c> is a security-relevant value once a document is signed (it participates in
    /// <see cref="StandardSecurityHandler"/>'s file-key derivation, per
    /// <c>IncrementalUpdateWriter.ComputeId</c>'s remarks), and unlike a GUID, a CSPRNG draw
    /// carries an explicit unpredictability guarantee rather than one that merely happens to
    /// hold on today's BCL implementation.
    /// </remarks>
    public static byte[] CreateDocumentId(bool deterministic) =>
        deterministic ? (byte[])FixedId.Clone() : RandomNumberGenerator.GetBytes(16);
}
