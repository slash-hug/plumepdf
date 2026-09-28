namespace PlumePdf;

/// <summary>
/// A PDF boolean object, <c>true</c> or <c>false</c> (ISO 32000-1 §7.3.2). Interned:
/// every <c>true</c> in a document is <see cref="True"/>, every <c>false</c>
/// is <see cref="False"/> — reference equality and value equality coincide.
/// </summary>
/// <example>
/// <code>
/// PdfObject flag = PdfBoolean.Get(true);
/// bool isTrue = ReferenceEquals(flag, PdfBoolean.True); // always true for PdfBoolean.Get(true)
/// </code>
/// </example>
public sealed class PdfBoolean : PdfObject
{
    /// <summary>The shared instance representing <c>true</c>.</summary>
    public static PdfBoolean True { get; } = new(true);

    /// <summary>The shared instance representing <c>false</c>.</summary>
    public static PdfBoolean False { get; } = new(false);

    private PdfBoolean(bool value) => Value = value;

    /// <summary>The underlying boolean value.</summary>
    public bool Value { get; }

    /// <summary>Returns the interned instance for <paramref name="value"/>.</summary>
    public static PdfBoolean Get(bool value) => value ? True : False;

    /// <inheritdoc/>
    public override string ToString() => Value ? "true" : "false";
}
