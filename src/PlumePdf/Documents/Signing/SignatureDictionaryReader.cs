namespace PlumePdf.Documents.Signing;

/// <summary>
/// One discovered signature (or document timestamp) dictionary (ISO 32000-1 §12.8.1), parsed
/// leniently: a malformed <c>/ByteRange</c> or missing <c>/Contents</c> never throws here —
/// <see cref="CoversWholeDocument"/> simply comes back <see langword="false"/>, and
/// <see cref="SignatureVerifier"/> reports the corresponding
/// <see cref="SignatureCryptographicStatus.Malformed"/> outcome (a deliberate read-leniently,
/// verify-strictly split).
/// </summary>
internal sealed class SignatureDictionaryInfo
{
    internal SignatureDictionaryInfo(
        string fieldName,
        IndirectReference fieldReference,
        IndirectReference dictionaryReference,
        PdfDictionary dictionary,
        bool isDocTimeStamp,
        byte[] contents,
        IReadOnlyList<long> byteRange,
        bool coversWholeDocument,
        string? subFilter,
        DateTimeOffset? signingTime,
        string? reason,
        string? location,
        string? contactInfo)
    {
        FieldName = fieldName;
        FieldReference = fieldReference;
        DictionaryReference = dictionaryReference;
        Dictionary = dictionary;
        IsDocTimeStamp = isDocTimeStamp;
        Contents = contents;
        ByteRange = byteRange;
        CoversWholeDocument = coversWholeDocument;
        SubFilter = subFilter;
        SigningTime = signingTime;
        Reason = reason;
        Location = location;
        ContactInfo = contactInfo;
    }

    /// <summary>The owning <c>/FT /Sig</c> AcroForm field's fully-qualified name.</summary>
    public string FieldName { get; }

    /// <summary>The owning field's own indirect-object identity.</summary>
    public IndirectReference FieldReference { get; }

    /// <summary>This signature (or document timestamp) dictionary's own indirect-object identity.</summary>
    public IndirectReference DictionaryReference { get; }

    /// <summary>The raw signature/timestamp dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>Whether this is a standalone <c>/Type /DocTimeStamp</c> dictionary (B-LTA maintenance) rather than an ordinary <c>/Type /Sig</c> signature.</summary>
    public bool IsDocTimeStamp { get; }

    /// <summary>The decoded (already hex-un-encoded) <c>/Contents</c> bytes — the raw CMS/PKCS#7 blob.</summary>
    public byte[] Contents { get; }

    /// <summary>The raw <c>/ByteRange</c> integers, in document order — whatever entries parsed successfully; may be empty or have any length for a malformed document.</summary>
    public IReadOnlyList<long> ByteRange { get; }

    /// <summary>
    /// The structural coverage verdict (deliberately un-hideable): <see langword="true"/>
    /// only when <c>/ByteRange</c> is exactly two segments, the first starts at byte 0, the
    /// second ends at the file's actual length, and the gap between them is exactly as wide as
    /// <see cref="Contents"/>'s own hex encoding (<c>2 + 2 * Contents.Length</c> — the
    /// <c>&lt;...&gt;</c> delimiters plus two hex digits per byte). A signature can be
    /// cryptographically valid over the bytes it covers while this is still
    /// <see langword="false"/> (e.g. content appended after signing, or a producer that
    /// deliberately signs only part of the file) — surfaced separately from the crypto verdict
    /// for exactly that reason.
    /// </summary>
    public bool CoversWholeDocument { get; }

    /// <summary>The <c>/SubFilter</c> value naming this signature's encoding, or <see langword="null"/> when absent.</summary>
    public string? SubFilter { get; }

    /// <summary>The claimed signing time (<c>/M</c>), or <see langword="null"/> when absent/unparseable.</summary>
    public DateTimeOffset? SigningTime { get; }

    /// <summary>The signer-supplied <c>/Reason</c>, or <see langword="null"/>.</summary>
    public string? Reason { get; }

    /// <summary>The signer-supplied <c>/Location</c>, or <see langword="null"/>.</summary>
    public string? Location { get; }

    /// <summary>The signer-supplied <c>/ContactInfo</c>, or <see langword="null"/>.</summary>
    public string? ContactInfo { get; }
}

/// <summary>
/// Discovers every signature/document-timestamp dictionary in a document — the read
/// model every other signing-subsystem piece (<see cref="SignatureVerifier"/>,
/// <see cref="SignatureCollection"/>, <see cref="SigningOrchestrator"/>'s Save-guard
/// consumer) builds on. Three discovery sources, deduplicated by the signature dictionary's
/// object identity: the <c>/AcroForm</c> field tree (the normal case), page <c>/Annots</c>
/// signature widgets (real producers — the veraPDF Permissions fixtures among them — attach the
/// widget to a page with no <c>/AcroForm</c> at all; pdfsig finds these, so must PlumePDF), and
/// the catalog's <c>/Perms</c> dictionary (usage-rights/DocMDP signatures, §12.8.4, which need
/// no field anywhere).
/// </summary>
internal static class SignatureDictionaryReader
{
    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName ByteRangeName = PdfName.ByteRange;
    private static readonly PdfName SubFilterName = PdfName.SubFilter;
    private static readonly PdfName TypeName = PdfName.Type;
    private static readonly PdfName DocTimeStampName = PdfName.DocTimeStamp;
    private static readonly PdfName MName = PdfName.M;
    private static readonly PdfName ReasonName = PdfName.Reason;
    private static readonly PdfName LocationName = PdfName.Location;
    private static readonly PdfName ContactInfoName = PdfName.ContactInfo;
    private static readonly PdfName VName = PdfName.V;

    /// <summary>
    /// Walks <paramref name="document"/>'s <c>/AcroForm</c> field tree and parses every
    /// <c>/FT /Sig</c> field's <c>/V</c> into a <see cref="SignatureDictionaryInfo"/>, in field
    /// tree (document) order. A field with no <c>/V</c> (an unsigned signature field/widget) is
    /// skipped — it carries nothing to verify.
    /// </summary>
    public static IReadOnlyList<SignatureDictionaryInfo> ReadAll(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var read = AcroFormReader.Read(document, document.Diagnostics);
        var results = new List<SignatureDictionaryInfo>();
        var totalLength = document.Source?.Length;

        foreach (var field in read.Fields)
        {
            if (field.Kind != AcroFieldKind.Signature)
            {
                continue;
            }

            if (!field.Dictionary.TryGetValue(VName, out var vValue))
            {
                continue;
            }

            var (dictionaryReference, dictionary) = vValue switch
            {
                PdfReference reference => (reference.Target, document.Objects[reference.Target] as PdfDictionary),
                PdfDictionary direct => (field.Reference, direct), // inline /V: attribute changes to the field's own object.
                _ => (default(IndirectReference), null),
            };

            if (dictionary is null)
            {
                continue;
            }

            var info = Parse(field.FullyQualifiedName, field.Reference, dictionaryReference, dictionary, totalLength, document.Options);
            if (info is not null)
            {
                results.Add(info);
            }
        }

        var seen = new HashSet<IndirectReference>(results.Select(static r => r.DictionaryReference));
        AppendPageAnnotationSignatures(document, results, seen, totalLength);
        AppendPermsSignatures(document, results, seen, totalLength);

        return results;
    }

    private static void AppendPageAnnotationSignatures(PdfDocument document, List<SignatureDictionaryInfo> results, HashSet<IndirectReference> seen, long? totalLength)
    {
        var annotsName = PdfName.Get("Annots");
        var ftName = PdfName.Get("FT");
        var sigName = PdfName.Get("Sig");
        var tName = PdfName.Get("T");

        foreach (var page in document.Pages)
        {
            if (!page.Dictionary.TryGetValue(annotsName, out var annotsValue) || Resolve(document, annotsValue) is not PdfArray annots)
            {
                continue;
            }

            foreach (var entry in annots)
            {
                if (Resolve(document, entry) is not PdfDictionary annot
                    || !annot.TryGetValue(ftName, out var ftValue) || !sigName.Equals(Resolve(document, ftValue))
                    || !annot.TryGetValue(VName, out var vValue))
                {
                    continue;
                }

                var (dictionaryReference, dictionary) = ResolveSignatureValue(document, vValue, entry as PdfReference);
                if (dictionary is null || !seen.Add(dictionaryReference))
                {
                    continue;
                }

                var fieldName = annot.TryGetValue(tName, out var tValue) && Resolve(document, tValue) is PdfString tString
                    ? tString.GetText()
                    : "(unnamed page-annotation signature)";
                var fieldReference = entry is PdfReference annotReference ? annotReference.Target : dictionaryReference;

                var info = Parse(fieldName, fieldReference, dictionaryReference, dictionary, totalLength, document.Options);
                if (info is not null)
                {
                    results.Add(info);
                }
            }
        }
    }

    private static void AppendPermsSignatures(PdfDocument document, List<SignatureDictionaryInfo> results, HashSet<IndirectReference> seen, long? totalLength)
    {
        if (document.Catalog is not { } catalog
            || !catalog.Dictionary.TryGetValue(PdfName.Perms, out var permsValue)
            || Resolve(document, permsValue) is not PdfDictionary perms)
        {
            return;
        }

        foreach (var (key, value) in perms)
        {
            var (dictionaryReference, dictionary) = ResolveSignatureValue(document, value, annotReference: null);

            // A /Perms entry is a signature dictionary by definition (§12.8.4), but a hostile or
            // broken producer can point one anywhere — only dictionaries that look like a
            // signature (a /ByteRange or /Contents) are surfaced; anything else is ignored, per
            // the reader's overall lenient stance.
            if (dictionary is null
                || (!dictionary.ContainsKey(ByteRangeName) && !dictionary.ContainsKey(ContentsName))
                || !seen.Add(dictionaryReference))
            {
                continue;
            }

            var info = Parse($"(/Perms /{key.Value})", dictionaryReference, dictionaryReference, dictionary, totalLength, document.Options);
            if (info is not null)
            {
                results.Add(info);
            }
        }
    }

    private static (IndirectReference Reference, PdfDictionary? Dictionary) ResolveSignatureValue(PdfDocument document, PdfObject value, PdfReference? annotReference) => value switch
    {
        PdfReference reference => (reference.Target, document.Objects[reference.Target] as PdfDictionary),
        PdfDictionary direct => (annotReference?.Target ?? default, direct),
        _ => (default, null),
    };

    private static PdfObject? Resolve(PdfDocument document, PdfObject value) =>
        value is PdfReference reference ? document.Objects[reference.Target] : value;

    private static SignatureDictionaryInfo? Parse(
        string fieldName,
        IndirectReference fieldReference,
        IndirectReference dictionaryReference,
        PdfDictionary dictionary,
        long? totalLength,
        PdfOptions options)
    {
        var contents = dictionary.TryGetValue(ContentsName, out var contentsValue) && contentsValue is PdfString contentsString
            ? contentsString.Bytes.ToArray()
            : [];

        if (contents.Length > options.MaxSignatureContentsBytes)
        {
            throw new PlumePdfException("PLUME6052", $"Field '{fieldName}''s signature /Contents is {contents.Length} bytes, exceeding the configured cap of {options.MaxSignatureContentsBytes} (PdfOptions.MaxSignatureContentsBytes).");
        }

        var byteRange = ReadByteRange(dictionary, fieldName, options);
        var coversWholeDocument = EvaluateCoverage(byteRange, contents.Length, totalLength);

        var isDocTimeStamp = dictionary.TryGetValue(TypeName, out var typeValue) && DocTimeStampName.Equals(typeValue);
        var subFilter = dictionary.TryGetValue(SubFilterName, out var subFilterValue) && subFilterValue is PdfName subFilterName ? subFilterName.Value : null;
        var signingTime = dictionary.TryGetValue(MName, out var mValue) && mValue is PdfString mString ? MetadataReader.ParsePdfDate(mString.GetText()) : null;
        var reason = dictionary.TryGetValue(ReasonName, out var reasonValue) && reasonValue is PdfString reasonString ? reasonString.GetText() : null;
        var location = dictionary.TryGetValue(LocationName, out var locationValue) && locationValue is PdfString locationString ? locationString.GetText() : null;
        var contactInfo = dictionary.TryGetValue(ContactInfoName, out var contactValue) && contactValue is PdfString contactString ? contactString.GetText() : null;

        return new SignatureDictionaryInfo(fieldName, fieldReference, dictionaryReference, dictionary, isDocTimeStamp, contents, byteRange, coversWholeDocument, subFilter, signingTime, reason, location, contactInfo);
    }

    private static IReadOnlyList<long> ReadByteRange(PdfDictionary dictionary, string fieldName, PdfOptions options)
    {
        if (!dictionary.TryGetValue(ByteRangeName, out var byteRangeValue) || byteRangeValue is not PdfArray array)
        {
            return [];
        }

        if (array.Count > options.MaxByteRangeSegments * 2)
        {
            throw new PlumePdfException("PLUME6052", $"Field '{fieldName}''s /ByteRange has {array.Count} entries, exceeding the configured cap of {options.MaxByteRangeSegments * 2} (PdfOptions.MaxByteRangeSegments).");
        }

        var values = new List<long>(array.Count);
        foreach (var item in array)
        {
            // Every document-supplied /ByteRange number goes through
            // PdfNumber's Try-forms, never a throwing conversion — an out-of-range or
            // non-integer entry just makes this signature's coverage verdict "not whole
            // document" rather than crashing the read.
            if (item is PdfNumber number && number.TryToInt64(out var converted))
            {
                values.Add(converted);
            }
            else
            {
                return []; // one bad entry invalidates the whole array as a coverage source.
            }
        }

        return values;
    }

    private static bool EvaluateCoverage(IReadOnlyList<long> byteRange, int contentsLength, long? totalLength)
    {
        if (totalLength is not long total || byteRange.Count != 4)
        {
            return false;
        }

        var seg1Offset = byteRange[0];
        var seg1Length = byteRange[1];
        var seg2Offset = byteRange[2];
        var seg2Length = byteRange[3];

        if (seg1Offset != 0 || seg1Length < 0 || seg2Length < 0 || seg2Offset < seg1Offset + seg1Length)
        {
            return false;
        }

        if (seg2Offset + seg2Length != total)
        {
            return false;
        }

        var gap = seg2Offset - (seg1Offset + seg1Length);
        var expectedGap = 2L + (2L * contentsLength); // '<' + 2 hex digits/byte + '>'
        return gap == expectedGap;
    }
}
