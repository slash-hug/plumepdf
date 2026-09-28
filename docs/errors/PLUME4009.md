# PLUME4009 — malformed ASN.1/DER input

**Cause:** `Objects.Signing.DerNormalizer.Normalize` was given input that is empty, contains
trailing bytes after its first top-level ASN.1 value, is not well-formed BER/CER, or uses an
ASN.1 high-tag-number form (tag number above 30) this normalizer does not support.

**Example:** Not directly reachable through the public API with well-formed input — surfaces
only when a CMS/timestamp/OCSP/CRL blob PlumePDF built or received (from an `IPdfSigner`,
`ITimestampAuthority`, or `IRevocationFetcher` implementation) is itself malformed.

**Fix:** If a custom `IPdfSigner`/`ITimestampAuthority`/`IRevocationFetcher` implementation
produced the blob, verify it returns a single, well-formed, standard-tag-range ASN.1 value.

**Recovery attempted:** None — DER normalization has no meaningful partial-recovery path for
malformed ASN.1; this is treated as an unrecoverable input failure, not a lenient-reading
deviation.
