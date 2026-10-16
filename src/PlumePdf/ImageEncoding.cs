namespace PlumePdf;

/// <summary>
/// The encoding family of an image's samples, as <c>Pdf.Compress</c> reports it before and
/// after (<see cref="ImageCompressionDecision.EncodingBefore"/>, <see cref="ImageCompressionDecision.EncodingAfter"/>).
/// </summary>
/// <remarks>
/// The family is the image stream's last <c>/Filter</c> that encodes the samples: an
/// <c>ASCIIHexDecode</c> or <c>ASCII85Decode</c> text wrapper doesn't change it, so
/// <c>[/ASCII85Decode /DCTDecode]</c> is <see cref="Jpeg"/>, and a chain of nothing but text
/// wrappers is <see cref="Uncompressed"/>. An inline image's abbreviated names
/// (<c>/Fl</c>, <c>/DCT</c>, <c>/CCF</c>, <c>/RL</c>, <c>/LZW</c>, <c>/AHx</c>, <c>/A85</c>)
/// count as their full names. The zero value is <see cref="Uncompressed"/>.
/// </remarks>
/// <example>
/// <code>
/// var result = Pdf.Compress("scan.pdf", "scan-small.pdf", new PdfCompressOptions
/// {
///     Images = new PdfImageCompression { JpegQuality = 70 },
/// });
/// var alreadyJpeg = result.Images.Count(static i => i.EncodingBefore == ImageEncoding.Jpeg);
/// var nowJpeg = result.Images.Count(static i => i.EncodingBefore != ImageEncoding.Jpeg
///     &amp;&amp; i.EncodingAfter == ImageEncoding.Jpeg);
/// Console.WriteLine($"{alreadyJpeg} image(s) were already JPEG; {nowJpeg} became JPEG");
/// </code>
/// </example>
public enum ImageEncoding
{
    /// <summary>No filter (or only <c>ASCIIHexDecode</c>/<c>ASCII85Decode</c> text wrappers): the samples are stored as they are.</summary>
    Uncompressed = 0,

    /// <summary><c>FlateDecode</c> (zlib/deflate), lossless.</summary>
    Flate,

    /// <summary><c>DCTDecode</c>: baseline or progressive JPEG, lossy.</summary>
    Jpeg,

    /// <summary><c>JPXDecode</c>: JPEG 2000.</summary>
    Jpeg2000,

    /// <summary><c>JBIG2Decode</c>: JBIG2, for bilevel images.</summary>
    Jbig2,

    /// <summary><c>CCITTFaxDecode</c>: CCITT Group 3 or Group 4 fax, for bilevel images.</summary>
    Ccitt,

    /// <summary><c>RunLengthDecode</c>: byte-oriented run-length encoding, lossless.</summary>
    RunLength,

    /// <summary><c>LZWDecode</c>: Lempel-Ziv-Welch, lossless.</summary>
    Lzw,

    /// <summary>Any other filter: <c>Crypt</c>, a name PlumePDF doesn't know, or a <c>/Filter</c> value that isn't a name or an array of names.</summary>
    Other,
}
