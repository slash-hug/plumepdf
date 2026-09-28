using System.Numerics;

namespace PlumePdf.Objects;

/// <summary>
/// Builds the primary hint stream's payload for <see cref="Linearizer"/> (ISO 32000-1 §F.4):
/// the page offset hint table (Tables F.3/F.4, always first, at offset 0) followed — at the
/// next byte boundary, the position the stream dictionary's <c>/S</c> entry names — by the
/// shared object hint table (Tables F.5/F.6). Every field is written MSB-first into a bit
/// stream at the exact widths the header section declares; every position and length fed in
/// by the caller is <em>hint-stream-elided</em> per §F.4's positioning rule (computed as if
/// the primary hint stream itself were not in the file). Each shared-object entry describes a
/// group of exactly one object — the representation §F.4.2 permits whenever grouping is not
/// used — which keeps identifiers a straight index: first-page objects in file order, then
/// shared-section objects in file order.
/// </summary>
internal static class LinearizationHintBuilder
{
    /// <summary>One page's hint inputs, all in hint-stream-elided coordinates.</summary>
    /// <param name="ObjectCount">The number of objects in the page's group, including the page object itself.</param>
    /// <param name="GroupLength">The byte length from the page object's first byte to the last byte of the group's last object.</param>
    /// <param name="ContentOffset">The offset of the page's content stream object from the beginning of the page's group, or 0 when the page has none.</param>
    /// <param name="ContentLength">The full serialized length of the content stream object (object overhead included), or 0 when the page has none.</param>
    /// <param name="SharedIdentifiers">The shared-object-table indices this page references, ascending; always empty for the first page (Table F.4 item 3).</param>
    public readonly record struct PageHint(int ObjectCount, long GroupLength, long ContentOffset, long ContentLength, int[] SharedIdentifiers);

    /// <summary>The outline hierarchy's group, for the <c>/O</c> generic hint table (Table F.9).</summary>
    /// <param name="FirstObjectNumber">The object number of the group's first object.</param>
    /// <param name="Location">The elided offset of the group's first object.</param>
    /// <param name="ObjectCount">The number of objects in the group.</param>
    /// <param name="GroupLength">The group's total byte length.</param>
    public readonly record struct OutlineGroup(int FirstObjectNumber, long Location, int ObjectCount, long GroupLength);

    /// <summary>
    /// Builds the payload. Returns the raw (uncompressed) hint bytes, the byte offset of the
    /// shared object hint table within them (the stream dictionary's <c>/S</c> value), and —
    /// when <paramref name="outlineGroup"/> is supplied — the byte offset of the outline hint
    /// table (the <c>/O</c> value).
    /// </summary>
    /// <param name="pages">Every page's hints, first page first.</param>
    /// <param name="firstPageObjectLocation">The elided offset of the first page's page object (Table F.3 item 2).</param>
    /// <param name="firstPageGroupLengths">The serialized length of each first-page-section object, file order — one single-object shared group per object (§F.4.2: every first-page object gets an entry, shared or not).</param>
    /// <param name="sharedSectionGroupLengths">The serialized length of each shared-objects-section (part 8) object, file order.</param>
    /// <param name="firstSharedObjectNumber">The object number of the first object in the shared objects section (Table F.5 item 1) — the number that section's first object carries, or would carry if the section is empty.</param>
    /// <param name="sharedSectionLocation">The elided offset of the shared objects section's first object (Table F.5 item 2), or of where it would begin if empty.</param>
    /// <param name="outlineGroup">The outline hierarchy's group, when the document has one — Table F.2's <c>O</c> key is present only if a document outline exists.</param>
    public static (byte[] Payload, int SharedTableOffset, int? OutlineTableOffset) Build(
        IReadOnlyList<PageHint> pages,
        long firstPageObjectLocation,
        IReadOnlyList<long> firstPageGroupLengths,
        IReadOnlyList<long> sharedSectionGroupLengths,
        int firstSharedObjectNumber,
        long sharedSectionLocation,
        OutlineGroup? outlineGroup = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(firstPageGroupLengths);
        ArgumentNullException.ThrowIfNull(sharedSectionGroupLengths);
        ArgumentOutOfRangeException.ThrowIfZero(pages.Count);

        var writer = new BitWriter();

        // --- Page offset hint table, header section (Table F.3) -----------------------------
        var leastObjects = pages.Min(static p => p.ObjectCount);
        var objectDeltaBits = BitsNeeded(pages.Max(static p => p.ObjectCount) - leastObjects);
        var leastLength = pages.Min(static p => p.GroupLength);
        var lengthDeltaBits = BitsNeeded(pages.Max(static p => p.GroupLength) - leastLength);
        var leastContentOffset = pages.Min(static p => p.ContentOffset);
        var contentOffsetDeltaBits = BitsNeeded(pages.Max(static p => p.ContentOffset) - leastContentOffset);
        var leastContentLength = pages.Min(static p => p.ContentLength);
        var contentLengthDeltaBits = BitsNeeded(pages.Max(static p => p.ContentLength) - leastContentLength);
        var sharedCountBits = BitsNeeded(pages.Max(static p => p.SharedIdentifiers.Length));
        var greatestIdentifier = pages.SelectMany(static p => p.SharedIdentifiers).DefaultIfEmpty(0).Max();
        var identifierBits = BitsNeeded(greatestIdentifier);
        const int numeratorBits = 0; // every numerator is 0: "first referenced in the first fraction" is the honest constant for a writer that does not track intra-content-stream reference positions.
        const int fractionDenominator = 4;

        writer.Write((ulong)leastObjects, 32);
        writer.Write((ulong)firstPageObjectLocation, 32);
        writer.Write((ulong)objectDeltaBits, 16);
        writer.Write((ulong)leastLength, 32);
        writer.Write((ulong)lengthDeltaBits, 16);
        writer.Write((ulong)leastContentOffset, 32);
        writer.Write((ulong)contentOffsetDeltaBits, 16);
        writer.Write((ulong)leastContentLength, 32);
        writer.Write((ulong)contentLengthDeltaBits, 16);
        writer.Write((ulong)sharedCountBits, 16);
        writer.Write((ulong)identifierBits, 16);
        writer.Write(numeratorBits, 16);
        writer.Write(fractionDenominator, 16);

        // --- Page offset hint table, per-page entries (Table F.4, item-major order) ----------
        // Each item's run is padded to the next byte boundary before the next item's run
        // begins. Annex F leaves this unstated ("fields of arbitrary width without regard to
        // byte boundaries"), but the qpdf oracle both writes and reads per-item-run byte
        // alignment (established from its output, never its source, per the clean-room policy
        // in AGENTS.md), and a linearized file only counts as valid here if `qpdf --check` says so.
        foreach (var page in pages)
        {
            writer.Write((ulong)(page.ObjectCount - leastObjects), objectDeltaBits);
        }

        writer.AlignToByte();
        foreach (var page in pages)
        {
            writer.Write((ulong)(page.GroupLength - leastLength), lengthDeltaBits);
        }

        writer.AlignToByte();
        foreach (var page in pages)
        {
            writer.Write((ulong)page.SharedIdentifiers.Length, sharedCountBits);
        }

        // Items 4 and 5 exist only for pages after the first (the first page's count is 0 by
        // Table F.4 item 3's own rule, so skipping it writes nothing either way).
        writer.AlignToByte();
        foreach (var page in pages.Skip(1))
        {
            foreach (var identifier in page.SharedIdentifiers)
            {
                writer.Write((ulong)identifier, identifierBits);
            }
        }

        // Item 5 (fraction numerators) is numeratorBits == 0 wide: nothing to write.
        writer.AlignToByte();
        foreach (var page in pages)
        {
            writer.Write((ulong)(page.ContentOffset - leastContentOffset), contentOffsetDeltaBits);
        }

        writer.AlignToByte();
        foreach (var page in pages)
        {
            writer.Write((ulong)(page.ContentLength - leastContentLength), contentLengthDeltaBits);
        }

        // --- Shared object hint table (Tables F.5/F.6), at the next byte boundary -----------
        writer.AlignToByte();
        var sharedTableOffset = writer.ByteCount;

        var allGroupLengths = firstPageGroupLengths.Concat(sharedSectionGroupLengths).ToArray();
        var leastGroupLength = allGroupLengths.Length == 0 ? 0 : allGroupLengths.Min();
        var groupLengthDeltaBits = allGroupLengths.Length == 0 ? 0 : BitsNeeded(allGroupLengths.Max() - leastGroupLength);
        const int groupObjectCountBits = 0; // every group holds exactly one object, so the stored count-minus-1 is always 0 and needs no bits

        writer.Write((ulong)firstSharedObjectNumber, 32);
        writer.Write((ulong)sharedSectionLocation, 32);
        writer.Write((ulong)firstPageGroupLengths.Count, 32);
        writer.Write((ulong)allGroupLengths.Length, 32);
        writer.Write((ulong)groupObjectCountBits, 16);
        writer.Write((ulong)leastGroupLength, 32);
        writer.Write((ulong)groupLengthDeltaBits, 16);

        // Entries run item-major across the whole table — first-page groups then
        // shared-section groups form one run per item, each run padded to a byte boundary,
        // matching the oracle's reading of §F.4.2's two-sequence layout.
        foreach (var length in allGroupLengths)
        {
            writer.Write((ulong)(length - leastGroupLength), groupLengthDeltaBits); // item 1: length delta
        }

        writer.AlignToByte();
        foreach (var _ in allGroupLengths)
        {
            writer.Write(0, 1); // item 2: signature-present flag — never; item 3 (the MD5 signature) is therefore absent.
        }

        writer.AlignToByte();
        foreach (var _ in allGroupLengths)
        {
            writer.Write(0, groupObjectCountBits); // item 4: objects in group, minus 1 — always a single-object group.
        }

        // --- Outline hint table (Table F.9's generic format), at the next byte boundary ------
        int? outlineTableOffset = null;
        if (outlineGroup is { } outlines)
        {
            writer.AlignToByte();
            outlineTableOffset = writer.ByteCount;
            writer.Write((ulong)outlines.FirstObjectNumber, 32);
            writer.Write((ulong)outlines.Location, 32);
            writer.Write((ulong)outlines.ObjectCount, 32);
            writer.Write((ulong)outlines.GroupLength, 32);
        }

        return (writer.ToArray(), sharedTableOffset, outlineTableOffset);
    }

    private static int BitsNeeded(long greatestValue) =>
        greatestValue <= 0 ? 0 : 64 - BitOperations.LeadingZeroCount((ulong)greatestValue);

    /// <summary>An MSB-first bit accumulator (§F.4: "a bit stream, high-order bit first").</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bitBuffer;
        private int _bitCount;

        public int ByteCount => _bytes.Count;

        public void Write(ulong value, int bits)
        {
            if (bits is < 0 or > 32)
            {
                throw new PlumePdfException("PLUME5010", $"Internal writer invariant violated: a linearization hint field was declared {bits} bits wide (the legal range is 0-32).");
            }

            if (bits < 64 && value >> bits != 0)
            {
                throw new PlumePdfException("PLUME5010", $"Internal writer invariant violated: linearization hint value {value} does not fit in its declared {bits}-bit field.");
            }

            for (var i = bits - 1; i >= 0; i--)
            {
                _bitBuffer = (_bitBuffer << 1) | (int)((value >> i) & 1);
                _bitCount++;
                if (_bitCount == 8)
                {
                    _bytes.Add((byte)_bitBuffer);
                    _bitBuffer = 0;
                    _bitCount = 0;
                }
            }
        }

        public void AlignToByte()
        {
            if (_bitCount > 0)
            {
                _bytes.Add((byte)(_bitBuffer << (8 - _bitCount)));
                _bitBuffer = 0;
                _bitCount = 0;
            }
        }

        public byte[] ToArray()
        {
            AlignToByte();
            return [.. _bytes];
        }
    }
}
