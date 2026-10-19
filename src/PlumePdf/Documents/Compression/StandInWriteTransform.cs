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
/// lever writes; any other stream, at its own payload length with its own dictionary. Stand-in
/// payloads are zeros from one shared buffer, grown to the largest stand-in.
/// </remarks>
internal sealed class StandInWriteTransform : IUnreadStreamWriteTransform
{
    private static readonly PdfName DctDecodeName = PdfName.Get("DCTDecode");
    private static readonly PdfName FlateDecodeName = PdfName.Get("FlateDecode");

    private readonly ObjectRegistry _objects;
    private readonly IReadOnlyDictionary<int, WrittenImage> _images;
    private readonly IReadOnlyDictionary<int, long> _flated;
    private readonly CancellationToken _cancellationToken;
    private byte[] _zeros = [];

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
            var changed = image.BytesAfter != image.BytesBefore || image.EncodingAfter != image.EncodingBefore;
            var written = changed && FilterFor(image.EncodingAfter) is { } filter
                ? ImageReencoder.Rewrite(dictionary, image.WidthAfter, image.HeightAfter, filter, dropDecode: false)
                : dictionary;
            return new PdfStream(written, Zeros(image.BytesAfter));
        }

        return _flated.TryGetValue(originalNumber, out var flated)
            ? new PdfStream(CompressionWriteTransform.FlatedDictionary(dictionary), Zeros(flated))
            : new PdfStream(dictionary, Zeros(CompressionEstimator.PayloadLength(_objects, originalNumber, dictionary)));
    }

    private static PdfName? FilterFor(ImageEncoding encoding) => encoding switch
    {
        ImageEncoding.Jpeg => DctDecodeName,
        ImageEncoding.Flate => FlateDecodeName,
        _ => null,
    };

    private ReadOnlyMemory<byte> Zeros(long length)
    {
        var count = (int)Math.Clamp(length, 0, Array.MaxLength);
        if (_zeros.Length < count)
        {
            _zeros = new byte[count];
        }

        return _zeros.AsMemory(0, count);
    }
}
