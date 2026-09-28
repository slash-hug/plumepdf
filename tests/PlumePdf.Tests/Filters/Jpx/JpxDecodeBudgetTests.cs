using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>The per-decode working-memory ledger's own arithmetic: both directions guard a negative amount, and neither can push <see cref="JpxDecodeBudget.Used"/> outside <c>[0, Limit]</c>.</summary>
public class JpxDecodeBudgetTests
{
    [Fact]
    public void Charge_WithinTheLimit_Accumulates_AndReleaseSubtracts()
    {
        var budget = new JpxDecodeBudget(maxImagePixels: 1000);
        Assert.Equal(1000L * JpxDecodeBudget.BytesPerReferencePixel, budget.Limit);
        Assert.Equal(1000L * JpxDecodeBudget.OutputPlanesPerReferencePixel, budget.OutputSampleLimit);

        budget.Charge(100, "a");
        budget.Charge(200, "b");
        Assert.Equal(300, budget.Used);

        budget.Release(120);
        Assert.Equal(180, budget.Used);
    }

    [Fact]
    public void Charge_PastTheLimit_IsPlume3718_AndLeavesUsedUnchanged()
    {
        var budget = new JpxDecodeBudget(maxImagePixels: 10);
        budget.Charge(budget.Limit - 1, "almost all of it");

        var ex = Assert.Throws<PlumePdfException>(() => budget.Charge(2, "one more"));

        Assert.Equal(JpxDiagnosticCodes.ImageTooLarge, ex.Code);
        Assert.Equal(budget.Limit - 1, budget.Used);
    }

    [Fact]
    public void Charge_Negative_IsPlume3718_NotACredit()
    {
        var budget = new JpxDecodeBudget(maxImagePixels: 10);
        budget.Charge(5, "a");

        var ex = Assert.Throws<PlumePdfException>(() => budget.Charge(-3, "a negative amount"));

        Assert.Equal(JpxDiagnosticCodes.ImageTooLarge, ex.Code);
        Assert.Equal(5, budget.Used);
    }

    [Fact]
    public void Release_Negative_IsRejected_AndNeverRaisesUsed()
    {
        // Mirrors Charge's guard: a negative release is a
        // bookkeeping error, and applying it would grow the ledger — the one thing a release
        // must never do.
        var budget = new JpxDecodeBudget(maxImagePixels: 10);
        budget.Charge(5, "a");

        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Release(-1));
        Assert.Equal(5, budget.Used);
    }

    [Fact]
    public void Release_MoreThanCharged_FloorsAtZero()
    {
        var budget = new JpxDecodeBudget(maxImagePixels: 10);
        budget.Charge(5, "a");

        budget.Release(50);

        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void RequireOutputWithinLimit_RefusesOverflowingAndExcessivePlaneCounts()
    {
        var budget = new JpxDecodeBudget(maxImagePixels: 100);

        budget.RequireOutputWithinLimit(JpxDecodeBudget.OutputPlanesPerReferencePixel, 100, "exactly at the limit");
        Assert.Equal(JpxDiagnosticCodes.ImageTooLarge, Assert.Throws<PlumePdfException>(() => budget.RequireOutputWithinLimit(JpxDecodeBudget.OutputPlanesPerReferencePixel + 1, 100, "one plane too many")).Code);
        Assert.Equal(JpxDiagnosticCodes.ImageTooLarge, Assert.Throws<PlumePdfException>(() => budget.RequireOutputWithinLimit(long.MaxValue / 2, 4, "an overflowing product")).Code);
    }
}
