using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace PlumePdf.Analyzers;

/// <summary>
/// PLMP0001: flags a local built from <c>new Manuscript()</c> (or a collection/object
/// initializer over it) that never has <c>.Render(...)</c> called on it, and a local built
/// from <c>PdfDocument.Compose(...)</c> that never has <c>.Save(...)</c>/<c>.SaveIncremental(...)</c>
/// called on it — the write-side analogue of "opened a resource, never used it": the caller
/// paid the cost of building a document and produced no output.
/// </summary>
/// <remarks>
/// Deliberately conservative to keep false positives at zero: a tracked local that is
/// returned from the method, passed as an argument to another method or constructor, or
/// assigned to anything other than itself (a field, a property, an array/indexer element, ...)
/// is treated as having escaped analysis (the callee, or whatever holds the assigned-to
/// location, might render/save it) and is never flagged. Matching is by fully-qualified name
/// only (<c>PlumePdf.Manuscript</c>, <c>PlumePdf.PdfDocument.Compose</c>), so the analyzer
/// works whether it is analyzing code that references the real PlumePdf assembly or a test
/// double of the same shape.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ManuscriptNeverRenderedAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic ID this analyzer reports.</summary>
    public const string DiagnosticId = "PLMP0001";

    private const string Category = "Usage";
    private const string ManuscriptTypeName = "PlumePdf.Manuscript";
    private const string PdfDocumentTypeName = "PlumePdf.PdfDocument";
    private const string ComposeMethodName = "Compose";
    private const string RenderMethodName = "Render";

    private static readonly ImmutableHashSet<string> SaveMethodNames =
        ImmutableHashSet.Create("Save", "SaveIncremental");

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Composed document is never rendered or saved",
        messageFormat: "'{0}' is {1} but '{2}(...)' is never called on it",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "A PlumePdf.Manuscript built with 'new Manuscript { ... }' produces no output until " +
            "Render() is called on it, and a PlumePdf.PdfDocument returned by PdfDocument.Compose(...) " +
            "produces no file until Save()/SaveIncremental() is called on it. A local that is built " +
            "but never rendered or saved almost always means the document was never actually written.",
        helpLinkUri: "https://github.com/slash-hug/plumepdf/blob/main/docs/analyzers/PLMP0001.md");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationBlockStartAction(AnalyzeBlockStart);
    }

    private enum TrackedKind
    {
        Manuscript,
        ComposeResult,
    }

    private readonly struct TrackedLocal
    {
        public TrackedLocal(SyntaxNode declarationSite, TrackedKind kind)
        {
            DeclarationSite = declarationSite;
            Kind = kind;
        }

        public SyntaxNode DeclarationSite { get; }

        public TrackedKind Kind { get; }
    }

    private static void AnalyzeBlockStart(OperationBlockStartAnalysisContext context)
    {
        var candidates = new Dictionary<ILocalSymbol, TrackedLocal>(SymbolEqualityComparer.Default);
        var touched = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        var escaped = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);

        context.RegisterOperationAction(
            operationContext =>
            {
                var declarator = (IVariableDeclaratorOperation)operationContext.Operation;
                if (declarator.Symbol is not { } local)
                {
                    return;
                }

                var initializerValue = declarator.Initializer?.Value;
                if (initializerValue is null)
                {
                    return;
                }

                var kind = ClassifyInitializer(initializerValue);
                if (kind is { } trackedKind)
                {
                    candidates[local] = new TrackedLocal(declarator.Syntax, trackedKind);
                }
            },
            OperationKind.VariableDeclarator);

        context.RegisterOperationAction(
            operationContext =>
            {
                var invocation = (IInvocationOperation)operationContext.Operation;

                if (Unwrap(invocation.Instance) is ILocalReferenceOperation instanceLocal &&
                    candidates.TryGetValue(instanceLocal.Local, out var tracked))
                {
                    var methodName = invocation.TargetMethod.Name;
                    var terminatesTracking = tracked.Kind switch
                    {
                        TrackedKind.Manuscript => methodName == RenderMethodName,
                        TrackedKind.ComposeResult => SaveMethodNames.Contains(methodName),
                        _ => false,
                    };

                    if (terminatesTracking)
                    {
                        touched.Add(instanceLocal.Local);
                    }
                }

                // A tracked local handed to another method/constructor as an argument escapes
                // analysis — the callee might be the one that renders/saves it.
                foreach (var argument in invocation.Arguments)
                {
                    if (Unwrap(argument.Value) is ILocalReferenceOperation argumentLocal &&
                        candidates.ContainsKey(argumentLocal.Local))
                    {
                        escaped.Add(argumentLocal.Local);
                    }
                }
            },
            OperationKind.Invocation);

        context.RegisterOperationAction(
            operationContext =>
            {
                var returnOperation = (IReturnOperation)operationContext.Operation;
                if (Unwrap(returnOperation.ReturnedValue) is ILocalReferenceOperation returnedLocal &&
                    candidates.ContainsKey(returnedLocal.Local))
                {
                    escaped.Add(returnedLocal.Local);
                }
            },
            OperationKind.Return);

        // A tracked local passed as a constructor argument (`new Wrapper(m)`) escapes analysis
        // the same way a method argument does — the constructor might be the one that stores
        // it somewhere it later gets rendered/saved from.
        context.RegisterOperationAction(
            operationContext =>
            {
                var creation = (IObjectCreationOperation)operationContext.Operation;
                foreach (var argument in creation.Arguments)
                {
                    if (Unwrap(argument.Value) is ILocalReferenceOperation argumentLocal &&
                        candidates.ContainsKey(argumentLocal.Local))
                    {
                        escaped.Add(argumentLocal.Local);
                    }
                }
            },
            OperationKind.ObjectCreation);

        // A tracked local assigned to anything other than itself (a field, a property, an
        // array/indexer element, ...) escapes analysis the same way — the assignment target
        // might be the thing that later gets rendered/saved.
        context.RegisterOperationAction(
            operationContext =>
            {
                var assignment = (ISimpleAssignmentOperation)operationContext.Operation;
                if (Unwrap(assignment.Value) is ILocalReferenceOperation assignedLocal &&
                    candidates.ContainsKey(assignedLocal.Local))
                {
                    escaped.Add(assignedLocal.Local);
                }
            },
            OperationKind.SimpleAssignment);

        context.RegisterOperationBlockEndAction(
            endContext =>
            {
                foreach (var pair in candidates)
                {
                    var local = pair.Key;
                    var tracked = pair.Value;

                    if (touched.Contains(local) || escaped.Contains(local))
                    {
                        continue;
                    }

                    var (describedAs, expectedMethod) = tracked.Kind == TrackedKind.Manuscript
                        ? ("constructed", RenderMethodName)
                        : ("composed", "Save");

                    endContext.ReportDiagnostic(
                        Diagnostic.Create(Rule, tracked.DeclarationSite.GetLocation(), local.Name, describedAs, expectedMethod));
                }
            });
    }

    private static TrackedKind? ClassifyInitializer(IOperation initializer)
    {
        var unwrapped = Unwrap(initializer);

        if (unwrapped is IObjectCreationOperation { Type: { } createdType } &&
            createdType.ToDisplayString() == ManuscriptTypeName)
        {
            return TrackedKind.Manuscript;
        }

        if (unwrapped is IInvocationOperation invocation &&
            invocation.TargetMethod.IsStatic &&
            invocation.TargetMethod.Name == ComposeMethodName &&
            invocation.TargetMethod.ContainingType?.ToDisplayString() == PdfDocumentTypeName)
        {
            return TrackedKind.ComposeResult;
        }

        return null;
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }
}
