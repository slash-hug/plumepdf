using PlumePdf.Objects;

namespace PlumePdf.Documents.PageRemoval;

/// <summary>Small read-and-copy helpers the save-time clean-up passes share: they never mutate what they are given.</summary>
internal static class FormObjects
{
    private const int MaxReferenceChain = 8;

    /// <summary>Follows <paramref name="value"/> through at most a few references to a direct value (resolve before type-checking).</summary>
    public static PdfObject? Resolve(ObjectRegistry objects, PdfObject? value)
    {
        var hops = 0;
        while (value is PdfReference reference && hops++ < MaxReferenceChain)
        {
            value = objects[reference.Target];
        }

        return value;
    }

    /// <summary>A shallow copy of <paramref name="dictionary"/>: same entries in the same order, values shared.</summary>
    public static PdfDictionary Copy(PdfDictionary dictionary)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in dictionary)
        {
            copy.Set(key, value);
        }

        return copy;
    }
}
