---
name: Bug report
about: Something PlumePDF does wrong — a crash, an incorrect result, or a coded refusal that looks mistaken
title: ""
labels: bug
assignees: ""
---

<!--
Security-shaped reports (memory/CPU exhaustion on untrusted input, signature verification
overclaiming trust, redacted content recoverable, crashes on malformed input) go through the
private flow in .github/SECURITY.md — please do not file them as public issues.
-->

**What happened**

A clear description of the behavior, including the full `PlumePdfException` code + message if
one was thrown (the `PLUME####` code and its `HelpLink` page are the fastest route to a
diagnosis), or the relevant `doc.Diagnostics` entries.

**Minimal reproduction**

```csharp
// The smallest program that shows the problem.
```

If the problem is input-dependent, attach the smallest PDF that reproduces it — or, if the
file can't be shared, the output of `qpdf --check` / a description of its structure
(encryption, xref style, producer).

**Expected behavior**

What you expected instead — including, for lenient-vs-strict questions, whether you ran with
`PdfOptions.Strict`.

**Environment**

- PlumePdf version (NuGet version or commit):
- .NET version / OS:
- Deployment shape (JIT / NativeAOT):
