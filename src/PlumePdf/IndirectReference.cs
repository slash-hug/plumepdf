namespace PlumePdf;

/// <summary>
/// Identifies an indirect object by its object number <em>and</em> generation, per
/// ISO 32000-1 §7.3.10. Two references are equal only when both the number and the
/// generation match — the same object number under a newer generation identifies a
/// different revision (see the cross-reference table's free-list handling, §7.5.4).
/// </summary>
/// <example>
/// <code>
/// var reference = new IndirectReference(12, 0);
/// PdfObject resolved = document.Objects[reference];
/// </code>
/// </example>
public readonly struct IndirectReference : IEquatable<IndirectReference>
{
    /// <summary>Creates a reference to object <paramref name="number"/>, generation <paramref name="generation"/>.</summary>
    /// <param name="number">The object number. Must be non-negative.</param>
    /// <param name="generation">The generation number. Must be non-negative.</param>
    public IndirectReference(int number, int generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        Number = number;
        Generation = generation;
    }

    /// <summary>The object number.</summary>
    public int Number { get; }

    /// <summary>The generation number.</summary>
    public int Generation { get; }

    /// <inheritdoc/>
    public bool Equals(IndirectReference other) => Number == other.Number && Generation == other.Generation;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is IndirectReference other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Number, Generation);

    /// <summary>Compares two references for equality.</summary>
    public static bool operator ==(IndirectReference left, IndirectReference right) => left.Equals(right);

    /// <summary>Compares two references for inequality.</summary>
    public static bool operator !=(IndirectReference left, IndirectReference right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => $"{Number} {Generation} obj";
}

/// <summary>
/// A PDF indirect reference object, <c>N G R</c> (ISO 32000-1 §7.3.10) — an unresolved
/// pointer as it appears inside another object's value (e.g. a dictionary entry or array
/// element), distinct from <see cref="IndirectReference"/> itself, which is just the
/// number+generation identity. Resolution to the referenced <see cref="PdfObject"/> goes
/// through <c>ObjectRegistry</c>'s indexer, keeping object access lazy: parsing an
/// indirect reference never triggers resolving the object it points to.
/// </summary>
/// <example>
/// <code>
/// if (catalog[PdfName.Get("Pages")] is PdfReference pagesRef)
/// {
///     PdfObject pages = document.Objects[pagesRef.Target];
/// }
/// </code>
/// </example>
public sealed class PdfReference : PdfObject
{
    /// <summary>Creates a reference wrapping <paramref name="target"/>.</summary>
    public PdfReference(IndirectReference target) => Target = target;

    /// <summary>The identity of the object this reference points to.</summary>
    public IndirectReference Target { get; }

    /// <inheritdoc/>
    public override string ToString() => Target.ToString();
}
