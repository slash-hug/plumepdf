using System.Globalization;

namespace PlumePdf;

/// <summary>
/// A PDF numeric object — integer or real (ISO 32000-1 §7.3.3). Both forms share one type
/// because PDF itself draws no hard line between them at the object-model level (a real
/// number with no fractional part and an integer are interchangeable almost everywhere);
/// <see cref="IsInteger"/> records the lexical form for round-tripping. Small integers are
/// cached — <see cref="Get(long)"/> for a cached value returns the same instance
/// every time; values equal by <see cref="Value"/> and <see cref="IsInteger"/> are always
/// equal by <see cref="Equals(object?)"/>, whether or not they share an instance.
/// </summary>
/// <example>
/// <code>
/// PdfNumber count = PdfNumber.Get(3);
/// PdfNumber sameCount = PdfNumber.Get(3);
/// bool cached = ReferenceEquals(count, sameCount); // true — 3 is within the small-integer cache
/// </code>
/// </example>
public sealed class PdfNumber : PdfObject, IEquatable<PdfNumber>
{
    private const long CacheMin = -32;
    private const long CacheMax = 255;
    private static readonly PdfNumber[] Cache = BuildCache();

    private PdfNumber(double value, bool isInteger)
    {
        Value = value;
        IsInteger = isInteger;
    }

    /// <summary>The numeric value, widened to <see cref="double"/> regardless of lexical form.</summary>
    public double Value { get; }

    /// <summary>Whether this number was written without a decimal point (an integer per §7.3.3).</summary>
    public bool IsInteger { get; }

    /// <summary>Returns a <see cref="PdfNumber"/> for an integer value, using the small-integer cache when possible.</summary>
    public static PdfNumber Get(long value)
    {
        if (value >= CacheMin && value <= CacheMax)
        {
            return Cache[value - CacheMin];
        }

        return new PdfNumber(value, isInteger: true);
    }

    /// <summary>Returns a <see cref="PdfNumber"/> for a real (non-integer-lexed) value.</summary>
    public static PdfNumber Get(double value) => new(value, isInteger: false);

    /// <summary>Converts the value to <see cref="int"/>, truncating any fractional part.</summary>
    /// <exception cref="OverflowException">The value does not fit in an <see cref="int"/>.</exception>
    public int ToInt32() => checked((int)Value);

    /// <summary>Converts the value to <see cref="long"/>, truncating any fractional part.</summary>
    /// <exception cref="OverflowException">The value does not fit in a <see cref="long"/>.</exception>
    public long ToInt64() => checked((long)Value);

    /// <summary>
    /// Converts the value to <see cref="int"/> without throwing. Use this (never
    /// <see cref="ToInt32"/>) for any number read from a document: a hostile or corrupt file
    /// can carry values past <see cref="int"/> range, and a checked conversion there is a
    /// bare <see cref="OverflowException"/> escaping the coded-error contract.
    /// </summary>
    /// <param name="result">The converted value, or 0 when the value doesn't fit.</param>
    /// <returns><see langword="true"/> when the value fits in an <see cref="int"/>.</returns>
    public bool TryToInt32(out int result)
    {
        if (Value is >= int.MinValue and <= int.MaxValue)
        {
            result = (int)Value;
            return true;
        }

        result = 0;
        return false;
    }

    /// <summary>
    /// Converts the value to <see cref="long"/> without throwing. See
    /// <see cref="TryToInt32(out int)"/> for when to prefer the Try form.
    /// </summary>
    /// <param name="result">The converted value, or 0 when the value doesn't fit.</param>
    /// <returns><see langword="true"/> when the value fits in a <see cref="long"/>.</returns>
    public bool TryToInt64(out long result)
    {
        // (double)long.MaxValue rounds UP to 2^63, so "<= long.MaxValue" would admit 2^63
        // itself and overflow in the cast; the exclusive bound trades the unrepresentable
        // top few doubles for a conversion that can never throw.
        if (Value >= long.MinValue && Value < 9223372036854775808d)
        {
            result = (long)Value;
            return true;
        }

        result = 0;
        return false;
    }

    /// <inheritdoc/>
    public bool Equals(PdfNumber? other) =>
        other is not null && IsInteger == other.IsInteger && Value.Equals(other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PdfNumber);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Value, IsInteger);

    /// <inheritdoc/>
    /// <remarks>
    /// A real value uses .NET's default (no format specifier) double formatting, which since
    /// .NET Core 3.0 produces the <em>shortest string that round-trips back to the exact same
    /// double</em> — so <c>12.3456789</c> comes back as <c>12.3456789</c> rather than
    /// truncated to a fixed six decimal places, and <c>0.1</c> comes back as <c>0.1</c> rather
    /// than whatever else happens to also print with six decimals. PDF real numbers never use
    /// scientific notation (§7.3.3) though, and shortest-round-trip formatting switches to it
    /// for very large/small magnitudes — the fallback below (rarely hit by real-world content:
    /// page coordinates, matrix entries, and opacities all stay well within plain-decimal
    /// range) re-renders those as fixed-point instead.
    /// </remarks>
    public override string ToString()
    {
        if (IsInteger)
        {
            return ((long)Value).ToString(CultureInfo.InvariantCulture);
        }

        var text = Value.ToString(CultureInfo.InvariantCulture);
        return text.Contains('E') ? FormatWithoutScientificNotation() : text;
    }

    private string FormatWithoutScientificNotation()
    {
        // "F17" always has enough decimal digits to round-trip any double exactly; trim the
        // trailing zeros that full fixed-point precision otherwise leaves in.
        var text = Value.ToString("F17", CultureInfo.InvariantCulture);
        if (!text.Contains('.'))
        {
            return text;
        }

        text = text.TrimEnd('0');
        return text.EndsWith('.') ? text[..^1] : text;
    }

    private static PdfNumber[] BuildCache()
    {
        var cache = new PdfNumber[CacheMax - CacheMin + 1];
        for (var i = 0; i < cache.Length; i++)
        {
            cache[i] = new PdfNumber(CacheMin + i, isInteger: true);
        }

        return cache;
    }
}
