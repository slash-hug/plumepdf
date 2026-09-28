using System.Text.RegularExpressions;

namespace PlumePdf.Documents;

/// <summary>
/// Implements the field-name lookup semantics: an exact
/// <see cref="AcroFormField.FullyQualifiedName"/> match first; failing that, a match against
/// the trailing (last-dot) segment of every field's fully-qualified name, succeeding only
/// when exactly one field's trailing segment matches — real forms carry UTF-16BE XFA-style
/// paths like <c>topmostSubform[0].Page1[0].c1_01[0]</c>, and an agent naturally types
/// <c>"c1_01"</c> (no array-index suffix), so trailing-segment comparison normalizes away a
/// trailing <c>[N]</c> on both sides before comparing. An ambiguous partial match throws,
/// naming every candidate, rather than silently picking one — the wrong-field-match risk
/// this decision exists to close.
/// </summary>
internal sealed partial class FieldNameIndex
{
    private readonly Dictionary<string, AcroFormField> _exact;
    private readonly ILookup<string, AcroFormField> _byTrailingSegment;

    public FieldNameIndex(IReadOnlyList<AcroFormField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _exact = new Dictionary<string, AcroFormField>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            _exact[field.FullyQualifiedName] = field;
        }

        _byTrailingSegment = fields.ToLookup(f => Normalize(LastSegment(f.FullyQualifiedName)), StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves <paramref name="name"/> to a field: exact fully-qualified match, else the
    /// unique field whose trailing name segment matches.
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// <paramref name="name"/> matches more than one field by trailing segment
    /// (<c>PLUME6030</c>), or no field at all (<c>PLUME6031</c>).
    /// </exception>
    public AcroFormField Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_exact.TryGetValue(name, out var exact))
        {
            return exact;
        }

        var candidates = _byTrailingSegment[Normalize(name)].ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            > 1 => throw new PlumePdfException("PLUME6030", $"Field name '{name}' matches {candidates.Count} fields by trailing segment: {string.Join(", ", candidates.Select(c => c.FullyQualifiedName))}. Use the fully-qualified name to disambiguate."),
            _ => throw new PlumePdfException("PLUME6031", $"No form field named '{name}' was found (checked an exact fully-qualified match and a unique trailing-segment match)."),
        };
    }

    /// <summary>Attempts to resolve <paramref name="name"/>; returns <see langword="false"/> only when no field matches at all. An ambiguous partial match still throws (the whole point of this lookup).</summary>
    public bool TryResolve(string name, out AcroFormField? field)
    {
        if (_exact.TryGetValue(name, out var exact))
        {
            field = exact;
            return true;
        }

        var candidates = _byTrailingSegment[Normalize(name)].ToList();
        if (candidates.Count == 1)
        {
            field = candidates[0];
            return true;
        }

        if (candidates.Count > 1)
        {
            throw new PlumePdfException("PLUME6030", $"Field name '{name}' matches {candidates.Count} fields by trailing segment: {string.Join(", ", candidates.Select(c => c.FullyQualifiedName))}. Use the fully-qualified name to disambiguate.");
        }

        field = null;
        return false;
    }

    private static string LastSegment(string fullyQualifiedName)
    {
        var dot = fullyQualifiedName.LastIndexOf('.');
        return dot < 0 ? fullyQualifiedName : fullyQualifiedName[(dot + 1)..];
    }

    private static string Normalize(string segment) => ArrayIndexSuffix().Replace(segment, string.Empty);

    [GeneratedRegex(@"\[\d+\]$")]
    private static partial Regex ArrayIndexSuffix();
}
