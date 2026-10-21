namespace PlumePdf.Objects;

/// <summary>
/// A write-time replacement for individual streams during a full-rewrite save. The writer
/// offers each stream it is about to write (after caller overrides, never an excluded
/// number) and, when <see cref="Handles"/> says yes, writes what <see cref="Transform"/>
/// returns instead, then lets both go. That keeps a whole-document recompression to one
/// stream in memory at a time.
/// </summary>
/// <remarks>
/// A replacement may reference only a subset of the objects the original references: the
/// writer has already numbered everything reachable, so a new reference has no number to
/// write and is refused (<c>PLUME5010</c>).
/// </remarks>
internal interface IStreamWriteTransform
{
    /// <summary>Whether <see cref="Transform"/> wants the stream originally numbered <paramref name="originalNumber"/>.</summary>
    bool Handles(int originalNumber);

    /// <summary>The stream to write in place of <paramref name="original"/>, which may be <paramref name="original"/> itself.</summary>
    PdfStream Transform(int originalNumber, PdfStream original);
}

/// <summary>
/// A <see cref="IStreamWriteTransform"/> that stands in for streams without reading them: the
/// writer hands it the stream's dictionary as discovery read it, and writes what
/// <see cref="ReplaceUnread"/> returns without ever parsing the original's payload. A dry run
/// that only measures the output uses it, so it never reads a large image it won't encode.
/// </summary>
/// <remarks>The same rule applies: the replacement may reference only a subset of what the dictionary does.</remarks>
internal interface IUnreadStreamWriteTransform : IStreamWriteTransform
{
    /// <summary>The stream to write in place of the stream originally numbered <paramref name="originalNumber"/>, whose dictionary is <paramref name="dictionary"/>.</summary>
    PdfStream ReplaceUnread(int originalNumber, PdfDictionary dictionary);
}
