namespace PlumePdf.Fonts.Tables;

/// <summary>
/// The <c>hhea</c> table (OpenType spec §5.2.3) — horizontal-layout metrics, and the number
/// of explicit entries at the front of <c>hmtx</c> (<see cref="NumberOfHMetrics"/>).
/// </summary>
internal readonly struct HheaTable
{
    private const int MinimumLength = 36;

    public required short Ascender { get; init; }

    public required short Descender { get; init; }

    public required short LineGap { get; init; }

    public required ushort NumberOfHMetrics { get; init; }

    /// <summary>Parses a font's <c>hhea</c> table. Throws <c>PLUME8004</c> if it is shorter than the fixed 36-byte layout requires.</summary>
    public static HheaTable Parse(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        if (span.Length < MinimumLength)
        {
            throw new PlumePdfException("PLUME8004", $"'hhea' table is truncated: needs {MinimumLength} bytes, has {span.Length}.");
        }

        SfntPrimitives.TryReadInt16(span, 4, out var ascender);
        SfntPrimitives.TryReadInt16(span, 6, out var descender);
        SfntPrimitives.TryReadInt16(span, 8, out var lineGap);
        SfntPrimitives.TryReadUInt16(span, 34, out var numberOfHMetrics);

        return new HheaTable
        {
            Ascender = ascender,
            Descender = descender,
            LineGap = lineGap,
            NumberOfHMetrics = numberOfHMetrics,
        };
    }
}
