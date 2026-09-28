using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace PlumePdf.Analyzers.Tests;

/// <summary>
/// Unit tests for <see cref="ManuscriptNeverRenderedAnalyzer"/> (PLMP0001). Compiles small
/// snippets against minimal stand-in <c>PlumePdf.Manuscript</c>/<c>PlumePdf.PdfDocument</c>
/// types (matching them by fully-qualified name only, exactly as the analyzer does against the
/// real assembly) so these tests don't depend on the Phase 2 creation API existing yet.
/// </summary>
public class ManuscriptNeverRenderedAnalyzerTests
{
    private const string StubTypes = """
        namespace PlumePdf
        {
            public sealed class Manuscript
            {
                public void Render() { }
            }

            public sealed class PdfDocument
            {
                public static PdfDocument Compose(System.Action<object> build) => new PdfDocument();

                public void Save(string path) { }

                public void SaveIncremental(string path) { }
            }
        }
        """;

    private static readonly ImmutableArray<MetadataReference> BclReferences = GetBclReferences();

    private static ImmutableArray<MetadataReference> GetBclReferences()
    {
        var trustedPlatformAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(StubTypes + Environment.NewLine + source);
        var compilation = CSharpCompilation.Create(
            assemblyName: "PlumePdf.Analyzers.Tests.Snippet",
            syntaxTrees: new[] { tree },
            references: BclReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compilationErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        Assert.True(compilationErrors.IsEmpty, "Snippet failed to compile: " + string.Join(Environment.NewLine, compilationErrors));

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new ManuscriptNeverRenderedAnalyzer()));

        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics;
    }

    [Fact]
    public async Task Flags_ManuscriptConstructedButNeverRendered()
    {
        const string source = """
            class C
            {
                void M()
                {
                    var m = new PlumePdf.Manuscript();
                }
            }
            """;

        var diagnostics = await GetDiagnosticsAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ManuscriptNeverRenderedAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains("m", diagnostic.GetMessage());
        Assert.Contains("Render", diagnostic.GetMessage());
    }

    [Fact]
    public async Task DoesNotFlag_ManuscriptRendered()
    {
        const string source = """
            class C
            {
                void M()
                {
                    var m = new PlumePdf.Manuscript();
                    m.Render();
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Flags_ComposeResultNeverSaved()
    {
        const string source = """
            class C
            {
                void M()
                {
                    var doc = PlumePdf.PdfDocument.Compose(_ => { });
                }
            }
            """;

        var diagnostics = await GetDiagnosticsAsync(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ManuscriptNeverRenderedAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains("Save", diagnostic.GetMessage());
    }

    [Fact]
    public async Task DoesNotFlag_ComposeResultSaved()
    {
        const string source = """
            class C
            {
                void M()
                {
                    var doc = PlumePdf.PdfDocument.Compose(_ => { });
                    doc.Save("out.pdf");
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task DoesNotFlag_ComposeResultSavedIncrementally()
    {
        const string source = """
            class C
            {
                void M()
                {
                    var doc = PlumePdf.PdfDocument.Compose(_ => { });
                    doc.SaveIncremental("out.pdf");
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task DoesNotFlag_ComposeResultReturned()
    {
        const string source = """
            class C
            {
                PlumePdf.PdfDocument M()
                {
                    var doc = PlumePdf.PdfDocument.Compose(_ => { });
                    return doc;
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task DoesNotFlag_ManuscriptPassedToAnotherMethod()
    {
        const string source = """
            class C
            {
                void M()
                {
                    var m = new PlumePdf.Manuscript();
                    Helper(m);
                }

                void Helper(PlumePdf.Manuscript m) => m.Render();
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task DoesNotFlag_ManuscriptPassedToAnotherConstructor()
    {
        const string source = """
            class Wrapper
            {
                public Wrapper(PlumePdf.Manuscript m) { }
            }

            class C
            {
                Wrapper M()
                {
                    var m = new PlumePdf.Manuscript();
                    return new Wrapper(m);
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task DoesNotFlag_ManuscriptAssignedToField()
    {
        const string source = """
            class C
            {
                PlumePdf.Manuscript _kept;

                void M()
                {
                    var k = new PlumePdf.Manuscript();
                    _kept = k;
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task DoesNotFlag_UnrelatedLocalOfADifferentType()
    {
        const string source = """
            class C
            {
                void M()
                {
                    var text = "not a manuscript";
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }
}
