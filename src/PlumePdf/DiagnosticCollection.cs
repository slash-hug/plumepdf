using System.Collections;
using System.Collections.Concurrent;

namespace PlumePdf;

/// <summary>
/// The thread-safe, append-only home for a document's <see cref="PdfDiagnostic"/> entries
/// — <c>doc.Diagnostics</c>. Lazy object resolution can run on multiple
/// threads concurrently once a document is open (<c>docs/architecture.md</c>, "Threading
/// &amp; mutation"), so appends must never race or lose entries. Enumeration takes a
/// snapshot at the moment it is called: an entry added after an enumerator was obtained is
/// never observed by that enumerator, and never causes it to throw.
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("damaged.pdf");
/// foreach (var diagnostic in document.Diagnostics)
/// {
///     Console.WriteLine(diagnostic);
/// }
/// </code>
/// </example>
public sealed class DiagnosticCollection : IReadOnlyCollection<PdfDiagnostic>
{
    private readonly ConcurrentQueue<PdfDiagnostic> _items = new();

    /// <inheritdoc/>
    public int Count => _items.Count;

    /// <summary>Appends a diagnostic. Safe to call from any thread, including concurrently with other appends and with enumeration.</summary>
    public void Add(PdfDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _items.Enqueue(diagnostic);
    }

    /// <inheritdoc/>
    public IEnumerator<PdfDiagnostic> GetEnumerator() => ((IEnumerable<PdfDiagnostic>)_items.ToArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
