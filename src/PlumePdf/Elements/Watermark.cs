namespace PlumePdf.Elements;

/// <summary>
/// Faint, rotated text painted behind a <see cref="Section"/>'s content on every page it
/// produces (composed-document watermarking only — watermarking an already-opened
/// document is a separate writer feature, not this). Set via <see cref="Section.Watermark"/>.
/// </summary>
/// <example>
/// <code>
/// var section = new Section
/// {
///     Body = body,
///     Watermark = new Watermark { Text = "DRAFT" },
/// };
/// </code>
/// </example>
public sealed class Watermark
{
    /// <summary>The text to paint diagonally across every page.</summary>
    public required string Text { get; init; }

    /// <summary>The font size in points. Defaults to 72.</summary>
    public double FontSize { get; init; } = 72;

    /// <summary>The paint opacity, from 0 (invisible) to 1 (opaque). Defaults to 0.15.</summary>
    public double Opacity { get; init; } = 0.15;

    /// <summary>The counterclockwise rotation, in degrees, about the page's center. Defaults to 45.</summary>
    public double RotationDegrees { get; init; } = 45;

    /// <summary>The fill color. Defaults to a mid gray.</summary>
    public MarkColor Color { get; init; } = MarkColor.Gray;
}

/// <summary>A device-RGB color, each channel from 0 to 1, shared by <see cref="Watermark"/> and <see cref="Stamp"/>.</summary>
public readonly record struct MarkColor(double Red, double Green, double Blue)
{
    /// <summary>A mid gray, the default <see cref="Watermark"/> color.</summary>
    public static readonly MarkColor Gray = new(0.6, 0.6, 0.6);

    /// <summary>A dark red, the default <see cref="Stamp"/> color.</summary>
    public static readonly MarkColor DarkRed = new(0.7, 0.1, 0.1);
}
