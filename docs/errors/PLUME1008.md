# PLUME1008 — could not create the temporary copy of a non-seekable input

**Cause:** `Pdf.Compress(Stream, Stream)` (or `CompressAsync`) was given an input stream that
can't seek — an HTTP response body, say — so PlumePDF copies it to a temporary file in the
system temp directory (`Path.GetTempPath()`) to read it with random access. Creating or
writing that file failed: the temp directory doesn't exist, isn't writable, or the disk filled
up. The message names the temporary file's path and the operating system's reason; the
original `IOException` or `UnauthorizedAccessException` is the `InnerException`. Much more
rarely, removing the temporary file after a successful run failed; the message then says the
output was written.

A failure *reading your input stream* while it is copied is not this code: it propagates exactly
as your stream threw it.

**Example:**

```csharp
// TMPDIR points at a directory that doesn't exist, or the temp volume is full.
await Pdf.CompressAsync(httpResponseBody, upload);
```

**Fix:** Make sure the temp directory exists, is writable and has room for a copy of the whole
input (on Unix, `TMPDIR`; on Windows, `TEMP`/`TMP`). Or give `Compress` a seekable input — a
`FileStream` or a `MemoryStream` — which is read in place and never copied.

**Recovery attempted:** None. The failure comes before anything is written to the output
stream, and no temporary file is left behind.
