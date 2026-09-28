---
name: Feature request
about: Propose new API surface or behavior
title: ""
labels: enhancement
assignees: ""
---

<!--
Scope check before filing: docs/spec.md defines what PlumePDF is and deliberately is not
(rendering/rasterization, XFA, OCR, HTML-to-PDF, and Office conversion are permanently out
of scope). Features already declined need a new argument, not a re-request.
-->

**The problem**

What are you trying to do that PlumePDF can't (or makes awkward)? Concrete code showing the
gap beats an abstract description.

**Proposed shape**

What would the call look like? PlumePDF favors verb-first facades (`Pdf.*`,
`doc.*`), rich result objects over booleans, coded refusals over silent fallbacks, and
`PdfOptions` knobs over method-parameter sprawl — a proposal in that shape travels fastest.

```csharp
// Sketch of the API you'd want to write.
```

**Alternatives considered**

Including "do it with the existing object-model escape hatch (`doc.Objects`)" if you tried.
