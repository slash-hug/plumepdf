# PLUME8015 — CMap declares more entries than the configured limit

**Cause:** A `/ToUnicode` overlay stream or a Type0 font's embedded `/Encoding` CMap stream declares more `begincodespacerange`/`begincidrange`/`begincidchar`/`beginbfrange`/`beginbfchar` entries (counting each element of an array-form `beginbfrange` target individually) than the configured limit — a decompression-bomb-shaped hazard on untrusted input, not something any real-world CMap approaches.

**Example:** A crafted `/ToUnicode` stream with millions of `beginbfchar`/`endbfchar` blocks.

**Fix:** This is expected behavior against hostile input, not a bug to fix in a legitimate document. If a real-world font's CMap is legitimately this large, raise `PdfOptions.MaxCMapEntries`.

**Recovery attempted:** Parsing stops at the limit and returns a partial `CMap` built from everything parsed so far — entries declared after the limit are simply absent from lookups, never a crash. `PdfOptions.Strict` throws this code instead of tolerating the partial map.
