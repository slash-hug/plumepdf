# PLUME5015 — signature placeholder written outside a signing pass

**Cause:** `Objects.ObjectSerializer.WriteValue` encountered a `Objects.PdfContentsPlaceholder`
or `Objects.PdfByteRangePlaceholder` while writing with no `onPlaceholder` callback supplied —
i.e. an ordinary `PdfDocument.Save`/`SaveIncremental` call, not a
`Objects.SigningWriteSession` pass. Both placeholder types are writer-internal plumbing that
should only ever exist in an `ObjectRegistry`'s dirty-object set for the brief span between
`SigningOrchestrator`/`DocumentTimestamper` registering a new signature dictionary and that same
call's `SigningWriteSession` patching in the real `/Contents`/`/ByteRange` values. Reaching this
code path with `onPlaceholder == null` means a prior `doc.Signatures.SignAsync`/
`AddDocumentTimestampAsync` call registered the placeholder but then failed before patching it
in — without this guard, `Save`/`SaveIncremental` would happily serialize the placeholder's fake
`<0000...0000>` `/Contents` and all-zero `/ByteRange` as a syntactically well-formed but entirely
fake signature dictionary, with no error at all.

**Example:**

```csharp
using var document = PdfDocument.Open("input.pdf");
try
{
    await document.Signatures.SignAsync("signed.pdf", new PdfSignOptions { Signer = failingHsmSigner });
}
catch (PlumePdfException)
{
    // A failed signing pass now rolls back its own registry mutations (SigningOrchestrator),
    // so document.Objects no longer carries the half-finished placeholder here — but if that
    // rollback were ever bypassed, the next line would trip PLUME5015 instead of silently
    // writing a fake signature.
    document.SaveIncremental("fallback.pdf");
}
```

**Fix:** This should not be reachable through the public API — `SigningOrchestrator` and
`DocumentTimestamper` roll back their own `ObjectRegistry` mutations when a signing pass fails
partway through, so a failed `SignAsync`/`AddDocumentTimestampAsync` call never leaves an
unfinished placeholder registered. If this surfaces through normal use, file an issue; it
indicates a gap in that rollback, not a malformed input document. Discard the `PdfDocument`
instance and re-open/re-sign rather than trying to save it directly.

**Recovery attempted:** None — this is a defensive check against an internal-invariant failure
(a half-finished signing pass), not a recoverable document deviation.
