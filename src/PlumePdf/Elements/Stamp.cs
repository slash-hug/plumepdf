namespace PlumePdf.Elements;

/// <summary>
/// Short, opaque text painted at a fixed corner of every page a <see cref="Section"/>
/// produces, on top of its content (composed-document stamping only — the same
/// scope boundary as <see cref="Watermark"/>). Set via <see cref="Section.Stamps"/>.
/// </summary>
/// <example>
/// <code>
/// var section = new Section
/// {
///     Body = body,
///     Stamps = [new Stamp { Text = "CONFIDENTIAL", Position = StampPosition.TopRight }],
/// };
/// </code>
/// </example>
public sealed class Stamp
{
    /// <summary>The text to paint.</summary>
    public required string Text { get; init; }

    /// <summary>The font size in points. Defaults to 12.</summary>
    public double FontSize { get; init; } = 12;

    /// <summary>The paint opacity, from 0 (invisible) to 1 (opaque). Defaults to 1.</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>Which page corner the stamp anchors to. Defaults to <see cref="StampPosition.TopRight"/>.</summary>
    public StampPosition Position { get; init; } = StampPosition.TopRight;

    /// <summary>The fill color. Defaults to a dark red.</summary>
    public MarkColor Color { get; init; } = MarkColor.DarkRed;
}

/// <summary>Which corner of the page a <see cref="Stamp"/> anchors to.</summary>
public enum StampPosition
{
    /// <summary>The top-left corner, inside the page margins.</summary>
    TopLeft,

    /// <summary>The top-right corner, inside the page margins.</summary>
    TopRight,

    /// <summary>The bottom-left corner, inside the page margins.</summary>
    BottomLeft,

    /// <summary>The bottom-right corner, inside the page margins.</summary>
    BottomRight,
}
