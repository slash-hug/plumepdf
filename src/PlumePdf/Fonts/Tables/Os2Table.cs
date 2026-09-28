namespace PlumePdf.Fonts.Tables;

/// <summary>
/// The <c>OS/2</c> table (OpenType spec §5.2.6) — weight/width class, typographic metrics,
/// and the fields <see cref="FontObjectBuilder"/> needs to compute a PDF
/// <c>/FontDescriptor</c>'s <c>/Flags</c>, <c>/CapHeight</c>, and <c>/StemV</c> estimate. The
/// table's byte layout has grown across OS/2 versions 0–5; this reads the version-0 core
/// (present in every version) plus the version-2+ fields (<see cref="CapHeight"/>), treating
/// the latter as optional rather than failing the whole table when they're absent.
/// </summary>
internal readonly struct Os2Table
{
    private const int Version0Length = 78;

    public required ushort WeightClass { get; init; }

    public required short TypoAscender { get; init; }

    public required short TypoDescender { get; init; }

    public required ushort FsSelection { get; init; }

    /// <summary>Bit 0 of <c>fsSelection</c>: the font is italic.</summary>
    public bool IsItalic => (FsSelection & 0x0001) != 0;

    /// <summary><c>sCapHeight</c> (version 2+ only). 0 when the table's version doesn't carry it.</summary>
    public required short CapHeight { get; init; }

    /// <summary>Parses a font's <c>OS/2</c> table. Throws <c>PLUME8004</c> if it is shorter than the version-0 core layout requires; version-2+ fields default to 0 when absent.</summary>
    public static Os2Table Parse(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        if (span.Length < Version0Length)
        {
            throw new PlumePdfException("PLUME8004", $"'OS/2' table is truncated: needs at least {Version0Length} bytes, has {span.Length}.");
        }

        SfntPrimitives.TryReadUInt16(span, 4, out var weightClass);
        SfntPrimitives.TryReadUInt16(span, 62, out var fsSelection);
        SfntPrimitives.TryReadInt16(span, 68, out var typoAscender);
        SfntPrimitives.TryReadInt16(span, 70, out var typoDescender);

        short capHeight = 0;
        if (span.Length >= 90)
        {
            SfntPrimitives.TryReadInt16(span, 88, out capHeight);
        }

        return new Os2Table
        {
            WeightClass = weightClass,
            TypoAscender = typoAscender,
            TypoDescender = typoDescender,
            FsSelection = fsSelection,
            CapHeight = capHeight,
        };
    }
}
