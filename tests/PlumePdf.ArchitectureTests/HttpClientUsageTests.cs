using NetArchTest.Rules;
using Xunit;

namespace PlumePdf.ArchitectureTests;

/// <summary>
/// Enforces "offline by default; the network is an injected seam" as a
/// mechanical fact about the whole assembly, not just an intent about which type gets
/// constructed: nothing outside <c>PlumePdf.IO.Http</c> — the network seam built for the
/// default TSA/revocation clients — may reference <c>System.Net.Http</c>. A grep-based check
/// would catch the same thing textually; this asserts it against the compiled IL instead, so
/// it also catches an indirect reference (a field/property/generic-argument of an
/// <c>HttpClient</c>-family type) that a naive text grep for the literal string
/// <c>HttpClient</c> could miss.
/// </summary>
/// <remarks>
/// A separate file from <see cref="LayeringTests"/> deliberately — that file predates this
/// seam; this one is the network seam's own addition, added as part of its verify step.
/// </remarks>
public class HttpClientUsageTests
{
    [Fact]
    public void OnlyIoHttpNamespaceReferencesSystemNetHttp()
    {
        var result = Types.InAssembly(typeof(PlumePdfException).Assembly)
            .That().DoNotResideInNamespace("PlumePdf.IO.Http")
            .ShouldNot().HaveDependencyOn("System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, FailureMessage(result));
    }

    private static string FailureMessage(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "System.Net.Http (HttpClient and friends) is referenced outside PlumePdf.IO.Http — the offline-by-default promise is violated. Offenders: " +
              string.Join(", ", result.FailingTypes?.Select(t => t.FullName ?? t.Name) ?? []);
}
