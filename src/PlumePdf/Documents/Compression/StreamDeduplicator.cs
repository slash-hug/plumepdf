using System.Globalization;
using System.Security.Cryptography;
using PlumePdf.Objects;

namespace PlumePdf.Documents.Compression;

/// <summary>
/// Finds byte-identical streams a full-rewrite save would write and maps each duplicate to
/// one canonical copy, for <c>PdfDocument.SaveWithWriteHooks</c>. Hashes one payload at a
/// time and keeps none.
/// </summary>
/// <remarks>
/// Candidates are exactly the streams the save writes as themselves: the graph is walked the
/// way <see cref="FullRewriteWriter"/> discovers it (from the rebuilt catalog, the kept pages
/// and <c>/Info</c>, through the save's replacements, never into an excluded number), and a
/// replaced stream is never a candidate on either side. Metadata, cross-reference and object
/// streams, external-file streams (<c>/F</c>) and Form XObjects are never merged.
///
/// Two streams are equal when their dictionaries are equal once <c>/Length</c> is set aside,
/// keys are ordered and every reference is read as its target's canonical number, and their
/// raw payloads have the same length and SHA-256. Streams are first bucketed by dictionary and
/// declared length, so only payloads that could match are read (through
/// <see cref="IObjectSource.ReadPayloadOnce"/>, which keeps nothing). Merging repeats until
/// nothing changes, so a parent whose children merged in one round (an image naming its own
/// copy of a shared soft mask) is found equal in the next. The canonical is always the lowest
/// number of its group, which is never excluded since excluded numbers are never discovered.
/// </remarks>
internal static class StreamDeduplicator
{
    private static readonly PdfName FormName = PdfName.Get("Form");
    private static readonly PdfName XRefName = PdfName.Get("XRef");
    private static readonly PdfName ObjStmName = PdfName.Get("ObjStm");
    private static readonly PdfName ExternalFileName = PdfName.Get("F");

    /// <summary>
    /// The alias map (duplicate original number → canonical original number) for saving
    /// <paramref name="document"/> with <paramref name="saveOptions"/>; empty when nothing repeats.
    /// </summary>
    /// <param name="document">The document the save writes.</param>
    /// <param name="saveOptions">The effective options of that save: they decide its exclusion set and clean-up replacements.</param>
    /// <param name="cancellationToken">Checked per object walked and per payload read.</param>
    public static IReadOnlyDictionary<int, int> FindAliases(
        PdfDocument document,
        PdfOptions saveOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(saveOptions);
        cancellationToken.ThrowIfCancellationRequested();

        var cleanup = document.PrepareFullRewrite(saveOptions);
        return FindAliases(document.Objects, document.Catalog?.Reference, cleanup.Catalog, cleanup.Pages, cleanup.Excluded, cleanup.Replacements, cancellationToken);
    }

    /// <summary>
    /// The alias map for a full rewrite given the same inputs <see cref="FullRewriteWriter.Write"/>
    /// takes: the catalog and kept pages as they will be written, the exclusion set and the
    /// replacement values.
    /// </summary>
    internal static IReadOnlyDictionary<int, int> FindAliases(
        ObjectRegistry objects,
        IndirectReference? catalogReference,
        PdfDictionary? catalog,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> pages,
        IReadOnlySet<int> excluded,
        IReadOnlyDictionary<int, PdfObject>? replacements,
        CancellationToken cancellationToken)
    {
        var candidates = Discover(objects, catalogReference, catalog, pages, excluded, replacements, out var payloadLengths, cancellationToken);
        return Merge(objects, candidates, payloadLengths, excluded, cancellationToken);
    }

    // The writer's discovery walk, keeping the dictionary of every stream it would write as
    // itself and may merge. The catalog and pages are rebuilt around a stand-in /Pages
    // reference (the writer's fresh node), which is never followed. A candidate handed back
    // whole (built in memory, or already read) has its exact payload length recorded, since such
    // a stream need not carry a /Length.
    private static SortedDictionary<int, PdfDictionary> Discover(
        ObjectRegistry objects,
        IndirectReference? catalogReference,
        PdfDictionary? catalog,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> pages,
        IReadOnlySet<int> excluded,
        IReadOnlyDictionary<int, PdfObject>? replacements,
        out Dictionary<int, long> payloadLengths,
        CancellationToken cancellationToken)
    {
        payloadLengths = [];
        var overrides = FullRewriteWriter.SeedOverrides(catalogReference, pages, excluded, replacements);
        var pagesRoot = new PdfReference(new IndirectReference(0, 0));
        var seen = new HashSet<int>();
        var pending = new Queue<int>();

        void Visit(int number)
        {
            if (!excluded.Contains(number) && seen.Add(number))
            {
                pending.Enqueue(number);
            }
        }

        foreach (var (reference, dictionary) in pages)
        {
            overrides[reference.Number] = FullRewriteWriter.BuildPageCopy(dictionary, pagesRoot);
            Visit(reference.Number);
        }

        if (catalogReference is { } realCatalog && catalog is not null)
        {
            overrides[realCatalog.Number] = FullRewriteWriter.BuildCatalogCopy(catalog, pagesRoot);
            Visit(realCatalog.Number);
        }

        if (objects.Trailer.TryGetValue(PdfName.Info, out var info) && info is PdfReference infoReference)
        {
            Visit(infoReference.Target.Number);
        }

        var candidates = new SortedDictionary<int, PdfDictionary>();
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = pending.Dequeue();
            var replaced = overrides.TryGetValue(number, out var value);
            var isStream = value is PdfStream;
            if (!replaced)
            {
                value = ((IObjectSource)objects).ResolveForDiscovery(new IndirectReference(number, 0), out isStream);
            }

            var streamDictionary = !isStream ? null : value as PdfDictionary ?? (value as PdfStream)?.Dictionary;
            var references = streamDictionary is not null ? FullRewriteWriter.FindStreamDictionaryReferences(streamDictionary) : FullRewriteWriter.FindReferences(value!);
            foreach (var reference in references)
            {
                if (!ReferenceEquals(reference, pagesRoot))
                {
                    Visit(reference.Target.Number);
                }
            }

            if (!replaced && streamDictionary is not null && IsEligible(streamDictionary))
            {
                candidates[number] = streamDictionary;
                if (value is PdfStream whole)
                {
                    payloadLengths[number] = whole.RawBytes.Length;
                }
            }
        }

        return candidates;
    }

    // Streams that must stay one-to-one with what references them: metadata (often required
    // per object), the file-structure streams, external-file streams, and Form XObjects, whose
    // sharing would change what optional-content and marked-content resources attach to. A
    // /Type or /Subtype that is itself a reference can't be classified, so it isn't merged.
    private static bool IsEligible(PdfDictionary dictionary)
    {
        if (dictionary.ContainsKey(ExternalFileName))
        {
            return false;
        }

        if (dictionary.TryGetValue(PdfName.Type, out var type)
            && (type is PdfReference || ReferenceEquals(type, PdfName.Metadata) || ReferenceEquals(type, XRefName) || ReferenceEquals(type, ObjStmName)))
        {
            return false;
        }

        return !dictionary.TryGetValue(PdfName.Subtype, out var subtype) || (subtype is not PdfReference && !ReferenceEquals(subtype, FormName));
    }

    private static Dictionary<int, int> Merge(
        ObjectRegistry objects,
        SortedDictionary<int, PdfDictionary> candidates,
        Dictionary<int, long> payloadLengths,
        IReadOnlySet<int> excluded,
        CancellationToken cancellationToken)
    {
        var aliases = new Dictionary<int, int>();
        var duplicatesOf = new Dictionary<int, List<int>>();
        var declaredLengths = payloadLengths;
        var payloadKeys = new Dictionary<int, string?>();
        var buffer = new MemoryStream();

        int Canonical(int number) => aliases.TryGetValue(number, out var canonical) ? canonical : number;

        void Alias(int duplicate, int canonical)
        {
            aliases[duplicate] = canonical;
            if (!duplicatesOf.TryGetValue(canonical, out var list))
            {
                duplicatesOf[canonical] = list = [];
            }

            list.Add(duplicate);

            // A canonical displaced by a lower equal number takes its duplicates along: the
            // writer refuses an alias onto an object that is itself aliased.
            if (duplicatesOf.Remove(duplicate, out var displaced))
            {
                foreach (var moved in displaced)
                {
                    aliases[moved] = canonical;
                    list.Add(moved);
                }
            }
        }

        bool merged;
        do
        {
            merged = false;

            // Candidates are walked in number order, so each bucket lists its members ascending
            // and buckets are visited by their lowest member: the map never depends on hashing order.
            var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var bucketOrder = new List<List<int>>();
            foreach (var (number, dictionary) in candidates)
            {
                if (aliases.ContainsKey(number))
                {
                    continue;
                }

                if (!declaredLengths.TryGetValue(number, out var declared))
                {
                    declaredLengths[number] = declared = DeclaredLength(objects, dictionary);
                }

                var key = string.Concat(DictionaryKey(dictionary, Canonical, excluded, buffer), "|", declared.ToString(CultureInfo.InvariantCulture));
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    buckets[key] = bucket = [];
                    bucketOrder.Add(bucket);
                }

                bucket.Add(number);
            }

            foreach (var bucket in bucketOrder)
            {
                if (bucket.Count < 2)
                {
                    continue;
                }

                var groups = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var number in bucket)
                {
                    if (!payloadKeys.TryGetValue(number, out var payloadKey))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        payloadKeys[number] = payloadKey = PayloadKey(objects, number);
                    }

                    if (payloadKey is null)
                    {
                        continue;
                    }

                    if (groups.TryGetValue(payloadKey, out var canonical))
                    {
                        Alias(number, canonical);
                        merged = true;
                    }
                    else
                    {
                        groups[payloadKey] = number;
                    }
                }
            }
        }
        while (merged);

        return aliases;
    }

    // The /Length the dictionary declares, to bucket by before any payload is read; -1 when it
    // can't be read as an integer (the payload hash still decides).
    internal static long DeclaredLength(ObjectRegistry objects, PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(PdfName.Length, out var length))
        {
            return -1;
        }

        if (length is PdfReference reference)
        {
            length = ((IObjectSource)objects).ResolveForDiscovery(reference.Target, out var isStream);
            if (isStream)
            {
                return -1;
            }
        }

        return length is PdfNumber { IsInteger: true } number && number.TryToInt64(out var declared) ? declared : -1;
    }

    // The raw payload's length and SHA-256, read for this one pass and let go; null when the
    // stream can't be read, which leaves it unmerged (the save reports it in its own context).
    private static string? PayloadKey(ObjectRegistry objects, int number)
    {
        PdfStream? stream;
        try
        {
            stream = ((IObjectSource)objects).ReadPayloadOnce(new IndirectReference(number, 0));
        }
        catch (PlumePdfException)
        {
            return null;
        }

        if (stream is null)
        {
            return null;
        }

        var raw = stream.RawBytes.Span;
        return string.Concat(raw.Length.ToString(CultureInfo.InvariantCulture), ":", Convert.ToHexString(SHA256.HashData(raw)));
    }

    // SHA-256 of the dictionary's canonical form: /Length left out (the writer recomputes it),
    // keys in ordinal order, each reference as its target's canonical number (or null when the
    // target is excluded, as the writer writes it).
    private static string DictionaryKey(PdfDictionary dictionary, Func<int, int> canonical, IReadOnlySet<int> excluded, MemoryStream buffer)
    {
        buffer.SetLength(0);
        WriteCanonical(buffer, dictionary, canonical, excluded, topLevel: true);
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }

    private static void WriteCanonical(MemoryStream output, PdfObject value, Func<int, int> canonical, IReadOnlySet<int> excluded, bool topLevel = false)
    {
        switch (value)
        {
            case PdfReference reference:
                var target = reference.Target.Number;
                WriteAscii(output, excluded.Contains(target) ? "null" : string.Concat(canonical(target).ToString(CultureInfo.InvariantCulture), " R"));
                break;

            case PdfArray array:
                WriteAscii(output, "[");
                foreach (var item in array)
                {
                    WriteCanonical(output, item, canonical, excluded);
                    WriteAscii(output, " ");
                }

                WriteAscii(output, "]");
                break;

            case PdfDictionary dictionary:
                WriteAscii(output, "<<");
                foreach (var (key, item) in dictionary.OrderBy(static e => e.Key.Value, StringComparer.Ordinal))
                {
                    if (topLevel && ReferenceEquals(key, PdfName.Length))
                    {
                        continue;
                    }

                    ObjectSerializer.WriteValue(output, key);
                    WriteAscii(output, " ");
                    WriteCanonical(output, item, canonical, excluded);
                    WriteAscii(output, " ");
                }

                WriteAscii(output, ">>");
                break;

            default:
                ObjectSerializer.WriteValue(output, value);
                break;
        }
    }

    private static void WriteAscii(MemoryStream output, string text)
    {
        foreach (var c in text)
        {
            output.WriteByte((byte)c);
        }
    }
}
