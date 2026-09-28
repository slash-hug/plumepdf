# PLUME6063 — redacting a permissions-restricted source (diagnostic) — **deprecated**

**Deprecated (2026-08-19):** This diagnostic was unreachable dead code from the day it shipped
and has been removed from `RedactionEngine`. It was meant to record the advisory
posture when `Redact` ran against a source whose `/Encrypt` dictionary clears the "modify"
permission bit — but `PdfDocument.Permissions` only differs from all-granted on an *encrypted*
source, and `Redact` refuses every encrypted source outright (`PLUME6061`) before the
permissions check could ever run. No reachable path could emit it, so no build has ever actually
recorded it. The advisory stance itself is unchanged: `/P` is advisory metadata; check
`document.Permissions` yourself if your application wants to enforce it as policy.

Per `docs/errors/README.md`: codes are never renumbered or reused once shipped; this page stays
to keep the code stable, rather than being deleted (the PLUME6026/PLUME6027 retirement
precedent).

---

*Original page, preserved for history:*

**Cause:** `Redact` was called on a document whose `/Encrypt` dictionary clears the "modify"
permission bit (bit 4, ISO 32000-1 §7.6.3.2 Table 22 — `PdfPermissions.Modify`). PlumePDF
treats `/P` as advisory metadata rather than an enforcement mechanism throughout the library
(the encryption key is already derived once a document opens successfully, so refusing based
on `/P` alone would be security theater) — this is recorded to `document.Diagnostics` as an
informational note, never a refusal.

**Fix:** No action needed — this is informational. If your application wants to *enforce*
`/P`'s modify restriction as a policy matter, check `document.Permissions.HasFlag(PdfPermissions.Modify)`
yourself before calling `Redact`.

**Recovery attempted:** N/A — this code never throws; it only appears as a
`DiagnosticSeverity.Info` entry.
