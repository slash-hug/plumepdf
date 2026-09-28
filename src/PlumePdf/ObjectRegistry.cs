using System.Collections.Concurrent;
using System.Diagnostics;
using PlumePdf.Objects;

namespace PlumePdf;

/// <summary>
/// The public "escape hatch, all the way down" to a document's raw object graph
/// — <c>doc.Objects</c>. Resolution is lazy and safe for concurrent reads from
/// multiple threads: the first access to a given object number parses it from the source
/// and caches the result; later access, from any thread, returns the cached value
/// (<c>docs/architecture.md</c>, "Threading &amp; mutation"). Mutation
/// (<see cref="MarkDirty"/>, <see cref="RegisterNew"/>, <see cref="AllocateNumber"/>) is
/// single-threaded by contract, matching <see cref="PageCollection"/>'s own rule — never
/// call these while another thread may still be reading through this registry (e.g. parallel
/// page extraction), and never call them from more than one thread at once. A debug build
/// asserts this contract; a release build trusts it and pays no cost for checking.
/// </summary>
/// <example>
/// <code>
/// PdfDictionary trailer = document.Objects.Trailer;
/// if (trailer[PdfName.Root] is PdfReference rootRef)
/// {
///     PdfObject catalog = document.Objects[rootRef.Target];
/// }
/// </code>
/// </example>
public sealed class ObjectRegistry : IObjectSource
{
    private readonly IObjectSource _resolver;

    // Objects registered via RegisterNew — an overlay checked ahead of the underlying
    // resolver, so a brand-new object (never present in the source's cross-reference table
    // at all) resolves through this registry's own indexer exactly like an original one.
    // Concurrent-safe so the read side (the indexer) never needs a lock, matching the
    // resolver it wraps (docs/architecture.md "Threading & mutation").
    private readonly ConcurrentDictionary<int, PdfObject> _newObjects = new();

    // Every object marked dirty (MarkDirty) or registered new (RegisterNew) since this
    // document was opened/composed. The value is a discard byte — ConcurrentDictionary is
    // used purely as a concurrent hash-set (no ConcurrentHashSet exists in the BCL).
    private readonly ConcurrentDictionary<IndirectReference, byte> _dirty = new();

    // Object numbers the source's own cross-reference free list (ISO 32000-1 §7.5.4) marked
    // available for reuse, object 0 (the free-list head/terminator, never a real object)
    // already excluded, smallest-first so AllocateNumber's reuse order is stable under
    // PdfOptions.Deterministic.
    private readonly ConcurrentQueue<int> _freeNumbers;

    // The next number AllocateNumber hands out once the free list above is exhausted — one
    // past the highest object number this document is known to use (see PdfDocument.OpenCore
    // for how this is derived robustly against a /Size that undercounts the real maximum).
    private int _nextFreshNumber;

    // The single-writer guard: incremented for the duration of any mutating call, decremented
    // after. Not itself gated behind #if DEBUG — the increments are cheap Interlocked ops on
    // the already-rare, already-single-threaded-by-contract mutation path — but the read-side
    // check below is a plain System.Diagnostics.Debug.Assert, which the BCL itself compiles
    // away entirely (call site and all) outside a DEBUG build, so a release build's indexer is
    // byte-for-byte the same lock-free read it always was.
    private int _mutating;

    internal ObjectRegistry(IObjectSource resolver, int nextObjectNumber = 1, IReadOnlyCollection<int>? freeObjectNumbers = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentOutOfRangeException.ThrowIfNegative(nextObjectNumber);
        _resolver = resolver;
        _nextFreshNumber = nextObjectNumber;
        _freeNumbers = new ConcurrentQueue<int>(
            freeObjectNumbers is { Count: > 0 } free
                ? free.Where(static number => number != 0).OrderBy(static number => number)
                : []);
    }

    /// <summary>The document's trailer dictionary — the entry point to the catalog and other document-level data.</summary>
    public PdfDictionary Trailer => _resolver.Trailer;

    /// <summary>
    /// The next object number <see cref="AllocateNumber"/> would hand out once this registry's own
    /// free list is exhausted — a read-only peek, not itself a mutation (unlike
    /// <see cref="AllocateNumber"/>, it does not advance the counter). Internal plumbing only
    /// (<c>docs/architecture.md</c>: no public "next fresh number" accessor by design) for seeding
    /// <see cref="Raster.ScratchObjectRegistry.CreateFor"/> with a number guaranteed past every
    /// object number the real document is known to use — the seam the widget
    /// render-time appearance-synthesis path needs to build a scratch registry that can never
    /// collide with a real object.
    /// </summary>
    internal int NextFreshObjectNumber => Volatile.Read(ref _nextFreshNumber);

    /// <summary>Resolves the object identified by <paramref name="reference"/>, parsing and caching it on first access.</summary>
    public PdfObject this[IndirectReference reference]
    {
        get
        {
            Debug.Assert(
                Volatile.Read(ref _mutating) == 0,
                "PlumePDF mutation is single-threaded by contract and must not overlap concurrent "
                + "reads (docs/architecture.md \"Threading & mutation\"): doc.Objects was "
                + "read while a mutation (MarkDirty/RegisterNew/AllocateNumber, or a Pages "
                + "reorder/removal) was still in progress, most likely from another thread. Finish "
                + "mutating before starting parallel reads (e.g. parallel page extraction), or vice versa.");

            return _newObjects.TryGetValue(reference.Number, out var overridden) ? overridden : _resolver.Resolve(reference);
        }
    }

    /// <summary>
    /// Allocates a fresh <see cref="IndirectReference"/> for a brand-new indirect object not
    /// present in the source document (ISO 32000-1 §7.5.4; the same <c>(Number, Generation)</c>
    /// identity). Reuses a number the source's own cross-reference free list marked available
    /// first, at generation 1 — the conservative reading of §7.5.4's "one greater than the
    /// generation the freed entry carried" rule, since the freed entry's own stored next-generation
    /// value is not preserved by the reader upstream — falling back to the next number beyond
    /// every object number this document is known to use, at generation 0, once the free list is
    /// exhausted. Free numbers are handed out smallest-first and fresh numbers always extend
    /// strictly upward, so calling this the same number of times in the same order always produces
    /// the same sequence of references — the allocation-order guarantee <see cref="PdfOptions.Deterministic"/>
    /// depends on.
    /// </summary>
    /// <remarks>
    /// The returned reference is not yet resolvable through this registry, and nothing is marked
    /// dirty by this call alone — pass the reference to <see cref="RegisterNew"/> together with the
    /// object's value to make both true. Calling this without ever registering the returned
    /// reference is harmless (the number is simply never used).
    /// </remarks>
    /// <example>
    /// <code>
    /// var reference = document.Objects.AllocateNumber();
    /// var annotation = new PdfDictionary();
    /// annotation.Set(PdfName.Type, PdfName.Get("Annot"));
    /// document.Objects.RegisterNew(reference, annotation);
    /// </code>
    /// </example>
    public IndirectReference AllocateNumber()
    {
        Interlocked.Increment(ref _mutating);
        try
        {
            return _freeNumbers.TryDequeue(out var reused)
                ? new IndirectReference(reused, 1)
                : new IndirectReference(Interlocked.Increment(ref _nextFreshNumber) - 1, 0);
        }
        finally
        {
            Interlocked.Decrement(ref _mutating);
        }
    }

    /// <summary>
    /// Marks the existing indirect object <paramref name="reference"/> as changed since this
    /// document was opened (or last saved). PlumePDF's object graph is mutated in place — resolve
    /// a dictionary through <c>document.Objects[reference]</c> and call
    /// <see cref="PdfDictionary.Set(PdfName,PdfObject)"/> directly on it — so nothing else observes
    /// that an edit happened; this call is what tells the next <see cref="PdfDocument.Save"/>/
    /// <see cref="PdfDocument.SaveIncremental(string,PdfOptions?)"/> to actually write the object's
    /// new value. An in-place edit made without a matching <see cref="MarkDirty"/> call is invisible
    /// to <see cref="PdfDocument.SaveIncremental(string,PdfOptions?)"/> and is silently dropped —
    /// <see cref="PdfDocument.Save"/> happens to still pick it up today because it always walks
    /// every reachable object from scratch, but that is an implementation detail, not a contract;
    /// call <see cref="MarkDirty"/> for every in-place edit regardless of which save path you use.
    /// </summary>
    /// <example>
    /// <code>
    /// if (document.Objects[fieldReference] is PdfDictionary field)
    /// {
    ///     field.Set(PdfName.Get("V"), PdfString.FromLiteral("Jane Doe"u8));
    ///     document.Objects.MarkDirty(fieldReference);
    /// }
    ///
    /// document.SaveIncremental("filled.pdf");
    /// </code>
    /// </example>
    public void MarkDirty(IndirectReference reference)
    {
        Interlocked.Increment(ref _mutating);
        try
        {
            _dirty[reference] = 0;
        }
        finally
        {
            Interlocked.Decrement(ref _mutating);
        }
    }

    /// <summary>
    /// Registers <paramref name="value"/> as a brand-new indirect object at
    /// <paramref name="reference"/> — normally a reference just obtained from
    /// <see cref="AllocateNumber"/> — making it resolvable through this registry's indexer from
    /// this point on and marking it dirty for the next save, in one call.
    /// </summary>
    /// <param name="reference">
    /// The object's identity. Passing a reference not obtained from <see cref="AllocateNumber"/> —
    /// in particular, one already in use by an existing object — permanently shadows that existing
    /// object's value with <paramref name="value"/> for the remainder of this document's lifetime;
    /// always allocate first.
    /// </param>
    /// <param name="value">The new object's value.</param>
    public void RegisterNew(IndirectReference reference, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Interlocked.Increment(ref _mutating);
        try
        {
            _newObjects[reference.Number] = value;
            _dirty[reference] = 0;
        }
        finally
        {
            Interlocked.Decrement(ref _mutating);
        }
    }

    /// <summary>
    /// Replaces the value an already-existing indirect object resolves to with
    /// <paramref name="newValue"/>, keeping <paramref name="reference"/>'s own object
    /// identity, and marks it dirty for the next save — the escape hatch for an object whose
    /// type has no setters of its own (<see cref="PdfStream"/> exposes no way to change
    /// <see cref="PdfStream.RawBytes"/> in place, by design; the copy-on-materialize
    /// contract). Phase 6 redaction uses this to re-point a page's
    /// <c>/Contents</c> stream at its redacted replacement while every other reference to that
    /// same object number — anywhere else in the graph — observes the new content too, without
    /// minting a new object number the way <see cref="RegisterNew"/> would. Shares the same
    /// new-object overlay <see cref="RegisterNew"/> writes into, so a replaced object resolves
    /// through this registry's indexer immediately, exactly like a brand-new one.
    /// </summary>
    /// <param name="reference">
    /// The existing object to replace. Must be a number this document already knows about —
    /// either an original object from the source, or one this registry already handed out via
    /// <see cref="AllocateNumber"/> — never a number picked arbitrarily; passing one throws,
    /// catching the "meant <see cref="RegisterNew"/> instead" mistake at the call site rather
    /// than silently creating an object with no original identity to replace.
    /// </param>
    /// <param name="newValue">The object's new value.</param>
    /// <exception cref="ArgumentException"><paramref name="reference"/>'s object number was never allocated in this document.</exception>
    /// <example>
    /// <code>
    /// if (document.Objects[pageReference] is PdfDictionary page
    ///     &amp;&amp; page[PdfName.Get("Contents")] is PdfReference contentsRef)
    /// {
    ///     var redacted = new PdfStream(new PdfDictionary(), redactedBytes);
    ///     document.Objects.Replace(contentsRef.Target, redacted);
    /// }
    /// </code>
    /// </example>
    public void Replace(IndirectReference reference, PdfObject newValue)
    {
        ArgumentNullException.ThrowIfNull(newValue);
        Interlocked.Increment(ref _mutating);
        try
        {
            if (!_newObjects.ContainsKey(reference.Number) && reference.Number >= _nextFreshNumber)
            {
                throw new ArgumentException($"Object {reference.Number} was never allocated in this document (its number is at or beyond the next fresh number this registry would hand out) — use RegisterNew for a brand-new object instead of Replace.", nameof(reference));
            }

            _newObjects[reference.Number] = newValue;
            _dirty[reference] = 0;
        }
        finally
        {
            Interlocked.Decrement(ref _mutating);
        }
    }

    /// <summary>
    /// Reverses one <see cref="RegisterNew"/> call: removes <paramref name="reference"/> from
    /// both the new-object overlay and the dirty set, as if it had never been registered.
    /// Internal rollback plumbing for a mutation that must not survive a failed multi-step
    /// operation (e.g. <c>Documents.Signing.SigningOrchestrator</c>'s signature-dictionary
    /// registration when CMS building fails partway through) — never call this for a reference
    /// any other code may already be holding onto as resolvable, since it makes the number
    /// unresolvable again without returning it to the free list.
    /// </summary>
    internal void UnregisterNew(IndirectReference reference)
    {
        Interlocked.Increment(ref _mutating);
        try
        {
            _newObjects.TryRemove(reference.Number, out _);
            _dirty.TryRemove(reference, out _);
        }
        finally
        {
            Interlocked.Decrement(ref _mutating);
        }
    }

    /// <summary>Whether <paramref name="reference"/> is currently in the dirty set (<see cref="MarkDirty"/>/<see cref="RegisterNew"/>).</summary>
    internal bool IsDirty(IndirectReference reference) => _dirty.ContainsKey(reference);

    /// <summary>
    /// Sets or clears <paramref name="reference"/>'s dirty flag directly, without touching the
    /// new-object overlay. Rollback plumbing paired with <see cref="IsDirty"/>: a caller that
    /// mutates a pre-existing object in place and marks it dirty can snapshot
    /// <see cref="IsDirty"/> first and restore it here if the surrounding operation fails —
    /// this only restores the flag, not the in-place edit itself, so callers doing that must
    /// undo the edit separately.
    /// </summary>
    internal void SetDirty(IndirectReference reference, bool dirty)
    {
        Interlocked.Increment(ref _mutating);
        try
        {
            if (dirty)
            {
                _dirty[reference] = 0;
            }
            else
            {
                _dirty.TryRemove(reference, out _);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _mutating);
        }
    }

    /// <summary>
    /// Every object marked dirty (<see cref="MarkDirty"/>) or registered new
    /// (<see cref="RegisterNew"/>) since this document was opened, paired with its current value,
    /// in ascending <c>(Number, Generation)</c> order — the order every writer consumes it in, so
    /// output stays stable under <see cref="PdfOptions.Deterministic"/>.
    /// </summary>
    internal IReadOnlyList<(IndirectReference Reference, PdfObject Value)> DirtyObjects =>
        _dirty.Keys
            .OrderBy(static reference => reference.Number)
            .ThenBy(static reference => reference.Generation)
            .Select(reference => (reference, this[reference]))
            .ToList();

    /// <summary>Called by <see cref="PdfDocument"/> to fold its own mutation entry points (e.g. a <see cref="PageCollection"/> reorder/removal) into this registry's single-writer guard, so every mutation path — not just <see cref="MarkDirty"/>/<see cref="RegisterNew"/>/<see cref="AllocateNumber"/> — is covered by one contract.</summary>
    internal void EnterExternalMutation() => Interlocked.Increment(ref _mutating);

    /// <summary>
    /// Whether <paramref name="reference"/> would resolve to a registered or live in-file object —
    /// no parse, no diagnostic. Used for hint-class dictionary reads that must not change a
    /// document's diagnostics or its <c>Strict</c> outcome; everything that affects
    /// what is painted keeps going through the indexer and its <c>PLUME2060</c> reporting.
    /// </summary>
    internal bool IsResolvable(IndirectReference reference) =>
        _newObjects.ContainsKey(reference.Number)
        || _resolver is not ObjectResolver fileResolver // an in-memory source never diagnoses — resolve normally
        || fileResolver.HasLiveEntry(reference.Number);

    /// <summary>Pairs with <see cref="EnterExternalMutation"/>.</summary>
    internal void ExitExternalMutation() => Interlocked.Decrement(ref _mutating);

    // Explicit implementation: IObjectSource is internal plumbing (ObjectResolver's own
    // contract) that extraction's font/encoding resolvers (PlumePdf.Fonts.Reading) also
    // consume against a document's object graph — implementing it here lets them take an
    // ObjectRegistry directly rather than needing a second adapter type, without adding a new
    // public member to ObjectRegistry's own surface (the indexer above already does this job
    // for public callers).
    PdfDictionary IObjectSource.Trailer => Trailer;

    PdfObject IObjectSource.Resolve(IndirectReference reference) => this[reference];
}
