using System.Runtime.CompilerServices;

// Grants PlumePdf.Tests access to internal layer implementation (tokenizer, parser,
// cross-reference reader, filter codecs, security handler, ...) so those types can be
// unit-tested directly without widening the public surface (layer namespaces are
// internals-only). Declared here — the root namespace's first file — rather than in a
// dedicated AssemblyInfo.cs — an assembly attribute can live in any compiled source file.
[assembly: InternalsVisibleTo("PlumePdf.Tests")]

// Grants PlumePdf.Benchmarks the same internals access, specifically so micro-benchmarks
// can target an internal hot-path type (e.g. PdfTokenizer, a zero-allocation ref
// struct) directly, without widening the public surface just to make it benchmarkable —
// see TokenizerBenchmarks.cs's remarks, which documented this as the expected next step.
[assembly: InternalsVisibleTo("PlumePdf.Benchmarks")]

// Grants the corpus lane internals access for exactly one purpose: the Phase 6 exit-demo test
// (tests/PlumePdf.CorpusTests/ExitDemoTests.cs) proves redaction unrecoverability with the
// brute-force RecoveryScanner "N G obj" byte scan — reconstructing every object physically
// present in the output regardless of reachability — the same proof the hermetic C5 suite
// (RedactionUnrecoverabilityTests) runs. Everything else in the corpus lane stays on the
// public surface; a conformance test that needs internals for any other reason should be
// questioned, not waved through on this grant.
[assembly: InternalsVisibleTo("PlumePdf.CorpusTests")]

// Grants the NativeAOT publish smoke consumer internals access for exactly one purpose:
// Fonts.Substitute.SubstituteFontStore's
// own remarks name this exact use — "for the AOT-smoke lane to enumerate and touch each one" —
// proving the compiled-in ~4.2 MB Liberation FieldRVA-blob bundle actually loads and parses
// correctly once ahead-of-time compiled, not merely that the trimmer's whole-assembly root
// (PlumePdf.AotSmoke.csproj's TrimmerRootAssembly) kept the bytes present. Everything else in
// the AOT-smoke lane stays on the public surface.
[assembly: InternalsVisibleTo("PlumePdf.AotSmoke")]

namespace PlumePdf;

/// <summary>
/// Base exception for all PlumePDF failures. Every failure carries a stable, greppable
/// <see cref="Code"/> (for example <c>PLUME2001</c>, in the Objects-layer range) and sets
/// <see cref="Exception.HelpLink"/> to that code's reference page, so the
/// next action is following the link, not guessing a search.
/// </summary>
/// <example>
/// <code>
/// try
/// {
///     // any PlumePDF operation
/// }
/// catch (PlumePdfException ex)
/// {
///     Console.WriteLine($"{ex.Code}: {ex.Message} — see {ex.HelpLink}");
/// }
/// </code>
/// </example>
public class PlumePdfException : Exception
{
    /// <summary>Creates an exception with a stable error code and an actionable message.</summary>
    /// <param name="code">The stable error code, e.g. <c>PLUME2001</c>. Codes are allocated per layer and documented individually — never renumbered.</param>
    /// <param name="message">An actionable description: what failed, where, and what was attempted.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public PlumePdfException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        HelpLink = $"https://github.com/slash-hug/plumepdf/blob/main/docs/errors/{code}.md";
    }

    /// <summary>The stable, greppable error code identifying this failure class.</summary>
    public string Code { get; }
}
