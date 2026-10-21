using System.Collections.Concurrent;

namespace PlumePdf.Objects;

/// <summary>
/// One read operation's own stream cache. While a scope is active on the current call's flow
/// (an <see cref="AsyncLocal{T}"/>, so concurrent calls on other threads each have their own),
/// a stream the document hasn't already cached is parsed into the scope instead of the
/// document's cache, and released when the scope ends. Repeat resolves within the scope get
/// the same instance, so identity-keyed per-call caches keep working; a stream already cached
/// (one the caller holds through <c>doc.Objects</c>, edits included) is the one returned.
/// Non-stream objects are cached as usual.
/// </summary>
/// <remarks>
/// The read verbs (<c>ExtractImages</c>, <c>ExtractText</c>, <c>Rasterize</c>) open one per page,
/// so reading every page of a large document holds one page's streams at a time rather than
/// every stream the document has.
/// </remarks>
internal sealed class StreamReadScope : IDisposable
{
    private static readonly AsyncLocal<StreamReadScope?> CurrentScope = new();

    private readonly ObjectResolver _resolver;
    private readonly StreamReadScope? _previous;
    private readonly ConcurrentDictionary<int, PdfObject> _streams = new();
    private bool _disposed;

    private StreamReadScope(ObjectResolver resolver, StreamReadScope? previous)
    {
        _resolver = resolver;
        _previous = previous;
    }

    /// <summary>
    /// Begins a scope for <paramref name="objects"/>'s file resolver on the current flow. A
    /// document with no file behind it holds every object anyway, and a scope already open for
    /// the same resolver is kept (nested reads share the outer one), so either way the returned
    /// handle may do nothing on dispose.
    /// </summary>
    public static IDisposable Begin(ObjectRegistry objects)
    {
        if (objects.FileResolver is not { } resolver || CurrentScope.Value?.For(resolver) is not null)
        {
            return NoScope.Instance;
        }

        var scope = new StreamReadScope(resolver, CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>The scope open for <paramref name="resolver"/> on the current flow, or null.</summary>
    internal static StreamReadScope? Current(ObjectResolver resolver) => CurrentScope.Value?.For(resolver);

    /// <summary>The stream this scope already read as object <paramref name="number"/>, or the result of <paramref name="read"/>, kept for the scope when it is a stream.</summary>
    internal PdfObject GetOrRead(int number, Func<int, PdfObject> read)
    {
        if (_streams.TryGetValue(number, out var held))
        {
            return held;
        }

        var value = read(number);
        return value is PdfStream ? _streams.GetOrAdd(number, value) : value;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _streams.Clear();
        CurrentScope.Value = _previous;
    }

    private StreamReadScope? For(ObjectResolver resolver)
    {
        for (var scope = this; scope is not null; scope = scope._previous)
        {
            if (ReferenceEquals(scope._resolver, resolver))
            {
                return scope;
            }
        }

        return null;
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}
