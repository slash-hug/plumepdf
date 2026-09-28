namespace PlumePdf.Fonts.Tables;

/// <summary>The <c>maxp</c> table (OpenType spec §5.2.5) — this Phase 2 subset only needs <see cref="NumGlyphs"/>, present at a fixed offset in every version.</summary>
internal readonly struct MaxpTable
{
    private const int MinimumLength = 6;

    public required ushort NumGlyphs { get; init; }

    /// <summary>
    /// Parses a font's <c>maxp</c> table and validates its glyph count against
    /// <paramref name="limits"/>. Throws <c>PLUME8004</c> if the table is too short to hold
    /// even the version + numGlyphs fields, or <c>PLUME8005</c> if the declared glyph count
    /// exceeds <see cref="FontReadLimits.MaxFontGlyphCount"/> (a hostile font can declare an
    /// arbitrarily large glyph count independent of what its <c>loca</c>/<c>glyf</c> tables
    /// can actually back, so this is checked before anything allocates per-glyph storage).
    /// </summary>
    public static MaxpTable Parse(ReadOnlyMemory<byte> table, FontReadLimits limits)
    {
        var span = table.Span;
        if (span.Length < MinimumLength)
        {
            throw new PlumePdfException("PLUME8004", $"'maxp' table is truncated: needs {MinimumLength} bytes, has {span.Length}.");
        }

        SfntPrimitives.TryReadUInt16(span, 4, out var numGlyphs);
        if (numGlyphs > limits.MaxFontGlyphCount)
        {
            throw new PlumePdfException("PLUME8005", $"Font declares {numGlyphs} glyphs, exceeding the configured limit of {limits.MaxFontGlyphCount}.");
        }

        return new MaxpTable { NumGlyphs = numGlyphs };
    }
}
