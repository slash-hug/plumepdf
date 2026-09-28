using BenchmarkDotNet.Attributes;
using PlumePdf.Elements;

namespace PlumePdf.Benchmarks;

// =============================================================================
// STATUS note (historical):
//
// This file was originally written against the ratified target API
// (Text.Direction, PdfOptions.MaxShapingLookupApplications) ahead of the
// OpenType Layout engine, the Arabic/Devanagari shapers, and the Text.Direction/UAX#9
// bidi work landing — so it would not build standalone until that
// integration merged, the same situation FormsBenchmarks.cs documented for its own
// dependency in Phase 4 (see that file's STATUS note for the precedent this follows).
// That integration has since landed.
// =============================================================================

/// <summary>
/// PlumePDF-only complex-script shaping benchmarks: a Latin paragraph baseline —
/// <see cref="Manuscript.Render(PdfOptions?)"/>'s
/// pre-existing simple-shaper path, unchanged by this phase — against Arabic and Devanagari
/// paragraphs that exercise the new OpenType Layout engine end to end (script segmentation,
/// GSUB/GPOS lookup execution, UAX#9 bidi resolution, cluster-driven <c>/ToUnicode</c>).
/// No permissively-licensed .NET complex shaper exists to compare against, and
/// <c>bench.sln</c>'s AGPL-isolated competitors (iText7, PdfPig, PDFsharp, confined to
/// <c>benchmarks/PlumePdf.Benchmarks.Comparisons/</c> per the AGPL isolation wall) do not
/// shape complex scripts on the creation side either — so this phase's benchmark
/// gate is demoted to a PlumePDF-only suite with a stored baseline (the <see cref="LatinParagraph"/>
/// benchmark, <c>Baseline = true</c>) rather than a competitor comparison, amending
/// <c>docs/spec.md</c>'s benchmark-gate wording (the same demotion precedent used elsewhere
/// in this repo).
///
/// The Arabic and Devanagari benchmarks need the pinned Noto Naskh Arabic / Noto Sans
/// Devanagari fixtures the corpus and cookbook tests use
/// (<c>scripts/fetch-corpora.sh</c> into gitignored <c>corpora/fonts/</c>, not always
/// present). Following <see cref="OpenBenchmarks"/>'s "a benchmark suite should run with
/// nothing but <c>dotnet run</c>" rule, <see cref="Setup"/> checks for the fixtures up
/// front and the affected benchmarks become harmless no-ops (with one loud console
/// warning each) instead of crashing the whole run when a fixture is absent — the
/// benchmark-suite analogue of the cookbook recipes' self-skip pattern
/// (<c>CookbookTests.CreatePdfA</c>/<c>CreateArabicDocument</c>).
/// </summary>
[MemoryDiagnoser]
public class ShapingBenchmarks
{
    private const string LatinText =
        "PlumePDF renders paragraphs of ordinary Latin text through the same layout engine " +
        "every other creation recipe uses: word wrapping, line spacing, and Standard-14 glyph " +
        "selection, with no embedded font required. This paragraph exists purely to give the " +
        "shaping benchmarks a same-shape, same-length baseline to compare the Arabic and " +
        "Devanagari paragraphs against, so the reported ratio reflects shaping cost rather " +
        "than incidental differences in paragraph length or line count.";

    // "PlumePDF shapes paragraphs of Arabic text through the OpenType Layout engine: joining
    // forms, mark attachment, and right-to-left bidi resolution, mixed with plain digits like
    // 2026 exactly like a real invoice or letter." — an intentionally similar length/shape to
    // LatinText so the two are comparable.
    private const string ArabicText =
        "تعرض PlumePDF فقرات من النص العربي عبر محرك تخطيط أنواع الخطوط المفتوحة: أشكال " +
        "الوصل، وربط علامات التشكيل، وتحليل الاتجاه ثنائي من اليمين إلى اليسار، ممزوجة " +
        "بأرقام عادية مثل 2026 تمامًا كما في فاتورة أو رسالة حقيقية. هذه الفقرة موجودة " +
        "لتمنح مقاييس الأداء أساس مقارنة بنفس الشكل والطول تقريبًا.";

    // "PlumePDF shapes paragraphs of Devanagari text through the OpenType Layout engine:
    // conjunct formation, reph and pre-base matra reordering, mixed with digits like 2026
    // exactly like a real document." — again a comparable length/shape to LatinText.
    private const string DevanagariText =
        "प्लूमपीडीएफ ओपनटाइप लेआउट इंजन के माध्यम से देवनागरी पाठ के अनुच्छेदों को आकार " +
        "देता है: संयुक्ताक्षर निर्माण, रेफ और मात्रा पुनर्क्रमण, तथा 2026 जैसे सामान्य " +
        "अंकों के साथ मिश्रित, ठीक वैसे ही जैसे किसी वास्तविक दस्तावेज़ में होता है। यह " +
        "अनुच्छेद बेंचमार्क को लैटिन आधार रेखा जैसी ही लंबाई और आकार देने के लिए मौजूद है।";

    private Manuscript _latinParagraph = null!;
    private Manuscript? _arabicParagraph;
    private Manuscript? _devanagariParagraph;

    [GlobalSetup]
    public void Setup()
    {
        _latinParagraph = BuildParagraph(LatinText, font: null); // Standard-14 Helvetica — always available, nothing to embed.

        var arabicFontPath = Path.Combine(FindRepoRoot(), "corpora", "fonts", "NotoNaskhArabic-Regular.ttf");
        if (File.Exists(arabicFontPath))
        {
            _arabicParagraph = BuildParagraph(ArabicText, PdfFont.FromFile(arabicFontPath));
        }
        else
        {
            Console.WriteLine($"ShapingBenchmarks: SKIPPING ArabicParagraph — '{arabicFontPath}' not fetched (run scripts/fetch-corpora.sh).");
        }

        var devanagariFontPath = Path.Combine(FindRepoRoot(), "corpora", "fonts", "NotoSansDevanagari-Regular.ttf");
        if (File.Exists(devanagariFontPath))
        {
            _devanagariParagraph = BuildParagraph(DevanagariText, PdfFont.FromFile(devanagariFontPath));
        }
        else
        {
            Console.WriteLine($"ShapingBenchmarks: SKIPPING DevanagariParagraph — '{devanagariFontPath}' not fetched (run scripts/fetch-corpora.sh).");
        }
    }

    /// <summary>The regression-guard baseline: an ordinary Latin paragraph through the pre-existing simple-shaper path.</summary>
    [Benchmark(Baseline = true)]
    public PdfDocument LatinParagraph() => _latinParagraph.Render();

    /// <summary>An Arabic paragraph through the complex-script shaping engine (joining forms, mark attachment, UAX#9 bidi). No-op (returns <see langword="null"/>) when the Arabic font fixture hasn't been fetched.</summary>
    [Benchmark]
    public PdfDocument? ArabicParagraph() => _arabicParagraph?.Render();

    /// <summary>A Devanagari paragraph through the complex-script shaping engine (syllable reordering, conjuncts, UAX#9 bidi). No-op (returns <see langword="null"/>) when the Devanagari font fixture hasn't been fetched.</summary>
    [Benchmark]
    public PdfDocument? DevanagariParagraph() => _devanagariParagraph?.Render();

    private static Manuscript BuildParagraph(string text, PdfFont? font) => new()
    {
        Sections =
        [
            new Section
            {
                PageSize = PageSize.A4,
                Margins = Margins.Uniform(40),
                // Auto applies UAX#9's first-strong heuristic; every paragraph here is
                // single-script-dominant (plus embedded Western digits) so Auto resolves the
                // same base direction an explicit Direction would.
                Body = new Text(text) { Font = font, Direction = TextDirection.Auto, Align = HorizontalAlign.Start },
            },
        ],
    };

    /// <summary>
    /// Walks up from the executing assembly's output directory until <c>PlumePdf.sln</c> is
    /// found — mirrors <c>PlumePdf.CorpusTests.CorpusFixture.FindRepoRoot</c>'s reasoning
    /// (deliberately not a fixed relative path, which breaks the moment the output path
    /// depth changes) without taking a project reference across solution boundaries.
    /// </summary>
    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PlumePdf.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (no ancestor of AppContext.BaseDirectory contains PlumePdf.sln).");
    }
}
