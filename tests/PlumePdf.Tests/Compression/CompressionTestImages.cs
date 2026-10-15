using PlumePdf.Filters;
using PlumePdf.Objects;

namespace PlumePdf.Tests.Compression;

/// <summary>Builds image XObjects and the in-memory object graphs they resolve against, for the compression imaging tests.</summary>
internal static class CompressionTestImages
{
    public static ObjectRegistry Registry(params (int Number, PdfObject Value)[] objects) =>
        new(new InMemoryObjectSource(new PdfDictionary(), objects.ToDictionary(static o => o.Number, static o => o.Value)), nextObjectNumber: 100);

    /// <summary>Deterministic, mildly noisy gradient samples: compressible, but not trivially so.</summary>
    public static byte[] Samples(int width, int height, int channels, int seed = 1)
    {
        var random = new Random(seed);
        var samples = new byte[width * height * channels];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var value = ((x * 255 / Math.Max(1, width - 1)) + (y * 97 / Math.Max(1, height - 1)) + (c * 60) + random.Next(-6, 7)) % 256;
                    samples[(((y * width) + x) * channels) + c] = (byte)Math.Clamp(value, 0, 255);
                }
            }
        }

        return samples;
    }

    public static byte[] Flate(ReadOnlySpan<byte> data) => new FlateEncodingFilter().Encode(data.ToArray(), PdfOptions.Default);

    public static PdfDictionary ImageDictionary(int width, int height, PdfObject colorSpace, int bitsPerComponent = 8)
    {
        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Type, PdfName.Get("XObject"));
        dictionary.Set(PdfName.Subtype, PdfName.Image);
        dictionary.Set(PdfName.Get("Width"), PdfNumber.Get(width));
        dictionary.Set(PdfName.Get("Height"), PdfNumber.Get(height));
        dictionary.Set(PdfName.ColorSpace, colorSpace);
        dictionary.Set(PdfName.BitsPerComponent, PdfNumber.Get(bitsPerComponent));
        return dictionary;
    }

    public static PdfName ColorSpaceFor(int channels) => PdfName.Get(channels == 1 ? "DeviceGray" : "DeviceRGB");

    public static PdfStream FlateImage(byte[] samples, int width, int height, int channels)
    {
        var dictionary = ImageDictionary(width, height, ColorSpaceFor(channels));
        dictionary.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        return new PdfStream(dictionary, Flate(samples));
    }

    public static PdfStream DctImage(byte[] samples, int width, int height, int channels, int quality = 75)
    {
        var dictionary = ImageDictionary(width, height, ColorSpaceFor(channels));
        dictionary.Set(PdfName.Filter, PdfName.Get("DCTDecode"));
        return new PdfStream(dictionary, JpegEncoder.Encode(samples, width, height, channels, quality));
    }

    /// <summary>PNG-style row prediction (<c>/Predictor 15</c>): each row prefixed with filter type 1 (Sub) or 2 (Up), alternating.</summary>
    public static byte[] PngPredict(byte[] samples, int width, int height, int channels)
    {
        var stride = width * channels;
        var output = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var type = (byte)(y % 2 == 0 ? 1 : 2);
            output[y * (stride + 1)] = type;
            for (var i = 0; i < stride; i++)
            {
                var current = samples[(y * stride) + i];
                int predictor = type == 1
                    ? (i >= channels ? samples[(y * stride) + i - channels] : 0)
                    : (y > 0 ? samples[((y - 1) * stride) + i] : 0);
                output[(y * (stride + 1)) + 1 + i] = (byte)(current - predictor);
            }
        }

        return output;
    }

    /// <summary>TIFF horizontal differencing (<c>/Predictor 2</c>) at 8 bits per component.</summary>
    public static byte[] TiffPredict(byte[] samples, int width, int height, int channels)
    {
        var stride = width * channels;
        var output = new byte[samples.Length];
        for (var y = 0; y < height; y++)
        {
            for (var i = 0; i < stride; i++)
            {
                var left = i >= channels ? samples[(y * stride) + i - channels] : 0;
                output[(y * stride) + i] = (byte)(samples[(y * stride) + i] - left);
            }
        }

        return output;
    }

    public static PdfDictionary PredictorParms(int predictor, int width, int channels)
    {
        var parms = new PdfDictionary();
        parms.Set(PdfName.Predictor, PdfNumber.Get(predictor));
        parms.Set(PdfName.Colors, PdfNumber.Get(channels));
        parms.Set(PdfName.BitsPerComponent, PdfNumber.Get(8));
        parms.Set(PdfName.Columns, PdfNumber.Get(width));
        return parms;
    }
}
