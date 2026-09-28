using System.Text;
using PlumePdf.Fonts.Reading;

namespace PlumePdf.Fonts.Outlines;

/// <summary>
/// A render-path Type 1 / PFB font-program outline interpreter (Adobe Type 1 Font Format,
/// "the black book"), the third outline source Phase 8 adds alongside TrueType
/// (<c>GlyfTable</c>) and CFF/Type2 (<see cref="CffParser"/>). Structurally ported from Mozilla
/// pdf.js's <c>type1_parser.js</c> (Apache License 2.0) — the eexec/charstring decryption
/// algorithm and the Type 1 charstring operator semantics are Adobe's published specification,
/// expressed here independently against pdf.js's structure, not copied text, per the clean-room
/// policy in AGENTS.md.
/// Unlike <c>SfntFont</c>'s <c>PLUME8006</c> refusal, Type 1 has no pre-existing Fonts-layer
/// refusal code; a genuine parse failure here mints fresh into the Fonts <c>8xxx</c> band
/// (<c>PLUME8028</c>, the next free code).
/// </summary>
internal sealed class Type1Parser
{
    private const int MaxSubrDepth = 60; // Mirrors CffParser's bound — no spec-mandated cap, guards hostile/runaway recursion.

    private readonly Dictionary<string, byte[]> _charstringsByName;
    private readonly List<byte[]?> _subrs;
    private readonly int _lenIv;

    private Type1Parser(Dictionary<string, byte[]> charstringsByName, List<byte[]?> subrs, int lenIv, (string GlyphName, int Unicode)[]? builtInEncoding)
    {
        _charstringsByName = charstringsByName;
        _subrs = subrs;
        _lenIv = lenIv;
        BuiltInEncoding = builtInEncoding;
    }

    /// <summary>Every glyph name this font program defines a charstring for.</summary>
    public IReadOnlyCollection<string> GlyphNames => _charstringsByName.Keys;

    /// <summary>
    /// The program's own cleartext <c>/Encoding</c> (256 entries: <c>StandardEncoding</c>, or the
    /// <c>dup &lt;code&gt; /&lt;name&gt; put</c> table), shaped like an <see cref="Reading.EncodingResolver"/>
    /// table so it can serve as that resolver's built-in base; <see langword="null"/> when the
    /// program declares none.
    /// </summary>
    public (string GlyphName, int Unicode)[]? BuiltInEncoding { get; }

    /// <summary>
    /// Parses a Type 1 font program — either PFB (segmented, leading <c>0x80</c> marker bytes)
    /// or PFA-style (a single contiguous byte stream: cleartext header, <c>eexec</c>, the
    /// encrypted binary or ASCII-hex private portion, and the trailing zeros+<c>cleartomark</c>)
    /// — decrypting the private portion and extracting every <c>/CharStrings</c> and
    /// <c>/Subrs</c> entry, still charstring-encrypted at this point (decrypted lazily per
    /// glyph in <see cref="TryGetGlyphOutline"/> so a font with glyphs this caller never
    /// touches never pays the decrypt cost).
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME8028</c>: the program has no recognizable <c>eexec</c> section, or its <c>/CharStrings</c>/<c>/Subrs</c> structure is truncated or malformed.</exception>
    public static Type1Parser Parse(byte[] fontBytes, FontReadLimits limits)
    {
        ArgumentNullException.ThrowIfNull(fontBytes);

        var plain = UnwrapPfb(fontBytes);
        var eexecIndex = IndexOfAscii(plain, 0, "eexec");
        if (eexecIndex < 0)
        {
            throw new PlumePdfException("PLUME8028", "Type 1 font program has no 'eexec' section.");
        }

        var builtInEncoding = ParseBuiltInEncoding(plain, eexecIndex);

        var cipherStart = eexecIndex + 5;
        while (cipherStart < plain.Length && IsWhitespace(plain[cipherStart]))
        {
            cipherStart++;
        }

        var cipherBytes = LooksHexEncoded(plain, cipherStart) ? DecodeHex(plain, cipherStart) : plain[cipherStart..];
        var decrypted = Decrypt(cipherBytes, initialR: 55665, discard: 4);

        var lenIvIndex = IndexOfAscii(decrypted, 0, "/lenIV");
        var lenIv = 4;
        if (lenIvIndex >= 0)
        {
            var p = lenIvIndex + 6;
            if (TryReadInt(decrypted, ref p, out var declared))
            {
                lenIv = declared;
            }
        }

        var subrs = ParseSubrs(decrypted);
        var charstrings = ParseCharStrings(decrypted);

        if (charstrings.Count == 0)
        {
            throw new PlumePdfException("PLUME8028", "Type 1 font program's '/CharStrings' section produced no glyphs.");
        }

        return new Type1Parser(charstrings, subrs, lenIv, builtInEncoding);
    }

    /// <summary>
    /// Scans the cleartext header (<c>plain[0..eexecIndex)</c>, never encrypted) for the program's
    /// own <c>/Encoding</c> definition (Adobe Type 1 Font Format §7.2): either the literal
    /// <c>/Encoding StandardEncoding def</c>, or a <c>/Encoding &lt;N&gt; array …</c> table whose
    /// entries are populated either the classic way (<c>dup &lt;code&gt; /&lt;name&gt; put</c>) or
    /// via the equally-common idiom that <c>def</c>s the empty array before filling it (<c>/Encoding
    /// 256 array def</c> … <c>Encoding &lt;code&gt; /&lt;name&gt; put</c>, with no <c>dup</c>) —
    /// so entries are recognized by the <c>&lt;code&gt; /&lt;name&gt; put</c> shape itself,
    /// wherever it appears in the array's initializer, rather than requiring a specific token
    /// (<c>dup</c>, or the array's own variable name) immediately before the code. Every other
    /// token — <c>dup</c>, <c>array</c>, <c>def</c>, <c>readonly</c>, a <c>for</c>-loop filling
    /// <c>.notdef</c> entries by iteration rather than by name, and any other producer noise — is
    /// simply not <c>&lt;code&gt; /&lt;name&gt; put</c>-shaped and is skipped one token at a time.
    /// Deviations (a non-integer or out-of-range code, a malformed name token, a code glued to its
    /// name with no separating whitespace) are skipped, never thrown — this is advisory data read
    /// before the font is known to be well-formed at all. Returns <see langword="null"/> when the
    /// program declares no <c>/Encoding</c> in its cleartext portion, or when an explicit table is
    /// declared but not one entry could actually be parsed out of it (a blank 256-entry table is
    /// indistinguishable from "no built-in encoding" to every caller, so it must not masquerade as
    /// one — <see cref="Reading.EncodingResolver"/> would otherwise treat it as authoritative).
    /// </summary>
    private static (string GlyphName, int Unicode)[]? ParseBuiltInEncoding(byte[] plain, int eexecIndex)
    {
        var header = plain[..eexecIndex];
        var encodingIndex = IndexOfAscii(header, 0, "/Encoding");
        if (encodingIndex < 0)
        {
            return null;
        }

        var pos = encodingIndex + "/Encoding".Length;
        if (!TryReadToken(header, ref pos, out var firstToken))
        {
            return null;
        }

        if (firstToken == "StandardEncoding")
        {
            var clone = new (string, int)[SimpleFontEncodings.StandardEncoding.Length];
            Array.Copy(SimpleFontEncodings.StandardEncoding, clone, clone.Length);
            return clone;
        }

        if (!int.TryParse(firstToken, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            // Neither "StandardEncoding" nor "<N> array …" — unrecognized shape; treat as absent.
            return null;
        }

        var beforeArrayToken = pos;
        if (TryReadToken(header, ref pos, out var arrayToken) && arrayToken != "array")
        {
            pos = beforeArrayToken; // Not "array" — rewind; tolerate producers that omit it.
        }

        var table = new (string, int)[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (string.Empty, -1);
        }

        var anyEntryParsed = false;
        while (pos < header.Length)
        {
            var beforeEntry = pos;
            if (TryReadCodeAndName(header, ref pos, out var code, out var glyphName))
            {
                var unicode = SimpleFontEncodings.TryGlyphNameToUnicode(glyphName, out var resolved) ? resolved : -1;
                table[code] = (glyphName, unicode);
                anyEntryParsed = true;

                var beforePutToken = pos;
                if (TryReadToken(header, ref pos, out var putToken) && putToken != "put")
                {
                    pos = beforePutToken; // Not the trailing 'put' — rewind; lenient about its absence.
                }

                continue;
            }

            pos = beforeEntry;
            if (!TryReadToken(header, ref pos, out var other))
            {
                break; // End of header — nothing left to advance past.
            }

            // The array's own terminator ("readonly def" or a bare "def") ends the definition:
            // stop here so a later "<int> /<name> put"-shaped pair elsewhere in the cleartext
            // header (an unrelated dictionary entry) is never absorbed as an encoding entry.
            if (anyEntryParsed && (other is "def" || other is "readonly"))
            {
                break;
            }
        }

        return anyEntryParsed ? table : null;
    }

    /// <summary>
    /// Reads one <c>&lt;code&gt; /&lt;name&gt; [put]</c> encoding entry at <paramref name="pos"/>,
    /// tolerating producers that glue the code directly to the name with no separating whitespace
    /// (<c>65/A</c> as a single token, rather than <c>65 /A</c>). Leaves <paramref name="pos"/>
    /// unadvanced (via the caller's rewind) on any mismatch — a non-integer code, a code outside
    /// 0-255, or a name token that isn't a literal name (<c>/…</c>).
    /// </summary>
    private static bool TryReadCodeAndName(byte[] header, ref int pos, out int code, out string glyphName)
    {
        code = 0;
        glyphName = "";

        if (!TryReadToken(header, ref pos, out var codeToken))
        {
            return false;
        }

        var slash = codeToken.IndexOf('/');
        string nameToken;
        if (slash > 0)
        {
            // Glued together as one whitespace-delimited token, e.g. "65/A".
            if (!int.TryParse(codeToken[..slash], System.Globalization.CultureInfo.InvariantCulture, out code))
            {
                return false;
            }

            nameToken = codeToken[slash..];
        }
        else if (slash < 0)
        {
            if (!int.TryParse(codeToken, System.Globalization.CultureInfo.InvariantCulture, out code))
            {
                return false;
            }

            if (!TryReadToken(header, ref pos, out nameToken))
            {
                return false;
            }
        }
        else
        {
            return false; // Token starts with '/' — no numeric code at all.
        }

        if (code is < 0 or > 255 || nameToken.Length < 2 || nameToken[0] != '/')
        {
            return false;
        }

        glyphName = nameToken[1..];
        return true;
    }

    /// <summary>Decrypts and interprets glyph <paramref name="glyphName"/>'s Type 1 charstring into an outline. Returns <see langword="false"/> if the font has no glyph by that name.</summary>
    /// <exception cref="PlumePdfException"><c>PLUME8028</c>: the charstring is malformed, overflows the operand stack, or nests subroutine calls too deeply.</exception>
    public bool TryGetGlyphOutline(string glyphName, out GlyphOutline outline)
    {
        if (!_charstringsByName.TryGetValue(glyphName, out var cipher))
        {
            outline = GlyphOutline.Empty;
            return false;
        }

        outline = RunCharstring(cipher);
        return true;
    }

    private GlyphOutline RunCharstring(byte[] cipher)
    {
        var decrypted = Decrypt(cipher, initialR: 4330, discard: _lenIv);
        var interpreter = new Type1Interpreter(this, _lenIv);
        interpreter.Run(decrypted);
        return interpreter.Builder.Build();
    }

    private bool TryGetAccentGlyph(int standardEncodingCode, out byte[] cipher)
    {
        cipher = [];
        if (standardEncodingCode < 0 || standardEncodingCode >= SimpleFontEncodings.StandardEncoding.Length)
        {
            return false;
        }

        var name = SimpleFontEncodings.StandardEncoding[standardEncodingCode].GlyphName;
        if (string.IsNullOrEmpty(name) || !_charstringsByName.TryGetValue(name, out var found))
        {
            return false;
        }

        cipher = found;
        return true;
    }

    // ---- PFB unwrapping / eexec / charstring decryption (Adobe Type 1 Font Format Ch. 7) ----

    /// <summary>Strips PFB segment headers (<c>0x80 type len32le data</c>) if present, concatenating segment payloads into one contiguous byte stream; returns the input unchanged if it isn't PFB (doesn't start with the <c>0x80</c> marker).</summary>
    private static byte[] UnwrapPfb(byte[] data)
    {
        if (data.Length == 0 || data[0] != 0x80)
        {
            return data;
        }

        var result = new List<byte>(data.Length);
        var pos = 0;
        while (pos < data.Length && data[pos] == 0x80)
        {
            var type = data[pos + 1];
            if (type == 3)
            {
                break; // EOF marker.
            }

            var length = data[pos + 2] | (data[pos + 3] << 8) | (data[pos + 4] << 16) | (data[pos + 5] << 24);
            pos += 6;
            if (length < 0 || pos + length > data.Length)
            {
                throw new PlumePdfException("PLUME8028", "PFB segment length runs past the end of the file.");
            }

            result.AddRange(data.AsSpan(pos, length).ToArray());
            pos += length;
        }

        return [.. result];
    }

    private static bool LooksHexEncoded(byte[] data, int pos)
    {
        var checkCount = Math.Min(4, data.Length - pos);
        for (var i = 0; i < checkCount; i++)
        {
            if (!Uri.IsHexDigit((char)data[pos + i]))
            {
                return false;
            }
        }

        return checkCount > 0;
    }

    private static byte[] DecodeHex(byte[] data, int pos)
    {
        var result = new List<byte>((data.Length - pos) / 2);
        var hiNibble = -1;
        for (var i = pos; i < data.Length; i++)
        {
            var c = (char)data[i];
            if (!Uri.IsHexDigit(c))
            {
                continue; // Whitespace/newlines interleaved in hex dumps are ignored.
            }

            var value = Convert.ToInt32(c.ToString(), 16);
            if (hiNibble < 0)
            {
                hiNibble = value;
            }
            else
            {
                result.Add((byte)((hiNibble << 4) | value));
                hiNibble = -1;
            }
        }

        return [.. result];
    }

    /// <summary>The Type 1 eexec/charstring cipher (Adobe TN, Ch. 7): a rolling multiplicative stream cipher with a fixed <c>c1</c>/<c>c2</c>, discarding <paramref name="discard"/> leading decrypted bytes (random padding, not real content).</summary>
    private static byte[] Decrypt(byte[] cipher, ushort initialR, int discard)
    {
        const ushort c1 = 52845;
        const ushort c2 = 22719;
        var r = initialR;
        var plain = new byte[cipher.Length];
        for (var i = 0; i < cipher.Length; i++)
        {
            var c = cipher[i];
            plain[i] = (byte)(c ^ (r >> 8));
            r = (ushort)(((c + r) * c1) + c2);
        }

        return discard > 0 && discard <= plain.Length ? plain[discard..] : discard >= plain.Length ? [] : plain;
    }

    private static int IndexOfAscii(byte[] data, int start, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        for (var i = start; i <= data.Length - bytes.Length; i++)
        {
            var match = true;
            for (var j = 0; j < bytes.Length; j++)
            {
                if (data[i + j] != bytes[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0;

    private static void SkipWhitespace(byte[] data, ref int pos)
    {
        while (pos < data.Length && IsWhitespace(data[pos]))
        {
            pos++;
        }
    }

    private static bool TryReadToken(byte[] data, ref int pos, out string token)
    {
        SkipWhitespace(data, ref pos);
        var start = pos;
        while (pos < data.Length && !IsWhitespace(data[pos]))
        {
            pos++;
        }

        token = Encoding.ASCII.GetString(data, start, pos - start);
        return token.Length > 0;
    }

    private static bool TryReadInt(byte[] data, ref int pos, out int value)
    {
        if (!TryReadToken(data, ref pos, out var token) || !int.TryParse(token, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            value = 0;
            return false;
        }

        return true;
    }

    /// <summary>Extracts each <c>/Subrs N array ... dup &lt;index&gt; &lt;length&gt; &lt;RD&gt; &lt;bytes&gt; &lt;NP&gt;</c> entry's still-cipher charstring bytes (Type 1 Font Format §6.4).</summary>
    private static List<byte[]?> ParseSubrs(byte[] data)
    {
        var subrsIndex = IndexOfAscii(data, 0, "/Subrs");
        if (subrsIndex < 0)
        {
            return [];
        }

        var pos = subrsIndex + 6;
        if (!TryReadInt(data, ref pos, out var count) || count < 0)
        {
            throw new PlumePdfException("PLUME8028", "Type 1 '/Subrs' count is missing or invalid.");
        }

        var subrs = new List<byte[]?>(new byte[]?[count]);
        for (var i = 0; i < count; i++)
        {
            var dupIndex = IndexOfAscii(data, pos, "dup ");
            if (dupIndex < 0)
            {
                break; // Some producers write fewer 'dup' entries than the declared count — tolerate it (lenient reading).
            }

            pos = dupIndex + 4;
            if (!TryReadInt(data, ref pos, out var index) || !TryReadInt(data, ref pos, out var length))
            {
                throw new PlumePdfException("PLUME8028", "Type 1 '/Subrs' entry is malformed.");
            }

            TryReadToken(data, ref pos, out _); // The RD/-| binary-read operator name — its exact spelling is producer-defined.
            pos++; // Exactly one delimiter space precedes the binary blob (Type 1 Font Format §6.2).

            if (pos + length > data.Length || length < 0)
            {
                throw new PlumePdfException("PLUME8028", "Type 1 '/Subrs' charstring runs past the end of the decrypted program.");
            }

            if (index >= 0 && index < subrs.Count)
            {
                subrs[index] = data[pos..(pos + length)];
            }

            pos += length;
            TryReadToken(data, ref pos, out _); // The NP/|- binary-store operator name.
        }

        return subrs;
    }

    /// <summary>Extracts each <c>/CharStrings N dict dup begin /name &lt;length&gt; &lt;RD&gt; &lt;bytes&gt; &lt;ND&gt; ... end</c> entry's still-cipher charstring bytes (Type 1 Font Format §6.4).</summary>
    private static Dictionary<string, byte[]> ParseCharStrings(byte[] data)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sectionIndex = IndexOfAscii(data, 0, "/CharStrings");
        if (sectionIndex < 0)
        {
            return result;
        }

        var beginIndex = IndexOfAscii(data, sectionIndex, "begin");
        if (beginIndex < 0)
        {
            return result;
        }

        var pos = beginIndex + 5;
        while (pos < data.Length)
        {
            SkipWhitespace(data, ref pos);
            if (pos >= data.Length)
            {
                break;
            }

            if (data[pos] != (byte)'/')
            {
                if (!TryReadToken(data, ref pos, out var token))
                {
                    break;
                }

                if (token == "end")
                {
                    break;
                }

                continue; // Skip stray tokens (e.g. 'readonly', 'noaccess', 'put') between entries.
            }

            pos++; // Skip '/'.
            if (!TryReadToken(data, ref pos, out var name) || !TryReadInt(data, ref pos, out var length))
            {
                throw new PlumePdfException("PLUME8028", "Type 1 '/CharStrings' entry is malformed.");
            }

            TryReadToken(data, ref pos, out _); // RD/-| operator name.
            pos++; // Single delimiter space before the binary blob.

            if (length < 0 || pos + length > data.Length)
            {
                throw new PlumePdfException("PLUME8028", "Type 1 '/CharStrings' charstring runs past the end of the decrypted program.");
            }

            result[name] = data[pos..(pos + length)];
            pos += length;
            TryReadToken(data, ref pos, out _); // ND/|- operator name.
        }

        return result;
    }

    /// <summary>
    /// The Type 1 charstring interpreter (Adobe Type 1 Font Format Ch. 8/App. A) — walks one
    /// decrypted charstring's bytes, tracking the operand stack, current point, and the Flex/
    /// hint-replacement OtherSubrs protocol (App. A), emitting path commands into a
    /// <see cref="GlyphOutlineBuilder"/>.
    /// </summary>
    private sealed class Type1Interpreter(Type1Parser owner, int lenIv)
    {
        private readonly List<double> _stack = [];
        private readonly List<double> _psStack = [];
        private readonly List<(float X, float Y)> _flexPoints = [];
        private double _x;
        private double _y;
        private double _sbx;
        private bool _inFlex;
        private int _depth;

        public GlyphOutlineBuilder Builder { get; } = new();

        public void Run(byte[] decrypted) => Execute(decrypted);

        private void Execute(byte[] bytes)
        {
            if (++_depth > MaxSubrDepth)
            {
                throw new PlumePdfException("PLUME8028", "Type 1 charstring subroutine nesting exceeded the safety limit.");
            }

            var pos = 0;
            while (pos < bytes.Length)
            {
                var b0 = bytes[pos];

                if (b0 >= 32)
                {
                    pos = ReadNumber(bytes, pos, out var value);
                    if (_stack.Count >= 48)
                    {
                        throw new PlumePdfException("PLUME8028", "Type 1 charstring operand stack overflow.");
                    }

                    _stack.Add(value);
                    continue;
                }

                pos++;
                switch (b0)
                {
                    case 1: // hstem
                    case 3: // vstem
                        _stack.Clear();
                        break;

                    case 4: // vmoveto
                        RequireOperands(1);
                        MoveTo(0, _stack[0]);
                        _stack.Clear();
                        break;

                    case 5: // rlineto
                        RequireOperands(2);
                        LineTo(_stack[0], _stack[1]);
                        _stack.Clear();
                        break;

                    case 6: // hlineto
                        RequireOperands(1);
                        LineTo(_stack[0], 0);
                        _stack.Clear();
                        break;

                    case 7: // vlineto
                        RequireOperands(1);
                        LineTo(0, _stack[0]);
                        _stack.Clear();
                        break;

                    case 8: // rrcurveto
                        RequireOperands(6);
                        CurveTo(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                        _stack.Clear();
                        break;

                    case 9: // closepath
                        Builder.ClosePath();
                        _stack.Clear();
                        break;

                    case 10: // callsubr — no bias adjustment in Type 1 (unlike Type 2).
                        {
                            var index = (int)PopLast();
                            if (index >= 0 && index < owner._subrs.Count && owner._subrs[index] is { } cipher)
                            {
                                var decrypted = Decrypt(cipher, initialR: 4330, discard: lenIv);
                                Execute(decrypted);
                            }

                            break;
                        }

                    case 11: // return
                        _depth--;
                        return;

                    case 13: // hsbw: sbx wx hsbw
                        RequireOperands(2);
                        _sbx = _stack[0];
                        _x = _stack[0];
                        _y = 0;
                        _stack.Clear();
                        break;

                    case 14: // endchar
                        _stack.Clear();
                        _depth--;
                        return;

                    case 21: // rmoveto
                        RequireOperands(2);
                        MoveTo(_stack[0], _stack[1]);
                        _stack.Clear();
                        break;

                    case 22: // hmoveto
                        RequireOperands(1);
                        MoveTo(_stack[0], 0);
                        _stack.Clear();
                        break;

                    case 30: // vhcurveto: dy1 dx2 dy2 dx3
                        RequireOperands(4);
                        CurveTo(0, _stack[0], _stack[1], _stack[2], _stack[3], 0);
                        _stack.Clear();
                        break;

                    case 31: // hvcurveto: dx1 dx2 dy2 dy3
                        RequireOperands(4);
                        CurveTo(_stack[0], 0, _stack[1], _stack[2], 0, _stack[3]);
                        _stack.Clear();
                        break;

                    case 12: // escape (two-byte operator)
                        {
                            if (pos >= bytes.Length)
                            {
                                throw new PlumePdfException("PLUME8028", "Type 1 charstring escape operator is truncated.");
                            }

                            var b1 = bytes[pos++];
                            RunEscape(b1);
                            break;
                        }

                    default:
                        _stack.Clear();
                        break;
                }
            }

            _depth--;
        }

        private void RunEscape(byte op)
        {
            switch (op)
            {
                case 0: // dotsection — deprecated hint, no geometry effect.
                case 1: // vstem3
                case 2: // hstem3
                    _stack.Clear();
                    break;

                case 6: // seac: asb adx ady bchar achar seac
                    RequireOperands(5);
                    RunSeac(_stack[0], _stack[1], _stack[2], (int)_stack[3], (int)_stack[4]);
                    _stack.Clear();
                    break;

                case 7: // sbw: sbx sby wx wy sbw
                    RequireOperands(4);
                    _sbx = _stack[0];
                    _x = _stack[0];
                    _y = _stack[1];
                    _stack.Clear();
                    break;

                case 12: // div
                    {
                        RequireOperands(2);
                        var b = _stack[^1];
                        _stack.RemoveAt(_stack.Count - 1);
                        var a = _stack[^1];
                        _stack.RemoveAt(_stack.Count - 1);
                        _stack.Add(b == 0 ? 0 : a / b);
                        break; // div does not clear the stack — its result feeds the next operator.
                    }

                case 16: // callothersubr
                    RunCallOtherSubr();
                    break;

                case 17: // pop
                    _stack.Add(_psStack.Count > 0 ? PopPs() : 0);
                    break;

                case 33: // setcurrentpoint: x y setcurrentpoint
                    if (_stack.Count >= 2)
                    {
                        _x = _stack[0];
                        _y = _stack[1];
                    }

                    _stack.Clear();
                    break;

                default:
                    _stack.Clear();
                    break;
            }
        }

        /// <summary>
        /// The Flex (OtherSubrs 0/1/2) and hint-replacement (OtherSubr 3) protocol (Adobe Type 1
        /// Font Format Appendix A) — every real Type 1 font's <c>Subrs[0..3]</c> boilerplate
        /// routes through this operator regardless of which Subr index a given font assigns to
        /// which role, since the role is carried by the OtherSubr number pushed on the stack,
        /// not by which Subr called it.
        /// </summary>
        private void RunCallOtherSubr()
        {
            RequireOperands(2);
            var othersubr = (int)PopLast();
            var argCount = (int)PopLast();
            if (argCount < 0 || argCount > _stack.Count)
            {
                argCount = Math.Max(0, Math.Min(argCount, _stack.Count));
            }

            var args = new double[argCount];
            for (var i = argCount - 1; i >= 0; i--)
            {
                args[i] = PopLast();
            }

            switch (othersubr)
            {
                case 1: // Flex start.
                    _inFlex = true;
                    _flexPoints.Clear();
                    break;

                case 2: // Flex point marker — no-op; points are captured by MoveTo's interception below.
                    break;

                case 0: // Flex end: emit the two curves the 7 captured points imply, then hand back (x, y) for the standard "pop pop setcurrentpoint" that follows.
                    _inFlex = false;
                    if (_flexPoints.Count >= 7)
                    {
                        var p = _flexPoints;
                        Builder.CurveTo(p[1].X, p[1].Y, p[2].X, p[2].Y, p[3].X, p[3].Y);
                        Builder.CurveTo(p[4].X, p[4].Y, p[5].X, p[5].Y, p[6].X, p[6].Y);
                        _x = p[6].X;
                        _y = p[6].Y;
                    }

                    // Pushed so the first 'pop' yields x, the second yields y (list is LIFO from the end).
                    _psStack.Add(_y);
                    _psStack.Add(_x);
                    break;

                case 3: // Hint replacement: hand back the same subr# so the following 'pop callsubr' still executes it (harmless — hint-only subrs emit no geometry).
                    _psStack.Add(args.Length > 0 ? args[0] : 0);
                    break;

                default:
                    // Unrecognized OtherSubr (vendor-specific extension) — hand back whatever
                    // args were popped, in their original push order, rather than dropping them
                    // silently; a lenient degrade for real-world fonts using non-standard OtherSubrs.
                    foreach (var a in args)
                    {
                        _psStack.Add(a);
                    }

                    break;
            }
        }

        private void RunSeac(double asb, double adx, double ady, int bchar, int achar)
        {
            if (!owner.TryGetAccentGlyph(bchar, out var baseCipher))
            {
                return;
            }

            var baseInterp = new Type1Interpreter(owner, lenIv);
            baseInterp.Execute(Decrypt(baseCipher, initialR: 4330, discard: lenIv));
            foreach (var cmd in baseInterp.Builder.Build().Commands)
            {
                AppendCommand(Builder, cmd, 0, 0);
            }

            if (!owner.TryGetAccentGlyph(achar, out var accentCipher))
            {
                return;
            }

            var accentInterp = new Type1Interpreter(owner, lenIv);
            accentInterp.Execute(Decrypt(accentCipher, initialR: 4330, discard: lenIv));
            var dx = (float)(_sbx - asb + adx - accentInterp._sbx);
            var dy = (float)ady;
            foreach (var cmd in accentInterp.Builder.Build().Commands)
            {
                AppendCommand(Builder, cmd, dx, dy);
            }
        }

        private static void AppendCommand(GlyphOutlineBuilder sink, GlyphPathCommand cmd, float dx, float dy)
        {
            switch (cmd.Kind)
            {
                case GlyphPathCommandKind.MoveTo: sink.MoveTo(cmd.X + dx, cmd.Y + dy); break;
                case GlyphPathCommandKind.LineTo: sink.LineTo(cmd.X + dx, cmd.Y + dy); break;
                case GlyphPathCommandKind.CurveTo: sink.CurveTo(cmd.X1 + dx, cmd.Y1 + dy, cmd.X2 + dx, cmd.Y2 + dy, cmd.X + dx, cmd.Y + dy); break;
                case GlyphPathCommandKind.ClosePath: sink.ClosePath(); break;
            }
        }

        private void RequireOperands(int count)
        {
            if (_stack.Count < count)
            {
                throw new PlumePdfException("PLUME8028", $"Type 1 charstring operator needs {count} operand(s) but the stack has {_stack.Count}.");
            }
        }

        private double PopLast()
        {
            if (_stack.Count == 0)
            {
                throw new PlumePdfException("PLUME8028", "Type 1 charstring operand stack underflow.");
            }

            var value = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return value;
        }

        private double PopPs()
        {
            var value = _psStack[^1];
            _psStack.RemoveAt(_psStack.Count - 1);
            return value;
        }

        private void MoveTo(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            if (_inFlex)
            {
                _flexPoints.Add(((float)_x, (float)_y));
                return;
            }

            Builder.MoveTo((float)_x, (float)_y);
        }

        private void LineTo(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            Builder.LineTo((float)_x, (float)_y);
        }

        private void CurveTo(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            var x1 = _x + dx1;
            var y1 = _y + dy1;
            var x2 = x1 + dx2;
            var y2 = y1 + dy2;
            _x = x2 + dx3;
            _y = y2 + dy3;
            Builder.CurveTo((float)x1, (float)y1, (float)x2, (float)y2, (float)_x, (float)_y);
        }

        private static int ReadNumber(byte[] bytes, int pos, out double value)
        {
            var b0 = bytes[pos];
            switch (b0)
            {
                case 255:
                    if (pos + 5 > bytes.Length)
                    {
                        throw new PlumePdfException("PLUME8028", "Type 1 charstring operand is truncated.");
                    }

                    value = (bytes[pos + 1] << 24) | (bytes[pos + 2] << 16) | (bytes[pos + 3] << 8) | bytes[pos + 4];
                    return pos + 5;

                case >= 32 and <= 246:
                    value = b0 - 139;
                    return pos + 1;

                case >= 247 and <= 250:
                    if (pos + 2 > bytes.Length)
                    {
                        throw new PlumePdfException("PLUME8028", "Type 1 charstring operand is truncated.");
                    }

                    value = ((b0 - 247) * 256) + bytes[pos + 1] + 108;
                    return pos + 2;

                default: // 251-254
                    if (pos + 2 > bytes.Length)
                    {
                        throw new PlumePdfException("PLUME8028", "Type 1 charstring operand is truncated.");
                    }

                    value = (-(b0 - 251) * 256) - bytes[pos + 1] - 108;
                    return pos + 2;
            }
        }
    }
}
