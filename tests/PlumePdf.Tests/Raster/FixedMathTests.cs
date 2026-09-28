using PlumePdf.Raster;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary><see cref="FixedMath"/>'s libm-free helpers.</summary>
public class FixedMathTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    [InlineData(0.25)]
    [InlineData(1_000_000.0)]
    [InlineData(1e-6)]
    public void Sqrt_MatchesSystemMathWithinTolerance(double value)
    {
        var expected = System.Math.Sqrt(value);
        var actual = FixedMath.Sqrt(value);
        Assert.True(System.Math.Abs(expected - actual) <= System.Math.Max(1e-9, expected * 1e-9), $"Sqrt({value}) expected {expected}, got {actual}");
    }

    [Fact]
    public void Sqrt_NegativeOrNaN_ReturnsZero()
    {
        Assert.Equal(0, FixedMath.Sqrt(-4));
        Assert.Equal(0, FixedMath.Sqrt(double.NaN));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(4L, 2L)]
    [InlineData(15L, 3L)]
    [InlineData(16L, 4L)]
    [InlineData(1_000_000L, 1000L)]
    public void IntegerSqrt_IsFloorOfExactSqrt(long value, long expected)
    {
        Assert.Equal(expected, FixedMath.IntegerSqrt(value));
    }

    [Fact]
    public void CosSinTables_MatchUnitCircleIdentity()
    {
        for (var i = 0; i < FixedMath.AngleTableSize; i++)
        {
            var c = (double)FixedMath.CosTable[i] / FixedMath.AngleTableScale;
            var s = (double)FixedMath.SinTable[i] / FixedMath.AngleTableScale;
            Assert.InRange((c * c) + (s * s), 0.999, 1.001);
        }
    }

    [Fact]
    public void UnitVector_ZeroIndex_IsPositiveXAxis()
    {
        var (cos, sin) = FixedMath.UnitVector(0);
        Assert.Equal(FixedMath.AngleTableScale, cos);
        Assert.Equal(0, sin);
    }

    [Fact]
    public void UnitVector_WrapsNegativeAndOverflowIndices()
    {
        var direct = FixedMath.UnitVector(5);
        Assert.Equal(direct, FixedMath.UnitVector(5 + FixedMath.AngleTableSize));
        Assert.Equal(direct, FixedMath.UnitVector(5 - FixedMath.AngleTableSize));
    }

    [Fact]
    public void AngleIndexOf_PicksClosestTableEntry()
    {
        // Angle 0 (pointing along +X) should resolve to table index 0.
        Assert.Equal(0, FixedMath.AngleIndexOf(1, 0));

        // Angle 90 degrees (+Y) should resolve to table index 64 (256/4).
        Assert.Equal(64, FixedMath.AngleIndexOf(0, 1));
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(128, 128)]
    [InlineData(255, 255)]
    [InlineData(300, 255)]
    public void ClampByte_Clamps(int input, int expected)
    {
        Assert.Equal((byte)expected, FixedMath.ClampByte(input));
    }

    [Fact]
    public void ToSubpixel_FromSubpixel_RoundTrips()
    {
        var sub = FixedMath.ToSubpixel(12.5);
        Assert.Equal(12.5, FixedMath.FromSubpixel(sub), 3);
    }
}
