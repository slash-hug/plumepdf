using Xunit;

namespace PlumePdf.Tests;

public class ObjectModelTests
{
    [Fact]
    public void PdfNull_IsASingleton()
    {
        Assert.Same(PdfNull.Instance, PdfNull.Instance);
    }

    [Fact]
    public void PdfBoolean_InternsBothValues()
    {
        Assert.Same(PdfBoolean.True, PdfBoolean.Get(true));
        Assert.Same(PdfBoolean.False, PdfBoolean.Get(false));
        Assert.NotSame(PdfBoolean.True, PdfBoolean.False);
        Assert.True(PdfBoolean.True.Value);
        Assert.False(PdfBoolean.False.Value);
    }

    [Theory]
    [InlineData(-32)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(255)]
    public void PdfNumber_CachesSmallIntegers(long value)
    {
        var a = PdfNumber.Get(value);
        var b = PdfNumber.Get(value);
        Assert.Same(a, b);
    }

    [Fact]
    public void PdfNumber_OutsideCacheRange_IsNotInternedButIsValueEqual()
    {
        var a = PdfNumber.Get(100_000);
        var b = PdfNumber.Get(100_000);

        Assert.NotSame(a, b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void PdfNumber_DistinguishesIntegerFromReal()
    {
        var integer = PdfNumber.Get(4L);
        var real = PdfNumber.Get(4.0);

        Assert.True(integer.IsInteger);
        Assert.False(real.IsInteger);
        Assert.NotEqual(integer, real);
        Assert.Equal(4, integer.ToInt32());
    }

    [Theory]
    [InlineData(12.3456789, "12.3456789")]
    [InlineData(0.7, "0.7")]
    [InlineData(3.25, "3.25")]
    [InlineData(0.1, "0.1")]
    [InlineData(100.0, "100")]
    public void PdfNumber_RealToString_RoundTripsWithoutPrecisionLoss(double value, string expected)
    {
        // The fixed "0.######" format used to silently round to 6 decimal places (12.3456789
        // -> "12.345679") and to mask the tokenizer's own accumulation error (0.7 printed as
        // "0.7" purely because it happened to round to that at 6 places, not because the
        // stored value was actually 0.7). This asserts the exact printed text, not just
        // approximate numeric equality, to catch both classes of regression.
        Assert.Equal(expected, PdfNumber.Get(value).ToString());
    }

    [Fact]
    public void PdfNumber_RealToString_NeverUsesScientificNotation()
    {
        Assert.DoesNotContain("E", PdfNumber.Get(1e20).ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("E", PdfNumber.Get(1e-20).ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PdfName_InternsByText()
    {
        var a = PdfName.Get("Type");
        var b = PdfName.Get("Type");

        Assert.Same(a, b);
        Assert.Same(a, PdfName.Type);
        Assert.Equal("Type", a.Value);
    }

    [Fact]
    public void PdfName_DifferentTextIsNotEqual()
    {
        Assert.NotEqual(PdfName.Get("Type"), PdfName.Get("Subtype"));
    }

    [Fact]
    public void PdfName_StillReferencedByALiveDictionary_StaysTheSameInstance()
    {
        // A name held alive by something (here, a dictionary entry) must keep returning the
        // exact same instance from Get - the weak-reference intern table must not evict or
        // replace a name that's still actually in use, only ones nothing references any more.
        var unique = $"Live-{Guid.NewGuid():N}";
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get(unique), PdfBoolean.True);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var second = PdfName.Get(unique);
        Assert.Same(dict.Keys.Single(), second);
    }

    // No test asserts that an unreferenced PdfName actually gets garbage-collected: under
    // the JIT's Tier0/QuickJit tier (routine for a method that, like most test methods, only
    // ever runs once or twice), local variable liveness is reported conservatively - a local
    // can stay "reachable" for the method's whole body rather than only until its last real
    // use - which makes a direct GC.Collect()-then-assert-collected test intrinsically
    // environment/JIT-tier-dependent rather than a reliable regression guard (verified: the
    // same assertion is consistently reliable with DOTNET_TieredCompilation=0, and
    // consistently unreliable without it). PdfName_StillReferencedByALiveDictionary above
    // covers the correctness half that matters most (a name still in use is never evicted or
    // duplicated); the leak fix itself is a standard weak-reference-table pattern, reviewable
    // by inspection in PdfName.Get/SweepIfDue.

    [Fact]
    public void PdfString_LiteralRoundTripsBytesAsLatin1()
    {
        byte[] bytes = [0x48, 0x65, 0x6C, 0x6C, 0x6F]; // "Hello"
        var text = PdfString.FromLiteral(bytes);

        Assert.False(text.IsHex);
        Assert.Equal("Hello", text.GetText());
        Assert.Equal(bytes, text.Bytes.ToArray());
    }

    [Fact]
    public void PdfString_HighByte_RoundTripsThroughLatin1WithoutCorruption()
    {
        byte[] bytes = [0xE9, 0x00, 0xFF]; // arbitrary non-UTF8-valid bytes
        var value = PdfString.FromHex(bytes);

        Assert.True(value.IsHex);
        var decoded = value.GetText();
        var reEncoded = System.Text.Encoding.Latin1.GetBytes(decoded);
        Assert.Equal(bytes, reEncoded);
    }

    [Fact]
    public void PdfString_Utf16BigEndianBom_DecodesAsUnicodeText()
    {
        // U+FEFF BOM followed by "A" (0x0041) in UTF-16BE.
        byte[] bytes = [0xFE, 0xFF, 0x00, 0x41];
        var value = PdfString.FromLiteral(bytes);

        Assert.Equal("A", value.GetText());
    }

    [Fact]
    public void PdfArray_SupportsMutation()
    {
        var array = new PdfArray { PdfNumber.Get(1), PdfNumber.Get(2) };
        Assert.Equal(2, array.Count);

        array.Add(PdfNumber.Get(3));
        Assert.Equal(3, array.Count);
        Assert.Equal(3, ((PdfNumber)array[2]).ToInt32());

        array[0] = PdfNumber.Get(9);
        Assert.Equal(9, ((PdfNumber)array[0]).ToInt32());

        array.RemoveAt(1);
        Assert.Equal(2, array.Count);
    }

    [Fact]
    public void PdfDictionary_PreservesInsertionOrder()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Z"), PdfNumber.Get(1));
        dict.Set(PdfName.Get("A"), PdfNumber.Get(2));
        dict.Set(PdfName.Get("M"), PdfNumber.Get(3));

        Assert.Equal(["Z", "A", "M"], dict.Keys.Select(k => k.Value));
    }

    [Fact]
    public void PdfDictionary_SetReplacesInPlaceWithoutMovingKey()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("A"), PdfNumber.Get(1));
        dict.Set(PdfName.Get("B"), PdfNumber.Get(2));
        dict.Set(PdfName.Get("A"), PdfNumber.Get(99));

        Assert.Equal(["A", "B"], dict.Keys.Select(k => k.Value));
        Assert.Equal(99, ((PdfNumber)dict[PdfName.Get("A")]).ToInt32());
    }

    [Fact]
    public void PdfDictionary_TryGetValue_ReturnsFalseForMissingKey()
    {
        var dict = new PdfDictionary();
        Assert.False(dict.TryGetValue(PdfName.Get("Missing"), out _));
    }

    [Fact]
    public void PdfDictionary_Remove_ReindexesRemainingEntries()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("A"), PdfNumber.Get(1));
        dict.Set(PdfName.Get("B"), PdfNumber.Get(2));
        dict.Set(PdfName.Get("C"), PdfNumber.Get(3));

        Assert.True(dict.Remove(PdfName.Get("B")));
        Assert.Equal(["A", "C"], dict.Keys.Select(k => k.Value));
        Assert.Equal(3, ((PdfNumber)dict[PdfName.Get("C")]).ToInt32());
        Assert.False(dict.Remove(PdfName.Get("B")));
    }

    [Fact]
    public void PdfStream_HoldsDictionaryAndRawBytes()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Length, PdfNumber.Get(3));
        byte[] raw = [1, 2, 3];

        var stream = new PdfStream(dict, raw);

        Assert.Same(dict, stream.Dictionary);
        Assert.Equal(raw, stream.RawBytes.ToArray());
    }

    [Fact]
    public void IndirectReference_EqualityIsByNumberAndGeneration()
    {
        var a = new IndirectReference(1, 0);
        var b = new IndirectReference(1, 0);
        var c = new IndirectReference(1, 1);
        var d = new IndirectReference(2, 0);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void IndirectReference_RejectsNegativeComponents()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndirectReference(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndirectReference(0, -1));
    }
}
