using PlumePdf.Objects;

namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// The state one full-rewrite <c>Save</c> threads through the save-time clean-up passes after
/// pages were removed. Passes never mutate the document: they read <see cref="Objects"/>, and
/// express every change as a replacement value (<see cref="Catalog"/>, <see cref="Pages"/>,
/// <see cref="Replacements"/>, objects made with <see cref="Allocate"/>) or as a number added to
/// <see cref="Excluded"/>. The writers then serialize exactly that.
/// </summary>
internal sealed class SaveCleanupContext
{
    private int _nextProvisionalNumber;

    /// <summary>Creates the context for one save.</summary>
    public SaveCleanupContext(
        ObjectRegistry objects,
        IndirectReference? catalogReference,
        PdfDictionary? catalog,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> pages,
        HashSet<int> excluded,
        HashSet<int> removedPages,
        PdfOptions options)
    {
        Objects = objects;
        CatalogReference = catalogReference;
        Catalog = catalog;
        Pages = [.. pages];
        Excluded = excluded;
        RemovedPages = removedPages;
        Options = options;
        _nextProvisionalNumber = objects.NextFreshObjectNumber;
    }

    /// <summary>The document's object graph — read only.</summary>
    public ObjectRegistry Objects { get; }

    /// <summary>The catalog's identity, when it has one.</summary>
    public IndirectReference? CatalogReference { get; }

    /// <summary>The catalog to write. A pass that changes it assigns a modified copy here; the document's own catalog dictionary is never edited.</summary>
    public PdfDictionary? Catalog { get; set; }

    /// <summary>The kept pages to write, in order. A pass that changes a page replaces its entry with a modified copy of the dictionary.</summary>
    public List<(IndirectReference Reference, PdfDictionary Dictionary)> Pages { get; }

    /// <summary>Object numbers the writers must exclude (never followed, written as <c>null</c>). Seeded by <see cref="RemovedSetBuilder"/>; a pass adds every object it drops, so a dropped object cannot return through a reference the pass did not see.</summary>
    public HashSet<int> Excluded { get; }

    /// <summary>The object numbers of the removed pages (a subset of <see cref="Excluded"/>).</summary>
    public IReadOnlySet<int> RemovedPages { get; }

    /// <summary>Replacement values for original objects other than the catalog and the kept pages, plus the objects <see cref="Allocate"/> made.</summary>
    public Dictionary<int, PdfObject> Replacements { get; } = [];

    /// <summary>Form fields (terminal or not) the clean-up removed — the signature pre-scan does not count a signature field in here as invalidated.</summary>
    public HashSet<int> RemovedFields { get; } = [];

    /// <summary>What the clean-up removed, by class.</summary>
    public SaveCleanupCounts Counts { get; } = new();

    /// <summary>The effective options of this save (walk caps).</summary>
    public PdfOptions Options { get; }

    /// <summary>
    /// Makes a new indirect object for this save only: its number is above the registry's range
    /// and the registry itself is untouched. The writers discover it like any other object when
    /// something they write references it, and garbage-collect it otherwise.
    /// </summary>
    public IndirectReference Allocate(PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var reference = new IndirectReference(_nextProvisionalNumber++, 0);
        Replacements[reference.Number] = value;
        return reference;
    }
}

/// <summary>What one save's clean-up removed, by class.</summary>
internal sealed class SaveCleanupCounts
{
    /// <summary>Outline items deleted.</summary>
    public int Bookmarks { get; set; }

    /// <summary>Form fields removed (value included).</summary>
    public int Fields { get; set; }

    /// <summary>Widget annotations removed from fields that were kept.</summary>
    public int Widgets { get; set; }

    /// <summary>Link annotations on kept pages removed.</summary>
    public int Links { get; set; }

    /// <summary>1 when the catalog's open action was removed.</summary>
    public int OpenActions { get; set; }

    /// <summary>Named destinations removed.</summary>
    public int NamedDestinations { get; set; }

    /// <summary>Structure elements pruned.</summary>
    public int StructureElements { get; set; }

    /// <summary>1 when the form's XFA data was dropped.</summary>
    public int XfaForms { get; set; }

    /// <summary>What was removed, by kind, as a short English list ("2 bookmarks, 1 form field").</summary>
    public string Describe()
    {
        var parts = new List<string>();
        void Add(int count, string singular, string plural)
        {
            if (count > 0)
            {
                parts.Add($"{count} {(count == 1 ? singular : plural)}");
            }
        }

        Add(Bookmarks, "bookmark", "bookmarks");
        Add(Fields, "form field", "form fields");
        Add(Widgets, "widget", "widgets");
        Add(Links, "link", "links");
        Add(OpenActions, "open action", "open actions");
        Add(NamedDestinations, "named destination", "named destinations");
        Add(StructureElements, "structure element", "structure elements");
        Add(XfaForms, "XFA form", "XFA forms");
        return string.Join(", ", parts);
    }

    /// <summary>Whether the clean-up removed anything at all.</summary>
    public bool Any => Bookmarks + Fields + Widgets + Links + OpenActions + NamedDestinations + StructureElements + XfaForms > 0;
}
