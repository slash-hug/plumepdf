# PLUME1007 — could not write the output file

**Cause:** PlumePDF couldn't write a file it was asked to write to a path. The operating system
refused one of PlumePDF's own output-file operations: creating the temporary file next to the
destination, writing, flushing or closing it, or moving it into place over the destination
(`Save` and every path verb built on it — `Pdf.Compress`, `Pdf.Linearize`, `Pdf.FlattenForm`
and the rest — plus signing and document timestamping), or opening, appending to or writing the
destination directly (`SaveIncremental` and the path verbs built on it — `Pdf.Stamp`,
`Pdf.FillForm` without flattening, and the LTV write). The message names the
destination path and the operating system's reason; the original `IOException` or
`UnauthorizedAccessException` is the `InnerException`.

Failures that aren't the output file's own keep their own type: an exception thrown by a
caller's `Stream` (the `Pdf.Compress(Stream, Stream)` overload writes to your stream, so its
exceptions reach you unchanged), cancellation, and a failure reading the source while the output
is being written.

**Example:**

```csharp
document.Save("/no/such/directory/out.pdf"); // the directory doesn't exist
document.Save("/tmp");                       // the destination is a directory
document.Save("/read-only/out.pdf");         // no write permission in the directory
```

A full disk partway through a large save gives the same code.

**Fix:** Write to a path whose directory exists and that the process can write to; create the
directory first (`Directory.CreateDirectory`) if needed, and make sure the destination isn't a
directory or a read-only file. For a full disk, free space (or write somewhere else) and retry.
Check `InnerException` for the exact operating-system error.

**Recovery attempted:** None — PlumePDF doesn't retry a write. Writes that go through a
temporary file (`Save`, compress, signing, timestamping) leave no temporary file behind and an
existing file at the destination untouched. `SaveIncremental` writes the destination directly:
a failure partway through appending to the file the document was opened from can leave a
partial appended revision after the original bytes (the earlier revisions stay intact), and a
failure writing to a different path can leave a partial file there.
