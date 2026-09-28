namespace PlumePdf.Fonts.Reading;

/// <summary>
/// An <see cref="ExtractionFont"/> for a Type0 (composite) font (ISO 32000-1 §9.7): a
/// content-stream code is one or more bytes, resolved to a CID through the font's
/// <c>/Encoding</c>, and the CID indexes <c>/W</c>/<c>/DW</c> widths on the descendant CID
/// font. Two <c>/Encoding</c> shapes are covered for this phase:
/// <c>Identity-H</c>/<c>Identity-V</c> (always 2-byte codes, CID == code, built in — no CMap
/// resource needed) and an embedded CMap stream (<see cref="CMapParser"/>-parsed, arbitrary
/// code lengths per its own codespace ranges). Any other predefined CMap name (e.g.
/// <c>90ms-RKSJ-H</c>) is a documented known gap this phase: <see cref="ExtractionFontFactory"/>
/// reports a diagnostic and falls back to Identity-H code mapping so extraction still makes
/// forward progress rather than aborting the whole font.
/// </summary>
internal sealed class Type0ExtractionFont : ExtractionFont
{
    private readonly bool _isIdentity;
    private readonly CMap? _encodingCMap;
    private readonly IReadOnlyDictionary<int, double> _widthsByCid;
    private readonly double _defaultWidth;
    private readonly CMap? _toUnicode;

    /// <summary>Creates a <see cref="Type0ExtractionFont"/> from already-resolved font-dictionary data.</summary>
    /// <param name="baseFontName">The font's <c>/BaseFont</c> name.</param>
    /// <param name="isIdentity">Whether codes are decoded via the built-in Identity-H/V rule (2-byte code == CID) rather than <paramref name="encodingCMap"/>.</param>
    /// <param name="encodingCMap">The parsed embedded <c>/Encoding</c> CMap, when <paramref name="isIdentity"/> is <see langword="false"/>; otherwise ignored.</param>
    /// <param name="widthsByCid">CID → width (1000-unit glyph space), from the descendant CID font's <c>/W</c> array.</param>
    /// <param name="defaultWidth">The descendant CID font's <c>/DW</c>, or 1000 (the spec default, §9.7.4.3) when absent.</param>
    /// <param name="toUnicode">The parsed <c>/ToUnicode</c> CMap, or <see langword="null"/> when the font declares none or it failed to decode.</param>
    /// <param name="options">Active options.</param>
    /// <param name="diagnostics">Where recoverable deviations are recorded, if any.</param>
    public Type0ExtractionFont(
        string baseFontName,
        bool isIdentity,
        CMap? encodingCMap,
        IReadOnlyDictionary<int, double> widthsByCid,
        double defaultWidth,
        CMap? toUnicode,
        PdfOptions options,
        DiagnosticCollection? diagnostics)
        : base(baseFontName, options, diagnostics)
    {
        ArgumentNullException.ThrowIfNull(widthsByCid);
        if (!isIdentity && encodingCMap is null)
        {
            throw new ArgumentException("A non-Identity Type0 font requires a parsed encoding CMap.", nameof(encodingCMap));
        }

        _isIdentity = isIdentity;
        _encodingCMap = encodingCMap;
        _widthsByCid = widthsByCid;
        _defaultWidth = defaultWidth;
        _toUnicode = toUnicode;
    }

    /// <inheritdoc/>
    public override void DecodeNext(ReadOnlySpan<byte> bytes, out int codeLength, out string unicode, out double width)
    {
        if (bytes.Length == 0)
        {
            codeLength = 0;
            unicode = string.Empty;
            width = 0;
            return;
        }

        uint code;
        int cid;

        if (_isIdentity)
        {
            if (bytes.Length >= 2)
            {
                codeLength = 2;
                code = (uint)((bytes[0] << 8) | bytes[1]);
            }
            else
            {
                codeLength = 1;
                code = bytes[0];
                ReportDeviation("PLUME8022", $"Font '{BaseFontName}' (Identity-H/V, 2-byte codes) has a content-stream string with a trailing single byte; decoded as a 1-byte code.");
            }

            cid = (int)code;
        }
        else
        {
            codeLength = _encodingCMap!.GetCodeLength(bytes);
            code = ReadCode(bytes[..codeLength]);

            if (_encodingCMap.TryGetCid(code, out var mappedCid))
            {
                cid = mappedCid;
            }
            else
            {
                cid = 0;
                ReportUnmapped("PLUME8020", $"Character code {code} in font '{BaseFontName}' has no CID mapping in its embedded /Encoding CMap.");
            }
        }

        if (_toUnicode is { } toUnicode && toUnicode.TryGetUnicode(code, out var mapped))
        {
            unicode = mapped;
        }
        else
        {
            unicode = "\uFFFD";
            ReportUnmapped("PLUME8020", $"Character code {code} in font '{BaseFontName}' has no /ToUnicode mapping; substituted U+FFFD.");
        }

        width = _widthsByCid.TryGetValue(cid, out var declared) ? declared : _defaultWidth;
    }

    private static uint ReadCode(ReadOnlySpan<byte> bytes)
    {
        var value = 0u;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }
}
