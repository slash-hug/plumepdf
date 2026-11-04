using PlumePdf.Documents;
using PlumePdf.Objects;

namespace PlumePdf;

/// <summary>
/// Builds a new synthetic, in-memory <see cref="PdfDocument"/> out of pages imported from
/// one or more source documents — the shared machinery behind <see cref="Pdf.Merge(PdfDocument[])"/>
/// and <see cref="Pdf.Split(PdfDocument)"/>. The pages and everything they reference are
/// deep-copied by <see cref="PageGraphCopier"/> into a <see cref="FreshDocumentTarget"/>; this
/// class adds the new document's catalog, its flat <c>/Pages</c> root and the merged
/// <c>/AcroForm</c>.
/// </summary>
internal static class PageImporter
{
    private static readonly PdfName ParentName = PdfName.Get("Parent");
    private static readonly PdfName PagesName = PdfName.Get("Pages");
    private static readonly PdfName KidsName = PdfName.Get("Kids");
    private static readonly PdfName CountName = PdfName.Get("Count");

    /// <summary>Composes a new document whose pages are <paramref name="pages"/>, imported in order.</summary>
    public static PdfDocument Compose(IEnumerable<(PdfDocument Source, IndirectReference PageReference, PdfDictionary PageDictionary)> pages)
    {
        var pageList = pages.ToList();
        foreach (var (source, _, _) in pageList)
        {
            // Composing pages out of an encrypted source would
            // produce an unencrypted copy of restricted content — the same silent
            // decrypt-on-save Save/SaveIncremental refuse (PLUME5001/PLUME5002), so the
            // merge/split path refuses identically rather than downgrading to a warning.
            if (source.HasEncryptedSource)
            {
                throw new PlumePdfException("PLUME6012", "Pdf.Merge/Pdf.Split cannot compose pages from a document opened from an encrypted source (Phase 1 does not support encryption write).");
            }
        }

        var diagnostics = new DiagnosticCollection();
        // A page imported more than once comes from a further occurrence of its document, copied
        // independently (see ImportSource).
        var occurrences = new Dictionary<(PdfDocument, int), int>();
        var imports = pageList.ConvertAll(page =>
        {
            var key = (page.Source, page.PageReference.Number);
            var occurrence = occurrences.TryGetValue(key, out var seen) ? seen : 0;
            occurrences[key] = occurrence + 1;
            return (Source: new ImportSource(page.Source, occurrence), page.PageReference, page.PageDictionary);
        });

        var preparations = PageGraphCopier.Prepare(imports);
        // One entry per distinct report: repeated occurrences of a document usually leave out the same.
        foreach (var report in preparations.Values.Where(static p => p.Counts.Any).Select(static p => p.Counts.Describe()).Distinct(StringComparer.Ordinal))
        {
            diagnostics.Add(new PdfDiagnostic("PLUME5021", DiagnosticSeverity.Info, $"Left out what pointed at pages that were not imported: {report}."));
        }

        var target = new FreshDocumentTarget();
        var catalogReference = target.Allocate();
        var pagesReference = target.Allocate();
        var copier = new PageGraphCopier(target, preparations, ImportPolicy.Merge);

        var kidsReferences = new List<IndirectReference>();
        foreach (var (source, pageReference, _) in imports)
        {
            kidsReferences.Add(copier.ReservePage(source, pageReference));
        }

        // Carry /AcroForm through the merge — field-array
        // union (deterministic collision rename), /DR handling, /SigFlags|/NeedAppearances
        // reconciliation. Runs before the copier drains so a field object that a page's
        // own /Annots also reaches (the merged field+widget case — the norm) gets reserved
        // here first, with this call's rename Transform attached; the page-side Reserve call
        // reached during the drain then just finds the existing map entry (same dedup path
        // pages already share for fonts/images) rather than racing it — no orphaned widgets,
        // no duplicate copies of the same original object.
        var mergedAcroForm = AcroFormMerger.Merge(imports, copier.ImportValue, copier.Reserve, copier.IsExcluded);

        copier.Drain();

        var merged = target.Objects;
        foreach (var kidReference in kidsReferences)
        {
            if (merged.TryGetValue(kidReference.Number, out var value) && value is PdfDictionary pageDict)
            {
                pageDict.Set(ParentName, new PdfReference(pagesReference));
            }
        }

        var pagesDict = new PdfDictionary();
        pagesDict.Set(PdfName.Type, PagesName);
        pagesDict.Set(KidsName, new PdfArray(kidsReferences.ConvertAll(static r => (PdfObject)new PdfReference(r))));
        pagesDict.Set(CountName, PdfNumber.Get(kidsReferences.Count));
        target.Store(pagesReference, pagesDict);

        var catalogDict = new PdfDictionary();
        catalogDict.Set(PdfName.Type, PdfName.Get("Catalog"));
        catalogDict.Set(PagesName, new PdfReference(pagesReference));
        if (mergedAcroForm is not null)
        {
            var acroFormReference = target.Allocate();
            target.Store(acroFormReference, mergedAcroForm);
            catalogDict.Set(AcroFormNames.AcroForm, new PdfReference(acroFormReference));
        }

        target.Store(catalogReference, catalogDict);

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(target.Size));
        trailer.Set(PdfName.Root, new PdfReference(catalogReference));

        return PdfDocument.CreateSynthetic(new InMemoryObjectSource(trailer, merged), diagnostics);
    }
}
