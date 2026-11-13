using PlumePdf.Documents;
using PlumePdf.Documents.Signing;
using PlumePdf.Objects;

namespace PlumePdf;

/// <summary>
/// Adds a page to an opened document (<see cref="PageCollection.Insert(int, PdfPage)"/>). It runs
/// in three steps:
/// <list type="number">
/// <item><b>Refuse.</b> Everything it must refuse, before anything is read for the copy.</item>
/// <item><b>Stage.</b> The page and everything it references are copied into a
/// <see cref="StagingTarget"/> under provisional numbers, and the form merge is planned
/// (<see cref="AcroFormMerger.MergeInto"/>). Nothing in the destination changes, so a failure
/// here leaves it as it was.</item>
/// <item><b>Commit.</b> Under the destination's mutation guard: real numbers are allocated,
/// the copies registered, the form plan applied and the page put into the collection. Nothing
/// in this step reads the registry or can fail on document content.</item>
/// </list>
/// A page of the destination itself takes <see cref="SameDocumentPageDuplicator"/> instead.
/// </summary>
internal static class PageAddEngine
{
    private static readonly PdfName PagesName = PdfName.Get("Pages");
    private static readonly PdfName ParentName = PdfName.Get("Parent");
    private static readonly PdfName PermsName = PdfName.Get("Perms");
    private static readonly PdfName Ur3Name = PdfName.Get("UR3");

    /// <summary>Copies <paramref name="page"/> into <paramref name="destination"/> at <paramref name="index"/> and returns the new page.</summary>
    internal static PdfPage Insert(PdfDocument destination, int index, PdfPage page)
    {
        var source = page.Owner!;
        if (ReferenceEquals(source, destination))
        {
            return SameDocumentPageDuplicator.Insert(destination, index, page);
        }

        if (source.HasEncryptedSource && !destination.HasEncryptedSource)
        {
            throw new PlumePdfException("PLUME6012", "PageCollection.Add/Insert cannot copy a page from a document opened from an encrypted source into a document that is not encrypted: the copy would be saved unencrypted. Add it to an encrypted document instead.");
        }

        var preflight = Preflight(destination);

        var importSource = new ImportSource(source, 0);
        var staging = new StagingTarget();
        var copier = new PageGraphCopier(staging, PageGraphCopier.Prepare([(importSource, page.Reference, page.Dictionary)]), ImportPolicy.PageAdd);
        var provisionalPage = copier.ReservePage(importSource, page.Reference);
        var formPlan = AcroFormMerger.MergeInto(destination, importSource, page.Reference, page.Dictionary, copier, staging);
        copier.Drain();

        var pageDictionary = (PdfDictionary)staging.Objects[provisionalPage.Number];
        pageDictionary.Set(ParentName, new PdfReference(preflight.PagesReference));
        var sourceTagged = DestinationClaims.Read(source).IsTagged;

        var added = Commit(destination, index, staging, provisionalPage, pageDictionary, formPlan);

        Report(destination, preflight, sourceTagged, formPlan);
        return added;
    }

    /// <summary>What both page-add paths check on the destination before copying anything.</summary>
    /// <param name="PagesReference">The catalog's top <c>/Pages</c> reference, the new page's <c>/Parent</c>.</param>
    /// <param name="Claims">What the destination claims about itself.</param>
    /// <param name="Certification">The DocMDP certification, when there is one: its field and P-value.</param>
    /// <param name="HasUsageRights">The catalog carries <c>/Perms /UR3</c>.</param>
    internal sealed record PreflightResult(IndirectReference PagesReference, DestinationClaims Claims, (string? FieldName, int PValue)? Certification, bool HasUsageRights);

    /// <summary>
    /// Refuses a destination a page can't be added to (<c>PLUME6097</c>), and a certified one under
    /// <see cref="PdfOptions.Strict"/> (<c>PLUME6092</c>); otherwise returns what the commit and the
    /// diagnostics need.
    /// </summary>
    internal static PreflightResult Preflight(PdfDocument destination)
    {
        var claims = DestinationClaims.Read(destination);
        if (!claims.HasIndirectPages || destination.Catalog!.Dictionary[PagesName] is not PdfReference pagesReference)
        {
            throw new PlumePdfException("PLUME6097", "This document's catalog has no indirect /Pages reference, so a page can't be added under it.");
        }

        var hasUsageRights = destination.Catalog.Dictionary.TryGetValue(PermsName, out var permsValue)
            && AcroFormReader.Resolve(destination.Objects, permsValue) is PdfDictionary perms
            && perms.ContainsKey(Ur3Name);

        var (field, p) = DocMdpCertification.Find(destination);
        (string?, int)? certification = p is int pValue ? (field, pValue) : null;
        if (certification is { } certified && destination.Options.Strict)
        {
            throw new PlumePdfException("PLUME6092", $"{CertificationText(certified, hasUsageRights)} — refused (PdfOptions.Strict).");
        }

        return new PreflightResult(pagesReference.Target, claims, certification, hasUsageRights);
    }

    /// <summary>
    /// Registers the staged objects, applies <paramref name="formPlan"/> (when there is one) and puts
    /// the page into the collection, all under the destination's mutation guard.
    /// </summary>
    internal static PdfPage Commit(PdfDocument destination, int index, StagingTarget staging, IndirectReference provisionalPage, PdfDictionary pageDictionary, FormMergePlan? formPlan)
    {
        var objects = destination.Objects;
        objects.EnterExternalMutation();
        try
        {
            staging.Commit(objects);
            if (formPlan is not null)
            {
                formPlan.Rewrite(staging.Rewrite);
                formPlan.Apply();
                foreach (var dirty in formPlan.Dirty)
                {
                    objects.MarkDirty(dirty);
                }
            }

            var reference = staging.RealReferenceOf(provisionalPage);
            var added = new PdfPage(reference, pageDictionary, destination);
            destination.Pages.InsertCommitted(index, added);
            destination.MarkPagesTreeDirty();
            destination.RecordAddedPage(reference);
            return added;
        }
        finally
        {
            objects.ExitExternalMutation();
        }
    }

    /// <summary>Adds the diagnostics a successful page-add reports, each kind once per document (renames once each).</summary>
    internal static void Report(PdfDocument destination, PreflightResult preflight, bool sourceTagged, FormMergePlan? formPlan)
    {
        if (preflight.Certification is { } certified)
        {
            ReportOnce(destination, "PLUME6092", DiagnosticSeverity.Warning, $"{CertificationText(certified, preflight.HasUsageRights)}. The page was added anyway (advisory); PdfOptions.Strict refuses instead.");
        }

        if (sourceTagged)
        {
            ReportOnce(destination, "PLUME6093", DiagnosticSeverity.Info, "A copied page came from a tagged document. Its structure tags are not carried, so the added page is untagged.");
        }

        var claims = preflight.Claims;
        if (claims.HasConformanceClaims)
        {
            var what = new List<string>();
            if (claims.IsTagged)
            {
                what.Add("tagged");
            }

            if (claims.PdfAPart is { } part)
            {
                what.Add($"claims PDF/A-{part}");
            }

            if (claims.ClaimsPdfUa)
            {
                what.Add("claims PDF/UA");
            }

            ReportOnce(destination, "PLUME6095", DiagnosticSeverity.Info, $"This document is {string.Join(" and ", what)}. The added page is untagged, and the document's conformance claims are not checked again.");
        }

        if (claims.HasXfa)
        {
            ReportOnce(
                destination,
                "PLUME6096",
                claims.NeedsRendering ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                claims.NeedsRendering
                    ? "This document has a dynamic XFA form (/NeedsRendering true). A copied page's fields are added to its AcroForm but not to the XFA form, and an XFA-aware viewer, which renders the document from the XFA form, may not show the added page at all."
                    : "This document has an XFA form. A copied page's fields are added to its AcroForm but not to the XFA form, which XFA-aware viewers render the form from.");
        }

        if (!destination.Permissions.HasFlag(PdfPermissions.AssembleDocument))
        {
            ReportOnce(destination, "PLUME6098", DiagnosticSeverity.Info, "This document's /Encrypt permissions clear the 'assemble the document' bit (bit 11). PlumePDF treats /P as advisory and added the page anyway; see PdfDocument.Permissions.");
        }

        foreach (var (from, to) in formPlan?.Renames ?? [])
        {
            destination.Diagnostics.Add(new PdfDiagnostic("PLUME6099", DiagnosticSeverity.Info, $"The copied form field '{from}' was renamed '{to}' because this document already has a field with that name."));
        }

        foreach (var (from, to) in formPlan?.ResourceRenames ?? [])
        {
            destination.Diagnostics.Add(new PdfDiagnostic("PLUME6099", DiagnosticSeverity.Info, $"The copied form resource /{from} was added as /{to} because this document's form already uses /{from} for a different resource; the copied fields' /DA strings name /{to}."));
        }
    }

    private static string CertificationText((string? FieldName, int PValue) certified, bool hasUsageRights) =>
        $"'{certified.FieldName}' certifies this document at DocMDP P={certified.PValue} ({DocMdpCertification.DescribeP(certified.PValue)}), and adding a page changes it after certification"
        + (hasUsageRights ? "; it also invalidates the document's /Perms /UR3 usage rights" : string.Empty);

    internal static void ReportOnce(PdfDocument document, string code, DiagnosticSeverity severity, string message)
    {
        if (!document.Diagnostics.Any(d => d.Code == code))
        {
            document.Diagnostics.Add(new PdfDiagnostic(code, severity, message));
        }
    }
}
