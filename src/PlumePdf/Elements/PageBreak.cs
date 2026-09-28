namespace PlumePdf.Elements;

/// <summary>
/// A zero-height marker inside a <see cref="Column"/> that forces an unconditional page break
/// at that point, regardless of how much space remains on the current page — the manual
/// escape hatch alongside <c>PlumePdf.Layout.Paginator</c>'s automatic breaking.
/// </summary>
/// <example>
/// <code>
/// var body = new Column(
///     new Text("Section one..."),
///     new PageBreak(),
///     new Text("Section two starts on its own page."));
/// </code>
/// </example>
public sealed class PageBreak : Element;
