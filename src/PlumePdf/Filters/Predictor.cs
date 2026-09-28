namespace PlumePdf.Filters;

/// <summary>
/// PNG (predictor values 10-15, ISO 32000-1 §7.4.4.4 Table 8) and TIFF (predictor value 2)
/// un-filtering, applied after a stream's base filter (Flate or LZW) has decompressed its
/// bytes. PNG prediction is per-row and self-describing (each row is prefixed with a
/// filter-type byte); TIFF prediction is horizontal differencing across each row's
/// samples. Both operate purely on bytes and small integer parameters — no dependency on
/// the object model, which is exactly why Filters sits below Objects.
/// </summary>
internal static class Predictor
{
    /// <summary>
    /// Reverses predictor <paramref name="predictor"/> over <paramref name="data"/>.
    /// A <paramref name="predictor"/> of 1 or less means "no prediction" and returns the
    /// input unchanged.
    /// </summary>
    public static byte[] Undo(ReadOnlySpan<byte> data, int predictor, int colors, int bitsPerComponent, int columns)
    {
        if (predictor <= 1)
        {
            return data.ToArray();
        }

        // Each parameter must be a positive integer before the row-width arithmetic means
        // anything: negative values can combine to a positive rowBytes while still driving
        // negative indices inside the per-row loops (e.g. /Colors -1 with TIFF prediction).
        if (colors < 1 || bitsPerComponent < 1 || columns < 1)
        {
            throw new PlumePdfException("PLUME3103", $"Predictor parameters must be positive integers; got /Colors {colors}, /BitsPerComponent {bitsPerComponent}, /Columns {columns}.");
        }

        var bytesPerPixel = Math.Max(1, ((colors * bitsPerComponent) + 7) / 8);
        var rowBytes = ((colors * bitsPerComponent * columns) + 7) / 8;

        if (rowBytes <= 0)
        {
            throw new PlumePdfException("PLUME3100", "Predictor /Columns, /Colors, and /BitsPerComponent combine to a zero-byte row width.");
        }

        return predictor == 2
            ? UndoTiff(data, colors, bitsPerComponent, rowBytes)
            : UndoPng(data, bytesPerPixel, rowBytes);
    }

    private static byte[] UndoPng(ReadOnlySpan<byte> data, int bytesPerPixel, int rowBytes)
    {
        var stride = rowBytes + 1; // +1 for the per-row filter-type byte
        var rowCount = data.Length / stride;
        var output = new byte[rowCount * rowBytes];
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];

        for (var row = 0; row < rowCount; row++)
        {
            var srcOffset = row * stride;
            var filterType = data[srcOffset];
            var srcRow = data.Slice(srcOffset + 1, rowBytes);

            for (var i = 0; i < rowBytes; i++)
            {
                var raw = srcRow[i];
                byte a = i >= bytesPerPixel ? current[i - bytesPerPixel] : (byte)0;
                var b = previous[i];
                byte c = i >= bytesPerPixel ? previous[i - bytesPerPixel] : (byte)0;

                current[i] = filterType switch
                {
                    0 => raw,
                    1 => unchecked((byte)(raw + a)),
                    2 => unchecked((byte)(raw + b)),
                    3 => unchecked((byte)(raw + ((a + b) / 2))),
                    4 => unchecked((byte)(raw + PaethPredictor(a, b, c))),
                    _ => throw new PlumePdfException("PLUME3101", $"Unsupported PNG predictor filter type {filterType} on row {row}."),
                };
            }

            current.CopyTo(output.AsSpan(row * rowBytes, rowBytes));
            (previous, current) = (current, previous);
        }

        return output;
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    private static byte[] UndoTiff(ReadOnlySpan<byte> data, int colors, int bitsPerComponent, int rowBytes)
    {
        if (bitsPerComponent != 8)
        {
            // Sub-byte TIFF predictor components are vanishingly rare in the xref-stream and
            // object-stream contexts Phase 1 targets; scope this to the common 8-bit case.
            throw new PlumePdfException("PLUME3102", $"TIFF predictor with {bitsPerComponent}-bit components is not supported.");
        }

        var output = data.ToArray();
        var rowCount = output.Length / rowBytes;

        for (var row = 0; row < rowCount; row++)
        {
            var rowStart = row * rowBytes;
            for (var i = colors; i < rowBytes; i++)
            {
                output[rowStart + i] = unchecked((byte)(output[rowStart + i] + output[rowStart + i - colors]));
            }
        }

        return output;
    }
}
