using PlumePdf.Objects;

namespace PlumePdf.Raster;

/// <summary>
/// The Print/Hidden/NoView visibility decision (ISO 32000-1 §12.5.3 Table 165's <c>/F</c> flags)
/// shared by <c>Raster.Annotations.AnnotationReader</c> (every annotation) and
/// <c>Raster.Annotations.WidgetAppearanceSynthesizer</c>'s caller — a single source of truth per
/// Phase 9 decision, so
/// the annotation walk and the print-intent matrix cannot drift apart the way the rasterizer's
/// earlier split into two phases once needed late reconciliation for.
/// </summary>
/// <remarks>
/// Binding rule: <see cref="AnnotationFlags.Hidden"/> and <see cref="AnnotationFlags.NoView"/>
/// being honored is correct, expected author intent — <b>never</b> a diagnostic. Only the
/// separate "non-widget annotation has no usable <c>/AP</c>" and "optional-content layer
/// suppressed content" cases (handled by the callers of this type, not here) get an Info
/// diagnostic.
/// </remarks>
internal static class AnnotationFlagMatrix
{
    /// <summary>The <c>/F</c> annotation-flags bitfield (ISO 32000-1 Table 165), only the bits this type's visibility decision reads.</summary>
    [Flags]
    internal enum AnnotationFlags
    {
        /// <summary>No flags set (the default when <c>/F</c> is absent).</summary>
        None = 0,

        /// <summary>Bit 1: do not display a "no handler" icon for an unknown subtype. Not used by this decision.</summary>
        Invisible = 1 << 0,

        /// <summary>Bit 2: never display or print, regardless of any other flag.</summary>
        Hidden = 1 << 1,

        /// <summary>Bit 3: print the annotation when the page is printed.</summary>
        Print = 1 << 2,

        /// <summary>Bit 4: do not scale the annotation's appearance with zoom. Not used by this decision.</summary>
        NoZoom = 1 << 3,

        /// <summary>Bit 5: do not rotate the annotation's appearance with page rotation. Not used by this decision.</summary>
        NoRotate = 1 << 4,

        /// <summary>Bit 6: do not display on screen (a printer-only annotation), but may still print.</summary>
        NoView = 1 << 5,
    }

    private static readonly PdfName FName = PdfName.Get("F");

    /// <summary>Reads <paramref name="annotation"/>'s <c>/F</c> entry, or <see cref="AnnotationFlags.None"/> when absent or not a number (lenient — an annotation with a malformed <c>/F</c> renders as if no flags were set).</summary>
    public static AnnotationFlags ReadFlags(PdfDictionary annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        if (annotation.TryGetValue(FName, out var value) && value is PdfNumber number && number.TryToInt32(out var raw))
        {
            return (AnnotationFlags)raw;
        }

        return AnnotationFlags.None;
    }

    /// <summary>
    /// Whether an annotation carrying <paramref name="flags"/> should be rendered for the given
    /// <paramref name="printIntent"/> (ISO 32000-1 §12.5.3): <see cref="AnnotationFlags.Hidden"/>
    /// always wins; otherwise print intent requires <see cref="AnnotationFlags.Print"/>, and view
    /// intent (the default) is suppressed only by <see cref="AnnotationFlags.NoView"/>.
    /// </summary>
    public static bool ShouldRender(AnnotationFlags flags, bool printIntent)
    {
        if ((flags & AnnotationFlags.Hidden) != 0)
        {
            return false;
        }

        return printIntent ? (flags & AnnotationFlags.Print) != 0 : (flags & AnnotationFlags.NoView) == 0;
    }
}
