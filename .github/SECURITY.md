# Security Policy

PlumePDF parses untrusted input by design — a PDF file is an adversarial artifact, and the
library's resource-limit options (`PdfOptions`' per-hazard caps), coded refusals, and
no-reflection/AOT posture exist for exactly that reason. Security reports are taken seriously
and handled privately.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Use GitHub's private vulnerability reporting: **Security → Report a vulnerability** on this
repository. That opens a private advisory thread with the maintainer where the report,
reproduction, and fix can be discussed without exposing users before a patched release exists.

A useful report includes:

- the PlumePDF version (NuGet package version or commit),
- a minimal reproducing input — ideally the smallest PDF (or byte sequence) that triggers the
  behavior — and the API call that processes it,
- the observed impact (crash, unbounded memory/CPU, incorrect trust decision such as a
  signature verifying when it should not, redacted content surviving, etc.).

You should receive an acknowledgment within a few days. Coordinated disclosure is preferred:
please allow a fix to ship before publishing details.

## Scope notes

Reports especially in scope:

- memory or CPU exhaustion a `PdfOptions` cap should have prevented (a missing or ineffective
  cap on attacker-controlled input is a bug, not a configuration issue),
- signature verification returning a stronger trust claim than the bytes justify,
- redaction output from which redacted content can be recovered,
- crashes (as opposed to coded `PlumePdfException`s) on malformed input.

Not vulnerabilities:

- a malformed PDF being *rejected* with a coded `PlumePdfException` — that is the designed
  behavior for unrecoverable input,
- resource exhaustion only reachable by explicitly raising the relevant `PdfOptions` cap above
  its default.

## Supported versions

The latest minor release of the current major version is supported with security fixes.
