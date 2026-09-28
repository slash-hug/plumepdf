# PLUME4012 — RFC 3161 timestamp request failed or was not configured

**Cause:** Either a `PdfSignatureLevel.T`+ signing/timestamp request had no
`ITimestampAuthority` configured (`PdfSignOptions.TimestampAuthority`/`.TimestampAuthorityUrl`,
or `AddDocumentTimestampAsync`'s required parameter), or `IO.Http.HttpTimestampAuthority` (the
default, opt-in-only implementation) could not complete the request: request
construction failed, the TSA was unreachable, returned a non-success HTTP status, or returned a
token that does not answer the request.

**Example:**

```csharp
document.Signatures.Add("out.pdf", new PdfSignOptions { Certificate = cert, Level = PdfSignatureLevel.T }); // throws PLUME4012 — no TSA configured
```

**Fix:** Supply `PdfSignOptions.TimestampAuthority` (or `.TimestampAuthorityUrl`) for a B-T+
signing request, or `AddDocumentTimestampAsync`'s `timestampAuthority` parameter; confirm the
endpoint is reachable and RFC 3161-compliant.

**Recovery attempted:** None — a missing or failed timestamp is a hard failure for the
operation requesting it (offline-by-default), not a value PlumePDF can silently omit or
substitute.
