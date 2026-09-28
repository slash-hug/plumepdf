using PlumePdf.IO;

namespace PlumePdf.Objects;

/// <summary>
/// The incremental-update save path (<c>PdfDocument.SaveIncremental</c>, the default save
/// path — docs/architecture.md "Writing pipeline"): appends only changed objects and a new
/// cross-reference section, chained via <c>/Prev</c> onto the source's own most recent
/// cross-reference offset, preserving every prior revision byte-for-byte (required for
/// signed documents). Never renumbers an existing object — every original object keeps
/// exactly the <c>(Number, Generation)</c> it had in the source; only brand-new objects
/// (<see cref="ObjectRegistry.RegisterNew"/>) introduce numbers the source never used, via
/// <see cref="ObjectRegistry.AllocateNumber"/>. "Changed" is
/// <see cref="ObjectRegistry.DirtyObjects"/> — <see cref="ObjectRegistry.MarkDirty"/> plus
/// <see cref="ObjectRegistry.RegisterNew"/> — with the page-tree rewrite below layered on top
/// as one contributor among others, not the whole story. Layer-2 (Objects), like
/// <see cref="FullRewriteWriter"/> — see its remarks on why (the layering test's facade
/// rule). The caller is responsible for placing the bytes this writes at the correct
/// position (immediately after a byte-for-byte copy of the source, or at the end of the
/// still-open source file via an independent append handle).
/// </summary>
/// <remarks>
/// Phase 1 simplification (documented): always emits a classic cross-reference table for
/// its own new section, regardless of whether the source used a table, a stream, or a
/// hybrid file — <see cref="CrossReferenceReader"/> accepts any combination in a
/// <c>/Prev</c> chain since each section is dispatched by what is actually found at its
/// offset, so this is always readable, just not "matching the source's style" would ideally
/// describe. Revisit once there is a writer-side Flate encoder to produce a
/// compressed cross-reference stream.
/// </remarks>
internal static class IncrementalUpdateWriter
{
    private static readonly PdfName KidsName = PdfName.Get("Kids");
    private static readonly PdfName CountName = PdfName.Get("Count");
    private static readonly PdfName PagesName = PdfName.Get("Pages");
    private static readonly PdfName ParentName = PdfName.Get("Parent");

    /// <summary>
    /// Writes the incremental appendix — changed objects, a new cross-reference section,
    /// and a new trailer — to <paramref name="output"/>, whose current position must already
    /// equal the byte length of everything written before it (the copied source bytes, or an
    /// append handle already positioned at end-of-file).
    /// </summary>
    /// <param name="output">The destination stream, positioned where the appendix begins.</param>
    /// <param name="objects">The source document's object graph.</param>
    /// <param name="pagesTreeDirty">Whether <c>Pages</c> has been reordered or had a page removed since the document was opened.</param>
    /// <param name="topPagesReference">The catalog's original <c>/Pages</c> target, or <see langword="null"/> when unresolvable.</param>
    /// <param name="pages">The current pages, in final order.</param>
    /// <param name="previousStartXrefOffset">The offset the new trailer's <c>/Prev</c> chains onto.</param>
    /// <param name="options">Options controlling the write, notably <see cref="PdfOptions.Deterministic"/>.</param>
    /// <param name="securityHandler">
    /// When the source document is encrypted, the handler that already authenticated and
    /// derived its file key at open time — every appended object's strings/streams are
    /// re-encrypted through it before being written. <see langword="null"/>
    /// for an unencrypted source.
    /// </param>
    /// <param name="onPlaceholder">
    /// Forwarded to <see cref="ObjectSerializer.WriteIndirectObject"/> for every dirty object
    /// written — <see langword="null"/> outside a <see cref="SigningWriteSession"/> pass;
    /// see that type's remarks.
    /// </param>
    public static void WriteAppendix(
        Stream output,
        ObjectRegistry objects,
        bool pagesTreeDirty,
        IndirectReference? topPagesReference,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> pages,
        long previousStartXrefOffset,
        PdfOptions options,
        StandardSecurityHandler? securityHandler = null,
        Action<PdfObject, long>? onPlaceholder = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);

        var dirty = new SortedDictionary<int, (int Generation, PdfObject Value)>();

        // The general dirty-object set (ObjectRegistry.MarkDirty/RegisterNew) is the baseline —
        // every object mutated in place or newly registered since the document was opened,
        // regardless of which higher-level feature caused it. The page-tree contributor below
        // is layered on top as one source among others, not a replacement for
        // it: it always starts from each page's own current (already-live-mutated) dictionary,
        // so a page marked dirty for its own reasons is never undone by a page-tree rewrite.
        foreach (var (reference, value) in objects.DirtyObjects)
        {
            dirty[reference.Number] = (reference.Generation, value);
        }

        if (pagesTreeDirty && topPagesReference is { } topPagesRef)
        {
            BuildPageTreeUpdates(objects, topPagesRef, pages, dirty);
        }

        // Nothing dirty means nothing to append: the source bytes already end in a valid
        // revision, and ISO 32000-1 §7.5.4 forbids a cross-reference section with zero
        // subsections, so an "empty appendix" cannot be expressed as a well-formed update.
        if (dirty.Count == 0)
        {
            return;
        }

        // The copied source may end exactly at "%%EOF" with no trailing newline; a leading
        // EOL keeps the appendix's first object off the previous revision's comment line.
        WriteAscii(output, "\n");

        var offsets = new Dictionary<int, long>();
        foreach (var (number, entry) in dirty)
        {
            offsets[number] = output.Position;
            var owner = new IndirectReference(number, entry.Generation);
            var value = securityHandler is null ? entry.Value : EncryptForWrite(entry.Value, owner, securityHandler, options.Deterministic, previousStartXrefOffset);
            ObjectSerializer.WriteIndirectObject(output, number, entry.Generation, value, onPlaceholder: onPlaceholder);
        }

        WriteXrefAndTrailer(output, dirty, offsets, objects.Trailer, previousStartXrefOffset, options);
    }

    /// <summary>
    /// The internal entry point behind the <c>PdfDocument.SaveIncremental(Stream, PdfOptions?)</c>
    /// overload: copies <paramref name="source"/>'s bytes to
    /// <paramref name="output"/> — mirroring <c>PdfDocument.SaveIncremental(string, ...)</c>'s
    /// own different-path branch — then appends exactly as <see cref="WriteAppendix"/> does.
    /// A stream destination has no "same path as the source" concept to special-case, so this
    /// always copies first; a caller that already has the source open at the target path
    /// should prefer the path-based overload instead, which can append through an independent
    /// handle without re-copying unchanged bytes.
    /// </summary>
    /// <param name="output">The destination stream, positioned at its start.</param>
    /// <param name="source">The source document's original bytes.</param>
    /// <param name="objects">The source document's object graph.</param>
    /// <param name="pagesTreeDirty">Whether <c>Pages</c> has been reordered or had a page removed since the document was opened.</param>
    /// <param name="topPagesReference">The catalog's original <c>/Pages</c> target, or <see langword="null"/> when unresolvable.</param>
    /// <param name="pages">The current pages, in final order.</param>
    /// <param name="previousStartXrefOffset">The offset the new trailer's <c>/Prev</c> chains onto.</param>
    /// <param name="options">Options controlling the write, notably <see cref="PdfOptions.Deterministic"/>.</param>
    /// <param name="securityHandler">See <see cref="WriteAppendix"/>'s parameter of the same name.</param>
    public static void WriteFull(
        Stream output,
        ByteSource source,
        ObjectRegistry objects,
        bool pagesTreeDirty,
        IndirectReference? topPagesReference,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> pages,
        long previousStartXrefOffset,
        PdfOptions options,
        StandardSecurityHandler? securityHandler = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(source);

        source.CopyTo(output);
        WriteAppendix(output, objects, pagesTreeDirty, topPagesReference, pages, previousStartXrefOffset, options, securityHandler);
    }

    // Mirrors ObjectResolver.Decrypt's shape exactly, in the opposite direction: recursively
    // builds a freshly-encrypted COPY of value for serialization only. The live, in-memory
    // object graph is never touched here — a caller may keep reading/mutating through
    // doc.Objects (in decrypted form) after this call, and a second SaveIncremental later must
    // still see plaintext to re-encrypt, not the ciphertext this pass produced. Strings and
    // streams are already immutable value types in this codebase, so "copy" is cheap and
    // natural; dictionaries/arrays are shallow-rebuilt for the same reason (never mutated in
    // place, since these may still be the same live instances the caller holds).
    //
    // revisionSalt (the previous startxref offset — unique per appended revision, already
    // available here) is threaded down to StandardSecurityHandler's deterministic IV
    // derivation so the same object number re-encrypted across successive SaveIncremental
    // calls doesn't reuse the exact IV it got last time (see DeterministicIv's remarks).
    private static PdfObject EncryptForWrite(PdfObject value, IndirectReference owner, StandardSecurityHandler handler, bool deterministic, long revisionSalt)
    {
        switch (value)
        {
            case PdfString str:
                var encryptedBytes = handler.EncryptString(str.Bytes.Span, owner, deterministic, revisionSalt);
                return str.IsHex ? PdfString.FromHex(encryptedBytes) : PdfString.FromLiteral(encryptedBytes);

            case PdfArray array:
                var newArray = new PdfArray();
                foreach (var item in array)
                {
                    newArray.Add(EncryptForWrite(item, owner, handler, deterministic, revisionSalt));
                }

                return newArray;

            case PdfDictionary dict:
                var newDict = new PdfDictionary();
                var isSignatureDictionary = IsSignatureDictionary(dict);
                foreach (var (key, item) in dict)
                {
                    // A write-side carve-out: a signature
                    // dictionary's /Contents holds the raw CMS/PKCS#7 bytes /ByteRange's
                    // digest was computed over the *exact physical bytes as they sit in the
                    // file*; ISO 32000-2 §12.8.1 requires it stay unencrypted regardless of
                    // the document's own /Encrypt. Without this carve-out, re-saving an
                    // already-signed, encrypted document — e.g. to fix an unrelated form
                    // field, via SaveIncremental — would silently re-encrypt an untouched
                    // signature's /Contents the moment ANY object needs (re-)encryption in
                    // the same pass, invalidating the signature without a single byte of it
                    // being the intended edit. A SigningWriteSession's own /Contents
                    // placeholder (not yet a PdfString at this point) is unaffected either
                    // way — the default arm below already passes non-string values through
                    // unchanged.
                    if (isSignatureDictionary && ReferenceEquals(key, ContentsName))
                    {
                        newDict.Set(key, item);
                    }
                    else
                    {
                        newDict.Set(key, EncryptForWrite(item, owner, handler, deterministic, revisionSalt));
                    }
                }

                return newDict;

            case PdfStream stream:
                var encryptedDict = (PdfDictionary)EncryptForWrite(stream.Dictionary, owner, handler, deterministic, revisionSalt);
                if (handler.EncryptMetadata is false && IsMetadataStream(encryptedDict))
                {
                    return new PdfStream(encryptedDict, stream.RawBytes);
                }

                var encryptedRaw = handler.EncryptStream(stream.RawBytes.Span, owner, deterministic, revisionSalt);
                return new PdfStream(encryptedDict, encryptedRaw);

            default:
                // Numbers, names, booleans, null, and references carry no encrypted payload.
                return value;
        }
    }

    private static bool IsMetadataStream(PdfDictionary dict) =>
        dict.TryGetValue(PdfName.Type, out var type) && PdfName.Metadata.Equals(type);

    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName SigTypeName = PdfName.Get("Sig");
    private static readonly PdfName DocTimeStampTypeName = PdfName.Get("DocTimeStamp");

    // ISO 32000-1 §12.8.1's two signature-dictionary /Type values (an ordinary signature and
    // a document timestamp) — both carry the same /Contents-must-stay-plaintext rule.
    private static bool IsSignatureDictionary(PdfDictionary dict) =>
        dict.TryGetValue(PdfName.Type, out var type) && (SigTypeName.Equals(type) || DocTimeStampTypeName.Equals(type));

    private static void BuildPageTreeUpdates(
        ObjectRegistry objects,
        IndirectReference topPagesRef,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> pages,
        SortedDictionary<int, (int Generation, PdfObject Value)> dirty)
    {
        var originalPagesDict = objects[topPagesRef] as PdfDictionary ?? new PdfDictionary();
        var newPagesDict = new PdfDictionary();
        foreach (var (key, value) in originalPagesDict)
        {
            if (!ReferenceEquals(key, KidsName) && !ReferenceEquals(key, CountName))
            {
                newPagesDict.Set(key, value);
            }
        }

        if (!newPagesDict.ContainsKey(PdfName.Type))
        {
            newPagesDict.Set(PdfName.Type, PagesName);
        }

        newPagesDict.Set(KidsName, new PdfArray(pages.Select(static p => (PdfObject)new PdfReference(p.Reference))));
        newPagesDict.Set(CountName, PdfNumber.Get(pages.Count));
        dirty[topPagesRef.Number] = (topPagesRef.Generation, newPagesDict);

        foreach (var (reference, dictionary) in pages)
        {
            var parentMatches = dictionary.TryGetValue(ParentName, out var parentValue)
                && parentValue is PdfReference parentRef
                && parentRef.Target.Number == topPagesRef.Number;

            if (parentMatches)
            {
                continue;
            }

            var copy = new PdfDictionary();
            foreach (var (key, value) in dictionary)
            {
                if (!ReferenceEquals(key, ParentName))
                {
                    copy.Set(key, value);
                }
            }

            copy.Set(ParentName, new PdfReference(topPagesRef));
            dirty[reference.Number] = (reference.Generation, copy);
        }
    }

    private static void WriteXrefAndTrailer(
        Stream output,
        SortedDictionary<int, (int Generation, PdfObject Value)> dirty,
        Dictionary<int, long> offsets,
        PdfDictionary originalTrailer,
        long previousStartXrefOffset,
        PdfOptions options)
    {
        var xrefOffset = output.Position;
        WriteAscii(output, "xref\n");

        foreach (var (start, count) in GroupContiguous(dirty.Keys))
        {
            WriteAscii(output, $"{start} {count}\n");
            for (var number = start; number < start + count; number++)
            {
                var entry = dirty[number];
                WriteAscii(output, $"{offsets[number]:D10} {entry.Generation:D5} n \n");
            }
        }

        var originalSize = originalTrailer.TryGetValue(PdfName.Size, out var sizeValue) && sizeValue is PdfNumber { IsInteger: true } size && size.TryToInt32(out var converted) ? converted : 0;
        var maxDirtyExclusive = dirty.Count > 0 ? dirty.Keys.Max() + 1 : 0;
        var newSize = Math.Max(originalSize, maxDirtyExclusive);

        var (id0, id1) = ComputeId(originalTrailer, options);

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(newSize));
        trailer.Set(PdfName.Root, originalTrailer[PdfName.Root]);
        if (originalTrailer.TryGetValue(PdfName.Info, out var info))
        {
            trailer.Set(PdfName.Info, info);
        }

        // A third-party PDF processor determines whether a file is encrypted from the
        // NEWEST revision's own trailer, not by walking /Prev looking for /Encrypt (qpdf, and
        // presumably others, report a file with no /Encrypt in its latest trailer as
        // unencrypted outright, even when an earlier revision carried one — verified against
        // qpdf --check). Every appendix on an encrypted source must therefore repeat /Encrypt
        // in its own trailer, exactly like /Root and /Info above, or the file this writer just
        // (correctly) re-encrypted becomes unreadable by anything that isn't PlumePDF itself.
        if (originalTrailer.TryGetValue(PdfName.Encrypt, out var encrypt))
        {
            trailer.Set(PdfName.Encrypt, encrypt);
        }

        trailer.Set(PdfName.Prev, PdfNumber.Get(previousStartXrefOffset));
        trailer.Set(PdfName.Id, new PdfArray([PdfString.FromHex(id0), PdfString.FromHex(id1)]));

        WriteAscii(output, "trailer\n");
        ObjectSerializer.WriteValue(output, trailer);
        WriteAscii(output, $"\nstartxref\n{xrefOffset}\n%%EOF");
    }

    private static (byte[] Id0, byte[] Id1) ComputeId(PdfDictionary originalTrailer, PdfOptions options)
    {
        byte[] id0;
        if (originalTrailer.TryGetValue(PdfName.Id, out var idValue) && idValue is PdfArray { Count: > 0 } idArray && idArray[0] is PdfString existingId0)
        {
            id0 = existingId0.Bytes.ToArray();
        }
        else if (originalTrailer.ContainsKey(PdfName.Encrypt))
        {
            // StandardSecurityHandler derived this source's file key from whatever
            // /ID[0] (or its absence — an empty id, per ValidateEncryption's "byte[]? fileId"
            // contract) was present at Open time. Fabricating a fresh random /ID[0] here would
            // make this appendix's own trailer disagree with the id every already-encrypted
            // object (and this appendix's newly re-encrypted ones) were actually keyed
            // against, so on reopen the newly-derived key wouldn't authenticate. Preserve the
            // absence instead of inventing an id that was never part of the key derivation.
            id0 = [];
        }
        else
        {
            id0 = DeterministicContext.CreateDocumentId(options.Deterministic);
        }

        var id1 = DeterministicContext.CreateDocumentId(options.Deterministic);
        return (id0, id1);
    }

    private static IEnumerable<(int Start, int Count)> GroupContiguous(IEnumerable<int> sortedNumbers)
    {
        int? start = null;
        int? previous = null;
        var count = 0;

        foreach (var number in sortedNumbers)
        {
            if (start is null)
            {
                start = number;
                count = 1;
            }
            else if (number == previous + 1)
            {
                count++;
            }
            else
            {
                yield return (start.Value, count);
                start = number;
                count = 1;
            }

            previous = number;
        }

        if (start is not null)
        {
            yield return (start.Value, count);
        }
    }

    private static void WriteAscii(Stream output, string text) => output.Write(System.Text.Encoding.ASCII.GetBytes(text));
}
