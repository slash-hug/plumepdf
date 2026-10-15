namespace PlumePdf.Filters;

/// <summary>
/// Estimates the IJG quality setting a JPEG was encoded at, from its quantization tables.
/// </summary>
/// <remarks>
/// Reads only the marker segments before the first scan. Every quality 1–100 is tried: its
/// IJG-scaled Annex K base tables (the same scaling <see cref="JpegEncoder"/> applies) are
/// compared with the stream's luminance table and, when the frame has more than one component,
/// its chrominance table, and the quality with the smallest squared error wins. A stream whose
/// best match is still off by more than about one step per entry was not quantized by IJG
/// scaling (a camera's or another encoder's custom tables), and no estimate is given.
/// </remarks>
internal static class JpegQualityEstimator
{
    // Mean squared error per table entry above which the best quality is rejected: IJG
    // scaling reproduces its own tables exactly, and an encoder rounding the same formula
    // differently lands within one step.
    private const double MaxMeanSquaredError = 1.0;

    /// <summary>
    /// Reads the <c>DQT</c> segments before the first scan of <paramref name="jpeg"/> and
    /// recovers the quality (1–100) whose scaled standard tables match them. False when the
    /// tables aren't scaled standard tables or the header is unreadable; never throws.
    /// </summary>
    public static bool TryEstimate(ReadOnlySpan<byte> jpeg, out int quality)
    {
        quality = 0;
        if (!TryReadTables(jpeg, out var tables, out var lumaId, out var chromaId))
        {
            return false;
        }

        if (!tables.TryGetValue(lumaId, out var luma))
        {
            return false;
        }

        ushort[]? chroma = null;
        if (chromaId is { } id && !tables.TryGetValue(id, out chroma))
        {
            return false;
        }

        var bestError = long.MaxValue;
        var bestQuality = 0;
        for (var q = 1; q <= 100; q++)
        {
            var error = SquaredError(luma, JpegEncoder.ScaleQuantTable(JpegEncoder.BaseLuminanceQuant, q));
            if (chroma is not null)
            {
                error += SquaredError(chroma, JpegEncoder.ScaleQuantTable(JpegEncoder.BaseChrominanceQuant, q));
            }

            if (error < bestError)
            {
                bestError = error;
                bestQuality = q;
            }
        }

        var entries = chroma is null ? 64 : 128;
        if (bestError > MaxMeanSquaredError * entries)
        {
            return false;
        }

        quality = bestQuality;
        return true;
    }

    private static long SquaredError(ushort[] actual, ushort[] expected)
    {
        long error = 0;
        for (var i = 0; i < 64; i++)
        {
            long difference = actual[i] - expected[i];
            error += difference * difference;
        }

        return error;
    }

    // Walks SOI .. SOS collecting DQT tables by id, and reads which table the first component
    // (luminance) and the second (chrominance, when present) quantize with. Without a frame
    // header before the scan, table 0 is luminance and table 1, if defined, chrominance.
    private static bool TryReadTables(ReadOnlySpan<byte> jpeg, out Dictionary<byte, ushort[]> tables, out byte lumaId, out byte? chromaId)
    {
        tables = [];
        lumaId = 0;
        chromaId = null;
        var sawFrame = false;

        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != JpegMarkers.Soi)
        {
            return false;
        }

        var position = 2;
        while (position < jpeg.Length)
        {
            if (jpeg[position] != 0xFF)
            {
                position++;
                continue;
            }

            while (position < jpeg.Length && jpeg[position] == 0xFF)
            {
                position++;
            }

            if (position >= jpeg.Length)
            {
                return false;
            }

            var marker = jpeg[position++];
            if (marker == JpegMarkers.Tem || JpegMarkers.IsRestart(marker))
            {
                continue;
            }

            if (marker is JpegMarkers.Eoi or JpegMarkers.Sos)
            {
                break;
            }

            if (position + 2 > jpeg.Length)
            {
                return false;
            }

            var length = (jpeg[position] << 8) | jpeg[position + 1];
            if (length < 2 || position + length > jpeg.Length)
            {
                return false;
            }

            var payload = jpeg.Slice(position + 2, length - 2);
            position += length;

            if (marker == JpegMarkers.Dqt)
            {
                List<JpegQuantTable> parsed;
                try
                {
                    parsed = JpegMetadata.ParseDqt(payload);
                }
                catch (PlumePdfException)
                {
                    return false; // A table ending mid-segment.
                }

                foreach (var table in parsed)
                {
                    tables[table.Id] = table.NaturalOrderValues;
                }
            }
            else if (JpegMarkers.IsSupportedSof(marker) && !sawFrame)
            {
                // SOF: precision, height (2), width (2), component count, then 3 bytes each.
                if (payload.Length < 6 || payload.Length < 6 + (payload[5] * 3) || payload[5] == 0)
                {
                    return false;
                }

                sawFrame = true;
                lumaId = payload[6 + 2];
                chromaId = payload[5] > 1 ? payload[9 + 2] : null;
            }
        }

        if (!sawFrame && tables.ContainsKey(1))
        {
            chromaId = 1;
        }

        return tables.Count > 0;
    }
}
