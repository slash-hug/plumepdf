using System.Diagnostics;
using System.Text.RegularExpressions;
using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// The malformed-input gate for the JPEG 2000 decoder: every hand-built header that crashed, hung,
/// or allocated without bound during hardening — plus the edge values around them — must, within
/// <see cref="TimeLimitMilliseconds"/>, either throw a <see cref="PlumePdfException"/> carrying a
/// <c>PLUME37xx</c> code or return an image (with the deviation the case requires recorded). Never
/// a framework exception, never a hang. A fixed-seed byte-mutation fuzz over three committed
/// fixtures pins the same property for inputs nobody thought to hand-build, and an allocation
/// ceiling pins the memory posture (<see cref="JpxDecodeBudget"/>) so the
/// <c>double[,]</c>-per-level regression measured during hardening cannot come back silently.
/// </summary>
public class JpxMalformedInputTests
{
    /// <summary>Wall-clock ceiling per input. A decoder that is merely slow on a 100-byte header is a decoder with an unbounded loop.</summary>
    private const int TimeLimitMilliseconds = 2000;

    /// <summary>The outer bound of the malformed-input gate's alloc check: cumulative bytes per reference-grid pixel per component, over the whole decode. The decoder measures ≈ 17–19; the pre-fix <c>double[,]</c> wavelet measured 65.</summary>
    private const int MaxAllocatedBytesPerPixelPerComponent = 32;

    private static readonly Regex JpxCode = new("^PLUME37[0-9]{2}$", RegexOptions.Compiled);

    private sealed record Expectation(string? RefusalCode, string[] RequiredDiagnostics, string[] ForbiddenDiagnostics, bool AnyCodedOrDecoded)
    {
        public static Expectation Refused(string code) => new(code, [], [], false);

        public static Expectation Decodes(params string[] requiredDiagnostics) => new(null, requiredDiagnostics, [], false);

        public static Expectation DecodesWithout(params string[] forbiddenDiagnostics) => new(null, [], forbiddenDiagnostics, false);

        public static readonly Expectation CodedOrDecoded = new(null, [], [], true);
    }

    private sealed record Outcome(string Kind, string? Code, string? Detail, IReadOnlyList<string> Diagnostics, TimeSpan Elapsed);

    private static readonly Dictionary<string, (Func<byte[]> Build, Expectation Expect)> Cases = new()
    {
        // ---- B3 / C3: decomposition-level shifts (1 << 31, 1 << 32) ----
        ["nl-32-on-16x16"] = (() => new J2kBuilder { DecompositionLevels = 32 }.BuildCodestream(), Expectation.Decodes()),
        ["nl-31-on-16x16"] = (() => new J2kBuilder { DecompositionLevels = 31 }.BuildCodestream(), Expectation.Decodes()),
        ["nl-32-inside-jp2"] = (() => J2kBuilder.Jp2(new J2kBuilder { DecompositionLevels = 32 }.BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), J2kBuilder.ColrEnum(17)), Expectation.Decodes()),

        // ---- B1 / C4: RPCL/PCRL/CPRL precinct step shifted to zero or negative ----
        ["rpcl-step-shift-xrsiz2-nl16"] = (() => new J2kBuilder { Progression = 2, DecompositionLevels = 16, Xsiz = 64, Ysiz = 64, XTsiz = 64, YTsiz = 64, Components = [(7, 2, 2)] }.BuildCodestream(), Expectation.Decodes()),
        ["pcrl-step-shift-xrsiz2-nl16"] = (() => new J2kBuilder { Progression = 3, DecompositionLevels = 16, Xsiz = 64, Ysiz = 64, XTsiz = 64, YTsiz = 64, Components = [(7, 2, 2)] }.BuildCodestream(), Expectation.Decodes()),
        ["cprl-step-shift-xrsiz2-nl16"] = (() => new J2kBuilder { Progression = 4, DecompositionLevels = 16, Xsiz = 64, Ysiz = 64, XTsiz = 64, YTsiz = 64, Components = [(7, 2, 2)] }.BuildCodestream(), Expectation.Decodes()),
        ["rpcl-negative-step-xrsiz255-nl16"] = (() => new J2kBuilder { Progression = 2, DecompositionLevels = 16, Xsiz = 64, Ysiz = 64, XTsiz = 64, YTsiz = 64, Components = [(7, 255, 255)] }.BuildCodestream(), Expectation.Decodes()),
        ["rpcl-xrsiz1-nl16-ppx15"] = (() => new J2kBuilder { Progression = 2, DecompositionLevels = 16, Xsiz = 64, Ysiz = 64, XTsiz = 64, YTsiz = 64 }.BuildCodestream(), Expectation.Decodes()),

        // ---- B2 / H1: packet-header bit reader past end of data ----
        ["tag-tree-zero-bit-plane-run-past-end"] = (() => new J2kBuilder { Xsiz = 8, Ysiz = 8, XTsiz = 8, YTsiz = 8, DecompositionLevels = 0, Body = [0xC0] }.BuildCodestream(), Expectation.Decodes(JpxDiagnosticCodes.PacketHeaderInvalid)),
        ["tag-tree-zero-run-200-zero-bytes"] = (() => new J2kBuilder { Xsiz = 8, Ysiz = 8, XTsiz = 8, YTsiz = 8, DecompositionLevels = 0, Body = [0xC0, .. new byte[200]] }.BuildCodestream(), Expectation.Decodes(JpxDiagnosticCodes.PacketHeaderInvalid)),
        ["non-empty-packet-then-not-included"] = (() => new J2kBuilder { Xsiz = 8, Ysiz = 8, XTsiz = 8, YTsiz = 8, DecompositionLevels = 0, Body = [0x80] }.BuildCodestream(), Expectation.Decodes()),
        ["lblock-ones-run"] = (() => new J2kBuilder { Xsiz = 8, Ysiz = 8, XTsiz = 8, YTsiz = 8, DecompositionLevels = 0, Body = [0xC0, 0x00, 0x7F, .. Enumerable.Repeat((byte)0xFE, 600)] }.BuildCodestream(), Expectation.Decodes(JpxDiagnosticCodes.PacketHeaderInvalid)),
        ["tag-tree-burn-across-40-tiles"] = (() => TagTreeBurnAcrossTiles(40), Expectation.Decodes(JpxDiagnosticCodes.PacketHeaderInvalid)),

        // ---- B4 / C1: component transform over unequal grids; empty component grids ----
        ["mct-rct-over-subsampled-component"] = (() => new J2kBuilder { Mct = 1, Xsiz = 64, Ysiz = 64, XTsiz = 64, YTsiz = 64, Components = [(7, 1, 1), (7, 16, 16), (7, 1, 1)] }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodingStyleUnsupported)),
        ["mct-ict-over-subsampled-component"] = (() => new J2kBuilder { Mct = 1, Transform = 0, Sqcd = 0x42, QcdStepBytes = 8, Xsiz = 64, Ysiz = 64, XTsiz = 64, YTsiz = 64, Components = [(7, 1, 1), (7, 16, 16), (7, 1, 1)] }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodingStyleUnsupported)),
        ["mct-third-component-subsampled"] = (() => new J2kBuilder { Mct = 1, Xsiz = 64, Ysiz = 64, XTsiz = 64, YTsiz = 64, Components = [(7, 1, 1), (7, 1, 1), (7, 4, 4)] }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodingStyleUnsupported)),
        ["mct-over-mixed-bit-depths"] = (() => new J2kBuilder { Mct = 1, Xsiz = 64, Ysiz = 64, XTsiz = 64, YTsiz = 64, Components = [(7, 1, 1), (11, 1, 1), (7, 1, 1)] }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodingStyleUnsupported)),
        ["mct-over-mixed-bit-depths-third"] = (() => new J2kBuilder { Mct = 1, Xsiz = 64, Ysiz = 64, XTsiz = 64, YTsiz = 64, Components = [(7, 1, 1), (7, 1, 1), (15, 1, 1)] }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodingStyleUnsupported)),
        ["mct-on-single-component"] = (() => new J2kBuilder { Mct = 1, Csiz = 1 }.BuildCodestream(), Expectation.Decodes(JpxDiagnosticCodes.HeaderInvalid)),
        ["mct-on-two-components"] = (() => new J2kBuilder { Mct = 1, Csiz = 2 }.BuildCodestream(), Expectation.Decodes(JpxDiagnosticCodes.HeaderInvalid)),
        ["mct-on-three-components-is-not-reported"] = (() => new J2kBuilder { Mct = 1, Csiz = 3 }.BuildCodestream(), Expectation.DecodesWithout(JpxDiagnosticCodes.HeaderInvalid)),
        ["empty-component-grid"] = (() => new J2kBuilder { Xsiz = 100, XOsiz = 99, Ysiz = 100, YOsiz = 99, XTsiz = 100, YTsiz = 100, Components = [(7, 255, 255)] }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.GeometryInvalid)),
        ["second-component-grid-empty"] = (() => new J2kBuilder { Xsiz = 100, XOsiz = 99, Ysiz = 100, YOsiz = 99, XTsiz = 100, YTsiz = 100, Components = [(7, 1, 1), (7, 255, 255)] }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.GeometryInvalid)),
        ["layers-65535-x-400-empty-components"] = (() => new J2kBuilder { Layers = 65535, DecompositionLevels = 32, Xsiz = 100, XOsiz = 99, Ysiz = 100, YOsiz = 99, XTsiz = 100, YTsiz = 100, Components = Enumerable.Range(0, 400).Select(_ => (7, 255, 255)).ToArray(), Body = [0x00] }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.GeometryInvalid)),

        // ---- allocation and time not bounded by MaxImagePixels ----
        ["tile-grid-67m-tiles-of-1x1"] = (() => new J2kBuilder { Xsiz = 8192, Ysiz = 8192, XTsiz = 1, YTsiz = 1 }.BuildCodestream(), Expectation.Decodes()),
        ["tile-grid-121m-tiles-of-1x1"] = (() => new J2kBuilder { Xsiz = 11000, Ysiz = 11000, XTsiz = 1, YTsiz = 1 }.BuildCodestream(), Expectation.Decodes()),
        ["precinct-bomb-4096-pp0-nl0"] = (() => new J2kBuilder { Scod = 1, DecompositionLevels = 0, PrecinctBytes = [0], Xsiz = 4096, Ysiz = 4096, XTsiz = 4096, YTsiz = 4096 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.ImageTooLarge)),
        ["precinct-pp1-over-8000-square"] = (() => new J2kBuilder { Scod = 1, DecompositionLevels = 1, PrecinctBytes = [0x11, 0x11], Xsiz = 8000, Ysiz = 8000, XTsiz = 8000, YTsiz = 8000 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.ImageTooLarge)),
        ["precinct-exponent-zero-above-r0"] = (() => new J2kBuilder { Scod = 1, DecompositionLevels = 3, PrecinctBytes = [0, 0, 0, 0], Xsiz = 64, Ysiz = 64, XTsiz = 64, YTsiz = 64 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodingStyleUnsupported)),
        ["precinct-exponent-zero-at-r0-only"] = (() => new J2kBuilder { Scod = 1, DecompositionLevels = 3, PrecinctBytes = [0, 0x11, 0x22, 0x33], Xsiz = 64, Ysiz = 64, XTsiz = 64, YTsiz = 64 }.BuildCodestream(), Expectation.Decodes()),
        ["upsample-amplification-64-comps-xrsiz32-8192"] = (() => new J2kBuilder { Xsiz = 8192, Ysiz = 8192, XTsiz = 8192, YTsiz = 8192, Components = Enumerable.Range(0, 64).Select(_ => (7, 32, 32)).ToArray() }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.ImageTooLarge)),
        ["palette-255-columns-over-8192-square"] = (() => J2kBuilder.Jp2(new J2kBuilder { Xsiz = 8192, Ysiz = 8192, XTsiz = 8192, YTsiz = 8192 }.BuildCodestream(), J2kBuilder.Ihdr(8192, 8192, 1, 7), J2kBuilder.ColrEnum(16), J2kBuilder.Pclr(2, 255, 7), J2kBuilder.Cmap(255, i => (0, 1, i))), Expectation.Refused(JpxDiagnosticCodes.ImageTooLarge)),
        ["palette-255-columns-inconsistent-2-entry-cmap-over-8192-square"] = (() => Palette255WithInconsistentCmap(8192), Expectation.Refused(JpxDiagnosticCodes.ImageTooLarge)),
        ["palette-255-columns-inconsistent-2-entry-cmap-over-4096-square"] = (() => Palette255WithInconsistentCmap(4096), Expectation.Refused(JpxDiagnosticCodes.ImageTooLarge)),
        ["palette-255-columns-inconsistent-2-entry-cmap-over-32-square"] = (() => Palette255WithInconsistentCmap(32), Expectation.Decodes(JpxDiagnosticCodes.ColourBoxInvalid)),
        // Control: a CONSISTENT 2-entry cmap over the same 255-column palette decodes (2 planes). Sized at
        // 1024² so a legitimate decode stays well inside the 2 s bound on the CI runner — the 4096²
        // twin took 3.5 s under parallel test load on ubuntu-latest (main CI run on b8e4322).
        ["palette-255-columns-consistent-2-entry-cmap-over-1024-square"] = (() => J2kBuilder.Jp2(new J2kBuilder { Xsiz = 1024, Ysiz = 1024, XTsiz = 1024, YTsiz = 1024 }.BuildCodestream(), J2kBuilder.Ihdr(1024, 1024, 1, 7), J2kBuilder.ColrEnum(16), J2kBuilder.Pclr(2, 255, 7), J2kBuilder.Cmap(2, i => (0, 1, i))), Expectation.Decodes()),
        // Control for the charged wavelet scratch row (a legitimate wide decode). 4 M samples, not 20 M:
        // the 20 M version took 3.2 s on ubuntu-latest under parallel load (main CI run on b8e4322)
        // and this gate bounds *malformed* inputs at 2 s; a legitimate decode must sit far below it.
        ["wide-4m-by-1-scratch-row"] = (() => new J2kBuilder { Xsiz = 4_000_000, Ysiz = 1, XTsiz = 4_000_000, YTsiz = 1, DecompositionLevels = 1 }.BuildCodestream(), Expectation.Decodes()),
        ["cmap-16383-entries"] = (() => J2kBuilder.Jp2(new J2kBuilder { Xsiz = 32, Ysiz = 32, XTsiz = 32, YTsiz = 32 }.BuildCodestream(), J2kBuilder.Ihdr(32, 32, 1, 7), J2kBuilder.ColrEnum(16), J2kBuilder.Pclr(2, 255, 7), J2kBuilder.Cmap(16383, i => (0, 1, i % 255))), Expectation.Decodes(JpxDiagnosticCodes.ColourBoxInvalid)),
        ["pclr-zero-columns"] = (() => J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), J2kBuilder.ColrEnum(16), J2kBuilder.Pclr(4, 0, 7)), Expectation.Decodes(JpxDiagnosticCodes.ColourBoxInvalid)),
        ["pclr-38-bit-entries"] = (() => J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), J2kBuilder.ColrEnum(16), J2kBuilder.Pclr(4, 3, 37)), Expectation.Decodes(JpxDiagnosticCodes.ColourBoxInvalid)),
        ["csiz-21832-on-4x4"] = (() => new J2kBuilder { Xsiz = 4, Ysiz = 4, XTsiz = 4, YTsiz = 4, Components = Enumerable.Range(0, 21832).Select(_ => (7, 1, 1)).ToArray() }.BuildCodestream(), Expectation.Decodes()),
        ["many-tile-parts-20000-for-tile-0"] = (() => ManyTileParts(20000), Expectation.Decodes()),
        ["reference-grid-100000-square"] = (() => new J2kBuilder { Xsiz = 100000, Ysiz = 100000, XTsiz = 100000, YTsiz = 100000 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.ImageTooLarge)),

        // ---- POC volumes out of range ----
        ["poc-cepoc-200-with-csiz-1-lrcp"] = (() => WithPoc(0, (0, 0, 1, 2, 200, 0)), Expectation.Decodes()),
        ["poc-cepoc-200-with-csiz-1-rpcl"] = (() => WithPoc(2, (0, 0, 1, 2, 200, 2)), Expectation.Decodes()),
        ["poc-cepoc-200-with-csiz-1-cprl"] = (() => WithPoc(4, (0, 0, 1, 2, 200, 4)), Expectation.Decodes()),
        ["poc-repoc-255-lyepoc-65535"] = (() => WithPoc(0, (0, 0, 65535, 255, 1, 0)), Expectation.Decodes()),
        ["poc-cspoc-200-past-csiz"] = (() => WithPoc(0, (0, 200, 1, 2, 255, 0)), Expectation.Decodes(JpxDiagnosticCodes.PocInvalid)),
        ["poc-rspoc-past-repoc"] = (() => WithPoc(0, (5, 0, 1, 2, 1, 0)), Expectation.Decodes(JpxDiagnosticCodes.PocInvalid)),
        ["poc-zero-layers"] = (() => WithPoc(0, (0, 0, 0, 2, 1, 0)), Expectation.Decodes(JpxDiagnosticCodes.PocInvalid)),

        // ---- cdef channel index past the planes ----
        ["cdef-alpha-index-9999"] = (() => J2kBuilder.Jp2(new J2kBuilder { Xsiz = 8, Ysiz = 8, XTsiz = 8, YTsiz = 8 }.BuildCodestream(), J2kBuilder.Ihdr(8, 8, 1, 7), J2kBuilder.ColrEnum(17), J2kBuilder.Cdef((9999, 1, 0))), Expectation.Decodes(JpxDiagnosticCodes.ColourBoxInvalid)),
        ["cdef-alpha-index-equal-to-plane-count"] = (() => J2kBuilder.Jp2(new J2kBuilder { Xsiz = 8, Ysiz = 8, XTsiz = 8, YTsiz = 8, Csiz = 3 }.BuildCodestream(), J2kBuilder.Ihdr(8, 8, 3, 7), J2kBuilder.ColrEnum(16), J2kBuilder.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 0))), Expectation.Decodes(JpxDiagnosticCodes.ColourBoxInvalid)),

        // ---- Suggestions: M_b, QCD, SIZ, SOT, COD edge values ----
        ["mb-37-guard-7-exponent-31"] = (() => new J2kBuilder { Sqcd = 0xE0, QcdStepByte = 0xF8 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodingStyleUnsupported)),
        ["mb-31-is-accepted"] = (() => new J2kBuilder { Sqcd = 0x20, QcdStepByte = 0xF8 }.BuildCodestream(), Expectation.Decodes()),
        ["qcd-empty"] = (() => new J2kBuilder { QcdStepBytes = 0 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.HeaderInvalid)),
        ["qcd-style2-single-byte"] = (() => new J2kBuilder { Sqcd = 0x42, QcdStepBytes = 1 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.HeaderInvalid)),
        ["qcd-short-for-nl5"] = (() => new J2kBuilder { DecompositionLevels = 5, QcdStepBytes = 4 }.BuildCodestream(), Expectation.Decodes(JpxDiagnosticCodes.HeaderInvalid)),
        ["csiz-zero"] = (() => new J2kBuilder { Components = [], DeclaredCsiz = 0 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.GeometryInvalid)),
        ["csiz-declared-3-with-1-listed"] = (() => new J2kBuilder { DeclaredCsiz = 3 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.HeaderInvalid)),
        ["xrsiz-zero"] = (() => new J2kBuilder { Components = [(7, 0, 1)] }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.GeometryInvalid)),
        ["xrsiz-255-on-16x16"] = (() => new J2kBuilder { Components = [(7, 255, 255)] }.BuildCodestream(), Expectation.Decodes()),
        ["origin-at-image-size"] = (() => new J2kBuilder { XOsiz = 16, Xsiz = 16 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.GeometryInvalid)),
        ["tile-grid-not-covering-origin"] = (() => new J2kBuilder { XTOsiz = 4, XTsiz = 8 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.GeometryInvalid)),
        ["tile-grid-near-int-max"] = (() => new J2kBuilder { Xsiz = int.MaxValue, XOsiz = int.MaxValue - 16, XTsiz = int.MaxValue, Ysiz = 16, YTsiz = 16 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.GeometryInvalid)),
        ["xsiz-above-int-max"] = (() => new J2kBuilder { Xsiz = 0x8000_0010, XOsiz = 0x8000_0000, XTsiz = 16 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.GeometryInvalid)),
        ["lsiz-mismatch"] = (() => new J2kBuilder { LsizOverride = 100 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.HeaderInvalid)),
        ["lsiz-too-short"] = (() => new J2kBuilder { LsizOverride = 20 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.HeaderInvalid)),
        ["xcb-ycb-sum-over-12"] = (() => new J2kBuilder { XcbMinus2 = 8, YcbMinus2 = 8 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodeBlockSizeInvalid)),
        ["xcb-over-10"] = (() => new J2kBuilder { XcbMinus2 = 9, YcbMinus2 = 0 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodeBlockSizeInvalid)),
        ["nl-33"] = (() => new J2kBuilder { DecompositionLevels = 33 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodingStyleUnsupported)),
        ["layers-zero"] = (() => new J2kBuilder { Layers = 0 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodingStyleUnsupported)),
        ["progression-byte-7"] = (() => new J2kBuilder { Progression = 7 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodingStyleUnsupported)),
        ["mct-byte-2"] = (() => new J2kBuilder { Mct = 2, Csiz = 3 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodingStyleUnsupported)),
        ["wavelet-byte-2"] = (() => new J2kBuilder { Transform = 2 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.CodingStyleUnsupported)),
        ["rsiz-part2-bit"] = (() => new J2kBuilder { Rsiz = 0x8000 }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.Part2)),
        ["precision-17"] = (() => new J2kBuilder { Components = [(16, 1, 1)] }.BuildCodestream(), Expectation.Refused(JpxDiagnosticCodes.PrecisionUnsupported)),
        ["psot-int-max"] = (() => new J2kBuilder { Psot = 0x7FFFFFFF }.BuildCodestream(), Expectation.Decodes(JpxDiagnosticCodes.Truncated)),
        ["psot-uint-max"] = (() => new J2kBuilder { Psot = 0xFFFFFFFF }.BuildCodestream(), Expectation.Decodes(JpxDiagnosticCodes.Truncated)),
        ["psot-zero"] = (() => new J2kBuilder { Psot = 0 }.BuildCodestream(), Expectation.Decodes()),
        ["psot-one"] = (() => new J2kBuilder { Psot = 1 }.BuildCodestream(), Expectation.Decodes(JpxDiagnosticCodes.TilePartSequence)),
        ["psot-13-shorter-than-header"] = (() => new J2kBuilder { Psot = 13 }.BuildCodestream(), Expectation.Decodes(JpxDiagnosticCodes.TilePartSequence)),
        ["tnsot-zero"] = (() => new J2kBuilder { TNsot = 0 }.BuildCodestream(), Expectation.Decodes()),
        ["tpsot-out-of-order"] = (() => new J2kBuilder { TPsot = 3 }.BuildCodestream(), Expectation.Decodes(JpxDiagnosticCodes.TilePartSequence)),
        ["sot-tile-index-beyond-grid"] = (() => new J2kBuilder { Isot = 7 }.BuildCodestream(), Expectation.Decodes(JpxDiagnosticCodes.TilePartSequence)),
        ["no-tile-parts-at-all"] = (() => new J2kBuilder { EmitSot = false }.BuildCodestream(), Expectation.Decodes()),
        ["no-eoc"] = (() => new J2kBuilder { EmitEoc = false }.BuildCodestream(), Expectation.CodedOrDecoded),
        ["rgn-in-main-header"] = (() => WithExtraSegment([0xFF, 0x5E, 0x00, 0x05, 0x00, 0x00, 0x05]), Expectation.Refused(JpxDiagnosticCodes.RoiUnsupported)),
        ["ppm-in-main-header"] = (() => WithExtraSegment([0xFF, 0x60, 0x00, 0x03, 0x00]), Expectation.Refused(JpxDiagnosticCodes.PackedHeadersUnsupported)),
        ["com-marker-with-length-past-end"] = (() => WithExtraSegment([0xFF, 0x64, 0xFF, 0xFF, 0x00]), Expectation.Decodes(JpxDiagnosticCodes.Truncated)),
        ["qcc-with-length-past-end"] = (() => WithExtraSegment([0xFF, 0x5D, 0xFF, 0xFF, 0x00]), Expectation.Decodes(JpxDiagnosticCodes.Truncated)),
        ["duplicate-cod"] = (() => WithExtraSegment([0xFF, 0x52, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x04, 0x04, 0x00, 0x01]), Expectation.Refused(JpxDiagnosticCodes.HeaderInvalid)),

        // ---- JP2 box lengths ----
        // A jp2c whose declared length overshoots EOF is "cut short inside the codestream", not
        // "not JPEG 2000": the available bytes decode (here the whole codestream IS present, so
        // no PLUME3701 follows) and the wrapper's length disagreement is the recorded deviation
        // A length smaller than the header itself stays a
        // refusal — nothing recoverable about it.
        ["jp2-xlbox-max"] = (() => Jp2WithXlbox(0xFFFF_FFFF_FFFF_FFFF), Expectation.Decodes(JpxDiagnosticCodes.Jp2BoxInvalid)),
        ["jp2-xlbox-smaller-than-header"] = (() => Jp2WithXlbox(3), Expectation.Refused(JpxDiagnosticCodes.NotJpeg2000)),
        ["jp2-lbox-zero-runs-to-end"] = (() => Jp2WithLbox(0), Expectation.Decodes()),
        ["jp2-lbox-past-end"] = (() => Jp2WithLbox(0x7FFF_FFFF), Expectation.Decodes(JpxDiagnosticCodes.Jp2BoxInvalid)),
        ["jp2-no-jp2c"] = (() => J2kBuilder.Jp2([], J2kBuilder.Ihdr(16, 16, 1, 7))[..^(8)], Expectation.Refused(JpxDiagnosticCodes.NotJpeg2000)),
        ["jp2-empty-jp2c"] = (() => J2kBuilder.Jp2([], J2kBuilder.Ihdr(16, 16, 1, 7)), Expectation.Refused(JpxDiagnosticCodes.NotJpeg2000)),
        ["not-jpeg2000-at-all"] = (() => "%PDF-1.7 not a codestream"u8.ToArray(), Expectation.Refused(JpxDiagnosticCodes.NotJpeg2000)),
        ["soc-only"] = (() => [0xFF, 0x4F], Expectation.Refused(JpxDiagnosticCodes.NotJpeg2000)),
        ["empty-input"] = (() => [], Expectation.Refused(JpxDiagnosticCodes.NotJpeg2000)),
    };

    public static TheoryData<string> CaseNames => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void MalformedInput_IsRefusedWithAJpxCodeOrDecodedWithinTheTimeLimit(string caseName)
    {
        var (build, expect) = Cases[caseName];
        var outcome = Run(build());

        AssertOutcome(caseName, outcome, expect);
    }

    // ---- truncation at every offset of a small complete stream ----

    public static TheoryData<int> TruncationOffsets
    {
        get
        {
            var data = new TheoryData<int>();
            var length = new J2kBuilder().BuildCodestream().Length;
            for (var cut = 0; cut < length; cut++)
            {
                data.Add(cut);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(TruncationOffsets))]
    public void TruncatedAtEveryOffset_IsCodedOrDecodedWithinTheTimeLimit(int cut)
    {
        var full = new J2kBuilder().BuildCodestream();
        var outcome = Run(full.AsSpan(0, cut).ToArray());

        AssertOutcome($"truncated-at-{cut}/{full.Length}", outcome, Expectation.CodedOrDecoded);
    }

    // ---- fixed-seed byte-mutation fuzz over committed fixtures ----

    [Fact]
    public void ByteMutationFuzz_FixedSeed_NeverEscapesOrHangs()
    {
        const int seed = 20260905;
        const int mutationsPerFixture = 200;
        string[] fixtures = ["baseline-53.j2k", "wavelet-97.jp2", "tiles-3x2-partial.j2k"];
        var fixturesDir = Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "jpx");
        var rng = new Random(seed);
        var failures = new List<string>();
        var runs = 0;

        foreach (var fixture in fixtures)
        {
            var original = File.ReadAllBytes(Path.Combine(fixturesDir, fixture));
            for (var i = 0; i < mutationsPerFixture; i++)
            {
                var data = (byte[])original.Clone();
                var flips = 1 + rng.Next(8);
                for (var k = 0; k < flips; k++)
                {
                    // Three of four flips land in the first 400 bytes (the headers), where a
                    // byte change reaches a different decoder branch than in packet data.
                    var position = rng.Next(4) == 0 ? rng.Next(data.Length) : rng.Next(Math.Min(data.Length, 400));
                    data[position] = (byte)rng.Next(256);
                }

                var outcome = Run(data);
                runs++;
                if (outcome.Kind is not ("decoded" or "coded") || outcome.Elapsed.TotalMilliseconds > TimeLimitMilliseconds)
                {
                    failures.Add($"{fixture} mutation {i}: {outcome.Kind} {outcome.Code} {outcome.Detail} ({outcome.Elapsed.TotalMilliseconds:F0} ms)");
                }
            }
        }

        Assert.Equal(fixtures.Length * mutationsPerFixture, runs);
        Assert.True(failures.Count == 0, $"{failures.Count} fuzz escape(s)/hang(s):\n{string.Join("\n", failures)}");
    }

    // ---- allocation ceiling for pathological-but-legal headers under the cap ----

    [Theory]
    [InlineData(4096, 4096, 1, 5, 1, 0)] // 16.7 Mpx single component, 5 levels, 5/3
    [InlineData(2048, 2048, 3, 5, 0, 1)] // 4.2 Mpx RGB, 9/7 + ICT (the production shape)
    [InlineData(3000, 3000, 1, 1, 0, 0)] // 9 Mpx single component, 1 level, 9/7 (the review's 11585² shape, scaled)
    public void WholeDecodeAllocation_StaysWithinTheBudgetPerPixel(int width, int height, int components, int levels, int transform, int mct)
    {
        var builder = new J2kBuilder { Xsiz = width, Ysiz = height, XTsiz = width, YTsiz = height, Csiz = components, DecompositionLevels = levels, Transform = transform, Mct = mct };
        if (transform == 0)
        {
            builder.Sqcd = 0x42;
            builder.QcdStepBytes = 2 * ((3 * levels) + 1);
        }

        var input = builder.BuildCodestream();
        var pixels = (long)width * height;

        // Warm the path once so JIT/type-loading allocations are not attributed to the decode.
        JpxImageDecoder.Decode(new J2kBuilder { Csiz = components, Transform = transform, Mct = mct, Sqcd = builder.Sqcd, QcdStepBytes = builder.QcdStepBytes > 0 ? 2 * 4 : -1 }.BuildCodestream(), PdfOptions.Default, null);

        // GC.GetAllocatedBytesForCurrentThread() is exact and attributable to this decode alone
        // — unlike a process-wide GC.GetTotalMemory() peak sample, it cannot be inflated by
        // whatever else the test process happens to be running concurrently on other threads.
        // This test used to ALSO assert a process-wide peak
        // sample against the same per-pixel ceiling; that arm was logically redundant (a
        // decode's live peak can never exceed what it allocated in total, so a passing
        // per-thread assertion already proves the peak arm's own claim) but not noise-free in
        // practice — CI's full-suite run adds ~65 sibling JPX/cookbook test classes to the same
        // process, and their concurrent allocations pushed the process-wide peak over the
        // ceiling (52-65 B/px/component measured vs. this test's own 32 B/px/component budget)
        // even though this decode's own attributed allocation stayed within it every time. Kept
        // to the one exact, isolation-proof measurement rather than adding xUnit collection
        // serialization, which would only silence the flake for this class's own peers, not the
        // unrelated cookbook/oracle classes actually responsible for the noise.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var image = JpxImageDecoder.Decode(input, PdfOptions.Default, null);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(width, image.Width);
        Assert.Equal(components, image.Planes.Length);
        var ceiling = MaxAllocatedBytesPerPixelPerComponent * pixels * components;
        Assert.True(allocated <= ceiling, $"Decoding a {width}x{height}x{components} header allocated {allocated:N0} bytes ({(double)allocated / pixels / components:F1} B/px/component); the ceiling is {ceiling:N0} ({MaxAllocatedBytesPerPixelPerComponent} B/px/component).");
    }

    public static TheoryData<string> RefuseBeforeAllocatingShapes => new(RefuseBeforeAllocatingCases.Keys);

    private static readonly Dictionary<string, Func<byte[]>> RefuseBeforeAllocatingCases = new()
    {
        // 64 sub-sampled components on an 8192² grid pass the per-component-grid pixel check
        // (64 × 256² samples) but would upsample to 64 full planes — 4.3 GB.
        ["upsample-64-comps-xrsiz32-8192"] = () => new J2kBuilder { Xsiz = 8192, Ysiz = 8192, XTsiz = 8192, YTsiz = 8192, Components = Enumerable.Range(0, 64).Select(_ => (7, 32, 32)).ToArray() }.BuildCodestream(),

        // A 255-column palette under a two-entry cmap whose second entry names column 255: the
        // cmap is inconsistent, the expansion falls back to all 255 columns, and the charge must
        // count those 255 — not the cmap's 2 (4.35 GB at 4096², 17 GB at 8192² from 957 bytes).
        ["palette-255-inconsistent-cmap-8192"] = () => Palette255WithInconsistentCmap(8192),
        ["palette-255-inconsistent-cmap-4096"] = () => Palette255WithInconsistentCmap(4096),
    };

    [Theory]
    [MemberData(nameof(RefuseBeforeAllocatingShapes))]
    public void DecodeBudget_RefusesBeforeAllocatingWhatTheHeaderDeclares(string shape)
    {
        // The budget must refuse at the ledger, not after allocating a measurable fraction of
        // what the header declares.
        var input = RefuseBeforeAllocatingCases[shape]();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<PlumePdfException>(() => JpxImageDecoder.Decode(input, PdfOptions.Default, null));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(JpxDiagnosticCodes.ImageTooLarge, ex.Code);
        Assert.True(allocated < 8L * 1024 * 1024, $"{shape}: the refusal itself allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void RaisedCap_PlaneLargerThanAnArrayCanHold_IsRefusedWithTheCapCode_NotAnOverflow()
    {
        // MaxImagePixels is a public long; a caller who raises it past Array.MaxLength admits a
        // 100000×50000 header (5 Gpx) whose per-plane element count no array can hold. That must
        // be PLUME3718, not a System.OverflowException from an int `Width * Height` at the `new`.
        var input = new J2kBuilder { Xsiz = 100_000, Ysiz = 50_000, XTsiz = 100_000, YTsiz = 50_000 }.BuildCodestream();
        var raised = PdfOptions.Default with { MaxImagePixels = 6_000_000_000 };

        var before = GC.GetAllocatedBytesForCurrentThread();
        var outcome = Run(input, raised);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        AssertOutcome("raised-cap-100000x50000", outcome, Expectation.Refused(JpxDiagnosticCodes.ImageTooLarge));
        Assert.True(allocated < 8L * 1024 * 1024, $"The refusal itself allocated {allocated:N0} bytes.");
    }

    // ---- helpers ----

    private static void AssertOutcome(string caseName, Outcome outcome, Expectation expect)
    {
        Assert.True(outcome.Kind != "hung", $"{caseName}: did not finish within {TimeLimitMilliseconds} ms.");
        Assert.True(outcome.Kind != "escaped", $"{caseName}: escaped with {outcome.Detail}");
        Assert.True(outcome.Elapsed.TotalMilliseconds <= TimeLimitMilliseconds, $"{caseName}: finished ({outcome.Kind} {outcome.Code}) but took {outcome.Elapsed.TotalMilliseconds:F0} ms.");

        if (outcome.Kind == "coded")
        {
            Assert.True(outcome.Code is not null && JpxCode.IsMatch(outcome.Code), $"{caseName}: threw PlumePdfException with a non-JPX code {outcome.Code}: {outcome.Detail}");
        }

        if (expect.AnyCodedOrDecoded)
        {
            return;
        }

        if (expect.RefusalCode is { } code)
        {
            Assert.True(outcome.Kind == "coded" && outcome.Code == code, $"{caseName}: expected a {code} refusal, got {outcome.Kind} {outcome.Code} {outcome.Detail}");
            return;
        }

        Assert.True(outcome.Kind == "decoded", $"{caseName}: expected a decode, got {outcome.Kind} {outcome.Code}: {outcome.Detail}");
        foreach (var required in expect.RequiredDiagnostics)
        {
            Assert.True(outcome.Diagnostics.Contains(required), $"{caseName}: decoded but did not record {required} (recorded: {string.Join(", ", outcome.Diagnostics.Distinct())}).");
        }

        foreach (var forbidden in expect.ForbiddenDiagnostics)
        {
            Assert.True(!outcome.Diagnostics.Contains(forbidden), $"{caseName}: decoded but recorded {forbidden}, which this shape must not trigger (recorded: {string.Join(", ", outcome.Diagnostics.Distinct())}).");
        }
    }

    /// <summary>
    /// Decodes on a dedicated thread so a regression into an unbounded loop fails this test with a
    /// message instead of stalling the whole run. When the worker does not finish in time it is
    /// abandoned, still spinning: the decoder takes no <see cref="CancellationToken"/> (a hung
    /// loop has no cooperative check point to observe one anyway), and .NET has no safe thread
    /// abort. The thread is a background thread, so it dies with the test process — a "hung"
    /// outcome is a failed test, and the run ends shortly after.
    /// </summary>
    private static Outcome Run(byte[] input) => Run(input, PdfOptions.Default);

    private static Outcome Run(byte[] input, PdfOptions options)
    {
        Outcome? result = null;
        var stopwatch = Stopwatch.StartNew();
        var worker = new Thread(() =>
        {
            var diagnostics = new DiagnosticCollection();
            try
            {
                var image = JpxImageDecoder.Decode(input, options, diagnostics);
                if ((long)image.Width * image.Height <= 4_000_000)
                {
                    // The consumer path — a cdef escape only surfaced here.
                    _ = image.ToInterleaved8Bit(dropAlpha: true);
                }

                result = new Outcome("decoded", null, $"{image.Width}x{image.Height} planes={image.Planes.Length}", diagnostics.Select(static d => d.Code).ToArray(), stopwatch.Elapsed);
            }
            catch (PlumePdfException ex)
            {
                result = new Outcome("coded", ex.Code, ex.Message, diagnostics.Select(static d => d.Code).ToArray(), stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                result = new Outcome("escaped", null, $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace?.Split('\n').FirstOrDefault()}", [], stopwatch.Elapsed);
            }
        }, 16 * 1024 * 1024)
        {
            IsBackground = true,
        };

        worker.Start();
        if (!worker.Join(TimeLimitMilliseconds * 5))
        {
            return new Outcome("hung", null, $"no completion after {TimeLimitMilliseconds * 5} ms", [], stopwatch.Elapsed);
        }

        return result!;
    }

    /// <summary>A <paramref name="side"/>² single-component JP2 with a 2-entry × 255-column 8-bit palette and a two-entry <c>cmap</c> whose second entry names column 255 — one past the palette — so the map is inconsistent and expansion falls back to every column (the review's <c>pclr255-badcmap-*.jp2</c> probes).</summary>
    private static byte[] Palette255WithInconsistentCmap(int side) =>
        J2kBuilder.Jp2(new J2kBuilder { Xsiz = side, Ysiz = side, XTsiz = side, YTsiz = side }.BuildCodestream(), J2kBuilder.Ihdr(side, side, 1, 7), J2kBuilder.ColrEnum(16), J2kBuilder.Pclr(2, 255, 7), J2kBuilder.Cmap(2, i => (0, 1, i == 0 ? 0 : 255)));

    private static byte[] WithPoc(int progression, params (int RSpoc, int CSpoc, int LYEpoc, int REpoc, int CEpoc, int Ppoc)[] entries)
    {
        var builder = new J2kBuilder { Xsiz = 32, Ysiz = 32, XTsiz = 32, YTsiz = 32, DecompositionLevels = 1, Progression = progression };
        builder.ExtraMainHeaderSegments.Add(J2kBuilder.Poc(entries));
        return builder.BuildCodestream();
    }

    private static byte[] WithExtraSegment(byte[] segment)
    {
        var builder = new J2kBuilder();
        builder.ExtraMainHeaderSegments.Add(segment);
        return builder.BuildCodestream();
    }

    /// <summary>N tile-parts all claiming tile 0 with ascending <c>TPsot</c> (saturating at 255) and empty bodies.</summary>
    private static byte[] ManyTileParts(int count)
    {
        var ms = new MemoryStream();
        ms.Write(new J2kBuilder().BuildMainHeader());
        for (var i = 0; i < count; i++)
        {
            ms.Write(J2kBuilder.TilePart(isot: 0, tpsot: Math.Min(i, 255), tnsot: 0, body: []));
        }

        J2kBuilder.WriteU16(ms, 0xFFD9);
        return ms.ToArray();
    }

    /// <summary>N 1×1 tiles, each with a one-byte body (0xC0: non-empty packet, block included, then no bits for the zero-bit-plane tree) — one exhausted tag-tree walk per tile.</summary>
    private static byte[] TagTreeBurnAcrossTiles(int tiles)
    {
        var ms = new MemoryStream();
        ms.Write(new J2kBuilder { Xsiz = tiles, Ysiz = 1, XTsiz = 1, YTsiz = 1, DecompositionLevels = 0 }.BuildMainHeader());
        for (var i = 0; i < tiles; i++)
        {
            ms.Write(J2kBuilder.TilePart(isot: i, tpsot: 0, tnsot: 1, body: [0xC0]));
        }

        J2kBuilder.WriteU16(ms, 0xFFD9);
        return ms.ToArray();
    }

    private static byte[] Jp2WithXlbox(ulong xlbox)
    {
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7));
        var index = IndexOf(jp2, "jp2c"u8);
        var result = new List<byte>(jp2[..(index - 4)]);
        result.AddRange([0, 0, 0, 1]); // LBox = 1 => XLBox follows TBox
        result.AddRange(jp2[index..(index + 4)]);
        for (var shift = 56; shift >= 0; shift -= 8)
        {
            result.Add((byte)(xlbox >> shift));
        }

        result.AddRange(jp2[(index + 4)..]);
        return [.. result];
    }

    private static byte[] Jp2WithLbox(uint lbox)
    {
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7));
        var index = IndexOf(jp2, "jp2c"u8);
        jp2[index - 4] = (byte)(lbox >> 24);
        jp2[index - 3] = (byte)(lbox >> 16);
        jp2[index - 2] = (byte)(lbox >> 8);
        jp2[index - 1] = (byte)lbox;
        return jp2;
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        var index = haystack.AsSpan().IndexOf(needle);
        Assert.True(index >= 0, "marker not found in the built JP2");
        return index;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlumePdf.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }
}
