using System.Text;
using PlumePdf.Filters;
using PlumePdf.Objects;
using static PlumePdf.Tests.Compression.CompressionTestImages;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// Source documents for the <c>Pdf.Compress</c> tests, written to temporary files, and readers
/// for what the compressed output holds. Images are real encodings of deterministic samples
/// (<see cref="CompressionTestImages.Samples"/>), so every lever has something to decode.
/// </summary>
internal static class CompressFixtures
{
    /// <summary>A 300-pixel image drawn on a 72-point square: 300 DPI on both axes.</summary>
    public const int Pixels = 300;

    /// <summary>The square, in points, a <see cref="Pixels"/>-wide image is drawn on for 300 DPI.</summary>
    public const int Edge = 72;

    /// <summary>The levers most tests run with: downsample to 100 DPI and re-encode at quality 80.</summary>
    public static PdfCompressOptions AllLevers { get; } = new() { Images = new PdfImageCompression { MaxDpi = 100, JpegQuality = 80 } };

    /// <summary>An image XObject dictionary (without brackets or <c>/Length</c>).</summary>
    public static string Image(int width = Pixels, int height = Pixels, string colorSpace = "/DeviceRGB", int bits = 8, string? filter = "/FlateDecode", string extra = "") =>
        $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colorSpace} /BitsPerComponent {bits}"
        + (filter is null ? string.Empty : $" /Filter {filter}")
        + (extra.Length == 0 ? string.Empty : $" {extra}");

    /// <summary>Flate-encoded noisy gradient samples.</summary>
    public static byte[] FlateSamples(int width = Pixels, int height = Pixels, int channels = 3, int seed = 1) =>
        Flate(Samples(width, height, channels, seed));

    /// <summary>A baseline JPEG of noisy gradient samples.</summary>
    public static byte[] JpegSamples(int width = Pixels, int height = Pixels, int channels = 3, int quality = 75) =>
        JpegEncoder.Encode(Samples(width, height, channels), width, height, channels, quality);

    /// <summary>Content drawing <paramref name="name"/> on an <paramref name="edge"/>-point square at the origin.</summary>
    public static string Draw(string name = "Im0", int edge = Edge) => $"q {edge} 0 0 {edge} 0 0 cm /{name} Do Q";

    /// <summary>One page drawing image object 10 (<paramref name="dictionary"/>, <paramref name="payload"/>) at 300 DPI.</summary>
    public static ScanTestPdf OneImage(string dictionary, byte[] payload) =>
        new ScanTestPdf()
            .Stream(10, dictionary, payload)
            .Page(3, Draw(), xobjects: "/Im0 10 0 R");

    /// <summary>A temporary file path for one test's input or output.</summary>
    public static string TempPath(string tag = "compress") => Path.Combine(Path.GetTempPath(), $"plumepdf-{tag}-{Guid.NewGuid():N}.pdf");

    /// <summary>Writes <paramref name="pdf"/> to a new temporary file and returns its path.</summary>
    public static string Write(ScanTestPdf pdf) => Write(pdf.Build());

    /// <summary>Writes <paramref name="bytes"/> to a new temporary file and returns its path.</summary>
    public static string Write(byte[] bytes)
    {
        var path = TempPath("source");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Deletes every path given that exists.</summary>
    public static void Delete(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>The temporary siblings a save leaves next to <paramref name="outputPath"/> (none, after any save that returned or threw).</summary>
    public static string[] TempSiblings(string outputPath) =>
        Directory.GetFiles(Path.GetDirectoryName(outputPath)!, $".{Path.GetFileName(outputPath)}.plumepdf-tmp-*");

    /// <summary>Compresses <paramref name="pdf"/> and returns the result plus the output's bytes; the files are deleted.</summary>
    public static (CompressionResult Result, byte[] Output) Compress(ScanTestPdf pdf, PdfCompressOptions? compressOptions = null, PdfOptions? options = null)
    {
        var source = Write(pdf);
        var output = TempPath();
        try
        {
            var result = Pdf.Compress(source, output, compressOptions, options);
            return (result, File.ReadAllBytes(output));
        }
        finally
        {
            Delete(source, output);
        }
    }

    /// <summary>The image a page's resources name <paramref name="name"/>, resolved, with its reference.</summary>
    public static (IndirectReference Reference, PdfStream Stream) PageImage(PdfDocument document, int pageIndex, string name = "Im0")
    {
        var resources = (PdfDictionary)Resolve(document, document.Pages[pageIndex].Dictionary[PdfName.Get("Resources")]!);
        var xobjects = (PdfDictionary)Resolve(document, resources[PdfName.Get("XObject")]!);
        var reference = (PdfReference)xobjects[PdfName.Get(name)]!;
        return (reference.Target, (PdfStream)document.Objects[reference.Target]);
    }

    /// <summary>The page's first content stream.</summary>
    public static PdfStream PageContent(PdfDocument document, int pageIndex)
    {
        var contents = document.Pages[pageIndex].Dictionary[PdfName.Get("Contents")]!;
        return (PdfStream)Resolve(document, contents is PdfArray array ? array[0] : contents);
    }

    /// <summary>A stream's <c>/Filter</c> as written (a name, or null when there is none).</summary>
    public static string? FilterOf(PdfStream stream) =>
        stream.Dictionary.TryGetValue(PdfName.Filter, out var filter) ? ((PdfName)filter).Value : null;

    /// <summary>An integer dictionary entry.</summary>
    public static int IntOf(PdfStream stream, string key) => (int)((PdfNumber)stream.Dictionary[PdfName.Get(key)]!).Value;

    /// <summary>Whether the file packs objects into object streams.</summary>
    public static bool HasObjectStreams(byte[] pdf) => Encoding.Latin1.GetString(pdf).Contains("/ObjStm", StringComparison.Ordinal);

    /// <summary>The decision for input object <paramref name="number"/>.</summary>
    public static ImageCompressionDecision Decision(CompressionResult result, int number) =>
        result.Images.Single(i => i.SourceReference?.Number == number);

    /// <summary>The repository root (where <c>PlumePdf.sln</c> is).</summary>
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlumePdf.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfReference reference ? document.Objects[reference.Target] : value;
}
