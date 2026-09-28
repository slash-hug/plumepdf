using System.Text;
using PlumePdf.Content;
using PlumePdf.Documents;
using PlumePdf.Fonts;
using PlumePdf.Fonts.Standard14;
using PlumePdf.Objects;

namespace PlumePdf;

/// <summary>
/// Stamps an already-opened document — closing the
/// v1.0 opened-document-stamping commitment an earlier phase deferred to "the Phase 3/4 writer
/// redesign" (built on the <see cref="ObjectRegistry.MarkDirty"/>/<see cref="ObjectRegistry.AllocateNumber"/>
/// machinery). Root-namespace internal orchestrator above the layer
/// namespaces (the <c>PageImporter</c> precedent): it consumes <c>Elements.Stamp</c> (the
/// same descriptor <c>Section.Stamps</c> uses for composed documents), which sits above the
/// <c>Documents</c> layer namespace and therefore cannot be referenced from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Additive by design</b> — the property that makes stamping legal through
/// <c>SaveIncremental</c> where redaction is not: nothing existing is
/// rewritten. Each stamped page's <c>/Contents</c> becomes
/// <c>[prefix, …original streams…, suffix]</c>, where <c>prefix</c> saves the page's initial
/// graphics state (<c>q</c>) and <c>suffix</c> restores it (<c>Q</c>) before painting the
/// stamp — so a dangling transform/color left by the original content cannot displace or
/// recolor the stamp. A single <c>q</c>/<c>Q</c> pair is <em>not</em> enough against an
/// UNBALANCED original stream (a dangling <c>q</c> makes the suffix's lone <c>Q</c> restore
/// the polluted state, not the initial one; a stray <c>Q</c> can pop the prefix's own save),
/// so the original streams are scanned (read-only — still additive) and the wrap is sized to
/// their actual net <c>q</c>/<c>Q</c> balance: enough prefix saves that a stray <c>Q</c> can
/// never underflow past the initial state, and exactly enough suffix restores to land back on
/// it. Original content streams are referenced, never touched; an incremental save appends
/// only the new streams, the font/ExtGState objects, and the updated page dictionaries,
/// leaving every prior-revision byte (including any signature's signed range) intact.
/// </para>
/// <para>
/// The stamp text draws in Standard-14 Helvetica-Bold (WinAnsi encoding, nothing embedded —
/// matching the composed-document <c>DrawStamp</c> exactly), marked as a pagination
/// <c>/Artifact</c> per ISO 32000-1 §14.8.2.2. Geometry compensates for the page's
/// <c>/Rotate</c>: positions are computed in visual page space (the same post-rotation space
/// Phase 3 extraction reports coordinates in) and mapped back into raw user space with the
/// inverse of <see cref="PageSpace.NormalizationMatrix"/>, so the stamp reads upright and
/// anchors to the visual corner regardless of page rotation.
/// </para>
/// <para>
/// Page dictionaries are mutated <em>twice</em>, deliberately: once on the
/// <see cref="PdfPage.Dictionary"/> instance (the inherited-attribute-merged copy the
/// full-rewrite writer serializes pages from) and once on the registry's own original object
/// (what <c>SaveIncremental</c>'s dirty-object set serializes) — the two writers read page
/// state from different sources, and updating only one would silently drop the stamp on the
/// other save path.
/// </para>
/// </remarks>
internal static class StampEngine
{
    /// <summary>The fixed inset, in points, between the page's visual edge and the stamp's anchor — the opened-document equivalent of the section margins the composed-document stamp anchors inside (a plain opened page has no margins to reuse).</summary>
    private const double EdgeInset = 36;

    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName ResourcesName = PdfName.Get("Resources");
    private static readonly PdfName FontName = PdfName.Get("Font");
    private static readonly PdfName ExtGStateName = PdfName.Get("ExtGState");
    private static readonly byte[] PrefixBytes = "q\n"u8.ToArray();
    private static readonly byte[] SuffixLeadInBytes = "Q\n"u8.ToArray();

    /// <summary>Applies <paramref name="stamp"/> to <paramref name="pageIndexes"/> (or every page when <see langword="null"/>) — see <see cref="PdfDocument.Stamp"/> for the public contract.</summary>
    public static void Stamp(PdfDocument document, Elements.Stamp stamp, IReadOnlyList<int>? pageIndexes)
    {
        if (document.Pages.Count == 0)
        {
            throw new PlumePdfException("PLUME6073", "Stamp requires at least one page — this document has none. (A zero-page document is rare but legal; there is nothing to paint a stamp onto.)");
        }

        if (pageIndexes is not null)
        {
            foreach (var index in pageIndexes)
            {
                if (index < 0 || index >= document.Pages.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(pageIndexes), index, $"This document has {document.Pages.Count} page(s); page index {index} is out of range.");
                }
            }
        }

        if (!Standard14Font.TryGet("Helvetica-Bold", out var font))
        {
            throw new InvalidOperationException("Helvetica-Bold is a Standard-14 font and always resolves; Standard14Font.TryGet returning false here is a PlumePDF bug.");
        }

        var encoded = EncodeWinAnsi(stamp.Text, font);
        var textWidthEm = 0.0;
        foreach (var code in encoded)
        {
            textWidthEm += font.GetAdvanceWidth(code);
        }

        var textWidth = textWidthEm * stamp.FontSize / 1000.0;

        // One font object (and, when translucent, one ExtGState) shared by every page this
        // call stamps — allocation order is page-independent, keeping Deterministic output
        // stable regardless of which pages were selected.
        var fontReference = FontObjectBuilder.BuildStandard14(font, value =>
        {
            var reference = document.Objects.AllocateNumber();
            document.Objects.RegisterNew(reference, value);
            return reference;
        });

        IndirectReference? extGStateReference = null;
        if (stamp.Opacity < 1)
        {
            var gs = new PdfDictionary();
            gs.Set(PdfName.Type, PdfName.Get("ExtGState"));
            gs.Set(PdfName.Get("ca"), PdfNumber.Get(stamp.Opacity));
            gs.Set(PdfName.Get("CA"), PdfNumber.Get(stamp.Opacity));
            var reference = document.Objects.AllocateNumber();
            document.Objects.RegisterNew(reference, gs);
            extGStateReference = reference;
        }

        // One shared single-"q" prefix stream for the common (balanced-content) case —
        // identical bytes on every page, so one object serves them all (content streams in a
        // /Contents array may be shared freely, §7.8.2). A page whose original content is
        // q/Q-unbalanced gets its own wider prefix from StampPage instead.
        var prefixReference = document.Objects.AllocateNumber();
        document.Objects.RegisterNew(prefixReference, new PdfStream(new PdfDictionary(), PrefixBytes));

        if (pageIndexes is null)
        {
            for (var i = 0; i < document.Pages.Count; i++)
            {
                StampPage(document, document.Pages[i], stamp, encoded, textWidth, fontReference, extGStateReference, prefixReference);
            }
        }
        else
        {
            foreach (var index in pageIndexes)
            {
                StampPage(document, document.Pages[index], stamp, encoded, textWidth, fontReference, extGStateReference, prefixReference);
            }
        }
    }

    private static void StampPage(
        PdfDocument document,
        PdfPage page,
        Elements.Stamp stamp,
        byte[] encodedText,
        double textWidth,
        IndirectReference fontReference,
        IndirectReference? extGStateReference,
        IndirectReference prefixReference)
    {
        var mediaBox = PageSpace.GetMediaBox(page.Dictionary);
        var rotate = PageSpace.GetRotation(page.Dictionary);

        // Visual page space (post-/Rotate, the space Phase 3 extraction reports in): a 90/270
        // rotation swaps the displayed width and height.
        var boxWidth = mediaBox.Urx - mediaBox.Llx;
        var boxHeight = mediaBox.Ury - mediaBox.Lly;
        var (visualWidth, visualHeight) = rotate is 90 or 270 ? (boxHeight, boxWidth) : (boxWidth, boxHeight);

        var (x, y) = stamp.Position switch
        {
            Elements.StampPosition.TopLeft => (EdgeInset, visualHeight - EdgeInset - stamp.FontSize),
            Elements.StampPosition.TopRight => (visualWidth - EdgeInset - textWidth, visualHeight - EdgeInset - stamp.FontSize),
            Elements.StampPosition.BottomLeft => (EdgeInset, EdgeInset),
            _ => (visualWidth - EdgeInset - textWidth, EdgeInset),
        };

        // Map visual page space back into raw user space: the inverse of
        // PageSpace.NormalizationMatrix(mediaBox, rotate), derived analytically per quadrant
        // rotation. Emitting it as a `cm` lets everything after it position in visual space.
        var (a, b, c, d, e, f) = InverseNormalization(mediaBox, rotate);

        var existingResources = ResolveDictionary(page.Dictionary.TryGetValue(ResourcesName, out var resourcesValue) ? resourcesValue : null, document.Objects);
        var fontResourceName = FindFreeResourceName(existingResources, FontName, document.Objects, "PlmStampF");
        var gsResourceName = extGStateReference is null ? null : FindFreeResourceName(existingResources, ExtGStateName, document.Objects, "PlmStampGS");

        var builder = new ContentStreamBuilder();
        builder.BeginArtifact();
        builder.SaveState();
        builder.Transform(a, b, c, d, e, f);
        if (gsResourceName is not null)
        {
            builder.ApplyExtGState(gsResourceName);
        }

        builder.SetFillRgb(stamp.Color.Red, stamp.Color.Green, stamp.Color.Blue);
        builder.BeginText();
        builder.SetFont(fontResourceName, stamp.FontSize);
        builder.SetTextMatrix(1, 0, 0, 1, x, y);
        builder.ShowText(encodedText);
        builder.EndText();
        builder.RestoreState();
        builder.EndMarkedContent();
        var stampOps = builder.Build();

        // Size the q/Q wrap to the original streams' ACTUAL net balance (see the class
        // remarks): a lone prefix q + suffix Q survives only a balanced original — a dangling
        // `... cm q` would make one Q restore the polluted state (stamp lands off-page), and a
        // stray leading Q would pop the prefix's save. prefixSaves guarantees a stray Q can
        // never underflow past the initial state; suffixRestores lands the stamp back on it.
        var (prefixSaves, suffixRestores) = ComputeWrapBalance(document, page.Dictionary);
        var pagePrefixReference = prefixReference;
        if (prefixSaves > 1)
        {
            var widePrefix = new byte[prefixSaves * PrefixBytes.Length];
            for (var i = 0; i < prefixSaves; i++)
            {
                PrefixBytes.CopyTo(widePrefix, i * PrefixBytes.Length);
            }

            pagePrefixReference = document.Objects.AllocateNumber();
            document.Objects.RegisterNew(pagePrefixReference, new PdfStream(new PdfDictionary(), widePrefix));
        }

        // The suffix stream leads with the `Q`s restoring the prefix's saved initial state, so
        // the stamp always paints from the page's initial graphics state, whatever the original
        // content left dangling.
        var suffixBytes = new byte[(suffixRestores * SuffixLeadInBytes.Length) + stampOps.Length];
        for (var i = 0; i < suffixRestores; i++)
        {
            SuffixLeadInBytes.CopyTo(suffixBytes, i * SuffixLeadInBytes.Length);
        }

        stampOps.CopyTo(suffixBytes, suffixRestores * SuffixLeadInBytes.Length);
        var suffixReference = document.Objects.AllocateNumber();
        document.Objects.RegisterNew(suffixReference, new PdfStream(new PdfDictionary(), suffixBytes));

        var newContents = BuildContentsArray(page.Dictionary.TryGetValue(ContentsName, out var contentsValue) ? contentsValue : null, document.Objects, pagePrefixReference, suffixReference);
        var newResources = BuildResources(existingResources, fontResourceName, fontReference, gsResourceName, extGStateReference, document.Objects);

        // Mutate BOTH page-dictionary views (see this type's remarks): the merged copy the
        // full-rewrite writer serializes, and the registry's original object the incremental
        // writer's dirty set serializes.
        page.Dictionary.Set(ContentsName, newContents);
        page.Dictionary.Set(ResourcesName, newResources);
        if (document.Objects[page.Reference] is PdfDictionary original && !ReferenceEquals(original, page.Dictionary))
        {
            original.Set(ContentsName, newContents);
            original.Set(ResourcesName, newResources);
        }

        document.Objects.MarkDirty(page.Reference);
    }

    /// <summary>
    /// Computes how many <c>q</c>s the prefix stream needs and how many <c>Q</c>s the suffix
    /// stream must emit so the stamp always paints from the page's initial graphics state,
    /// whatever the original streams' own (possibly unbalanced) <c>q</c>/<c>Q</c> usage does:
    /// simulating the original streams' net stack effect, <c>PrefixSaves = 1 - minDepth</c>
    /// (never fewer than one, one extra per stray <c>Q</c> that would otherwise pop the wrap's
    /// own save) and <c>SuffixRestores = PrefixSaves + finalDepth</c> (one per surviving save
    /// on the stack when the suffix begins). Original streams are only read — never modified —
    /// so stamping stays purely additive. A stream that fails to filter-decode contributes
    /// nothing to the count (the same leniency the extraction walk applies); a hostile
    /// operator-count bomb propagates <c>ContentStreamReader</c>'s <c>PLUME7010</c>
    /// resource-limit refusal.
    /// </summary>
    private static (int PrefixSaves, int SuffixRestores) ComputeWrapBalance(PdfDocument document, PdfDictionary pageDictionary)
    {
        var content = ReadOriginalContentBytes(document, pageDictionary);
        if (content.Length == 0)
        {
            return (1, 1);
        }

        var operations = ContentStreamReader.Read(content, document.Options, diagnostics: null, document.Options.MaxContentStreamOperators);
        var depth = 0;
        var minDepth = 0;
        foreach (var operation in operations)
        {
            if (operation.Operator == "q")
            {
                depth++;
            }
            else if (operation.Operator == "Q")
            {
                depth--;
                minDepth = Math.Min(minDepth, depth);
            }
        }

        var prefixSaves = 1 - minDepth;
        return (prefixSaves, prefixSaves + depth);
    }

    private static byte[] ReadOriginalContentBytes(PdfDocument document, PdfDictionary pageDictionary)
    {
        if (!pageDictionary.TryGetValue(ContentsName, out var contents))
        {
            return [];
        }

        var streams = PageContents.ResolveStreams(contents, document.Objects);
        if (streams.Count == 0)
        {
            return [];
        }

        using var buffer = new MemoryStream();
        foreach (var stream in streams)
        {
            byte[] decoded;
            try
            {
                decoded = stream.GetDecodedBytes(document.Options.Filters, document.Options, r => document.Objects[r]);
            }
            catch (PlumePdfException)
            {
                continue;
            }

            buffer.Write(decoded);
            buffer.WriteByte((byte)'\n');
        }

        return buffer.ToArray();
    }

    private static PdfArray BuildContentsArray(PdfObject? existing, ObjectRegistry objects, IndirectReference prefixReference, IndirectReference suffixReference)
    {
        var array = new PdfArray();
        array.Add(new PdfReference(prefixReference));

        // An existing /Contents that is an indirect reference to an ARRAY must be spliced
        // element-by-element (the LiveCycle/AEM-style reference-to-array page shape): keeping the reference whole would nest an
        // array inside the new /Contents array, which §7.7.3.3 does not allow. A reference to a
        // stream stays a single element, preserving its indirection.
        var resolvedExisting = existing is PdfReference existingReference && objects[existingReference.Target] is PdfArray referencedArray
            ? referencedArray
            : existing;
        switch (resolvedExisting)
        {
            case PdfReference single:
                array.Add(single);
                break;

            case PdfArray existingArray:
                foreach (var item in existingArray)
                {
                    array.Add(item);
                }

                break;
        }

        array.Add(new PdfReference(suffixReference));
        return array;
    }

    /// <summary>
    /// Builds the page's new direct <c>/Resources</c> dictionary: a shallow copy of the
    /// effective (possibly inherited, possibly indirect) resources, with the stamp's font —
    /// and ExtGState, when translucent — added under collision-free names. Shallow-copying
    /// into a fresh direct dictionary (rather than mutating the resolved one in place) matters
    /// because the source dictionary may be an indirect object shared across pages, or an
    /// ancestor's inherited value: mutating it would leak the stamp resources onto pages this
    /// call never touched.
    /// </summary>
    private static PdfDictionary BuildResources(
        PdfDictionary? existing,
        string fontResourceName,
        IndirectReference fontReference,
        string? gsResourceName,
        IndirectReference? extGStateReference,
        ObjectRegistry objects)
    {
        var resources = new PdfDictionary();
        if (existing is not null)
        {
            foreach (var (key, value) in existing)
            {
                resources.Set(key, value);
            }
        }

        var fonts = CopySubDictionary(resources, FontName, objects);
        fonts.Set(PdfName.Get(fontResourceName), new PdfReference(fontReference));
        resources.Set(FontName, fonts);

        if (gsResourceName is not null && extGStateReference is { } gsReference)
        {
            var extGStates = CopySubDictionary(resources, ExtGStateName, objects);
            extGStates.Set(PdfName.Get(gsResourceName), new PdfReference(gsReference));
            resources.Set(ExtGStateName, extGStates);
        }

        return resources;
    }

    private static PdfDictionary CopySubDictionary(PdfDictionary resources, PdfName key, ObjectRegistry objects)
    {
        var copy = new PdfDictionary();
        if (resources.TryGetValue(key, out var value) && ResolveDictionary(value, objects) is { } existing)
        {
            foreach (var (entryKey, entryValue) in existing)
            {
                copy.Set(entryKey, entryValue);
            }
        }

        return copy;
    }

    private static string FindFreeResourceName(PdfDictionary? resources, PdfName category, ObjectRegistry objects, string prefix)
    {
        var existing = resources is not null && resources.TryGetValue(category, out var value)
            ? ResolveDictionary(value, objects)
            : null;

        if (existing is null)
        {
            return prefix + "0";
        }

        for (var i = 0; ; i++)
        {
            var candidate = prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!existing.ContainsKey(PdfName.Get(candidate)))
            {
                return candidate;
            }
        }
    }

    private static PdfDictionary? ResolveDictionary(PdfObject? value, ObjectRegistry objects) => value switch
    {
        PdfDictionary direct => direct,
        PdfReference reference => objects[reference.Target] as PdfDictionary,
        _ => null,
    };

    /// <summary>
    /// The analytic inverse of <see cref="PageSpace.NormalizationMatrix"/> for each of the four
    /// legal <c>/Rotate</c> quadrants — maps visual (post-rotation, origin at the displayed
    /// bottom-left) page space back into raw user space.
    /// </summary>
    private static (double A, double B, double C, double D, double E, double F) InverseNormalization(
        (double Llx, double Lly, double Urx, double Ury) mediaBox, int rotate)
    {
        var (llx, lly, urx, ury) = mediaBox;
        var width = urx - llx;
        var height = ury - lly;

        return rotate switch
        {
            90 => (0, 1, -1, 0, width + llx, lly),
            180 => (-1, 0, 0, -1, width + llx, height + lly),
            270 => (0, -1, 1, 0, llx, height + lly),
            _ => (1, 0, 0, 1, llx, lly),
        };
    }

    /// <summary>
    /// Encodes <paramref name="text"/> to the Standard-14 font's own single-byte codes
    /// (WinAnsi for Helvetica-Bold). A codepoint with no code in the encoding is a coded
    /// refusal — never silent tofu, matching the composed-document path's <c>PLUME8009</c>
    /// discipline, but minted in the Documents/verbs range because the refusal happens in
    /// stamp orchestration, not the Fonts layer.
    /// </summary>
    private static byte[] EncodeWinAnsi(string text, Standard14Font font)
    {
        var encoded = new byte[text.Length];
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!font.TryGetGlyphId(rune.Value, out var code) || code < 0 || code > 255)
            {
                throw new PlumePdfException("PLUME6072", $"Stamp text contains '{rune}' (U+{rune.Value:X4}), which the Standard-14 {font.BaseFontName} font's WinAnsi encoding cannot represent. Opened-document stamping draws in Standard-14 Helvetica-Bold only (nothing is embedded); for text beyond WinAnsi, compose the document with an embedded font via Section.Stamps and PdfFont.FromFile/FromBytes instead.");
            }

            encoded[count++] = (byte)code;
        }

        return count == encoded.Length ? encoded : encoded[..count];
    }
}
