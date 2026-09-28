using System.Globalization;
using PlumePdf.Content;
using PlumePdf.Fonts.Reading;
using PlumePdf.Objects;

namespace PlumePdf.Documents.Forms.Appearances;

/// <summary>
/// The real <see cref="IAppearanceGenerator"/>: generates a widget's normal
/// appearance stream as a Form XObject — <c>/MK</c> background/border, then variable text laid
/// out per <see cref="VariableTextLayout"/> (quadding, <c>0 Tf</c> auto-size, multiline wrap)
/// in the effective <c>/DA</c> font/size/color, encoded through the same tables the decode
/// direction uses (<see cref="FontEncoder"/>). Buttons draw ZapfDingbats marks (check for a
/// checkbox on-state, a filled disc for a radio on-state). Failures throw coded exceptions the
/// filler catches to degrade to the <c>/NeedAppearances</c> escape hatch with a diagnostic —
/// generation never silently produces a wrong appearance.
/// </summary>
internal sealed class AppearanceGenerator : IAppearanceGenerator
{
    private static readonly PdfName TypeName = PdfName.Type;
    private static readonly PdfName XObjectName = PdfName.Get("XObject");
    private static readonly PdfName FormName = PdfName.Get("Form");
    private static readonly PdfName FontDirName = PdfName.Get("Font");
    private static readonly PdfName MKName = PdfName.Get("MK");
    private static readonly PdfName BGName = PdfName.Get("BG");
    private static readonly PdfName BCName = PdfName.Get("BC");
    private static readonly PdfName BSName = PdfName.Get("BS");
    private static readonly PdfName WName = PdfName.W;
    private const string TextFontResource = "PlumeF0";
    private const string DingbatsResource = "PlumeZaDb";
    private const int RadioFlagBit = 1 << 15; // /Ff bit 16 (ISO 32000-1 Table 226)
    private const int MultilineFlagBit = 1 << 12; // /Ff bit 13 (Table 228)

    private readonly IObjectSource _objects;
    private DocumentFontResolver? _fontResolver;
    private PdfObject? _dingbatsFontValue;

    public AppearanceGenerator(IObjectSource objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        _objects = objects;
    }

    /// <inheritdoc/>
    public IndirectReference GenerateAppearance(
        FieldValue value,
        PdfDictionary widget,
        string defaultAppearance,
        PdfDictionary resources,
        ObjectRegistry registry,
        PdfOptions options,
        DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(widget);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        var (width, height) = ReadRect(widget);

        var csb = new ContentStreamBuilder();
        var streamResources = new PdfDictionary();
        PaintBackgroundAndBorder(csb, widget, width, height);

        if (value.Kind == FormFieldKind.Button)
        {
            GenerateButtonMark(csb, widget, width, height, streamResources, registry);
        }
        else
        {
            GenerateVariableText(csb, value, widget, defaultAppearance, resources, width, height, streamResources, options, diagnostics);
        }

        var operators = csb.Build();
        if (operators.Length > options.MaxGeneratedAppearanceBytes)
        {
            throw new PlumePdfException("PLUME6045", $"Generated appearance stream is {operators.Length} bytes, exceeding PdfOptions.MaxGeneratedAppearanceBytes ({options.MaxGeneratedAppearanceBytes}).");
        }

        var dict = new PdfDictionary();
        dict.Set(TypeName, XObjectName);
        dict.Set(PdfName.Subtype, FormName);
        dict.Set(AcroFormNames.BBox, new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(width), PdfNumber.Get(height)]));
        if (streamResources.Count > 0)
        {
            dict.Set(AcroFormNames.Resources, streamResources);
        }

        dict.Set(PdfName.Length, PdfNumber.Get(operators.Length));

        var reference = registry.AllocateNumber();
        registry.RegisterNew(reference, new PdfStream(dict, operators));
        return reference;
    }

    private (double Width, double Height) ReadRect(PdfDictionary widget)
    {
        if (Resolve(widget.TryGetValue(AcroFormNames.Rect, out var rectValue) ? rectValue : null) is not PdfArray rect
            || rect.Count < 4
            || !TryNumber(rect[0], out var x1) || !TryNumber(rect[1], out var y1)
            || !TryNumber(rect[2], out var x2) || !TryNumber(rect[3], out var y2))
        {
            throw new PlumePdfException("PLUME6042", "Widget annotation has no usable /Rect; an appearance stream cannot be sized.");
        }

        var width = Math.Abs(x2 - x1);
        var height = Math.Abs(y2 - y1);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            throw new PlumePdfException("PLUME6042", $"Widget /Rect resolves to a degenerate {width}x{height} box; an appearance stream cannot be sized.");
        }

        return (width, height);
    }

    private void PaintBackgroundAndBorder(ContentStreamBuilder csb, PdfDictionary widget, double width, double height)
    {
        if (Resolve(widget.TryGetValue(MKName, out var mkValue) ? mkValue : null) is not PdfDictionary mk)
        {
            return;
        }

        if (TryReadColor(mk, BGName, out var bg))
        {
            csb.SaveState();
            ApplyFillColor(csb, bg);
            csb.Rectangle(0, 0, width, height).Fill().RestoreState();
        }

        if (TryReadColor(mk, BCName, out var bc))
        {
            var borderWidth = 1.0;
            if (Resolve(widget.TryGetValue(BSName, out var bsValue) ? bsValue : null) is PdfDictionary bs
                && bs.TryGetValue(WName, out var w) && TryNumber(w, out var declared) && declared >= 0)
            {
                borderWidth = declared;
            }

            if (borderWidth > 0)
            {
                csb.SaveState().SetLineWidth(borderWidth);
                ApplyStrokeColor(csb, bc);
                csb.Rectangle(borderWidth / 2, borderWidth / 2, width - borderWidth, height - borderWidth).Stroke().RestoreState();
            }
        }
    }

    private void GenerateVariableText(
        ContentStreamBuilder csb,
        FieldValue value,
        PdfDictionary widget,
        string defaultAppearance,
        PdfDictionary resources,
        double width,
        double height,
        PdfDictionary streamResources,
        PdfOptions options,
        DiagnosticCollection? diagnostics)
    {
        var text = value.Text ?? string.Empty;
        if (text.Length == 0)
        {
            return; // an empty value paints background/border only — a valid, empty appearance
        }

        var parsed = DefaultAppearanceParser.Parse(defaultAppearance);
        var fontName = parsed.FontResourceName;
        var fontSize = parsed.FontSize;
        if (fontName is null)
        {
            // Real-world forms frequently omit /DA entirely; Acrobat falls back to a /DR
            // font rather than refusing. Mirror that, loudly: first /DR /Font entry,
            // auto-sized, with a diagnostic naming the substitution.
            fontName = FirstFontResourceName(resources);
            fontSize = 0;
            if (fontName is null)
            {
                throw new PlumePdfException("PLUME6043", "The field's effective /DA declares no usable font (no Tf operator) and the AcroForm's /DR has no /Font entries to fall back to; cannot generate a text appearance.");
            }

            diagnostics?.Add(new PdfDiagnostic("PLUME6043", DiagnosticSeverity.Info, $"No effective /DA named a font; fell back to /DR font '/{fontName}' at auto size for the generated appearance."));
        }

        _fontResolver ??= new DocumentFontResolver(_objects, options, diagnostics);
        var font = _fontResolver.Resolve(fontName, resources)
            ?? throw new PlumePdfException("PLUME6043", $"/DA font '/{fontName}' could not be resolved in the AcroForm's /DR (missing, malformed, or a composite font — encode direction for Type0 fonts arrives with complex-script support).");

        if (!font.Encoder.TryEncode(text, out _, out var unencodable))
        {
            throw new PlumePdfException("PLUME6044", $"Character U+{unencodable:X4} in the fill value has no code in /DA font '/{fontName}'; refusing to generate a wrong appearance (use a /DR font covering the text, or the NeedAppearances escape hatch).");
        }

        double MeasureAtOne(string s)
        {
            if (!font.Encoder.TryEncode(s, out var bytes, out _) || bytes is null)
            {
                return 0;
            }

            var total = 0.0;
            var span = bytes.AsSpan();
            while (!span.IsEmpty)
            {
                font.Metrics.DecodeNext(span, out var consumed, out _, out var advance);
                total += advance;
                span = span[Math.Max(consumed, 1)..];
            }

            return total / 1000.0;
        }

        var quadding = 0;
        if (widget.TryGetValue(AcroFormNames.Q, out var qValue) && qValue is PdfNumber q && q.TryToInt32(out var declaredQ) && declaredQ is >= 0 and <= 2)
        {
            quadding = declaredQ;
        }

        var multiline = widget.TryGetValue(AcroFormNames.Ff, out var ffValue)
            && ffValue is PdfNumber ff && ff.TryToInt32(out var flags) && (flags & MultilineFlagBit) != 0;

        var (effectiveSize, lines) = VariableTextLayout.Layout(text, MeasureAtOne, width, height, fontSize, quadding, multiline, out var truncated);
        if (truncated)
        {
            diagnostics?.Add(new PdfDiagnostic("PLUME6046", DiagnosticSeverity.Warning, "Multiline fill value did not fit the widget's rectangle; trailing lines were omitted from the generated appearance (the full value is still in /V)."));
        }

        var fontDir = new PdfDictionary();
        fontDir.Set(PdfName.Get(TextFontResource), font.ResourceValue);
        streamResources.Set(FontDirName, fontDir);

        csb.SaveState();
        ApplyColorOperators(csb, parsed.ColorOperators);
        csb.BeginText().SetFont(TextFontResource, effectiveSize);
        double lastX = 0, lastY = 0;
        foreach (var line in lines)
        {
            csb.MoveText(line.X - lastX, line.Y - lastY);
            font.Encoder.TryEncode(line.Text, out var lineBytes, out _);
            csb.ShowText(lineBytes);
            lastX = line.X;
            lastY = line.Y;
        }

        csb.EndText().RestoreState();
    }

    private void GenerateButtonMark(ContentStreamBuilder csb, PdfDictionary widget, double width, double height, PdfDictionary streamResources, ObjectRegistry registry)
    {
        var isRadio = widget.TryGetValue(AcroFormNames.Ff, out var ffValue)
            && ffValue is PdfNumber ff && ff.TryToInt32(out var flags) && (flags & RadioFlagBit) != 0;

        // ZapfDingbats check (code 0x34, '4') for a checkbox; filled disc (0x6C, 'l') for a
        // radio on-state — the marks every mainstream filler draws. Their AFM widths are 791
        // and 791 units respectively (Adobe Core-14 metrics, already vendored for Standard-14).
        var code = isRadio ? (byte)0x6C : (byte)0x34;
        const double GlyphWidthFraction = 0.791;
        const double CapHeightFraction = 0.7;

        if (_dingbatsFontValue is null)
        {
            var dingbats = new PdfDictionary();
            dingbats.Set(TypeName, PdfName.Get("Font"));
            dingbats.Set(PdfName.Subtype, PdfName.Get("Type1"));
            dingbats.Set(PdfName.Get("BaseFont"), PdfName.Get("ZapfDingbats"));
            var reference = registry.AllocateNumber();
            registry.RegisterNew(reference, dingbats);
            _dingbatsFontValue = new PdfReference(reference);
        }

        var fontDir = new PdfDictionary();
        fontDir.Set(PdfName.Get(DingbatsResource), _dingbatsFontValue);
        streamResources.Set(FontDirName, fontDir);

        var size = Math.Min(width, height) * 0.8;
        var x = (width - (size * GlyphWidthFraction)) / 2;
        var y = (height - (size * CapHeightFraction)) / 2;

        csb.SaveState().SetFillGray(0)
            .BeginText().SetFont(DingbatsResource, size).MoveText(x, y);
        csb.ShowText([code]);
        csb.EndText().RestoreState();
    }

    private string? FirstFontResourceName(PdfDictionary resources)
    {
        if (Resolve(resources.TryGetValue(FontDirName, out var fonts) ? fonts : null) is not PdfDictionary fontDir)
        {
            return null;
        }

        foreach (var key in fontDir.Keys)
        {
            return key.Value;
        }

        return null;
    }

    private static void ApplyColorOperators(ContentStreamBuilder csb, string colorOperators)
    {
        var parts = colorOperators.Split(' ');
        var values = new double[parts.Length - 1];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = double.Parse(parts[i], CultureInfo.InvariantCulture);
        }

        switch (parts[^1])
        {
            case "g" when values.Length == 1:
                csb.SetFillGray(values[0]);
                break;
            case "rg" when values.Length == 3:
                csb.SetFillRgb(values[0], values[1], values[2]);
                break;
            default:
                // /DA CMYK (k) has no ContentStreamBuilder op yet; approximate via RGB.
                if (values.Length == 4)
                {
                    csb.SetFillRgb((1 - values[0]) * (1 - values[3]), (1 - values[1]) * (1 - values[3]), (1 - values[2]) * (1 - values[3]));
                }
                else
                {
                    csb.SetFillGray(0);
                }

                break;
        }
    }

    private void ApplyFillColor(ContentStreamBuilder csb, double[] components)
    {
        switch (components.Length)
        {
            case 1: csb.SetFillGray(components[0]); break;
            case 3: csb.SetFillRgb(components[0], components[1], components[2]); break;
            case 4: csb.SetFillRgb((1 - components[0]) * (1 - components[3]), (1 - components[1]) * (1 - components[3]), (1 - components[2]) * (1 - components[3])); break;
            default: csb.SetFillGray(1); break;
        }
    }

    private void ApplyStrokeColor(ContentStreamBuilder csb, double[] components)
    {
        switch (components.Length)
        {
            case 1: csb.SetStrokeGray(components[0]); break;
            case 3: csb.SetStrokeRgb(components[0], components[1], components[2]); break;
            case 4: csb.SetStrokeRgb((1 - components[0]) * (1 - components[3]), (1 - components[1]) * (1 - components[3]), (1 - components[2]) * (1 - components[3])); break;
            default: csb.SetStrokeGray(0); break;
        }
    }

    private bool TryReadColor(PdfDictionary mk, PdfName key, out double[] components)
    {
        components = [];
        if (Resolve(mk.TryGetValue(key, out var value) ? value : null) is not PdfArray array || array.Count is 0 or > 4)
        {
            return false;
        }

        var result = new double[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            if (!TryNumber(array[i], out result[i]))
            {
                return false;
            }
        }

        components = result;
        return true;
    }

    private static bool TryNumber(PdfObject? value, out double result)
    {
        result = 0;
        if (value is not PdfNumber number || !double.IsFinite(number.Value))
        {
            return false;
        }

        result = number.Value;
        return true;
    }

    private PdfObject? Resolve(PdfObject? value) =>
        value is PdfReference reference ? _objects.Resolve(reference.Target) : value;
}
