# PLUME5011 — signature larger than its reserved `/Contents` space

**Cause:** `Objects.SigningWriteSession.PatchContents` was given a CMS signature longer than the
`/Contents` reservation (`PdfSignOptions.ContentsReservationBytes`, or the automatic size
estimate) chosen when the signature dictionary was built. The reservation is
fixed before the real signature bytes exist (the placeholder's lexical width cannot change
without shifting every byte after it, which would invalidate every offset the write session
already computed) — an oversized signature is a coded refusal, never truncation.

**Example:** Not directly reachable through normal use — surfaces when a certificate chain,
timestamp token, or custom `IPdfSigner` produces a signature larger than PlumePDF's automatic
estimate anticipated.

**Fix:** Set a larger `PdfSignOptions.ContentsReservationBytes` explicitly, then sign again.

**Recovery attempted:** None — widening a placeholder in place after the fact would move every
byte after it, invalidating already-computed offsets; the only fix is a fresh signing pass with
a larger reservation.
