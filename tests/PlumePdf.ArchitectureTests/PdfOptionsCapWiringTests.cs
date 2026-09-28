using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace PlumePdf.ArchitectureTests;

// =============================================================================
// SCOPING (Phase 6 review): three PdfOptions resource caps shipped as public API —
// complete with XML-doc examples showing callers tightening them — while no production
// code ever read them: MaxStructureTreeDepth and MaxStructureElementCount (the
// structure-tree reader/builder borrowed MaxObjectNestingDepth/MaxLayoutElementCount
// instead), and MaxRedactionMatches (a duplicate of PdfRedactOptions.MaxMatches with a
// different default, since deleted). A declared-but-dead cap is worse than no cap: a
// caller who tightens it believes they hardened their pipeline and did nothing. This
// class of bug recurring is exactly the repo's lesson-to-gate doctrine's trigger, so
// the prose rule ("wire every cap you declare") becomes IL-level mechanical
// enforcement here: every public PdfOptions property whose name starts with "Max"
// must have its getter called from at least one method body in src/PlumePdf outside
// the PdfOptions type itself. IL (Mono.Cecil, the ReflectionBanTests precedent) rather
// than source grep, so an XML-doc mention can never satisfy the rule.
// =============================================================================

/// <summary>
/// Mechanical enforcement against declared-but-dead resource caps: every public
/// <c>PdfOptions</c> <c>Max*</c> property must actually be consumed (its getter called)
/// somewhere in <c>src/PlumePdf</c> outside <c>PdfOptions</c> itself. A new cap added
/// without wiring fails this test until real enforcement code reads it.
/// </summary>
public class PdfOptionsCapWiringTests
{
    [Fact]
    public void EveryPublicMaxCapOnPdfOptionsIsReadOutsidePdfOptionsItself()
    {
        var assemblyPath = typeof(PlumePdfException).Assembly.Location;
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        var optionsType = module.Types.Single(static t => t.FullName == "PlumePdf.PdfOptions");
        var caps = optionsType.Properties
            .Where(static p => p.Name.StartsWith("Max", StringComparison.Ordinal) && p.GetMethod is { IsPublic: true })
            .Select(static p => p.Name)
            .ToList();

        Assert.NotEmpty(caps); // sanity: the reflection over PdfOptions itself still works.

        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in module.Types)
        {
            CollectCapReads(type, optionsType.FullName, consumed);
        }

        var dead = caps.Where(cap => !consumed.Contains("get_" + cap)).ToList();
        Assert.True(dead.Count == 0,
            "Every public PdfOptions Max* cap must be enforced by real code — a declared cap " +
            "nothing reads silently no-ops for every caller who tightens it. Wire each of these " +
            "into the code path it claims to bound (or remove it):\n  " +
            string.Join("\n  ", dead));
    }

    private static void CollectCapReads(TypeDefinition type, string optionsTypeFullName, HashSet<string> consumed)
    {
        // Calls from PdfOptions' own members (the record's compiler-generated Equals/
        // GetHashCode/PrintMembers/clone read every property) — or from a type nested inside
        // it — must not count as "wiring".
        if (!type.FullName.StartsWith(optionsTypeFullName, StringComparison.Ordinal))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt))
                    {
                        continue;
                    }

                    if (instruction.Operand is MethodReference callee
                        && callee.DeclaringType?.FullName == optionsTypeFullName
                        && callee.Name.StartsWith("get_Max", StringComparison.Ordinal))
                    {
                        consumed.Add(callee.Name);
                    }
                }
            }
        }

        foreach (var nested in type.NestedTypes)
        {
            CollectCapReads(nested, optionsTypeFullName, consumed);
        }
    }
}
