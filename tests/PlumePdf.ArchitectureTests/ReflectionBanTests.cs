using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace PlumePdf.ArchitectureTests;

// =============================================================================
// SCOPING:
//
// Measured, 2026-08-19, against src/PlumePdf as it stood when the Phase 4 plan was
// approved. A first pass grepping source for "System.Reflection"/".GetType()" found
// three call sites (LayoutEngine.cs, ObjectSerializer.cs, EncodingResolver.cs), each
// `.GetType().Name` used only to name a runtime type in an exception/diagnostic
// message. But NetArchTest's namespace-level `HaveDependencyOnAny("System.Reflection")`
// — the obvious first design for this rule — turned out too coarse to actually run:
// it also flags every public type with a C# indexer (ObjectRegistry, PdfArray,
// PdfDictionary, PageCollection, ...), because the compiler stamps every indexer type
// with `[System.Reflection.DefaultMemberAttribute]` whether or not the source ever
// touches reflection. A blanket namespace ban would fail on our own indexer-heavy
// public API today, for zero actual AOT risk (DefaultMemberAttribute is metadata the
// compiler always emits; nothing reads it at runtime under AOT). Confirmed with a
// Mono.Cecil probe (the same library NetArchTest itself uses) walking every member
// reference in each flagged type's methods, fields, and attributes:
//   - ObjectRegistry/PdfArray/PdfDictionary/PageCollection: their ONLY reference into
//     System.Reflection is the compiler-emitted DefaultMemberAttribute on the type
//     itself — no method body anywhere touches System.Reflection.
//   - LayoutEngine/ObjectSerializer/EncodingResolver: exactly one IL instruction each,
//     `callvirt System.Reflection.MemberInfo::get_Name()` — the `.Name` property is
//     declared on `MemberInfo`, which `System.Type` derives from, so `.GetType().Name`
//     compiles to a virtual call into System.Reflection even though nothing about the
//     call enumerates members or invokes anything. This is basic type identity, always
//     available under NativeAOT/trimming with no annotation needed (it is NOT the
//     `GetType().GetProperties()` shape the architecture research flagged as the actual
//     danger — full member enumeration needs trimmer-preserved metadata and silently
//     breaks or returns nothing under aggressive trimming).
//
// Conclusion: the rule below does NOT use NetArchTest's namespace-level dependency
// check. It walks IL directly (Mono.Cecil, already a transitive dependency of
// NetArchTest.Rules — no new package needed) and flags `call`/`callvirt`/`newobj`/
// `ldftn`/`ldvirtftn` instructions targeting the member-enumeration/dynamic-invocation
// surface — while leaving type-level attributes (like the compiler's own
// DefaultMemberAttribute) alone entirely.
//
// A SECOND, sharper gap surfaced while writing this rule: the canary this test is
// meant to catch, `values.GetType().GetProperties()`, does NOT resolve to a
// `System.Reflection.*`-declared method at all — `Type.GetProperties()` is declared
// directly on `System.Type` itself (namespace `System`), even though its return type
// (`System.Reflection.PropertyInfo[]`) is. A namespace-prefix check on the *called
// method's declaring type* — the design that caught the DefaultMemberAttribute false
// positive above — silently MISSES this canary entirely (verified locally: the first
// draft of this rule passed even with the canary present). This is precisely the kind
// of gate-evading gap this rule exists to close, so the rule below treats `System.Type`
// specially: it bans a curated, named set of member-enumeration/dynamic-invocation
// methods declared on `System.Type` (GetProperties/GetMethods/GetFields/GetMembers/
// their singular Get*-by-name overloads, GetConstructors/GetEvents/GetNestedTypes/
// GetInterfaces, InvokeMember, and the Make*Type generic/array/pointer/byref
// constructors) while leaving the rest of `System.Type`'s surface (Name, FullName,
// IsAssignableFrom, and similar always-available type-identity operations) alone —
// plus the namespace-prefix ban for `System.Reflection.*` and `System.Activator`
// members (Type/TypeInfo delegate their invocation surface to MethodInfo/
// PropertyInfo/etc., which do live in `System.Reflection` and so ARE caught by the
// prefix check). The one measured, legitimate exception (`MemberInfo.get_Name`, i.e.
// `Type.Name`, for diagnostic messages) is allowlisted explicitly below; nothing else
// is, and `System.Type`'s banned-member list is exhaustive by name, not by prefix, so
// it can't silently regrow a loophole the way the namespace-only design just did.
//
// A THIRD gap in the same shape: the static `Type.GetType(string[, ...])` overloads —
// dynamic type loading by name, the other classic AOT-hostile shape (a string-named
// type may not exist at all in a trimmed/AOT binary) — are likewise declared directly
// on `System.Type`, not `System.Reflection`, so they need the same by-name treatment
// as `GetProperties` etc. Added to `BannedSystemTypeMembers` below as `"GetType"`;
// this bans only the static overloads (declared on `System.Type` itself) and leaves
// the ubiquitous instance `object.GetType()` (declared on `System.Object`, resolves
// to a different declaring type in IL) untouched.
// =============================================================================

/// <summary>
/// Mechanical enforcement of the AOT-safety commitment (docs/architecture.md,
/// docs/agent-forward.md "From Phase 2 on"): <c>src/PlumePdf</c> must never call into the
/// member-enumeration/dynamic-invocation surface of <c>System.Reflection</c> (or
/// <c>System.Activator</c>). This exists because neither the trim/AOT analyzer nor
/// <c>tests/PlumePdf.AotSmoke</c> catches the specific shape the rule is meant to forbid —
/// the architecture research for Phase 4 (AcroForms) demonstrated that
/// <c>values.GetType().GetProperties()</c> compiles clean under the existing gates and
/// would pass the aot-smoke lane silently. Converting that finding into a gate (this
/// test) is the repo's own self-improvement doctrine (a recurring lesson becomes
/// mechanical enforcement, not a prose rule that gets skipped under pressure).
/// </summary>
public class ReflectionBanTests
{
    /// <summary>
    /// (DeclaringType.FullName, MemberName) pairs that are reflection-namespace IL calls
    /// but are metadata-only type-identity lookups, not member enumeration or invocation —
    /// safe under NativeAOT/trimming with no annotation. The only entry today is the
    /// measured, audited <c>Type.Name</c> idiom (declared on <c>MemberInfo</c>) used solely
    /// for diagnostic/exception messages (see the SCOPING note above). Extend this — never
    /// widen the ban's target namespaces/types — for the next genuinely safe exception, and
    /// record why here.
    /// </summary>
    private static readonly HashSet<(string DeclaringType, string MemberName)> AllowedReflectionCalls =
    [
        ("System.Reflection.MemberInfo", "get_Name"),
    ];

    /// <summary>
    /// <c>System.Type</c> member-enumeration/dynamic-invocation methods, plus the static
    /// dynamic-type-loading-by-name overloads (<c>GetType</c>), banned by exact name rather
    /// than by declaring-type namespace (see the SCOPING note's second and third gaps: these
    /// are declared on <c>System.Type</c>, namespace <c>System</c>, not
    /// <c>System.Reflection</c>). Everything else on <c>System.Type</c> — <c>Name</c>,
    /// <c>FullName</c>, <c>IsAssignableFrom</c>, and similar always-available
    /// type-identity operations — is left alone.
    /// </summary>
    private static readonly HashSet<string> BannedSystemTypeMembers =
    [
        "GetProperties", "GetProperty",
        "GetMethods", "GetMethod",
        "GetFields", "GetField",
        "GetMembers", "GetMember", "FindMembers",
        "GetConstructors", "GetConstructor",
        "GetEvents", "GetEvent",
        "GetNestedTypes", "GetNestedType",
        "GetInterfaces", "GetInterfaceMap", "FindInterfaces",
        "GetDefaultMembers",
        "InvokeMember",
        "MakeGenericType", "MakeArrayType", "MakeByRefType", "MakePointerType",
        // Dynamic type loading by name (the static Type.GetType(string[, ...]) overloads) is
        // the other classic AOT-hostile shape — a string-named type may not exist in a
        // trimmed/AOT binary at all. Declared as static members ON System.Type itself (same
        // "System" namespace gap as GetProperties above, not "System.Reflection"), so only the
        // namespace-prefix check would miss it too. Note this bans the *static* Type.GetType
        // overloads specifically: the ubiquitous instance object.GetType() (declared on
        // System.Object, always available under AOT, needed for the allowlisted Type.Name
        // idiom above) resolves to a different declaring type and is unaffected.
        "GetType",
    ];

    [Fact]
    public void SourceAssemblyNeverCallsIntoReflectionMemberSurface()
    {
        var assemblyPath = typeof(PlumePdfException).Assembly.Location;
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        var offenders = new List<string>();

        foreach (var type in module.Types)
        {
            CheckType(type, offenders);
        }

        Assert.True(offenders.Count == 0,
            "src/PlumePdf must not call into System.Reflection's member-enumeration/invocation " +
            "surface (AOT-safety commitment, docs/architecture.md/docs/agent-forward.md " +
            "'From Phase 2 on'). Offenders:\n" +
            string.Join("\n", offenders));
    }

    private static void CheckType(TypeDefinition type, List<string> offenders)
    {
        foreach (var method in type.Methods)
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (!IsCallLikeOpCode(instruction.OpCode))
                {
                    continue;
                }

                if (instruction.Operand is not MemberReference member)
                {
                    continue;
                }

                var declaringTypeName = member.DeclaringType?.FullName;
                if (declaringTypeName is null)
                {
                    continue;
                }

                var isBannedSystemTypeMember = declaringTypeName == "System.Type" &&
                    BannedSystemTypeMembers.Contains(member.Name);
                var isBannedNamespaceMember = IsBannedNamespace(declaringTypeName) &&
                    !AllowedReflectionCalls.Contains((declaringTypeName, member.Name));

                if (!isBannedSystemTypeMember && !isBannedNamespaceMember)
                {
                    continue;
                }

                offenders.Add($"  {type.FullName}.{method.Name}: {instruction.OpCode} {member.FullName}");
            }
        }

        foreach (var nested in type.NestedTypes)
        {
            CheckType(nested, offenders);
        }
    }

    private static bool IsCallLikeOpCode(OpCode opCode) =>
        opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj ||
        opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn;

    private static bool IsBannedNamespace(string declaringTypeFullName) =>
        declaringTypeFullName.StartsWith("System.Reflection", StringComparison.Ordinal) ||
        declaringTypeFullName.StartsWith("System.Activator", StringComparison.Ordinal);
}
