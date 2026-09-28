# PLUME4011 — OCSP/CRL revocation fetch failed

**Cause:** `IO.Http.HttpRevocationFetcher` (the default, opt-in-only `IRevocationFetcher`)
could not complete an OCSP request or CRL fetch: the request itself failed (DNS,
connection, timeout), the responder/distribution point returned a non-success HTTP status, or
an OCSP responder returned no successful response.

**Example:**

```csharp
var fetcher = new IO.Http.HttpRevocationFetcher();
await document.Signatures.AddLtvAsync("out.pdf", fetcher); // throws PLUME4011 if a responder is unreachable
```

**Fix:** Confirm network connectivity and that the certificate's AIA/CDP URLs are reachable
from this process. This never happens unless the caller explicitly supplies a network-backed
`IRevocationFetcher` — offline by default.

**Recovery attempted:** None — a failed revocation fetch is a hard failure for the LTV
operation requesting it, not a value PlumePDF can silently substitute.
