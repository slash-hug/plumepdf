using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace PlumePdf.ArchitectureTests;

// =============================================================================
// The per-call paint intent
// (RasterPaintContext — image resampling mode, anti-aliasing) is threaded EXPLICITLY
// through every paint entry point in PlumePdf.Raster. The failure this guards against is
// the PdfOptionsCapWiringTests class of bug in reverse: a public option the caller sets
// that some paint path quietly ignores because it defaulted the context, or a scanline
// sweep that hard-codes anti-aliasing. IL-level (Mono.Cecil), so a doc comment can never
// satisfy it.
// =============================================================================

public class RasterPaintContextWiringTests
{
    private const string RasterNamespace = "PlumePdf.Raster";
    private const string ContextTypeName = "PlumePdf.Raster.RasterPaintContext";

    [Fact]
    public void SweepAndImagePaintCarryTheContext_NoFlaglessOverloadSurvives()
    {
        using var module = ModuleDefinition.ReadModule(typeof(PlumePdfException).Assembly.Location);

        var sweeps = module.GetTypes().Single(static t => t.FullName == "PlumePdf.Raster.Agg.ScanlineRasterizer").Methods.Where(static m => m.Name == "Sweep").ToList();
        var sweep = Assert.Single(sweeps);
        Assert.Contains(sweep.Parameters, static p => p.Name == "antiAlias" && p.ParameterType.FullName == "System.Boolean");

        var paints = module.GetTypes().Single(static t => t.FullName == "PlumePdf.Raster.ImagePainter").Methods.Where(static m => m.Name == "Paint").ToList();
        Assert.NotEmpty(paints);
        Assert.All(paints, static m => Assert.Contains(m.Parameters, static p => p.ParameterType.FullName == ContextTypeName));

        var glyphPaints = module.GetTypes().Single(static t => t.FullName == "PlumePdf.Raster.Glyphs.GlyphRasterizer").Methods.Where(static m => m.Name == "Paint").ToList();
        Assert.NotEmpty(glyphPaints);
        Assert.All(glyphPaints, static m => Assert.Contains(m.Parameters, static p => p.Name == "antiAlias"));
    }

    [Fact]
    public void NoRasterTypeAssumesTheDefaultContext()
    {
        using var module = ModuleDefinition.ReadModule(typeof(PlumePdfException).Assembly.Location);
        var offenders = new List<string>();
        foreach (var type in module.GetTypes())
        {
            if (!type.FullName.StartsWith(RasterNamespace + ".", StringComparison.Ordinal) || type.FullName.StartsWith(ContextTypeName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var method in type.Methods.Where(static m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    // Three ways to conjure a context the caller never asked for:
                    // reading Default, constructing one (newobj), or default(T) (initobj).
                    var conjures = instruction.OpCode.Code switch
                    {
                        // A struct's `new T(args)` compiles to `call .ctor` on a local, not `newobj`, so
                        // the constructor is caught under Call as well as Newobj.
                        Code.Call or Code.Callvirt => instruction.Operand is MethodReference callee
                            && callee.DeclaringType.FullName == ContextTypeName
                            && callee.Name is "get_Default" or ".ctor",
                        Code.Newobj => instruction.Operand is MethodReference ctor && ctor.DeclaringType.FullName == ContextTypeName,
                        Code.Initobj => instruction.Operand is TypeReference t && t.FullName == ContextTypeName,
                        _ => false,
                    };
                    if (conjures)
                    {
                        offenders.Add($"{type.FullName}.{method.Name} ({instruction.OpCode.Code})");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A Raster paint path must never conjure its own RasterPaintContext — not Default, not new(...), not default(T) " +
            "(whose AntiAlias is FALSE) — because it silently ignores the caller's ImageResampling/AntiAlias. Thread the context instead:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void EverySweepCallerReceivesItsAntiAliasFlagAsAParameter()
    {
        // Every method in the library that calls ScanlineRasterizer.Sweep must itself take the
        // flag as input — either a RasterPaintContext or a bool named antiAlias — so the value
        // can only come from the caller's options, never from a literal chosen inside the Raster
        // layer (one exemption below, ClipRegionResolver, by design). (A literal `true` at a call site in a method that also has the parameter would
        // slip past this test; reviewers hold that line — the IL shape is otherwise identical.)
        using var module = ModuleDefinition.ReadModule(typeof(PlumePdfException).Assembly.Location);
        var offenders = new List<string>();
        foreach (var type in module.GetTypes())
        {
            if (type.FullName == "PlumePdf.Raster.ClipRegionResolver")
            {
                // The one documented exemption: clip coverage is always
                // anti-aliased — measured PDFium parity (chromium/8009 leaves clip masks and text
                // untouched under FPDF_RENDER_NO_SMOOTHPATH|NO_SMOOTHTEXT) — so the resolver's
                // sweep passes a literal true by design and takes no flag.
                continue;
            }

            foreach (var method in type.Methods.Where(static m => m.HasBody))
            {
                var callsSweep = method.Body.Instructions.Any(static i =>
                    i.OpCode.Code is Code.Call or Code.Callvirt
                    && i.Operand is MethodReference { Name: "Sweep" } callee
                    && callee.DeclaringType.FullName == "PlumePdf.Raster.Agg.ScanlineRasterizer");
                if (!callsSweep)
                {
                    continue;
                }

                var hasFlag = method.Parameters.Any(static p =>
                    p.ParameterType.FullName == ContextTypeName
                    || (p.Name == "antiAlias" && p.ParameterType.FullName == "System.Boolean"));
                if (!hasFlag)
                {
                    offenders.Add($"{type.FullName}.{method.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A method that sweeps scanlines must receive the anti-aliasing decision from its caller (RasterPaintContext " +
            "or a bool antiAlias parameter), not decide it locally:\n  " + string.Join("\n  ", offenders));
    }
}
