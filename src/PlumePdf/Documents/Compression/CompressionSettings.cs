namespace PlumePdf.Documents.Compression;

/// <summary>
/// The levers one compression run applies, as the engine reads them: the internal mirror of
/// the public options.
/// </summary>
/// <param name="Lossless">Apply the lossless levers (Flate unfiltered streams, object-stream packing, duplicate removal).</param>
/// <param name="MaxDpi">Downsample images placed above this effective resolution, or null to keep every image's size.</param>
/// <param name="JpegQuality">Re-encode eligible images as JPEG at this quality (1–100), or null to keep each image's family.</param>
internal sealed record CompressionSettings(bool Lossless, double? MaxDpi, int? JpegQuality);
