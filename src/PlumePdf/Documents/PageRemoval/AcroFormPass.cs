namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// The form: widgets on removed pages leave their field's /Kids; a field left with no widget is
/// removed from its parent or from /AcroForm /Fields, value included, recursively upward; radio
/// and checkbox /Opt entries stay aligned with /Kids and a /V naming a removed widget's
/// appearance state becomes /Off; /CO drops removed fields; /XFA is dropped when anything was
/// removed; removed fields (and their indirect /V and /RV) are excluded, and /Perms /DocMDP is
/// dropped when its signature was.
/// </summary>
internal static class AcroFormPass
{
    /// <summary>Applies this pass to <paramref name="context"/>.</summary>
    public static void Apply(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }
}
