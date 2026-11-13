namespace PlumePdf;

/// <summary>
/// Builds the page <see cref="Pdf.FromImages(IEnumerable{RasterImage}, PdfOptions?)"/> builds
/// for one frame. <see cref="PageCollection.InsertImage"/> renders it into a scratch one-page
/// document and copies that page in.
/// </summary>
internal static class ImagePagePipeline
{
    /// <summary>
    /// A one-page document showing <paramref name="frame"/>, rendered with
    /// <paramref name="options"/> minus any PDF/A conformance; with
    /// <paramref name="wrapAsArtifact"/>, its content is marked as an artifact.
    /// </summary>
    internal static PdfDocument BuildScratch(RasterImageFrame frame, bool wrapAsArtifact, PdfOptions options) =>
        throw new NotImplementedException("stub: image page pipeline (workstream C)");
}
