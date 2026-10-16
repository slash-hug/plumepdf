# Work with very large files

Multi-gigabyte documents, typically long scans, open and save with bounded memory, provided
you pick the right doors:

- **Open from a path.** `PdfDocument.Open(path)` memory-maps the file and reads objects only
  when they are used; opening an intact file costs its cross-reference data, not the file. A
  damaged file whose cross-reference data must be rebuilt by scanning the whole file is bounded
  by one .NET array (`PLUME1005`). `Open(Stream)` reads a seekable stream through a small buffer
  and never loads it whole; `OpenAsync` on a non-seekable stream, or with
  `PdfOptions.PreferStreamIo`, buffers the whole file. `Open(ReadOnlyMemory<byte>)` works on the
  buffer you already hold. Files over 2 GiB are supported. A single object needing more than one
  array (~2 GiB) can't be read; that object alone resolves as null with `PLUME2061` on
  `doc.Diagnostics`, and the rest of the document still opens.
- **`Save` holds one stream at a time.** A full rewrite reads each stream's payload once,
  writes it, and lets it go. Streams you have already read, and possibly edited in place, are
  written from your instance. Measured on a 2.67 GB scanned document: under 80 MB of managed
  heap, with or without `Optimize`. `SaveIncremental` copies the original bytes through a small
  buffer. Not yet bounded: a linearized save (`PdfOptions.Linearize`, `Pdf.Linearize`) and form
  flattening (`Form.Flatten`, `Pdf.FlattenForm`) still hold every stream they write.
- **Write to a different path.** A full `Save`, or a sign, LTV or timestamp write, over the file
  you opened first buffers the whole source into memory, on every platform (a mapped file can't
  be replaced on Windows, so the mapping is always released first), and refuses past 2 GiB with
  `PLUME1006`.
- **What you read stays cached until you dispose the document.** `ExtractImages`,
  `ExtractText`, `Rasterize` and `doc.Objects` all cache what they resolve, image payloads
  included, so walking every image of a 2 GB scan holds all of it. Work in batches of pages
  and reopen between batches, as below.

<!-- snippet: large-files -->
<a id='snippet-large-files'></a>
```cs
const string path = "output/large-files-scan.pdf";

// Open from a path: the file is memory-mapped and objects are read only when used,
// so opening a multi-gigabyte file costs little memory. Files over 2 GiB work.
int pageCount;
using (var document = PdfDocument.Open(path))
{
    pageCount = document.Pages.Count;

    // A full rewrite holds one stream at a time, not the whole document. Write to a
    // different path: saving over the file you opened first loads all of it into memory.
    document.Save("output/large-files-rewritten.pdf", PdfOptions.Default with { Optimize = true });
}

// Everything you read (ExtractImages, ExtractText, Rasterize, doc.Objects) stays cached
// until the document is disposed. To walk every image of a very large file, work in
// batches of pages and reopen between batches, so memory holds one batch at a time.
const int pagesPerBatch = 2;
var imageCount = 0;
for (var first = 0; first < pageCount; first += pagesPerBatch)
{
    using var batch = PdfDocument.Open(path);
    for (var i = first; i < Math.Min(first + pagesPerBatch, pageCount); i++)
    {
        imageCount += batch.Pages[i].ExtractImages().Count;
    }
}
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L581-L609' title='Snippet source file'>snippet source</a> | <a href='#snippet-large-files' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output:

<!-- snippet: CookbookTests.LargeFiles.verified.txt -->
<a id='snippet-CookbookTests.LargeFiles.verified.txt'></a>
```txt
Pages: 5
Images extracted in batches: 5
Rewritten copy reopens with pages: 5
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.LargeFiles.verified.txt#L1-L3' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.LargeFiles.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To make a large scan *smaller*, not just rewrite it, use `Pdf.Compress`: it downsamples
and re-encodes images one at a time while writing, so memory stays bounded by the largest
image — see [compress.md](compress.md).

Related: [compress.md](compress.md), [optimize-linearize.md](optimize-linearize.md)
(`Optimize` shrinks the cross-reference and object overhead; it doesn't recompress images),
[extract-images.md](extract-images.md), [handle-damaged-pdf.md](handle-damaged-pdf.md).
