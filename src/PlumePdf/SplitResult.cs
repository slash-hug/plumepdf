namespace PlumePdf;

/// <summary>The result of <see cref="Pdf.Split(PdfDocument)"/> — one document per source page, in order.</summary>
/// <example>
/// <code>
/// using var split = Pdf.Split(document);
/// split.SaveAll("part-{n}.pdf");
/// </code>
/// </example>
public sealed class SplitResult : IDisposable
{
    private bool _disposed;

    internal SplitResult(IReadOnlyList<PdfDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        Documents = documents;
    }

    /// <summary>The resulting documents, one per source page, in order.</summary>
    public IReadOnlyList<PdfDocument> Documents { get; }

    /// <summary>
    /// Saves every document in <see cref="Documents"/> using a full rewrite
    /// (<see cref="PdfDocument.Save"/>), substituting <c>{n}</c> in <paramref name="pathPattern"/>
    /// with each document's 1-based position.
    /// </summary>
    /// <param name="pathPattern">A path containing the literal token <c>{n}</c>, e.g. <c>"part-{n}.pdf"</c>.</param>
    /// <param name="options">Options controlling each write. Defaults to each document's own options.</param>
    /// <exception cref="ArgumentException"><paramref name="pathPattern"/> does not contain <c>{n}</c>.</exception>
    public void SaveAll(string pathPattern, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathPattern);
        if (!pathPattern.Contains("{n}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The path pattern must contain the literal token '{n}', e.g. \"part-{n}.pdf\".", nameof(pathPattern));
        }

        for (var i = 0; i < Documents.Count; i++)
        {
            var path = pathPattern.Replace("{n}", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            Documents[i].Save(path, options);
        }
    }

    /// <summary>Disposes every document in <see cref="Documents"/>. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var document in Documents)
        {
            document.Dispose();
        }
    }
}
