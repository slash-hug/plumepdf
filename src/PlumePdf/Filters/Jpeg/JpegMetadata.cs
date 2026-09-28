namespace PlumePdf.Filters;

/// <summary>One JPEG frame component (ISO/IEC 10918-1 §B.2.2's frame header component-specification list).</summary>
internal sealed class JpegComponentInfo
{
    public required byte Id { get; init; }
    public required byte HorizontalSampling { get; init; }
    public required byte VerticalSampling { get; init; }
    public required byte QuantTableId { get; init; }
}

/// <summary>A parsed <c>SOF0</c>/<c>SOF1</c>/<c>SOF2</c> frame header (§B.2.2).</summary>
internal sealed class JpegFrameHeader
{
    public required byte Precision { get; init; }
    public required int Height { get; init; }
    public required int Width { get; init; }
    public required bool IsProgressive { get; init; }
    public required IReadOnlyList<JpegComponentInfo> Components { get; init; }
}

/// <summary>One component's Huffman-table selectors within a scan (§B.2.3).</summary>
internal readonly record struct JpegScanComponent(byte ComponentSelector, byte DcTableId, byte AcTableId);

/// <summary>A parsed <c>SOS</c> scan header (§B.2.3): which components this scan covers, and (for progressive frames) which spectral band and successive-approximation bit position.</summary>
internal sealed class JpegScanHeader
{
    public required IReadOnlyList<JpegScanComponent> Components { get; init; }
    public required byte SpectralStart { get; init; }
    public required byte SpectralEnd { get; init; }
    public required byte SuccessiveApproxHigh { get; init; }
    public required byte SuccessiveApproxLow { get; init; }
}

/// <summary>A parsed <c>DQT</c> table entry: id plus 64 values in natural (row-major) order, already de-zigzagged.</summary>
internal readonly record struct JpegQuantTable(byte Id, ushort[] NaturalOrderValues);

/// <summary>A parsed <c>DHT</c> table entry: class (0 = DC, 1 = AC), id, and the raw BITS/HUFFVAL pair <see cref="JpegHuffmanTable"/> builds from.</summary>
internal readonly record struct JpegHuffmanTableEntry(byte TableClass, byte Id, byte[] Bits, byte[] Values);

/// <summary>
/// The Adobe <c>APP14</c> marker's declared color transform (ISO/IEC 10918-1 has no
/// normative text for this - it is Adobe's own convention, universally honored by
/// decoders since it is the only signal that disambiguates a 3- or 4-component JPEG's
/// color space).
/// </summary>
internal enum JpegAdobeTransform
{
    /// <summary>Unknown/CMYK (4 components) or RGB (3 components) - no color transform was applied.</summary>
    Unknown = 0,

    /// <summary>YCbCr (3 components).</summary>
    YCbCr = 1,

    /// <summary>YCCK (4 components: Y/Cb/Cr + K).</summary>
    Ycck = 2,
}

/// <summary>Marker byte values this decoder recognizes (ISO/IEC 10918-1 Table B.1).</summary>
internal static class JpegMarkers
{
    public const byte Tem = 0x01;
    public const byte Sof0 = 0xC0;
    public const byte Sof1 = 0xC1;
    public const byte Sof2 = 0xC2;
    public const byte Sof3 = 0xC3;
    public const byte Dht = 0xC4;
    public const byte Sof5 = 0xC5;
    public const byte Sof6 = 0xC6;
    public const byte Sof7 = 0xC7;
    public const byte Sof9 = 0xC9;
    public const byte Sof10 = 0xCA;
    public const byte Sof11 = 0xCB;
    public const byte Sof13 = 0xCD;
    public const byte Sof14 = 0xCE;
    public const byte Sof15 = 0xCF;
    public const byte Rst0 = 0xD0;
    public const byte Rst7 = 0xD7;
    public const byte Soi = 0xD8;
    public const byte Eoi = 0xD9;
    public const byte Sos = 0xDA;
    public const byte Dqt = 0xDB;
    public const byte Dnl = 0xDC;
    public const byte Dri = 0xDD;
    public const byte App0 = 0xE0;
    public const byte App1 = 0xE1;
    public const byte App14 = 0xEE;
    public const byte Com = 0xFE;

    public static bool IsRestart(byte marker) => marker is >= Rst0 and <= Rst7;

    public static bool IsArithmeticSof(byte marker) => marker is Sof9 or Sof10 or Sof11 or Sof13 or Sof14 or Sof15;

    public static bool IsUnsupportedSof(byte marker) => marker is Sof3 or Sof5 or Sof6 or Sof7;

    public static bool IsSupportedSof(byte marker) => marker is Sof0 or Sof1 or Sof2;
}

/// <summary>
/// Pure segment parsers for JPEG's marker-delimited header structure (ISO/IEC 10918-1 §B):
/// each method takes one marker segment's payload (already length-delimited by the caller,
/// <see cref="JpegDecoder"/>'s marker-walk loop) and returns the parsed structure, or reports
/// a coded deviation/refusal via <see cref="FilterDiagnostics"/>. Holds no state of its own -
/// the decoder accumulates parsed tables/headers as it walks the marker stream, since a
/// JPEG's marker segments and its entropy-coded scan data are interleaved and only the
/// decoder (not a standalone parser) can drive that interleaving.
/// </summary>
internal static class JpegMetadata
{
    /// <summary>Parses an <c>SOF0</c>/<c>SOF1</c>/<c>SOF2</c> payload. Throws <c>PLUME3202</c> for zero dimensions or an out-of-range component count.</summary>
    public static JpegFrameHeader ParseSof(ReadOnlySpan<byte> payload, byte marker)
    {
        if (payload.Length < 6)
        {
            throw new PlumePdfException("PLUME3203", "JPEG SOF marker segment is shorter than the minimum 6-byte frame header.");
        }

        var precision = payload[0];
        var height = (payload[1] << 8) | payload[2];
        var width = (payload[3] << 8) | payload[4];
        var componentCount = payload[5];

        if (width == 0 || componentCount == 0)
        {
            throw new PlumePdfException("PLUME3202", $"JPEG frame header declares width={width}, {componentCount} components - not a decodable image.");
        }

        if (payload.Length < 6 + (componentCount * 3))
        {
            throw new PlumePdfException("PLUME3203", "JPEG SOF marker segment is shorter than its declared component count requires.");
        }

        var components = new JpegComponentInfo[componentCount];
        for (var i = 0; i < componentCount; i++)
        {
            var offset = 6 + (i * 3);
            components[i] = new JpegComponentInfo
            {
                Id = payload[offset],
                HorizontalSampling = (byte)(payload[offset + 1] >> 4),
                VerticalSampling = (byte)(payload[offset + 1] & 0x0F),
                QuantTableId = payload[offset + 2],
            };
        }

        return new JpegFrameHeader
        {
            Precision = precision,
            Height = height,
            Width = width,
            IsProgressive = marker == JpegMarkers.Sof2,
            Components = components,
        };
    }

    /// <summary>Parses a <c>DQT</c> payload, which may carry more than one table back to back.</summary>
    public static List<JpegQuantTable> ParseDqt(ReadOnlySpan<byte> payload)
    {
        var tables = new List<JpegQuantTable>();
        var offset = 0;
        while (offset < payload.Length)
        {
            var precisionAndId = payload[offset++];
            var precision = precisionAndId >> 4;
            var id = (byte)(precisionAndId & 0x0F);
            var bytesPerValue = precision == 0 ? 1 : 2;

            if (offset + (64 * bytesPerValue) > payload.Length)
            {
                throw new PlumePdfException("PLUME3203", "JPEG DQT marker segment ends mid-table.");
            }

            var natural = new ushort[64];
            for (var z = 0; z < 64; z++)
            {
                ushort value = precision == 0
                    ? payload[offset++]
                    : (ushort)((payload[offset] << 8) | payload[offset + 1]);
                if (precision != 0)
                {
                    offset += 2;
                }

                natural[JpegHuffmanTable.ZigzagToNatural[z]] = value;
            }

            tables.Add(new JpegQuantTable(id, natural));
        }

        return tables;
    }

    /// <summary>Parses a <c>DHT</c> payload, which may carry more than one table back to back.</summary>
    public static List<JpegHuffmanTableEntry> ParseDht(ReadOnlySpan<byte> payload)
    {
        var tables = new List<JpegHuffmanTableEntry>();
        var offset = 0;
        while (offset < payload.Length)
        {
            if (offset + 17 > payload.Length)
            {
                throw new PlumePdfException("PLUME3203", "JPEG DHT marker segment ends mid-table header.");
            }

            var classAndId = payload[offset++];
            var tableClass = (byte)(classAndId >> 4);
            var id = (byte)(classAndId & 0x0F);

            var bits = new byte[17];
            var total = 0;
            for (var length = 1; length <= 16; length++)
            {
                bits[length] = payload[offset++];
                total += bits[length];
            }

            if (offset + total > payload.Length)
            {
                throw new PlumePdfException("PLUME3203", "JPEG DHT marker segment ends mid-table values.");
            }

            var values = payload.Slice(offset, total).ToArray();
            offset += total;
            tables.Add(new JpegHuffmanTableEntry(tableClass, id, bits, values));
        }

        return tables;
    }

    /// <summary>Parses a <c>DRI</c> payload: the restart interval, in MCUs (0 = no restart markers).</summary>
    public static int ParseDri(ReadOnlySpan<byte> payload) =>
        payload.Length >= 2 ? (payload[0] << 8) | payload[1] : 0;

    /// <summary>Parses an <c>SOS</c> payload's scan header (not the entropy-coded data that follows it).</summary>
    public static JpegScanHeader ParseSos(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
        {
            throw new PlumePdfException("PLUME3203", "JPEG SOS marker segment is empty.");
        }

        var componentCount = payload[0];
        if (payload.Length < 1 + (componentCount * 2) + 3)
        {
            throw new PlumePdfException("PLUME3203", "JPEG SOS marker segment is shorter than its declared component count requires.");
        }

        var components = new JpegScanComponent[componentCount];
        for (var i = 0; i < componentCount; i++)
        {
            var offset = 1 + (i * 2);
            components[i] = new JpegScanComponent(payload[offset], (byte)(payload[offset + 1] >> 4), (byte)(payload[offset + 1] & 0x0F));
        }

        var tail = 1 + (componentCount * 2);
        return new JpegScanHeader
        {
            Components = components,
            SpectralStart = payload[tail],
            SpectralEnd = payload[tail + 1],
            SuccessiveApproxHigh = (byte)(payload[tail + 2] >> 4),
            SuccessiveApproxLow = (byte)(payload[tail + 2] & 0x0F),
        };
    }

    /// <summary>Parses an <c>APP0</c> "JFIF" payload's density fields. Returns <see langword="null"/> when the payload isn't a JFIF identifier or declares aspect-ratio-only units (0).</summary>
    public static (double XDpi, double YDpi)? ParseApp0Jfif(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9 || !IsIdentifier(payload, "JFIF"u8))
        {
            return null;
        }

        var units = payload[7];
        var x = (payload[8] << 8) | (payload.Length > 9 ? payload[9] : 0);
        var y = (payload.Length > 11) ? (payload[10] << 8) | payload[11] : 0;

        return units switch
        {
            1 => (x, y), // dots per inch
            2 => (x * 2.54, y * 2.54), // dots per cm -> dpi
            _ => null, // 0 = aspect ratio only, no absolute density
        };
    }

    /// <summary>
    /// Parses an <c>APP1</c> "Exif" payload's <c>XResolution</c>/<c>YResolution</c>/<c>ResolutionUnit</c>
    /// IFD0 tags (Exif 2.3 §4.6.4) - just enough of a TIFF header reader for these three
    /// tags, not a general TIFF parser (that is the separate <c>TiffReader</c>, an unrelated,
    /// container-format concern this codec has no reason to depend on).
    /// </summary>
    public static (double XDpi, double YDpi)? ParseApp1Exif(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8 || !IsIdentifier(payload, "Exif\0\0"u8))
        {
            return null;
        }

        var tiff = payload[6..];
        if (tiff.Length < 8)
        {
            return null;
        }

        bool littleEndian;
        if (tiff[0] == 0x49 && tiff[1] == 0x49)
        {
            littleEndian = true;
        }
        else if (tiff[0] == 0x4D && tiff[1] == 0x4D)
        {
            littleEndian = false;
        }
        else
        {
            return null;
        }

        var ifdOffset = ReadUInt32(tiff, 4, littleEndian);
        if (ifdOffset + 2 > tiff.Length)
        {
            return null;
        }

        var entryCount = ReadUInt16(tiff, (int)ifdOffset, littleEndian);
        double? xRes = null;
        double? yRes = null;
        var unit = 2; // Exif default: 2 = inches.

        for (var i = 0; i < entryCount; i++)
        {
            var entryOffset = (int)ifdOffset + 2 + (i * 12);
            if (entryOffset + 12 > tiff.Length)
            {
                break;
            }

            var tag = ReadUInt16(tiff, entryOffset, littleEndian);
            var type = ReadUInt16(tiff, entryOffset + 2, littleEndian);
            switch (tag)
            {
                case 0x011A when type == 5: // XResolution, RATIONAL
                    xRes = ReadRational(tiff, entryOffset + 8, littleEndian);
                    break;
                case 0x011B when type == 5: // YResolution, RATIONAL
                    yRes = ReadRational(tiff, entryOffset + 8, littleEndian);
                    break;
                case 0x0128 when type == 3: // ResolutionUnit, SHORT
                    unit = ReadUInt16(tiff, entryOffset + 8, littleEndian);
                    break;
            }
        }

        if (xRes is null || yRes is null || unit == 1) // 1 = no absolute unit.
        {
            return null;
        }

        return unit == 3 ? (xRes.Value * 2.54, yRes.Value * 2.54) : (xRes.Value, yRes.Value); // 3 = cm -> dpi.
    }

    /// <summary>Parses an <c>APP14</c> "Adobe" payload's transform byte. Returns <see langword="null"/> when the payload isn't an Adobe identifier.</summary>
    public static JpegAdobeTransform? ParseApp14Adobe(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12 || !IsIdentifier(payload, "Adobe"u8))
        {
            return null;
        }

        return payload[11] switch
        {
            1 => JpegAdobeTransform.YCbCr,
            2 => JpegAdobeTransform.Ycck,
            _ => JpegAdobeTransform.Unknown,
        };
    }

    private static bool IsIdentifier(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> identifier) =>
        payload.Length >= identifier.Length && payload[..identifier.Length].SequenceEqual(identifier);

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian ? (ushort)(data[offset] | (data[offset + 1] << 8)) : (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian
            ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
            : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static double ReadRational(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        // RATIONAL entries >4 bytes long store an offset (from the TIFF header start) to the
        // actual numerator/denominator pair rather than the value inline; ParseApp1Exif's
        // caller always passes the IFD-entry-relative offset, so the 4-byte "value/offset"
        // field at entryOffset+8 is itself that absolute offset for a RATIONAL.
        var valueOffset = (int)ReadUInt32(data, offset, littleEndian);
        if (valueOffset + 8 > data.Length)
        {
            return 0;
        }

        var numerator = ReadUInt32(data, valueOffset, littleEndian);
        var denominator = ReadUInt32(data, valueOffset + 4, littleEndian);
        return denominator == 0 ? 0 : (double)numerator / denominator;
    }
}
