using System.IO.Compression;
using System.Text;
using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests;

public class FlateFilterTests
{
    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static byte[] RawDeflateCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var compressor = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    [Fact]
    public void Decode_ZlibWrapped_RoundTrips()
    {
        var original = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog.");
        var compressed = ZlibCompress(original);

        var decoded = FlateFilter.Decode(compressed, long.MaxValue, null, null);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_RawDeflate_FallsBackAndRoundTrips()
    {
        var original = Encoding.ASCII.GetBytes("Some producers emit raw DEFLATE with no zlib header.");
        var compressed = RawDeflateCompress(original);
        var diagnostics = new DiagnosticCollection();

        var decoded = FlateFilter.Decode(compressed, long.MaxValue, diagnostics, null);

        Assert.Equal(original, decoded);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3001");
    }

    [Fact]
    public void Decode_TruncatedStream_ReturnsPartialDataWithDiagnostic()
    {
        var original = Encoding.ASCII.GetBytes(new string('A', 5000)); // compresses well, multiple deflate blocks
        var compressed = ZlibCompress(original);
        var truncated = compressed[..(compressed.Length - 4)]; // chop off the tail
        var diagnostics = new DiagnosticCollection();

        var decoded = FlateFilter.Decode(truncated, long.MaxValue, diagnostics, null);

        Assert.NotEmpty(decoded);
        Assert.True(decoded.Length <= original.Length);
        Assert.Contains(decoded, b => b == (byte)'A');
    }

    [Fact]
    public void Decode_CompletelyInvalidData_Throws()
    {
        byte[] garbage = [0x01, 0x02, 0x03, 0x04, 0x05];

        var ex = Assert.Throws<PlumePdfException>(() => FlateFilter.Decode(garbage, long.MaxValue, null, null));
        Assert.Equal("PLUME3002", ex.Code);
    }

    [Fact]
    public void Decode_ExceedsDecompressionCap_Throws()
    {
        var original = Encoding.ASCII.GetBytes(new string('B', 100_000));
        var compressed = ZlibCompress(original);

        var ex = Assert.Throws<PlumePdfException>(() => FlateFilter.Decode(compressed, maxDecompressedBytes: 1024, null, null));
        Assert.Equal("PLUME3004", ex.Code);
    }

    [Fact]
    public void PdfFilterRegistry_Default_DecodesFlateDecodeByName()
    {
        var original = Encoding.ASCII.GetBytes("Hello via the public registry seam.");
        var compressed = ZlibCompress(original);

        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));

        var decoded = PdfFilterRegistry.Default.Decode(dict, compressed, PdfOptions.Default);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void PdfFilterRegistry_UnknownFilter_ThrowsCoded()
    {
        // JBIG2Decode was this test's unregistered-filter example, then JPXDecode, until that too
        // was registered by default. This test moved again, to a vendor name that will never collide with a filter
        // PlumePDF ships — every ISO 32000-1 §7.4 filter is registered by default now.
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("PlumeVendorTestDecode"));

        var ex = Assert.Throws<PlumePdfException>(() => PdfFilterRegistry.Default.Decode(dict, [1, 2, 3], PdfOptions.Default));
        Assert.Equal("PLUME3010", ex.Code);
    }

    [Fact]
    public void PdfFilterRegistry_CustomFilter_CanBeRegistered()
    {
        var registry = new PdfFilterRegistry();
        registry.Register("Identity", new IdentityFilter());

        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("Identity"));

        var decoded = registry.Decode(dict, [9, 8, 7], PdfOptions.Default);

        Assert.Equal([9, 8, 7], decoded);
    }

    /// <summary>
    /// The cookbook's "opt back out of JPX" recipe used to build its
    /// override on <c>new PdfFilterRegistry()</c>, which is EMPTY — FlateDecode and every other
    /// built-in vanished with it, and opening an ordinary object-stream document threw. The
    /// built-in filter classes are internal, so a caller could not rebuild the set themselves,
    /// and mutating <see cref="PdfFilterRegistry.Default"/> is a process-wide footgun.
    /// <see cref="PdfFilterRegistry.CreateDefault"/> is the additive seam: a fresh copy of the
    /// built-in set that one filter can be replaced in, leaving Flate intact and
    /// <see cref="PdfFilterRegistry.Default"/> untouched.
    /// </summary>
    [Fact]
    public void PdfFilterRegistry_CreateDefault_WithJpxReplaced_StillDecodesFlate_AndLeavesDefaultUntouched()
    {
        var original = Encoding.ASCII.GetBytes("Flate survives a JPXDecode override on a CreateDefault() registry.");
        var compressed = ZlibCompress(original);

        var registry = PdfFilterRegistry.CreateDefault();
        registry.Register("JPXDecode", new IdentityFilter());
        Assert.NotSame(PdfFilterRegistry.Default, registry);

        var flateDict = new PdfDictionary();
        flateDict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        Assert.Equal(original, registry.Decode(flateDict, compressed, PdfOptions.Default));

        // The override really took: the identity stand-in returns the "JPX" bytes verbatim,
        // where the built-in decoder would have refused them as no codestream (PLUME3700).
        var jpxDict = new PdfDictionary();
        jpxDict.Set(PdfName.Filter, PdfName.Get("JPXDecode"));
        Assert.Equal([9, 8, 7], registry.Decode(jpxDict, [9, 8, 7], PdfOptions.Default));

        // ...and the shared singleton still carries the built-in JPX decoder.
        var ex = Assert.Throws<PlumePdfException>(() => PdfFilterRegistry.Default.Decode(jpxDict, [9, 8, 7], PdfOptions.Default));
        Assert.Equal("PLUME3700", ex.Code);

        // The footgun the recipe used to have, pinned as documented behaviour: an empty registry
        // with only JPXDecode registered has no Flate at all.
        var empty = new PdfFilterRegistry();
        empty.Register("JPXDecode", new IdentityFilter());
        var emptyEx = Assert.Throws<PlumePdfException>(() => empty.Decode(flateDict, compressed, PdfOptions.Default));
        Assert.Equal("PLUME3010", emptyEx.Code);
    }

    [Fact]
    public void PdfFilterRegistry_FilterChain_AppliesInOrder()
    {
        var original = Encoding.ASCII.GetBytes("chained");
        var compressed = ZlibCompress(original);

        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, new PdfArray { PdfName.Get("FlateDecode") });

        var decoded = PdfFilterRegistry.Default.Decode(dict, compressed, PdfOptions.Default);

        Assert.Equal(original, decoded);
    }

    // PdfFilterRegistry.Decode gained an
    // optional resolver so an indirect /Filter or /DecodeParms entry (uncommon, but
    // conformant per ISO 32000-1 §7.4) resolves instead of being treated as absent. PLUME3011
    // — the diagnostic this class of stream used to trip unconditionally, regardless of
    // whether resolution was even possible — is retired (docs/errors/PLUME3011.md); these
    // tests now cover both halves of the new contract: no resolver still degrades (silently,
    // no diagnostic, since the retired code is never reused), and a resolver actually
    // resolves.

    [Fact]
    public void PdfFilterRegistry_IndirectFilterNoResolver_DegradesSilentlyWithoutPlume3011()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, new PdfReference(new IndirectReference(9, 0)));
        var diagnostics = new DiagnosticCollection();

        var decoded = PdfFilterRegistry.Default.Decode(dict, [1, 2, 3], PdfOptions.Default, diagnostics);

        Assert.Equal([1, 2, 3], decoded); // unfiltered - no resolver was supplied to follow the reference
        Assert.Empty(diagnostics); // PLUME3011 retired: no diagnostic for this now-silent fallback
    }

    [Fact]
    public void PdfFilterRegistry_IndirectFilterWithResolver_ResolvesAndDecodes()
    {
        var original = Encoding.ASCII.GetBytes("indirect filter name");
        var compressed = ZlibCompress(original);

        var filterReference = new IndirectReference(9, 0);
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, new PdfReference(filterReference));

        var decoded = PdfFilterRegistry.Default.Decode(
            dict, compressed, PdfOptions.Default,
            resolver: r => r == filterReference ? PdfName.Get("FlateDecode") : null);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void PdfFilterRegistry_IndirectDecodeParmsNoResolver_DegradesSilentlyWithoutPlume3011()
    {
        var original = Encoding.ASCII.GetBytes("no predictor applied");
        var compressed = ZlibCompress(original);

        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        dict.Set(PdfName.DecodeParms, new PdfReference(new IndirectReference(9, 0)));
        var diagnostics = new DiagnosticCollection();

        var decoded = PdfFilterRegistry.Default.Decode(dict, compressed, PdfOptions.Default, diagnostics);

        Assert.Equal(original, decoded); // predictor un-filtering skipped - no resolver was supplied
        Assert.Empty(diagnostics); // PLUME3011 retired: no diagnostic for this now-silent fallback
    }

    [Fact]
    public void PdfFilterRegistry_IndirectDecodeParmsWithResolver_ResolvesAndAppliesPredictor()
    {
        // TIFF predictor 2 (horizontal differencing), 1 color, 8bpc, 2 columns: "AC" (0x41,
        // 0x43) encodes to [0x41, 0x02] (byte 0 unchanged, byte 1 = 0x43 - 0x41).
        byte[] predicted = [0x41, 0x02];
        var compressed = ZlibCompress(predicted);

        var decodeParmsReference = new IndirectReference(9, 0);
        var decodeParmsDict = new PdfDictionary();
        decodeParmsDict.Set(PdfName.Predictor, PdfNumber.Get(2));
        decodeParmsDict.Set(PdfName.Colors, PdfNumber.Get(1));
        decodeParmsDict.Set(PdfName.BitsPerComponent, PdfNumber.Get(8));
        decodeParmsDict.Set(PdfName.Columns, PdfNumber.Get(2));

        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        dict.Set(PdfName.DecodeParms, new PdfReference(decodeParmsReference));

        var decoded = PdfFilterRegistry.Default.Decode(
            dict, compressed, PdfOptions.Default,
            resolver: r => r == decodeParmsReference ? decodeParmsDict : null);

        Assert.Equal([0x41, 0x43], decoded); // "AC" - predictor undone
    }

    // Post-Phase-7 hardening: PLUME3011's retirement note draws a line
    // between two failure shapes once a resolver is threaded through
    // (docs/errors/PLUME3011.md) - no resolver at all stays silent (covered above), but a
    // resolver that IS supplied and still can't make sense of the entry (dangling reference, or
    // resolved to the wrong object shape) is a real deviation and must surface as PLUME3012
    // (docs/errors/PLUME3012.md), not be swallowed. These tests cover that half.

    [Fact]
    public void PdfFilterRegistry_IndirectFilterWithResolver_DanglingReference_ReportsPlume3012()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, new PdfReference(new IndirectReference(9, 0)));
        var diagnostics = new DiagnosticCollection();

        var decoded = PdfFilterRegistry.Default.Decode(
            dict, [1, 2, 3], PdfOptions.Default, diagnostics,
            resolver: static _ => null); // resolver supplied, but object 9 0 doesn't exist

        Assert.Equal([1, 2, 3], decoded); // still degrades to unfiltered - just no longer silently
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME3012", diagnostic.Code);
    }

    [Fact]
    public void PdfFilterRegistry_IndirectFilterWithResolver_WrongShape_ReportsPlume3012()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, new PdfReference(new IndirectReference(9, 0)));
        var diagnostics = new DiagnosticCollection();

        var decoded = PdfFilterRegistry.Default.Decode(
            dict, [1, 2, 3], PdfOptions.Default, diagnostics,
            resolver: static _ => new PdfDictionary()); // resolves, but not a name/array of names

        Assert.Equal([1, 2, 3], decoded);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME3012", diagnostic.Code);
    }

    [Fact]
    public void PdfFilterRegistry_IndirectDecodeParmsWithResolver_DanglingReference_ReportsPlume3012()
    {
        var original = Encoding.ASCII.GetBytes("no predictor applied");
        var compressed = ZlibCompress(original);

        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        dict.Set(PdfName.DecodeParms, new PdfReference(new IndirectReference(9, 0)));
        var diagnostics = new DiagnosticCollection();

        var decoded = PdfFilterRegistry.Default.Decode(
            dict, compressed, PdfOptions.Default, diagnostics,
            resolver: static _ => null); // resolver supplied, but object 9 0 doesn't exist

        Assert.Equal(original, decoded); // predictor un-filtering still skipped
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME3012", diagnostic.Code);
    }

    [Fact]
    public void PdfFilterRegistry_IndirectDecodeParmsWithResolver_WrongShape_ReportsPlume3012()
    {
        var original = Encoding.ASCII.GetBytes("no predictor applied");
        var compressed = ZlibCompress(original);

        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        dict.Set(PdfName.DecodeParms, new PdfReference(new IndirectReference(9, 0)));
        var diagnostics = new DiagnosticCollection();

        var decoded = PdfFilterRegistry.Default.Decode(
            dict, compressed, PdfOptions.Default, diagnostics,
            resolver: static _ => PdfName.Get("NotADictionary")); // resolves, but wrong shape

        Assert.Equal(original, decoded);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME3012", diagnostic.Code);
    }

    [Fact]
    public void PdfFilterRegistry_IndirectDecodeParmsArrayEntryWithResolver_WrongShape_ReportsPlume3012()
    {
        var original = Encoding.ASCII.GetBytes("array entry wrong shape");
        var compressed = ZlibCompress(original);

        var entryReference = new IndirectReference(9, 0);
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, new PdfArray { PdfName.Get("FlateDecode") });
        dict.Set(PdfName.DecodeParms, new PdfArray { new PdfReference(entryReference) });
        var diagnostics = new DiagnosticCollection();

        var decoded = PdfFilterRegistry.Default.Decode(
            dict, compressed, PdfOptions.Default, diagnostics,
            resolver: r => r == entryReference ? PdfNumber.Get(1) : null); // resolves, wrong shape

        Assert.Equal(original, decoded);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME3012", diagnostic.Code);
    }

    [Fact]
    public void PdfFilterRegistry_IndirectFilterWithResolver_DanglingReference_StrictThrowsPlume3012()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, new PdfReference(new IndirectReference(9, 0)));

        var ex = Assert.Throws<PlumePdfException>(() =>
            PdfFilterRegistry.Default.Decode(
                dict, [1, 2, 3], PdfOptions.Default with { Strict = true },
                resolver: static _ => null));

        Assert.Equal("PLUME3012", ex.Code);
    }

    private sealed class IdentityFilter : IPdfFilter
    {
        public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) => data.ToArray();
    }
}
