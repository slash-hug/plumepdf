namespace PlumePdf.Fonts.Tables;

/// <summary>
/// The <c>head</c> table (OpenType spec §5.2.4) — font-wide metrics needed by every other
/// table: the glyph coordinate space (<see cref="UnitsPerEm"/>), the glyph bounding box, and
/// which <c>loca</c> table format (<see cref="IndexToLocFormat"/>) the font uses.
/// </summary>
internal readonly struct HeadTable
{
    private const int MinimumLength = 54;

    public required ushort UnitsPerEm { get; init; }

    public required short XMin { get; init; }

    public required short YMin { get; init; }

    public required short XMax { get; init; }

    public required short YMax { get; init; }

    public required ushort MacStyle { get; init; }

    /// <summary>0 = <c>loca</c> entries are <c>uint16</c> half-offsets; 1 = <c>uint32</c> byte offsets.</summary>
    public required short IndexToLocFormat { get; init; }

    /// <summary>Parses a font's <c>head</c> table. Throws <c>PLUME8004</c> if it is shorter than the fixed 54-byte layout requires.</summary>
    public static HeadTable Parse(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        if (span.Length < MinimumLength)
        {
            throw new PlumePdfException("PLUME8004", $"'head' table is truncated: needs {MinimumLength} bytes, has {span.Length}.");
        }

        SfntPrimitives.TryReadUInt16(span, 18, out var unitsPerEm);
        SfntPrimitives.TryReadInt16(span, 36, out var xMin);
        SfntPrimitives.TryReadInt16(span, 38, out var yMin);
        SfntPrimitives.TryReadInt16(span, 40, out var xMax);
        SfntPrimitives.TryReadInt16(span, 42, out var yMax);
        SfntPrimitives.TryReadUInt16(span, 44, out var macStyle);
        SfntPrimitives.TryReadInt16(span, 50, out var indexToLocFormat);

        return new HeadTable
        {
            // unitsPerEm of 0 is nonsensical (would divide-by-zero downstream in every
            // em-to-PDF-space conversion) — fall back to the common 1000 rather than
            // propagate a value that turns every subsequent metric computation into NaN.
            UnitsPerEm = unitsPerEm == 0 ? (ushort)1000 : unitsPerEm,
            XMin = xMin,
            YMin = yMin,
            XMax = xMax,
            YMax = yMax,
            MacStyle = macStyle,
            IndexToLocFormat = indexToLocFormat,
        };
    }
}
