# PLUME2063 — failed to read an object out of its object stream (diagnostic)

**Cause:** A cross-reference entry says an object lives inside an object stream, but reading
it failed — the container is not actually a valid object stream, the index is out of range
for the entries it declares, or its `/N`/`/First` framing is missing or malformed.

**Example:** A cross-reference stream claims the document catalog is entry 5 of object
stream 12, but object 12 declares only one entry.

**Fix:** Under default (lenient) reading the member object resolves as `null` and this
diagnostic records exactly which container and index failed; the rest of the document stays
readable. Under `PdfOptions.Strict` the same condition throws. Inspect the named object
stream in the source file to repair it.

**Recovery attempted:** The member object is treated like any unparseable in-file object
(the same shape as `PLUME2061`): resolved as `null`, never fatal to `PdfDocument.Open`.
