using System.Collections.Concurrent;
using PlumePdf.Fonts.Outlines;

namespace PlumePdf.Fonts.Substitute;

/// <summary>
/// The AOT-safe loader for the compiled-in substitute-font bundle (later widened
/// to two formats): looks a bundled face up by key through
/// <see cref="SubstituteFontBlobs"/>'s or <see cref="SubstituteCffBlobs"/>'s generated switch (no
/// reflection — <c>Assembly.GetManifestResourceStream</c>/<c>Assembly.GetTypes</c> never enter the
/// picture, so this needs no <c>ReflectionBanTests</c> carve-out) and parses it once, caching the
/// parsed <see cref="TrueTypeFontProgram"/> or <see cref="CffParser"/> so repeated glyph lookups
/// against the same face across a render don't re-walk its table directory / re-run its Type 2
/// interpreter setup every time.
/// </summary>
internal static class SubstituteFontStore
{
    // A cache, not a shared mutable data structure the deterministic render path reads
    // order-dependently from: every entry is populated by GetOrAdd from the same
    // fixed, compiled-in blob regardless of population order, so two runs always converge on
    // byte-identical parsed state — this is memoization, not accumulated render state.
    private static readonly ConcurrentDictionary<string, TrueTypeFontProgram> Cache = new(StringComparer.Ordinal);

    // Same memoization contract as Cache above, for the bundled CFF faces (Foxit Symbol/Dingbats).
    // CffParser is immutable after Parse (its _limits is read nowhere after construction;
    // Type2Interpreter state is per call), so an entry is a pure function of the compiled-in blob.
    private static readonly ConcurrentDictionary<string, CffParser> CffCache = new(StringComparer.Ordinal);

    /// <summary>Every bundled face key this build was compiled with — both TrueType (Liberation) and CFF (Foxit) — e.g. for the AOT-smoke lane to enumerate and touch each one.</summary>
    public static IReadOnlyList<string> AvailableFaceKeys { get; } = [.. SubstituteFontBlobs.FaceKeys, .. SubstituteCffBlobs.FaceKeys];

    /// <summary>
    /// Which parser <paramref name="faceKey"/>'s bundled program needs — <see cref="TryGetFont"/>
    /// for <see cref="SubstituteFaceKind.TrueType"/>, <see cref="TryGetCffFont"/> for
    /// <see cref="SubstituteFaceKind.Cff"/>. An unrecognized key (not in either generated manifest)
    /// is reported as <see cref="SubstituteFaceKind.TrueType"/> — the caller's subsequent
    /// <see cref="TryGetFont"/> call is what actually reports "not found" — since every key this
    /// build can actually produce is guaranteed to be in one of the two manifests.
    /// </summary>
    public static SubstituteFaceKind GetFaceKind(string faceKey) =>
        SubstituteCffBlobs.TryGetBlob(faceKey, out _) ? SubstituteFaceKind.Cff : SubstituteFaceKind.TrueType;

    /// <summary>
    /// Looks up and lazily parses the bundled TrueType face named <paramref name="faceKey"/> (e.g.
    /// <c>"LiberationSans-Regular"</c> — see <see cref="SubstituteFontMap.Resolve"/> for how
    /// callers arrive at a key). Returns <see langword="false"/> for any key not in the compiled-in
    /// TrueType bundle rather than throwing — including a key that names a bundled CFF face
    /// instead (a kind mismatch is a programmer error in the caller: every code path that
    /// resolves a face key first consults <see cref="GetFaceKind"/> and routes accordingly), not a
    /// document-driven failure, so there is no <c>PLUME####</c> code for it.
    /// </summary>
    public static bool TryGetFont(string faceKey, FontReadLimits limits, out TrueTypeFontProgram font)
    {
        ArgumentException.ThrowIfNullOrEmpty(faceKey);

        if (Cache.TryGetValue(faceKey, out var cached))
        {
            font = cached;
            return true;
        }

        if (!SubstituteFontBlobs.TryGetBlob(faceKey, out var data))
        {
            font = null!;
            return false;
        }

        // TrueTypeFontProgram.Parse needs an owned byte[] (glyph spans are addressed relative
        // to it and FontSubsetter-adjacent code expects to hold onto it) — ReadOnlySpan<byte>
        // can't be stored past this call anyway, so the one ToArray() copy per *distinct* face
        // (never per glyph, thanks to the cache above) is the actual, unavoidable cost of
        // handing a FieldRVA blob to an API that needs an array.
        var parsed = TrueTypeFontProgram.Parse(data.ToArray(), limits);
        font = Cache.GetOrAdd(faceKey, parsed);
        return true;
    }

    /// <summary>
    /// Looks up and lazily parses the bundled bare-CFF face named <paramref name="faceKey"/> (the
    /// Foxit Symbol/Dingbats faces — see <see cref="Standard14SymbolFonts"/> for how callers arrive
    /// at a key). Returns <see langword="false"/> — never throws — both for a key not in the
    /// compiled-in CFF bundle (including one that names a bundled TrueType face instead; a kind
    /// mismatch is a programmer error, see <see cref="TryGetFont"/>'s remarks) and for a
    /// compiled-in blob that fails to parse under the caller's <paramref name="limits"/> (e.g. a
    /// glyph-count ceiling far below the face's real count): the caller falls back to an
    /// advances-only render rather than propagating a coded failure for a substitute the document
    /// never asked to embed.
    /// </summary>
    public static bool TryGetCffFont(string faceKey, FontReadLimits limits, out CffParser font)
    {
        ArgumentException.ThrowIfNullOrEmpty(faceKey);

        if (CffCache.TryGetValue(faceKey, out var cached))
        {
            font = cached;
            return true;
        }

        if (!SubstituteCffBlobs.TryGetBlob(faceKey, out var data))
        {
            font = null!;
            return false;
        }

        try
        {
            var parsed = CffParser.Parse(data.ToArray(), limits);
            font = CffCache.GetOrAdd(faceKey, parsed);
            return true;
        }
        catch (PlumePdfException)
        {
            font = null!;
            return false;
        }
    }
}
