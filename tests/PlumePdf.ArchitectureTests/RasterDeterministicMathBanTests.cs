using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace PlumePdf.ArchitectureTests;

// =============================================================================
// SCOPING (Phase 8): PdfOptions.Deterministic promises byte-identical Rasterize pixel output only on the SAME
// platform/architecture/.NET runtime — not cross-platform — precisely because a platform
// libm transcendental (Math.Sin/Cos/Pow/Exp/Log/Sqrt, or their MathF equivalents) is free to
// round differently across CPU architectures and libm implementations. The deterministic
// raster path (curve flattening, shading/function evaluation, blend math, DeviceCMYK
// conversion) is therefore required to use in-house fixed-point or table-driven math instead
// of any platform transcendental. This is the mechanical half of that rule: an architecture
// scan (Mono.Cecil, the ReflectionBanTests precedent) that fails if any type in the
// PlumePdf.Raster namespace (or a sub-namespace) calls a banned System.Math/System.MathF
// transcendental. It says nothing about whether a call is on a genuinely non-deterministic
// side path (there is none planned in Raster — every Raster type is part of the deterministic
// pixel-generation path) — if a reviewed exception is ever needed, it is
// added to BannedMethodAllowlist below with a comment recording why, not by narrowing the
// namespace scope.
// =============================================================================

/// <summary>
/// Mechanical enforcement: no type in namespace <c>PlumePdf.Raster</c>
/// (or a sub-namespace, e.g. <c>PlumePdf.Raster.Agg</c>, <c>PlumePdf.Raster.Shading</c>) may
/// call a platform <see cref="System.Math"/>/<see cref="System.MathF"/> transcendental —
/// <c>Sin</c>/<c>Cos</c>/<c>Tan</c>/<c>Pow</c>/<c>Exp</c>/<c>Log</c>/<c>Log2</c>/<c>Log10</c>/
/// <c>Sqrt</c>/<c>Cbrt</c>/<c>Atan</c>/<c>Atan2</c>/<c>Asin</c>/<c>Acos</c> — since those are
/// exactly the operations whose rounding is not guaranteed identical across CPU
/// architectures/libm implementations, which would silently break the same-platform-only
/// byte-identity promise <see cref="PdfOptions.Deterministic"/> makes for the Rasterize pixel
/// path. Passes vacuously until <c>PlumePdf.Raster</c> exists — this gate is
/// landed ahead of any Raster code so it bites from the first Raster commit.
/// </summary>
public class RasterDeterministicMathBanTests
{
    private const string TargetNamespace = "PlumePdf.Raster";

    /// <summary>
    /// System.Math/System.MathF method names whose result is not guaranteed bit-identical
    /// across platforms/architectures. Deliberately excludes non-transcendental helpers
    /// (Abs, Min, Max, Floor, Ceiling, Round, Sign, Clamp, DivRem) which are exact integer/
    /// bit operations with no cross-platform rounding hazard.
    /// </summary>
    private static readonly string[] BannedMethodNames =
    [
        "Sin", "Cos", "Tan", "Sinh", "Cosh", "Tanh",
        "Asin", "Acos", "Atan", "Atan2", "Asinh", "Acosh", "Atanh",
        "Pow", "Exp", "Exp2", "Exp10",
        "Log", "Log2", "Log10",
        "Sqrt", "Cbrt",
    ];

    /// <summary>
    /// Reviewed, explicitly-recorded exceptions to the ban above. Empty by design: every
    /// Raster type sits on the deterministic pixel-generation path, so no
    /// exception is expected. Add an entry here — with a comment citing why the call site is
    /// provably not on the deterministic path — rather than narrowing TargetNamespace.
    /// </summary>
    private static readonly (string TypeFullName, string MethodName)[] BannedMethodAllowlist = [];

    [Fact]
    public void NoRasterTypeCallsAPlatformMathTranscendental()
    {
        var assemblyPath = typeof(PlumePdfException).Assembly.Location;
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        var offenders = new List<string>();

        foreach (var type in module.Types)
        {
            CheckType(type, type.Namespace, offenders);
        }

        Assert.True(offenders.Count == 0,
            "No type in namespace " + TargetNamespace + " (or a sub-namespace, e.g. " +
            "PlumePdf.Raster.Agg/Shading/Color) may call a platform System.Math/System.MathF " +
            "transcendental (the same-platform-only Deterministic promise " +
            "for Rasterize pixel output depends on the deterministic path using only in-house " +
            "fixed-point/table-driven math, since libm transcendentals are not guaranteed " +
            "bit-identical across CPU architectures/runtimes). Replace the call with a " +
            "FixedMath helper, or add a reviewed entry to BannedMethodAllowlist with a comment " +
            "proving the call site is not on the deterministic path. Offenders:\n" +
            string.Join("\n", offenders));
    }

    private static bool IsInTargetNamespace(string ns) =>
        ns == TargetNamespace || ns.StartsWith(TargetNamespace + ".", StringComparison.Ordinal);

    // Deliberately no compiler-generated-type exemption here (unlike MutableStaticBanTests):
    // a lambda or local function inside a Raster type that calls Math.Sin is still a real
    // determinism offender, so compiler-generated closures/iterators are scanned like any
    // other nested type.
    private static void CheckType(TypeDefinition type, string enclosingNamespace, List<string> offenders)
    {
        var effectiveNamespace = string.IsNullOrEmpty(type.Namespace) ? enclosingNamespace : type.Namespace;

        if (IsInTargetNamespace(effectiveNamespace))
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

                    if (instruction.Operand is not MethodReference callee)
                    {
                        continue;
                    }

                    var declaringType = callee.DeclaringType?.FullName;
                    if (declaringType is not ("System.Math" or "System.MathF"))
                    {
                        continue;
                    }

                    if (!BannedMethodNames.Contains(callee.Name))
                    {
                        continue;
                    }

                    if (BannedMethodAllowlist.Any(a => a.TypeFullName == type.FullName && a.MethodName == callee.Name))
                    {
                        continue;
                    }

                    offenders.Add($"  {type.FullName}.{method.Name} calls {declaringType}.{callee.Name}");
                }
            }
        }

        foreach (var nested in type.NestedTypes)
        {
            CheckType(nested, effectiveNamespace, offenders);
        }
    }
}
