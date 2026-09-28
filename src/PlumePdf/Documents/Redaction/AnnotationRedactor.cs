using PlumePdf.Content;
using PlumePdf.Objects;

namespace PlumePdf.Documents.Redaction;

/// <summary>
/// Wipes the appearance streams of every annotation whose <c>/Rect</c> intersects a redaction
/// region (the over-redact-never-under-redact bias applied to annotation-rendered content):
/// annotations paint through their own <c>/AP</c> form XObjects (ISO 32000-1 §12.5.5), entirely
/// outside the page content stream <c>ContentStreamEditor</c> rewrites — a FreeText note or a
/// stamp sitting inside a redaction region would otherwise survive and render untouched. Every
/// <c>/AP</c> stream (<c>/N</c>, <c>/R</c>, <c>/D</c>, including appearance-state
/// sub-dictionaries) is emptied in place and the annotation's <c>/Contents</c> text removed;
/// each wipe records a <c>PLUME6075</c> diagnostic naming the annotation subtype and rectangle.
/// Text/pattern targets are <em>not</em> matched against <c>/AP</c> stream content in v1.0 —
/// region intersection is the only trigger here; <c>MetadataScrubber</c> separately matches
/// text targets against annotation <c>/Contents</c> strings (both limitations are documented in
/// <c>docs/cookbook/redact.md</c>).
/// </summary>
internal static class AnnotationRedactor
{
    private static readonly PdfName AnnotsName = PdfName.Get("Annots");
    private static readonly PdfName RectName = PdfName.Get("Rect");
    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName ApName = PdfName.AP;
    private static readonly PdfName SubtypeName = PdfName.Subtype;
    private static readonly PdfName[] AppearanceKeys = [PdfName.N, PdfName.Get("R"), PdfName.Get("D")];

    /// <summary>
    /// Wipes every annotation on <paramref name="page"/> whose <c>/Rect</c> intersects any of
    /// <paramref name="regions"/> (normalized page space — the same space
    /// <see cref="RedactionTargetResolver"/> resolves regions in), returning how many
    /// annotations were wiped.
    /// </summary>
    public static int WipeIntersecting(PdfDocument document, PdfPage page, IReadOnlyList<PdfRectangle> regions)
    {
        if (regions.Count == 0 || !page.Dictionary.TryGetValue(AnnotsName, out var annotsValue)
            || PageSpace.Resolve(annotsValue, document.Objects) is not PdfArray annots)
        {
            return 0;
        }

        var mediaBox = PageSpace.GetMediaBox(page.Dictionary);
        var rotate = PageSpace.GetRotation(page.Dictionary);
        var normalization = PageSpace.NormalizationMatrix(mediaBox, rotate);

        var wiped = 0;
        foreach (var entry in annots)
        {
            var annotReference = entry as PdfReference;
            if (PageSpace.Resolve(entry, document.Objects) is not PdfDictionary annot)
            {
                continue;
            }

            if (NormalizedRect(annot, normalization) is not { } rect || !IntersectsAny(rect, regions))
            {
                continue;
            }

            if (!WipeAnnotation(document, annot))
            {
                continue; // nothing to wipe (no /AP, no /Contents) — not a surviving-content risk.
            }

            if (annotReference is not null)
            {
                document.Objects.MarkDirty(annotReference.Target);
            }

            wiped++;
            var subtype = annot.TryGetValue(SubtypeName, out var st) && st is PdfName subtypeName ? subtypeName.Value : "(unknown)";
            document.Diagnostics.Add(new PdfDiagnostic(
                "PLUME6075",
                DiagnosticSeverity.Info,
                $"Redaction wiped the appearance of a /{subtype} annotation at page rect ({rect.Left:0.##}, {rect.Bottom:0.##})-({rect.Right:0.##}, {rect.Top:0.##}) because it intersects a redaction region: annotation appearance streams render outside the page content stream, so an intersecting annotation is wiped whole (over-redact, never under-redact) rather than partially edited."));
        }

        return wiped;
    }

    private static bool WipeAnnotation(PdfDocument document, PdfDictionary annot)
    {
        var touched = false;

        if (annot.TryGetValue(ApName, out var apValue) && PageSpace.ResolveDictionary(apValue, document.Objects) is { } ap)
        {
            foreach (var key in AppearanceKeys)
            {
                if (!ap.TryGetValue(key, out var appearanceValue))
                {
                    continue;
                }

                touched |= WipeAppearanceEntry(document, ap, key, appearanceValue);
            }
        }

        if (annot.Remove(ContentsName))
        {
            touched = true;
        }

        return touched;
    }

    // An /AP entry is either a form-XObject stream (directly or by reference), or an
    // appearance-state sub-dictionary mapping state names to such streams (§12.5.5). Streams
    // are emptied in place via ObjectRegistry.RegisterNew shadowing — same mechanism
    // ContentStreamEditor uses for a matched Form XObject — so a shared appearance can never
    // survive through another referrer.
    private static bool WipeAppearanceEntry(PdfDocument document, PdfDictionary container, PdfName key, PdfObject value)
    {
        switch (value)
        {
            case PdfReference reference when document.Objects[reference.Target] is PdfStream stream:
                document.Objects.RegisterNew(reference.Target, EmptiedCopy(stream));
                return true;

            case PdfReference reference when document.Objects[reference.Target] is PdfDictionary states:
                return WipeStateDictionary(document, states, reference.Target);

            case PdfStream direct:
                container.Set(key, EmptiedCopy(direct));
                return true;

            case PdfDictionary directStates:
                return WipeStateDictionary(document, directStates, null);

            default:
                return false;
        }
    }

    private static bool WipeStateDictionary(PdfDocument document, PdfDictionary states, IndirectReference? statesReference)
    {
        var touched = false;
        var directReplacements = new List<(PdfName Key, PdfStream Empty)>();

        foreach (var (stateName, stateValue) in states)
        {
            switch (stateValue)
            {
                case PdfReference reference when document.Objects[reference.Target] is PdfStream stream:
                    document.Objects.RegisterNew(reference.Target, EmptiedCopy(stream));
                    touched = true;
                    break;

                case PdfStream direct:
                    directReplacements.Add((stateName, EmptiedCopy(direct)));
                    break;
            }
        }

        foreach (var (stateName, empty) in directReplacements)
        {
            states.Set(stateName, empty);
            touched = true;
        }

        if (touched && statesReference is { } reference2)
        {
            document.Objects.MarkDirty(reference2);
        }

        return touched;
    }

    // Preserves only the structural keys a valid-but-empty form XObject needs (the same set
    // ContentStreamEditor's whole-form wipe keeps); everything else — including the original
    // stream bytes and their /Filter//Length — is dropped.
    private static PdfStream EmptiedCopy(PdfStream stream)
    {
        var dictionary = new PdfDictionary();
        foreach (var (key, value) in stream.Dictionary)
        {
            if (key.Value is "Type" or "Subtype" or "BBox" or "Matrix" or "Group")
            {
                dictionary.Set(key, value);
            }
        }

        return new PdfStream(dictionary, Array.Empty<byte>());
    }

    private static PdfRectangle? NormalizedRect(PdfDictionary annot, PdfMatrix normalization)
    {
        if (!annot.TryGetValue(RectName, out var rectValue) || rectValue is not PdfArray { Count: 4 } array
            || array[0] is not PdfNumber x1 || array[1] is not PdfNumber y1 || array[2] is not PdfNumber x2 || array[3] is not PdfNumber y2)
        {
            return null;
        }

        var p0 = normalization.Transform(x1.Value, y1.Value);
        var p1 = normalization.Transform(x2.Value, y2.Value);
        if (!double.IsFinite(p0.X) || !double.IsFinite(p0.Y) || !double.IsFinite(p1.X) || !double.IsFinite(p1.Y))
        {
            return null;
        }

        return new PdfRectangle(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y));
    }

    private static bool IntersectsAny(PdfRectangle rect, IReadOnlyList<PdfRectangle> regions)
    {
        foreach (var region in regions)
        {
            if (rect.Left < region.Right && rect.Right > region.Left && rect.Bottom < region.Top && rect.Top > region.Bottom)
            {
                return true;
            }
        }

        return false;
    }
}
