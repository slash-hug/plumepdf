namespace PlumePdf.Raster.DisplayList;

/// <summary>
/// A transparency group's own ExtGState-applied soft mask (ISO 32000-1 §11.6.4.3/§11.6.5.2):
/// the parsed mask descriptor (<see cref="Transparency.SoftMask"/>) plus its own <c>/G</c>
/// form's already-built display-list content. Built at pass-1 time
/// (<see cref="RasterInterpreter.BuildDisplayList"/>'s <c>Do</c> handling, when the group Form
/// XObject the mask applies to is encountered) rather than at paint time, because rendering the
/// mask group needs the same image-decode/font/optional-content machinery
/// <see cref="RasterInterpreter.BuildDisplayList"/> already has in scope there — pass 2's own
/// group compositing (<see cref="FormPageObject.SoftMask"/>'s consumer) only has to walk this
/// already-normalized tree and reduce it to a coverage map.
/// </summary>
internal sealed class GroupSoftMask
{
    /// <summary>The parsed <c>/SMask</c> descriptor — subtype, <c>/BC</c> backdrop, and <c>/TR</c> transfer function.</summary>
    public required Transparency.SoftMask Mask { get; init; }

    /// <summary>The mask group's own <c>/G</c> form content, already built into a display-list subtree positioned in device space.</summary>
    public required FormPageObject Content { get; init; }
}

/// <summary>
/// A grouped subtree of <see cref="PageObject"/>s — a <c>Do</c>-invoked Form XObject (§8.10) that
/// is not itself an image, or a transparency group (§11.4.7) an <c>ExtGState</c>'s <c>/SMask</c>
/// or a group XObject's own <c>/Group /S /Transparency</c> entry introduces. Recursing the
/// display-list builder into the form's own content stream (rather than inlining its operators
/// flatly) keeps each <see cref="PageObject.Ctm"/> scoped to exactly the subtree it applies to,
/// which transparency-group compositing (<c>Transparency/TransparencyGroup.cs</c>) needs:
/// an isolated/knockout group paints into its own intermediate surface before being composited
/// into the parent, and that only works if the group's contents are addressable as one unit.
/// </summary>
internal sealed class FormPageObject : PageObject
{
    /// <summary>The form's content, in the order its content stream painted them.</summary>
    public required IReadOnlyList<PageObject> Children { get; init; }

    /// <summary>Whether this subtree is a transparency group (§11.4.7) rather than a plain Form XObject reused only for its content-stream grouping.</summary>
    public bool IsTransparencyGroup { get; init; }

    /// <summary>Whether an isolated group (<c>/I true</c>) — paints against a fully transparent backdrop rather than the page content beneath it. Meaningless unless <see cref="IsTransparencyGroup"/>.</summary>
    public bool IsIsolated { get; init; }

    /// <summary>Whether a knockout group (<c>/K true</c>) — each member composites directly against the group's initial backdrop rather than against earlier members' accumulated result, so overlapping semi-transparent siblings never stack into higher combined opacity (<see cref="Transparency.TransparencyGroup.KnockoutLayer"/>). Meaningless unless <see cref="IsTransparencyGroup"/>.</summary>
    public bool IsKnockout { get; init; }

    /// <summary>The ExtGState <c>/SMask</c> active when this group's <c>Do</c> was invoked, or <see langword="null"/> for none. Meaningless unless <see cref="IsTransparencyGroup"/>.</summary>
    public GroupSoftMask? SoftMask { get; init; }
}
