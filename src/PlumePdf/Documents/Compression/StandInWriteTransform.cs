using PlumePdf.Objects;

namespace PlumePdf.Documents.Compression;

/// <summary>
/// The estimate's write-time transform: every stream the full-rewrite writer reaches is
/// written as a stand-in of the size Compress would write it at, without reading its payload,
/// so running the real writer into a byte counter measures what a full rewrite drops
/// (superseded revisions, objects nothing reaches), what packing into object streams saves and
/// what the cross-reference section costs, all exactly.
/// </summary>
/// <remarks>
/// A planned image stands in at its estimated size (a sample's exact one), with the dictionary
/// Compress would write when its size, dimensions or encoding change; a stream the lossless
/// lever would Flate stands in at its Flated size (a sample's exact one) with the dictionary the
/// lever writes; any other stream, at its own payload length with its own dictionary. A sampled
/// image stands in with the very dictionary Compress wrote. A stand-in holds only a length; the
/// serializer writes it as zeros in small chunks, so measuring needs no payload-sized memory.
/// </remarks>
internal sealed class StandInWriteTransform : IUnreadStreamWriteTransform
{
    private static readonly PdfName DctDecodeName = PdfName.Get("DCTDecode");
    private static readonly PdfName FlateDecodeName = PdfName.Get("FlateDecode");

    private readonly ObjectRegistry _objects;
    private readonly IReadOnlyDictionary<int, WrittenImage> _images;
    private readonly IReadOnlyDictionary<int, long> _flated;
    private readonly CancellationToken _cancellationToken;

    /// <summary>Creates the transform for one measured write.</summary>
    /// <param name="objects">The source document's objects.</param>
    /// <param name="images">Each planned image's estimated (or sampled) record, by original number.</param>
    /// <param name="flated">The Flated payload length of each non-image stream the lossless lever would Flate, by original number.</param>
    /// <param name="cancellationToken">Checked before each stream.</param>
    public StandInWriteTransform(ObjectRegistry objects, IReadOnlyDictionary<int, WrittenImage> images, IReadOnlyDictionary<int, long> flated, CancellationToken cancellationToken)
    {
        _objects = objects;
        _images = images;
        _flated = flated;
        _cancellationToken = cancellationToken;
    }

    /// <inheritdoc/>
    public bool Handles(int originalNumber) => true;

    /// <inheritdoc/>
    /// <remarks>The writer replaces every stream unread, so this is reached only for a stream it had already resolved some other way; it is measured the same way.</remarks>
    public PdfStream Transform(int originalNumber, PdfStream original) => ReplaceUnread(originalNumber, original.Dictionary);

    /// <inheritdoc/>
    public PdfStream ReplaceUnread(int originalNumber, PdfDictionary dictionary)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_images.TryGetValue(originalNumber, out var image))
        {
            return PdfStream.StandIn(image.WrittenDictionary ?? EstimatedDictionary(dictionary, image), image.BytesAfter);
        }

        return _flated.TryGetValue(originalNumber, out var flated)
            ? PdfStream.StandIn(CompressionWriteTransform.FlatedDictionary(dictionary), flated)
            : PdfStream.StandIn(dictionary, CompressionEstimator.PayloadLength(_objects, originalNumber, dictionary));
    }

    // An unsampled image's dictionary, by the branch Compress would take: the re-encoder's
    // rewrite for a lever (a non-JPEG becoming a JPEG drops a /Decode it bakes), the lossless
    // lever's for an image kept but Flated, and its own otherwise.
    private PdfDictionary EstimatedDictionary(PdfDictionary dictionary, WrittenImage image)
    {
        if (image.BytesAfter == image.BytesBefore && image.EncodingAfter == image.EncodingBefore)
        {
            return dictionary;
        }

        if (image.Outcome is ImageReencodeOutcome.Downsampled or ImageReencodeOutcome.Reencoded or ImageReencodeOutcome.DownsampledAndReencoded
            && FilterFor(image.EncodingAfter) is { } filter)
        {
            var bake = image.EncodingAfter == ImageEncoding.Jpeg && image.EncodingBefore != ImageEncoding.Jpeg && ImageReencoder.WouldBakeDecode(dictionary, _objects);
            return ImageReencoder.Rewrite(dictionary, image.WidthAfter, image.HeightAfter, filter, bake);
        }

        return image.EncodingAfter == ImageEncoding.Flate ? CompressionWriteTransform.FlatedDictionary(dictionary) : dictionary;
    }

    private static PdfName? FilterFor(ImageEncoding encoding) => encoding switch
    {
        ImageEncoding.Jpeg => DctDecodeName,
        ImageEncoding.Flate => FlateDecodeName,
        _ => null,
    };
}
