using System.Buffers;
using System.IO.Compression;

namespace PlumePdf.Filters;

/// <summary>
/// The <c>FlateDecode</c> filter (ISO 32000-1 §7.4.4): RFC 1950 zlib-wrapped DEFLATE data.
/// Real-world producers occasionally emit raw RFC 1951 DEFLATE with no zlib header, or
/// truncate the stream — both tolerated: a zlib-header failure retries as raw
/// DEFLATE, and a stream that decodes cleanly up to a corrupt or truncated tail returns
/// what decoded before the failure rather than nothing at all. Each fallback records a
/// <c>PLUME3xxx</c> diagnostic. Decompression is capped by <c>PdfOptions.MaxDecompressedStreamBytes</c>
/// — checked incrementally, so a decompression bomb is refused before it can
/// exhaust memory rather than after.
/// </summary>
internal static class FlateFilter
{
    /// <summary>Decodes <paramref name="data"/>, trying zlib framing first and raw DEFLATE second.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, long maxDecompressedBytes, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var result = TryDecode(data, useZlibHeader: true, maxDecompressedBytes, out var truncated);

        if (result is null)
        {
            diagnostics?.Add(new PdfDiagnostic("PLUME3001", DiagnosticSeverity.Warning, "FlateDecode: zlib-wrapped stream failed to decode; retrying as raw DEFLATE (missing or corrupt zlib header).", subject: subject));
            result = TryDecode(data, useZlibHeader: false, maxDecompressedBytes, out truncated);
        }

        if (result is null)
        {
            throw new PlumePdfException("PLUME3002", "FlateDecode: stream data is not valid zlib-wrapped or raw DEFLATE data.");
        }

        if (truncated)
        {
            diagnostics?.Add(new PdfDiagnostic("PLUME3003", DiagnosticSeverity.Warning, "FlateDecode: the stream ended before its DEFLATE data was fully consumed; returning the bytes decoded before the truncation.", subject: subject));
        }

        return result;
    }

    private static byte[]? TryDecode(ReadOnlySpan<byte> data, bool useZlibHeader, long maxDecompressedBytes, out bool truncated)
    {
        truncated = false;
        using var input = new MemoryStream(data.ToArray());

        Stream decompressor;
        try
        {
            decompressor = useZlibHeader
                ? new ZLibStream(input, CompressionMode.Decompress)
                : new DeflateStream(input, CompressionMode.Decompress);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        // Output assembly over pooled chunks + one exact-size final array (the old
        // MemoryStream grew by doubling and then ToArray'd — ~3× the decompressed size in churn
        // on every Flate stream, ~100 MB for one 33 MB scan image; this path runs for every
        // content stream and image in every document). The inflater reads straight into each
        // chunk, so the old intermediate copy buffer is gone too. Output bytes, diagnostics,
        // and the incremental PLUME3004 bomb check are unchanged.
        const int ChunkSize = 256 * 1024;
        var chunks = new List<byte[]>();
        var chunkFilled = 0;
        long total = 0;
        try
        {
            byte[]? chunk = null;
            while (true)
            {
                if (chunk is null || chunkFilled == chunk.Length)
                {
                    chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
                    chunks.Add(chunk);
                    chunkFilled = 0;
                }

                int read;
                try
                {
                    read = decompressor.Read(chunk, chunkFilled, chunk.Length - chunkFilled);
                }
                catch (InvalidDataException)
                {
                    // Corrupt or truncated tail: keep whatever decoded cleanly before this point.
                    truncated = true;
                    break;
                }

                if (read == 0)
                {
                    break;
                }

                if (total + read > maxDecompressedBytes)
                {
                    throw new PlumePdfException("PLUME3004", $"FlateDecode: decompressed output exceeds the {maxDecompressedBytes}-byte cap (PdfOptions.MaxDecompressedStreamBytes) - refusing to continue (possible decompression bomb).");
                }

                chunkFilled += read;
                total += read;
            }

            if (total == 0 && truncated)
            {
                // Nothing usable came out at all - let the caller try the other framing, or
                // surface PLUME3002 if both have already failed.
                return null;
            }

            var result = new byte[total];
            var written = 0;
            for (var i = 0; i < chunks.Count && written < total; i++)
            {
                var count = (int)Math.Min(chunks[i].Length, total - written);
                chunks[i].AsSpan(0, count).CopyTo(result.AsSpan(written));
                written += count;
            }

            return result;
        }
        finally
        {
            foreach (var rented in chunks)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            decompressor.Dispose();
        }
    }
}

/// <summary>Bridges the internal, object-model-free <see cref="FlateFilter"/> to the public <see cref="IPdfFilter"/> seam.</summary>
internal sealed class FlateFilterAdapter : IPdfFilter
{
    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
        FlateFilter.Decode(data.Span, options.MaxDecompressedStreamBytes, diagnostics, subject);
}
