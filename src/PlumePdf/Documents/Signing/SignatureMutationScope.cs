using PlumePdf.Objects;

namespace PlumePdf.Documents.Signing;

/// <summary>
/// Tracks the <see cref="ObjectRegistry"/> mutations a signing/timestamping pass performs while
/// attaching its new signature (or <c>/DocTimeStamp</c>) dictionary and field/widget —
/// <see cref="SigningOrchestrator"/>, <see cref="DocumentTimestamper"/>, and
/// <see cref="DssWriter"/> route every such
/// mutation through this scope instead of calling <c>document.Objects</c> directly, so that a
/// failure partway through (a TSA timeout, a signer/HSM error, a CMS build failure) can call
/// <see cref="Rollback"/> and leave the document exactly as it was before the call — rather than
/// permanently registering a half-finished <c>Objects.PdfContentsPlaceholder</c> that poisons a
/// caller's retry (a second registration trips <c>PLUME5013</c> on the very next attempt) or, if
/// the caller falls back to an ordinary save instead, gets serialized as a fake signature
/// (<c>PLUME5015</c> guards that path independently).
/// </summary>
internal sealed class SignatureMutationScope
{
    private readonly ObjectRegistry _registry;
    private readonly List<Action> _undo = [];

    public SignatureMutationScope(ObjectRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>Registers a brand-new indirect object, recording its removal for <see cref="Rollback"/>.</summary>
    public void RegisterNew(IndirectReference reference, PdfObject value)
    {
        _registry.RegisterNew(reference, value);
        _undo.Add(() => _registry.UnregisterNew(reference));
    }

    /// <summary>Marks an existing object dirty, recording its prior dirty state for <see cref="Rollback"/>.</summary>
    public void MarkDirty(IndirectReference reference)
    {
        var wasDirty = _registry.IsDirty(reference);
        _registry.MarkDirty(reference);
        if (!wasDirty)
        {
            _undo.Add(() => _registry.SetDirty(reference, false));
        }
    }

    /// <summary>
    /// Sets <paramref name="key"/> on <paramref name="dictionary"/> to <paramref name="value"/>
    /// in place, recording <paramref name="dictionary"/>'s prior value for that key (or its
    /// absence) for <see cref="Rollback"/>. Does <em>not</em> mark anything dirty — call
    /// <see cref="MarkDirty"/> for the owning object separately, matching
    /// <see cref="ObjectRegistry.MarkDirty"/>'s own contract.
    /// </summary>
    public void Set(PdfDictionary dictionary, PdfName key, PdfObject value)
    {
        if (dictionary.TryGetValue(key, out var previous))
        {
            dictionary.Set(key, value);
            _undo.Add(() => dictionary.Set(key, previous));
        }
        else
        {
            dictionary.Set(key, value);
            _undo.Add(() => dictionary.Remove(key));
        }
    }

    /// <summary>
    /// Undoes every mutation recorded through this scope, in reverse order, restoring the
    /// document's object graph to exactly the state it was in before this scope began.
    /// </summary>
    public void Rollback()
    {
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            _undo[i]();
        }
    }
}
