using PlumePdf.Objects;

namespace PlumePdf;

/// <summary>
/// Builds the page <see cref="Pdf.FromImages(IEnumerable{RasterImage}, PdfOptions?)"/> builds for
/// one frame. <see cref="PageCollection.InsertImage"/> renders it into a scratch one-page document
/// and copies that page in.
/// </summary>
internal static class ImagePagePipeline
{
    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName FilterName = PdfName.Get("Filter");
    private static readonly PdfName DecodeParmsName = PdfName.Get("DecodeParms");
    private static readonly PdfName LengthName = PdfName.Get("Length");

    /// <summary>One frame -> one same-size page: <see cref="Elements.Section.PageSize"/> and the body <see cref="Elements.Image"/>'s rendered size both come from the same DPI-derived point size (96 dpi fallback, <see cref="Elements.Image(RasterImageFrame)"/>'s own default), so <c>LayoutEngine.MeasureImage</c>'s width-fits-page check always passes exactly — <see cref="Elements.Margins.Uniform"/>(0) leaves no margin to mismatch against.</summary>
    internal static Elements.Section BuildPageSection(RasterImageFrame frame)
    {
        var dpiX = frame.XDpi ?? 96.0;
        var dpiY = frame.YDpi ?? 96.0;
        var pageWidth = frame.Width * 72.0 / dpiX;
        var pageHeight = frame.Height * 72.0 / dpiY;

        return new Elements.Section
        {
            PageSize = new Elements.PageSize(pageWidth, pageHeight),
            Margins = Elements.Margins.Uniform(0),
            Body = new Elements.Image(frame) { Width = pageWidth, Height = pageHeight },
        };
    }

    /// <summary>
    /// A one-page document showing <paramref name="frame"/>, rendered with
    /// <paramref name="options"/> minus any PDF/A conformance (the page is copied into a document
    /// that makes its own claims, and <c>Pdf.FromImages</c> refuses PDF/A, <c>PLUME3610</c>). With
    /// <paramref name="wrapAsArtifact"/>, the page's content is wrapped in <c>/Artifact BMC</c> …
    /// <c>EMC</c>, for a tagged destination: the image is then not untagged content but
    /// explicitly not part of the structure. The caller disposes the document.
    /// </summary>
    internal static PdfDocument BuildScratch(RasterImageFrame frame, bool wrapAsArtifact, PdfOptions options)
    {
        var scratch = new Manuscript { Sections = [BuildPageSection(frame)] }.Render(options with { PdfAConformance = PdfAConformance.None });
        if (wrapAsArtifact)
        {
            WrapAsArtifact(scratch);
        }

        return scratch;
    }

    // The renderer writes an image page's content as one stream, so that stream is replaced by a
    // wrapped copy (unfiltered: the content of an image page is a handful of operators).
    private static void WrapAsArtifact(PdfDocument scratch)
    {
        var page = scratch.Pages[0];
        var reference = ((PdfReference)page.Dictionary[ContentsName]).Target;
        var stream = (PdfStream)scratch.Objects[reference];
        var content = stream.GetDecodedBytes(scratch.Options.Filters).ToArray();

        var wrapped = new List<byte>(content.Length + 32);
        wrapped.AddRange("/Artifact BMC\n"u8.ToArray());
        wrapped.AddRange(content);
        wrapped.AddRange("\nEMC\n"u8.ToArray());

        var dictionary = new PdfDictionary();
        foreach (var (key, value) in stream.Dictionary)
        {
            if (!ReferenceEquals(key, FilterName) && !ReferenceEquals(key, DecodeParmsName) && !ReferenceEquals(key, LengthName))
            {
                dictionary.Set(key, value);
            }
        }

        scratch.Objects.Replace(reference, new PdfStream(dictionary, wrapped.ToArray()));
    }
}
