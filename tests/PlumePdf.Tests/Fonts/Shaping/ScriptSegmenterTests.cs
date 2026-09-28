using PlumePdf.Fonts.Shaping;
using Xunit;

namespace PlumePdf.Tests.Fonts.Shaping;

/// <summary>
/// Regression coverage for the CJK/Hebrew over-refusal bug: <see cref="ScriptSegmenter.Classify"/>
/// used to route CJK Unified Ideographs and Hebrew to <see cref="ScriptTag.Unsupported"/>,
/// making <see cref="ComplexShaper"/> throw <c>PLUME8026</c> for any document composing Chinese/
/// Japanese/Hebrew text after upgrading past Phase 6 — even though both scripts render correctly
/// through the plain cmap+GSUB-liga+GPOS-kern path with no contextual shaping at all, and did so
/// on <c>main</c> before this out-of-tier refusal shipped. The refusal policy
/// sanctions refusing scripts that <em>need</em> dedicated shaping logic this tier does not
/// implement, not every script this tier simply hasn't named.
/// </summary>
public class ScriptSegmenterTests
{
    [Fact]
    public void Han_ClassifiesAsItsOwnSimpleTag_NotUnsupported()
    {
        var runs = ScriptSegmenter.Segment("中"); // 中 — CJK Unified Ideographs

        var run = Assert.Single(runs);
        Assert.Equal(ScriptTag.Han, run.Script);
    }

    [Fact]
    public void Hebrew_ClassifiesAsItsOwnSimpleTag_NotUnsupported()
    {
        var runs = ScriptSegmenter.Segment("א"); // א — HEBREW LETTER ALEF

        var run = Assert.Single(runs);
        Assert.Equal(ScriptTag.Hebrew, run.Script);
    }

    [Fact]
    public void Thai_StillClassifiesAsUnsupported()
    {
        // The contrasting case: Thai genuinely needs contextual shaping (syllable-level glyph
        // reordering/composition) this tier's shipped shapers (Arabic + Devanagari only) do not
        // provide — the refusal is still correct for scripts that actually need it.
        var runs = ScriptSegmenter.Segment("ก"); // ก — THAI CHARACTER KO KAI

        var run = Assert.Single(runs);
        Assert.Equal(ScriptTag.Unsupported, run.Script);
    }

}
