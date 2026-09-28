using System.Text;
using PlumePdf.Elements;

namespace PlumePdf.Compose;

/// <summary>
/// The fluent lambda-composition veneer behind <see cref="PdfDocument.Compose"/> (Variant
/// A — QuestPDF-shaped nested closures). Every descriptor in this namespace builds a
/// <see cref="Manuscript"/>'s <see cref="Section"/> internally; nothing here holds layout
/// state of its own. Prefer <see cref="Manuscript"/>/<see cref="Elements.Section"/> directly
/// (Variant B) when you need to build, inspect, or transform the composed document
/// structure as data before rendering — <see cref="PdfDocument.Compose"/> is for everything
/// else (the "Compose vs Manuscript" rule).
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Compose(page =>
/// {
///     page.Size(PageSize.A4).Margin(40);
///     page.Header().Row(row =>
///     {
///         row.Justify(RowJustify.SpaceBetween);
///         row.Item().Text("INVOICE #1042").Bold().FontSize(20);
///     });
///     page.Content().Column(col =>
///     {
///         col.Spacing(12);
///         col.Item().Text("Bill to: Acme Corp");
///     });
///     page.Footer().Text(text =>
///     {
///         text.Span("Page ");
///         text.CurrentPageNumber();
///         text.Span(" of ");
///         text.TotalPageCount();
///     });
/// });
/// </code>
/// </example>
public sealed class PageDescriptor
{
    private PageSize _pageSize = PageSize.A4;
    private Elements.Margins _margins = Elements.Margins.Uniform(36);
    private ContainerDescriptor? _header;
    private ContainerDescriptor? _body;
    private ContainerDescriptor? _footer;
    private Watermark? _watermark;
    private readonly List<Stamp> _stamps = [];

    /// <summary>Sets the page size for this section. Defaults to <see cref="PageSize.A4"/>.</summary>
    public PageDescriptor Size(PageSize size)
    {
        _pageSize = size;
        return this;
    }

    /// <summary>Sets a uniform margin, in points, on all four sides. Defaults to 36.</summary>
    public PageDescriptor Margin(double points)
    {
        _margins = Elements.Margins.Uniform(points);
        return this;
    }

    /// <summary>Sets independent margins.</summary>
    public PageDescriptor Margins(Elements.Margins margins)
    {
        _margins = margins;
        return this;
    }

    /// <summary>Adds faint, rotated watermark text painted behind every page. See <see cref="Elements.Watermark"/>.</summary>
    public PageDescriptor Watermark(string text, double opacity = 0.15, double rotationDegrees = 45)
    {
        _watermark = new Elements.Watermark { Text = text, Opacity = opacity, RotationDegrees = rotationDegrees };
        return this;
    }

    /// <summary>Adds an opaque corner stamp painted atop every page. See <see cref="Elements.Stamp"/>.</summary>
    public PageDescriptor Stamp(string text, StampPosition position = StampPosition.TopRight)
    {
        _stamps.Add(new Elements.Stamp { Text = text, Position = position });
        return this;
    }

    /// <summary>Starts describing content repainted at the top of every page. Callable once per page description (<c>PLUME9009</c> on a repeat — the second call would silently discard the first's content otherwise).</summary>
    public ContainerDescriptor Header() => _header is null
        ? _header = new ContainerDescriptor()
        : throw new PlumePdfException("PLUME9009", "Header() was already called for this page description; describe all header content inside one call (a second call would silently replace the first's content).");

    /// <summary>Starts describing the section's flowing body content. Callable once per page description (<c>PLUME9009</c> on a repeat).</summary>
    public ContainerDescriptor Content() => _body is null
        ? _body = new ContainerDescriptor()
        : throw new PlumePdfException("PLUME9009", "Content() was already called for this page description; describe all body content inside one call (a second call would silently replace the first's content).");

    /// <summary>Starts describing content repainted at the bottom of every page. Callable once per page description (<c>PLUME9009</c> on a repeat).</summary>
    public ContainerDescriptor Footer() => _footer is null
        ? _footer = new ContainerDescriptor()
        : throw new PlumePdfException("PLUME9009", "Footer() was already called for this page description; describe all footer content inside one call (a second call would silently replace the first's content).");

    /// <summary>Builds the described <see cref="Section"/>.</summary>
    /// <exception cref="PdfLayoutException"><see cref="Content"/> was never called, or was called but nothing was described inside it.</exception>
    internal Section Build()
    {
        if (_body is null)
        {
            throw new PdfLayoutException(
                "PLUME9004",
                "Composed page has no content — call page.Content() and describe at least one element inside it.",
                "Section",
                [],
                "PdfDocument.Compose requires page.Content() to be called, with a Row/Column/Text/Image/Table described inside it.");
        }

        return new Section
        {
            PageSize = _pageSize,
            Margins = _margins,
            Header = _header?.Build(),
            Body = _body.Build(),
            Footer = _footer?.Build(),
            Watermark = _watermark,
            Stamps = _stamps,
        };
    }
}

/// <summary>Describes the single element that fills one container (a <see cref="PageDescriptor"/> header/content/footer, a <see cref="RowDescriptor"/>/<see cref="ColumnDescriptor"/> item, or a table cell).</summary>
public sealed class ContainerDescriptor
{
    private Func<Element>? _factory;
    private HorizontalAlign? _align;

    /// <summary>Centers this container's content.</summary>
    public ContainerDescriptor AlignCenter()
    {
        _align = HorizontalAlign.Center;
        return this;
    }

    /// <summary>Right-aligns this container's content.</summary>
    public ContainerDescriptor AlignRight()
    {
        _align = HorizontalAlign.Right;
        return this;
    }

    private void EnsureUndescribed()
    {
        if (_factory is not null)
        {
            throw new PlumePdfException("PLUME9009", "This container's content was already described; a container holds exactly one of Row/Column/Text/Image/Table (a second description would silently replace the first). Wrap multiple pieces in a Column or Row instead.");
        }
    }

    /// <summary>Describes this container's content as a <see cref="Row"/>.</summary>
    public void Row(Action<RowDescriptor> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureUndescribed();
        var descriptor = new RowDescriptor();
        configure(descriptor);
        _factory = () => descriptor.Build(_align);
    }

    /// <summary>Describes this container's content as a <see cref="Column"/>.</summary>
    public void Column(Action<ColumnDescriptor> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureUndescribed();
        var descriptor = new ColumnDescriptor();
        configure(descriptor);
        _factory = () => descriptor.Build(_align);
    }

    /// <summary>Describes this container's content as a single run of text.</summary>
    public TextSpanDescriptor Text(string text)
    {
        EnsureUndescribed();
        var descriptor = new TextSpanDescriptor(text, _align);
        _factory = descriptor.Build;
        return descriptor;
    }

    /// <summary>Describes this container's content as text assembled from several spans, e.g. a "Page N of M" footer.</summary>
    public void Text(Action<TextDescriptor> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureUndescribed();
        var descriptor = new TextDescriptor(_align);
        configure(descriptor);
        _factory = descriptor.Build;
    }

    /// <summary>Describes this container's content as an image.</summary>
    public ImageDescriptor Image(Elements.Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        EnsureUndescribed();
        var descriptor = new ImageDescriptor(image);
        _factory = descriptor.Build;
        return descriptor;
    }

    /// <summary>Describes this container's content as a <see cref="Elements.Table"/>.</summary>
    public void Table(Action<TableDescriptor> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureUndescribed();
        var descriptor = new TableDescriptor();
        configure(descriptor);
        _factory = descriptor.Build;
    }

    internal Element Build()
    {
        if (_factory is null)
        {
            throw new PdfLayoutException(
                "PLUME9004",
                "A container was described with no content.",
                "Section",
                [],
                "Every Header()/Content()/Footer()/Item()/Cell() container must call exactly one of Row/Column/Text/Image/Table on it.");
        }

        return _factory();
    }
}

/// <summary>Describes a <see cref="Row"/>'s children, in order.</summary>
public sealed class RowDescriptor
{
    private readonly List<ContainerDescriptor> _items = [];
    private double _spacing;
    private RowJustify _justify = RowJustify.Start;

    /// <summary>Sets the horizontal gap, in points, between children.</summary>
    public RowDescriptor Spacing(double points)
    {
        _spacing = points;
        return this;
    }

    /// <summary>Sets how children are distributed across the row's width. See <see cref="RowJustify"/>.</summary>
    public RowDescriptor Justify(RowJustify justify)
    {
        _justify = justify;
        return this;
    }

    /// <summary>Adds the next child.</summary>
    public ContainerDescriptor Item()
    {
        var descriptor = new ContainerDescriptor();
        _items.Add(descriptor);
        return descriptor;
    }

    internal Element Build(HorizontalAlign? align) =>
        new Row([.. _items.Select(static i => i.Build())]) { Spacing = _spacing, Justify = _justify, Align = align };
}

/// <summary>Describes a <see cref="Column"/>'s children, in order.</summary>
public sealed class ColumnDescriptor
{
    private readonly List<ContainerDescriptor> _items = [];
    private double _spacing;

    /// <summary>Sets the vertical gap, in points, between children.</summary>
    public ColumnDescriptor Spacing(double points)
    {
        _spacing = points;
        return this;
    }

    /// <summary>Adds the next child.</summary>
    public ContainerDescriptor Item()
    {
        var descriptor = new ContainerDescriptor();
        _items.Add(descriptor);
        return descriptor;
    }

    internal Element Build(HorizontalAlign? align) =>
        new Column([.. _items.Select(static i => i.Build())]) { Spacing = _spacing, Align = align };
}

/// <summary>Describes a single run of <see cref="Text"/>.</summary>
public sealed class TextSpanDescriptor(string content, HorizontalAlign? align)
{
    private bool _bold;
    private double _fontSize = 11;
    private PdfFont? _font;

    /// <summary>Renders in the bold weight.</summary>
    public TextSpanDescriptor Bold()
    {
        _bold = true;
        return this;
    }

    /// <summary>Sets the font size in points.</summary>
    public TextSpanDescriptor FontSize(double points)
    {
        _fontSize = points;
        return this;
    }

    /// <summary>Renders with a specific <see cref="PdfFont"/> — a Standard-14 name or an embedded <see cref="PdfFont.FromFile(string)"/> font — instead of the default Standard-14 Helvetica/Helvetica-Bold pair.</summary>
    public TextSpanDescriptor Font(PdfFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        _font = font;
        return this;
    }

    internal Element Build() => new Text(content) { Bold = _bold, FontSize = _fontSize, Font = _font, Align = align };
}

/// <summary>Describes <see cref="Text"/> assembled from several spans, including page-number placeholders — the fluent equivalent of a literal <c>"{page}"</c>/<c>"{pages}"</c> token in <see cref="Text.Content"/>.</summary>
public sealed class TextDescriptor(HorizontalAlign? align)
{
    private readonly StringBuilder _content = new();
    private bool _bold;
    private double _fontSize = 11;
    private PdfFont? _font;

    /// <summary>Appends literal text.</summary>
    public TextDescriptor Span(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _content.Append(text);
        return this;
    }

    /// <summary>Appends the current page number, resolved when each page is painted.</summary>
    public TextDescriptor CurrentPageNumber()
    {
        _content.Append("{page}");
        return this;
    }

    /// <summary>Appends the section's total page count, resolved once pagination completes.</summary>
    public TextDescriptor TotalPageCount()
    {
        _content.Append("{pages}");
        return this;
    }

    /// <summary>Renders in the bold weight.</summary>
    public TextDescriptor Bold()
    {
        _bold = true;
        return this;
    }

    /// <summary>Sets the font size in points.</summary>
    public TextDescriptor FontSize(double points)
    {
        _fontSize = points;
        return this;
    }

    /// <summary>Renders with a specific <see cref="PdfFont"/> — a Standard-14 name or an embedded <see cref="PdfFont.FromFile(string)"/> font — instead of the default Standard-14 Helvetica/Helvetica-Bold pair.</summary>
    public TextDescriptor Font(PdfFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        _font = font;
        return this;
    }

    internal Element Build() => new Text(_content.ToString()) { Bold = _bold, FontSize = _fontSize, Font = _font, Align = align };
}

/// <summary>Describes an <see cref="Elements.Image"/>'s rendered size.</summary>
public sealed class ImageDescriptor(Elements.Image image)
{
    private double? _width = image.Width;
    private double? _height = image.Height;

    /// <summary>Sets the rendered width in points.</summary>
    public ImageDescriptor Width(double points)
    {
        _width = points;
        return this;
    }

    /// <summary>Sets the rendered height in points.</summary>
    public ImageDescriptor Height(double points)
    {
        _height = points;
        return this;
    }

    // The internal copy constructor (not the RGB-24 constructor) preserves every payload a
    // RasterImageFrame-sourced Image can carry - gray/alpha pixels, JPEG DCT pass-through
    // bytes, AltText, Role - none of which the plain RGB-24 constructor has any way to
    // express. Reconstructing via that constructor here used to silently downgrade every
    // fluent-API image to an eagerly-RGB-expanded, Flate-re-encoded copy with its AltText and
    // Role dropped outright.
    internal Element Build() => new Elements.Image(image, _width, _height);
}

/// <summary>Describes a <see cref="Elements.Table"/>: its columns, an optional header row, and body cells that auto-flow row by row.</summary>
public sealed class TableDescriptor
{
    private readonly List<TableColumn> _columns = [];
    private readonly List<ContainerDescriptor> _headerCells = [];
    private readonly List<ContainerDescriptor> _bodyCells = [];

    /// <summary>Describes the table's columns, left to right.</summary>
    public TableDescriptor Columns(Action<TableColumnsDescriptor> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var descriptor = new TableColumnsDescriptor();
        configure(descriptor);
        _columns.AddRange(descriptor.Columns);
        return this;
    }

    /// <summary>Describes the header row, repainted at the top of every page the table spans.</summary>
    public TableDescriptor Header(Action<TableCellsDescriptor> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var descriptor = new TableCellsDescriptor();
        configure(descriptor);
        _headerCells.AddRange(descriptor.Cells);
        return this;
    }

    /// <summary>Adds the next body cell, auto-flowing into a new row every <c>Columns</c> count cells (row-major order).</summary>
    public ContainerDescriptor Cell()
    {
        var descriptor = new ContainerDescriptor();
        _bodyCells.Add(descriptor);
        return descriptor;
    }

    internal Element Build()
    {
        if (_columns.Count == 0)
        {
            throw new PdfLayoutException("PLUME9002", "Table.Columns(...) must describe at least one column before cells are added.", "Table", [], "Call table.Columns(c => ...) before table.Cell().");
        }

        var headerRow = _headerCells.Count > 0 ? (IReadOnlyList<Element>)[.. _headerCells.Select(static c => c.Build())] : null;
        var rows = new List<IReadOnlyList<Element>>();
        for (var i = 0; i < _bodyCells.Count; i += _columns.Count)
        {
            rows.Add([.. _bodyCells.Skip(i).Take(_columns.Count).Select(static c => c.Build())]);
        }

        return new Elements.Table { Columns = _columns, HeaderRow = headerRow, Rows = rows };
    }
}

/// <summary>Describes a <see cref="Elements.Table"/>'s column definitions.</summary>
public sealed class TableColumnsDescriptor
{
    internal List<TableColumn> Columns { get; } = [];

    /// <summary>Adds a column that takes a share of the table's remaining width, proportional to <paramref name="weight"/>.</summary>
    public TableColumnsDescriptor Relative(double weight)
    {
        Columns.Add(TableColumn.Relative(weight));
        return this;
    }

    /// <summary>Adds a column exactly <paramref name="points"/> wide.</summary>
    public TableColumnsDescriptor Fixed(double points)
    {
        Columns.Add(TableColumn.Fixed(points));
        return this;
    }
}

/// <summary>Collects one row's worth of table cells (used for <see cref="TableDescriptor.Header"/>).</summary>
public sealed class TableCellsDescriptor
{
    internal List<ContainerDescriptor> Cells { get; } = [];

    /// <summary>Adds the next cell in this row.</summary>
    public ContainerDescriptor Cell()
    {
        var descriptor = new ContainerDescriptor();
        Cells.Add(descriptor);
        return descriptor;
    }
}
