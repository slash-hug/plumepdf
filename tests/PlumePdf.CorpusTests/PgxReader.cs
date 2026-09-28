using System.Globalization;

namespace PlumePdf.CorpusTests;

/// <summary>One decoded PGX reference image: native-precision samples, row-major.</summary>
internal sealed class PgxImage
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Precision { get; init; }

    public required bool Signed { get; init; }

    public required int[] Samples { get; init; }
}

/// <summary>
/// Reads the PGX format opj_decompress emits for each JPEG 2000 component (test-only): a one-line ASCII header <c>PG &lt;endianness&gt; &lt;sign&gt; &lt;bit-depth&gt; &lt;width&gt; &lt;height&gt;</c>
/// followed by raw big- or little-endian samples, one or two bytes each depending on bit depth.
/// Every fixture in this repo is generated with opj_decompress's default big-endian ("ML") output,
/// but "LM" is accepted too since the format allows either.
/// </summary>
internal static class PgxReader
{
    public static PgxImage Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var newline = Array.IndexOf(bytes, (byte)'\n');
        if (newline < 0)
        {
            throw new InvalidDataException($"'{path}' has no PGX header line.");
        }

        var header = System.Text.Encoding.ASCII.GetString(bytes, 0, newline).Trim();
        var fields = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // "PG" <endianness: ML|LM> <sign: +|-> <bit-depth> <width> <height>
        if (fields.Length != 6 || fields[0] != "PG")
        {
            throw new InvalidDataException($"'{path}' has an unrecognised PGX header: '{header}'.");
        }

        var bigEndian = fields[1] == "ML";
        var signed = fields[2] == "-";
        var precision = int.Parse(fields[3], CultureInfo.InvariantCulture);
        var width = int.Parse(fields[4], CultureInfo.InvariantCulture);
        var height = int.Parse(fields[5], CultureInfo.InvariantCulture);

        var bytesPerSample = precision <= 8 ? 1 : 2;
        var pixelCount = width * height;
        var samples = new int[pixelCount];
        var dataStart = newline + 1;

        for (var i = 0; i < pixelCount; i++)
        {
            var offset = dataStart + (i * bytesPerSample);
            int raw;
            if (bytesPerSample == 1)
            {
                raw = bytes[offset];
            }
            else if (bigEndian)
            {
                raw = (bytes[offset] << 8) | bytes[offset + 1];
            }
            else
            {
                raw = (bytes[offset + 1] << 8) | bytes[offset];
            }

            if (signed && (raw & (1 << (precision - 1))) != 0)
            {
                raw -= 1 << precision;
            }

            samples[i] = raw;
        }

        return new PgxImage { Width = width, Height = height, Precision = precision, Signed = signed, Samples = samples };
    }
}
