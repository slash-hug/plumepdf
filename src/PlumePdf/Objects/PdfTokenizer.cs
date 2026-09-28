using PlumePdf.IO;

namespace PlumePdf.Objects;

/// <summary>
/// The lexical token kinds a <see cref="PdfTokenizer"/> produces, per ISO 32000-1 §7.2
/// (whitespace/comments, skipped rather than emitted) and §7.3 (objects). <see cref="Keyword"/>
/// covers <c>true</c>/<c>false</c>/<c>null</c>/<c>obj</c>/<c>endobj</c>/<c>stream</c>/
/// <c>endstream</c>/<c>R</c>/<c>xref</c>/<c>trailer</c>/<c>startxref</c> and any other
/// regular-character run — interpreting which keyword it is belongs to <see cref="ObjectParser"/>,
/// not the tokenizer.
/// </summary>
internal enum PdfTokenType
{
    /// <summary>No token has been read yet.</summary>
    None,

    /// <summary>An integer numeric object (no decimal point).</summary>
    Integer,

    /// <summary>A real numeric object (has a decimal point).</summary>
    Real,

    /// <summary>A literal string, <c>(...)</c>, already escape-decoded.</summary>
    LiteralString,

    /// <summary>A hexadecimal string, <c>&lt;...&gt;</c>, already decoded to bytes.</summary>
    HexString,

    /// <summary>A name object, <c>/Name</c>, already <c>#XX</c>-escape-decoded.</summary>
    Name,

    /// <summary>The <c>[</c> array-open delimiter.</summary>
    ArrayStart,

    /// <summary>The <c>]</c> array-close delimiter.</summary>
    ArrayEnd,

    /// <summary>The <c>&lt;&lt;</c> dictionary-open delimiter.</summary>
    DictStart,

    /// <summary>The <c>&gt;&gt;</c> dictionary-close delimiter.</summary>
    DictEnd,

    /// <summary>A bare keyword or regular-character run whose meaning is contextual.</summary>
    Keyword,

    /// <summary>The buffer has been fully consumed.</summary>
    EndOfInput,
}

/// <summary>
/// A forward-only, allocation-light lexer over an already-materialized byte buffer,
/// modeled on <see cref="System.Text.Json.Utf8JsonReader"/>: a <see langword="ref struct"/>
/// that exposes the current token via properties rather than allocating a token object per
/// call, with <see cref="Consumed"/> tracking how far into the buffer it has advanced so a
/// caller can resume elsewhere or report a byte offset. Malformed input never throws —
/// <see cref="IsMalformed"/> is set and a best-effort value is produced (typically zero, or
/// the raw bytes as scanned), so a caller can turn it into a <see cref="PdfDiagnostic"/>
/// rather than a crash (the recovery-ladder philosophy applies at the lexical level too).
/// Covers ISO 32000-1 §7.2 (whitespace, comments — both skipped, never emitted as tokens)
/// and §7.3 (the eight object lexical forms plus the array/dictionary delimiters).
/// </summary>
internal ref struct PdfTokenizer
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;
    private byte[] _scratch;

    /// <summary>Creates a tokenizer over <paramref name="buffer"/>, starting at its first byte.</summary>
    public PdfTokenizer(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
        _scratch = new byte[64];
        TokenType = PdfTokenType.None;
        ValueSpan = default;
        IntegerValue = 0;
        RealValue = 0;
        IsMalformed = false;
    }

    /// <summary>The kind of the current token.</summary>
    public PdfTokenType TokenType { get; private set; }

    /// <summary>
    /// The current token's decoded value bytes (for <see cref="PdfTokenType.Name"/>,
    /// <see cref="PdfTokenType.LiteralString"/>, <see cref="PdfTokenType.HexString"/>, and
    /// <see cref="PdfTokenType.Keyword"/>) or raw lexical bytes (for delimiters and numbers).
    /// Valid only until the next call to <see cref="Read"/>.
    /// </summary>
    public ReadOnlySpan<byte> ValueSpan { get; private set; }

    /// <summary>The value of an <see cref="PdfTokenType.Integer"/> token.</summary>
    public long IntegerValue { get; private set; }

    /// <summary>The value of a <see cref="PdfTokenType.Real"/> token.</summary>
    public double RealValue { get; private set; }

    /// <summary>Whether the current token deviated from well-formed lexical syntax and was best-effort recovered.</summary>
    public bool IsMalformed { get; private set; }

    /// <summary>How many bytes of the buffer have been consumed so far — the offset just past the current token.</summary>
    public int Consumed => _position;

    /// <summary>
    /// Rewinds (or fast-forwards) to <paramref name="position"/>, discarding the current
    /// token. Used for the bounded backtracking a recursive-descent parser needs — e.g.
    /// distinguishing a plain integer from the start of an <c>N G R</c> indirect reference,
    /// which requires reading up to two tokens ahead before knowing which it was.
    /// </summary>
    public void Reset(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        _position = position;
        TokenType = PdfTokenType.None;
        ValueSpan = default;
        IsMalformed = false;
    }

    /// <summary>
    /// Advances to the next token. Returns <see langword="false"/> at end of input (in which
    /// case <see cref="TokenType"/> is <see cref="PdfTokenType.EndOfInput"/>); otherwise
    /// <see langword="true"/>, including for malformed tokens.
    /// </summary>
    public bool Read()
    {
        SkipWhitespaceAndComments();
        IsMalformed = false;

        if (_position >= _buffer.Length)
        {
            TokenType = PdfTokenType.EndOfInput;
            ValueSpan = default;
            return false;
        }

        var b = _buffer[_position];
        switch (b)
        {
            case (byte)'[':
                _position++;
                TokenType = PdfTokenType.ArrayStart;
                ValueSpan = _buffer.Slice(_position - 1, 1);
                return true;

            case (byte)']':
                _position++;
                TokenType = PdfTokenType.ArrayEnd;
                ValueSpan = _buffer.Slice(_position - 1, 1);
                return true;

            case (byte)'<':
                if (Peek(1) == (byte)'<')
                {
                    _position += 2;
                    TokenType = PdfTokenType.DictStart;
                    ValueSpan = _buffer.Slice(_position - 2, 2);
                    return true;
                }

                return ReadHexString();

            case (byte)'>':
                if (Peek(1) == (byte)'>')
                {
                    _position += 2;
                    TokenType = PdfTokenType.DictEnd;
                    ValueSpan = _buffer.Slice(_position - 2, 2);
                    return true;
                }

                // Stray '>' with no matching '<<' - malformed; consume it and keep going.
                _position++;
                TokenType = PdfTokenType.Keyword;
                ValueSpan = _buffer.Slice(_position - 1, 1);
                IsMalformed = true;
                return true;

            case (byte)'(':
                return ReadLiteralString();

            case (byte)'/':
                return ReadName();

            case (byte)'{':
            case (byte)'}':
                // PostScript calculator-function braces (Type 4 functions) - not evaluated by
                // the object parser, but tokenized as single-character keywords so callers that
                // do care (content-stream layer, later phases) can see them.
                _position++;
                TokenType = PdfTokenType.Keyword;
                ValueSpan = _buffer.Slice(_position - 1, 1);
                return true;

            case (byte)'+':
            case (byte)'-':
            case (byte)'.':
                return ReadNumber();

            default:
                if (b is >= (byte)'0' and <= (byte)'9')
                {
                    return ReadNumber();
                }

                return ReadKeyword();
        }
    }

    private byte? Peek(int offset) => _position + offset < _buffer.Length ? _buffer[_position + offset] : null;

    private void SkipWhitespaceAndComments()
    {
        while (_position < _buffer.Length)
        {
            var b = _buffer[_position];
            if (PdfScanner.IsWhitespace(b))
            {
                _position++;
                continue;
            }

            if (b == (byte)'%')
            {
                _position++;
                while (_position < _buffer.Length && _buffer[_position] != (byte)'\n' && _buffer[_position] != (byte)'\r')
                {
                    _position++;
                }

                continue;
            }

            break;
        }
    }

    private bool ReadNumber()
    {
        var start = _position;
        var negative = false;
        if (_buffer[_position] is (byte)'+' or (byte)'-')
        {
            negative = _buffer[_position] == (byte)'-';
            _position++;
        }

        long integerPart = 0;
        var hasDigits = false;
        while (_position < _buffer.Length && _buffer[_position] is >= (byte)'0' and <= (byte)'9')
        {
            integerPart = unchecked((integerPart * 10) + (_buffer[_position] - (byte)'0'));
            hasDigits = true;
            _position++;
        }

        var isReal = false;
        if (_position < _buffer.Length && _buffer[_position] == (byte)'.')
        {
            isReal = true;
            _position++;
            while (_position < _buffer.Length && _buffer[_position] is >= (byte)'0' and <= (byte)'9')
            {
                hasDigits = true;
                _position++;
            }
        }

        ValueSpan = _buffer[start.._position];

        if (!hasDigits)
        {
            // Bare "+", "-", or "." with no digits at all - malformed, but we've already
            // consumed at least the sign/dot so the caller keeps making forward progress.
            TokenType = PdfTokenType.Integer;
            IntegerValue = 0;
            IsMalformed = true;
            return true;
        }

        if (isReal)
        {
            TokenType = PdfTokenType.Real;

            // Parsed directly from the matched bytes (rather than accumulated digit-by-digit
            // via repeated multiply-and-add) so the resulting double is the nearest
            // representable value to the literal exactly the way any standard parser rounds
            // it - e.g. "0.7" becomes precisely what .NET's own double parsing produces for
            // "0.7", not 0.7000000000000001 from compounding per-digit floating-point error.
            RealValue = System.Buffers.Text.Utf8Parser.TryParse(ValueSpan, out double parsed, out _) ? parsed : 0.0;
        }
        else
        {
            TokenType = PdfTokenType.Integer;
            IntegerValue = negative ? -integerPart : integerPart;
        }

        return true;
    }

    private bool ReadName()
    {
        _position++; // consume leading '/'
        var length = 0;

        while (_position < _buffer.Length && PdfScanner.IsRegular(_buffer[_position]))
        {
            var b = _buffer[_position];
            if (b == (byte)'#' && _position + 2 < _buffer.Length && IsHexDigit(_buffer[_position + 1]) && IsHexDigit(_buffer[_position + 2]))
            {
                AppendScratch(ref length, (byte)((HexValue(_buffer[_position + 1]) << 4) | HexValue(_buffer[_position + 2])));
                _position += 3;
            }
            else
            {
                AppendScratch(ref length, b);
                _position++;
            }
        }

        TokenType = PdfTokenType.Name;
        ValueSpan = _scratch.AsSpan(0, length);
        return true;
    }

    private bool ReadHexString()
    {
        _position++; // consume '<'
        var length = 0;
        int? pendingHighNibble = null;
        var malformed = false;

        while (_position < _buffer.Length && _buffer[_position] != (byte)'>')
        {
            var b = _buffer[_position];
            if (IsHexDigit(b))
            {
                if (pendingHighNibble is int high)
                {
                    AppendScratch(ref length, (byte)((high << 4) | HexValue(b)));
                    pendingHighNibble = null;
                }
                else
                {
                    pendingHighNibble = HexValue(b);
                }
            }
            else if (!PdfScanner.IsWhitespace(b))
            {
                malformed = true;
            }

            _position++;
        }

        if (pendingHighNibble is int lastHigh)
        {
            // Odd digit count: an implicit trailing 0 nibble, per §7.3.4.3.
            AppendScratch(ref length, (byte)(lastHigh << 4));
        }

        if (_position < _buffer.Length && _buffer[_position] == (byte)'>')
        {
            _position++;
        }
        else
        {
            malformed = true; // ran off the end of the buffer unterminated
        }

        TokenType = PdfTokenType.HexString;
        ValueSpan = _scratch.AsSpan(0, length);
        IsMalformed = malformed;
        return true;
    }

    private bool ReadLiteralString()
    {
        _position++; // consume '('
        var length = 0;
        var depth = 1;
        var malformed = false;

        while (_position < _buffer.Length && depth > 0)
        {
            var b = _buffer[_position];

            if (b == (byte)'\\')
            {
                _position++;
                if (_position >= _buffer.Length)
                {
                    malformed = true;
                    break;
                }

                var esc = _buffer[_position];
                switch (esc)
                {
                    case (byte)'n':
                        AppendScratch(ref length, (byte)'\n');
                        _position++;
                        break;
                    case (byte)'r':
                        AppendScratch(ref length, (byte)'\r');
                        _position++;
                        break;
                    case (byte)'t':
                        AppendScratch(ref length, (byte)'\t');
                        _position++;
                        break;
                    case (byte)'b':
                        AppendScratch(ref length, 0x08);
                        _position++;
                        break;
                    case (byte)'f':
                        AppendScratch(ref length, 0x0C);
                        _position++;
                        break;
                    case (byte)'(':
                        AppendScratch(ref length, (byte)'(');
                        _position++;
                        break;
                    case (byte)')':
                        AppendScratch(ref length, (byte)')');
                        _position++;
                        break;
                    case (byte)'\\':
                        AppendScratch(ref length, (byte)'\\');
                        _position++;
                        break;
                    case (byte)'\r':
                        _position++;
                        if (_position < _buffer.Length && _buffer[_position] == (byte)'\n')
                        {
                            _position++;
                        }

                        break; // line continuation - produces nothing
                    case (byte)'\n':
                        _position++;
                        break; // line continuation
                    case >= (byte)'0' and <= (byte)'7':
                        {
                            var value = 0;
                            var digits = 0;
                            while (digits < 3 && _position < _buffer.Length && _buffer[_position] is >= (byte)'0' and <= (byte)'7')
                            {
                                value = (value * 8) + (_buffer[_position] - (byte)'0');
                                _position++;
                                digits++;
                            }

                            AppendScratch(ref length, unchecked((byte)value));
                            break;
                        }

                    default:
                        // Unknown escape: the REVERSE SOLIDUS is ignored, the character is literal (§7.3.4.2).
                        AppendScratch(ref length, esc);
                        _position++;
                        break;
                }

                continue;
            }

            if (b == (byte)'(')
            {
                depth++;
                AppendScratch(ref length, b);
                _position++;
                continue;
            }

            if (b == (byte)')')
            {
                depth--;
                _position++;
                if (depth == 0)
                {
                    break;
                }

                AppendScratch(ref length, b);
                continue;
            }

            if (b == (byte)'\r')
            {
                // An unescaped CR or CRLF end-of-line marker normalizes to a single LF (§7.3.4.2).
                AppendScratch(ref length, (byte)'\n');
                _position++;
                if (_position < _buffer.Length && _buffer[_position] == (byte)'\n')
                {
                    _position++;
                }

                continue;
            }

            AppendScratch(ref length, b);
            _position++;
        }

        if (depth > 0)
        {
            malformed = true; // ran off the end of the buffer with unbalanced parens
        }

        TokenType = PdfTokenType.LiteralString;
        ValueSpan = _scratch.AsSpan(0, length);
        IsMalformed = malformed;
        return true;
    }

    private bool ReadKeyword()
    {
        var start = _position;
        while (_position < _buffer.Length && PdfScanner.IsRegular(_buffer[_position]))
        {
            _position++;
        }

        var malformed = false;
        if (_position == start)
        {
            // An unexpected byte the dispatcher couldn't classify - consume exactly one byte
            // so the tokenizer always makes forward progress instead of looping forever.
            _position++;
            malformed = true;
        }

        ValueSpan = _buffer[start.._position];
        TokenType = PdfTokenType.Keyword;
        IsMalformed = malformed;
        return true;
    }

    private void AppendScratch(ref int length, byte value)
    {
        if (_scratch.Length <= length)
        {
            Array.Resize(ref _scratch, Math.Max(length + 1, _scratch.Length * 2));
        }

        _scratch[length] = value;
        length++;
    }

    private static bool IsHexDigit(byte b) => b is (>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'f') or (>= (byte)'A' and <= (byte)'F');

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
        _ => 0,
    };
}
