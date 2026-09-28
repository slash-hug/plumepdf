namespace PlumePdf.Fonts.Tables;

/// <summary>
/// The <c>post</c> table (OpenType spec §5.2.7) — this Phase 2 subset reads only the
/// version-independent header fields (<see cref="ItalicAngle"/>, <see cref="IsFixedPitch"/>):
/// glyph-name tables (versions 1/2) are not needed for embedding or <c>/ToUnicode</c>.
/// </summary>
internal readonly struct PostTable
{
    private const int MinimumLength = 32;

    public required double ItalicAngle { get; init; }

    public required bool IsFixedPitch { get; init; }

    /// <summary>Parses a font's <c>post</c> table header. Throws <c>PLUME8004</c> if it is shorter than the fixed 32-byte header.</summary>
    public static PostTable Parse(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        if (span.Length < MinimumLength)
        {
            throw new PlumePdfException("PLUME8004", $"'post' table is truncated: needs {MinimumLength} bytes, has {span.Length}.");
        }

        SfntPrimitives.TryReadFixed(span, 4, out var italicAngle);
        SfntPrimitives.TryReadUInt32(span, 12, out var isFixedPitch);

        return new PostTable
        {
            ItalicAngle = italicAngle,
            IsFixedPitch = isFixedPitch != 0,
        };
    }
}
