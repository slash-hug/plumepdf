namespace PlumePdf;

/// <summary>
/// Adds a page to an opened document (<see cref="PageCollection.Insert(int, PdfPage)"/>): refuses
/// what it must, copies the page into a staging area outside any guard, then commits it under the
/// destination's mutation guard, so a failed copy leaves the destination unchanged.
/// </summary>
internal static class PageAddEngine
{
    /// <summary>Copies <paramref name="page"/> into <paramref name="destination"/> at <paramref name="index"/> and returns the new page.</summary>
    internal static PdfPage Insert(PdfDocument destination, int index, PdfPage page) =>
        throw new NotImplementedException("stub: page-add engine (workstream B)");
}
