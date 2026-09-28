namespace PlumePdf;

/// <summary>
/// A PDF stream object — a dictionary describing the payload plus the payload's raw,
/// still-encoded bytes (ISO 32000-1 §7.3.8). <see cref="RawBytes"/> is a managed copy
/// (the copy-on-materialize contract); decoding through the <c>/Filter</c> chain is
/// exposed via <see cref="GetDecodedBytes"/>.
/// </summary>
/// <example>
/// <code>
/// if (document.Objects[reference] is PdfStream stream)
/// {
///     byte[] decoded = stream.GetDecodedBytes(PdfFilterRegistry.Default, PdfOptions.Default);
/// }
/// </code>
/// </example>
public sealed class PdfStream : PdfObject
{
    /// <summary>Creates a stream from its dictionary and raw (still-encoded) payload bytes.</summary>
    public PdfStream(PdfDictionary dictionary, ReadOnlyMemory<byte> rawBytes)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        Dictionary = dictionary;
        RawBytes = rawBytes;
    }

    /// <summary>The stream's dictionary — the same object graph as any other <see cref="PdfDictionary"/>.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The stream's raw, still-encoded payload bytes (a managed copy, never a view over mapped memory).</summary>
    public ReadOnlyMemory<byte> RawBytes { get; }

    /// <summary>Decodes <see cref="RawBytes"/> through <see cref="Dictionary"/>'s <c>/Filter</c> chain using <paramref name="registry"/>.</summary>
    /// <param name="registry">The filter registry to resolve filter names against — typically <see cref="PdfFilterRegistry.Default"/>.</param>
    /// <param name="options">The options controlling resource limits during decode. Defaults to <see cref="PdfOptions.Default"/> when omitted.</param>
    /// <param name="resolver">
    /// Resolves an indirect object reference to its value, so an indirect <c>/Filter</c> or
    /// <c>/DecodeParms</c> entry decodes correctly instead of being treated as absent.
    /// Pass <c>reference => document.Objects[reference]</c> when this stream's
    /// document is available; omit it only when no object graph exists to resolve against.
    /// </param>
    /// <example>
    /// <code>
    /// byte[] decoded = stream.GetDecodedBytes(document.Options.Filters, document.Options, r => document.Objects[r]);
    /// </code>
    /// </example>
    public byte[] GetDecodedBytes(PdfFilterRegistry registry, PdfOptions? options = null, Func<IndirectReference, object?>? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Decode(Dictionary, RawBytes.Span, options ?? PdfOptions.Default, resolver: resolver);
    }
}
