using System.Numerics;

namespace PlumePdf.Filters.Jpx;

/// <summary>Which of the two coding modes a codeword segment uses (B.10.7, the selective-bypass style flag).</summary>
internal enum JpxSegmentKind
{
    /// <summary>MQ arithmetic-coded.</summary>
    Mq,

    /// <summary>Raw (bypass), bit-unstuffed like a packet header (B.10.7, style flag 0x01 past pass 10).</summary>
    Raw,
}

/// <summary>One codeword segment's shape within a packet's new passes for a code-block: how many coding passes it carries and which coder decoded them.</summary>
internal readonly record struct JpxSegmentPlan(int Passes, JpxSegmentKind Kind);

/// <summary>One packet's coordinates in tier-2 (B.9): which layer, resolution level, component and precinct it belongs to.</summary>
internal readonly record struct JpxPacketKey(int Layer, int Resolution, int Component, int Precinct);

/// <summary>
/// Tier-2 (T.800 B.10, B.12): packet headers (inclusion, zero bit-planes, pass counts, <c>Lblock</c>,
/// codeword-segment lengths), <c>SOP</c>/<c>EPH</c>, the five progression iterators and <c>POC</c>
/// volumes, layer accumulation across tile-parts. Fills each code-block's segments.
/// </summary>
/// <remarks>
/// The decision tables B.10.6 (number of coding passes) and B.10.7.1 (segment count/length) are
/// exposed as pure functions of already-known values (<see cref="PassCountFromFields"/>,
/// <see cref="PlanSegments"/>, <see cref="SegmentLengthFieldBits"/>) precisely so they are
/// unit-testable without a live bit stream, while the header/body reading path
/// (<see cref="DecodeOnePacket"/>) runs end-to-end over <see cref="JpxBitReader"/> and
/// <see cref="JpxTagTree"/> and is exercised by the oracle-backed integration tests.
/// The five progression-order iterators are likewise pure over
/// <see cref="JpxTile"/>/<see cref="JpxCodestreamHeader"/> data — no bit reading involved — and
/// are fully unit-tested.
/// </remarks>
internal static class JpxPacketDecoder
{
    private const byte CodeBlockStyleBypass = 0x01;
    private const byte CodeBlockStyleTermAll = 0x04;

    /// <summary>The last pass still coded by the initial MQ run under the bypass style (B.10.7): 1 (cleanup of the first bit-plane) + 3×3 (three full bit-planes of sig-prop/mag-ref/cleanup).</summary>
    private const int BypassMqRunLength = 10;

    /// <summary>
    /// Largest <c>Lblock</c> this decoder accepts. A codeword-segment length field is
    /// <c>Lblock + ⌊log₂ passes⌋</c> bits wide and must stay within
    /// <see cref="JpxBitReader.MaxFieldBits"/>; a tile-part is at most 2^32 bytes long, so no
    /// conformant segment ever needs a length field wider than 31 bits — a unary growth code
    /// pushing <c>Lblock</c> past this is a malformed header, not a huge segment.
    /// </summary>
    private const int MaxLblock = JpxBitReader.MaxFieldBits;

    /// <summary>Decodes every packet of <paramref name="tile"/> across its <paramref name="tileParts"/>, appending <see cref="JpxSegmentRef"/>s to each <see cref="JpxCodeBlock"/>.</summary>
    /// <remarks><paramref name="tileParts"/> is this tile's own tile-parts in <c>TPsot</c> order; <see cref="JpxSegmentRef.TilePartIndex"/> indexes into this list.</remarks>
    public static void DecodeTilePackets(JpxTile tile, JpxCodestreamHeader header, IReadOnlyList<JpxTilePart> tileParts, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (tileParts.Count == 0)
        {
            return;
        }

        var mainProgression = tile.Components[0].Coding.Progression;
        var mainLayers = tile.Components[0].Coding.Layers;
        var maxResolutions = 0;
        foreach (var comp in tile.Components)
        {
            maxResolutions = Math.Max(maxResolutions, comp.Resolutions.Length);
        }

        // Every packet (layer, resolution, component, precinct) appears exactly once in the
        // codestream (B.12): a POC volume re-covering a range an earlier volume (or the same
        // enumeration) already emitted skips those packets, so the visited set is keyed on the
        // FULL packet key — never on a (layer, resolution, component) triple, which under the
        // position-driven orders (RPCL/PCRL/CPRL) recurs non-contiguously for every precinct.
        var visited = new HashSet<JpxPacketKey>();
        var cursor = new PacketCursor(tileParts);

        foreach (var volume in tile.ProgressionVolumes)
        {
            var keys = EnumerateProgression(header, tile, volume.Progression, volume.LYEpoc, volume.RSpoc, volume.REpoc, volume.CSpoc, volume.CEpoc);
            if (!DecodeKeys(keys, visited, tile, header, cursor, options, diagnostics))
            {
                return;
            }
        }

        var remainder = EnumerateProgression(header, tile, mainProgression, mainLayers, 0, maxResolutions, 0, tile.Components.Length);
        DecodeKeys(remainder, visited, tile, header, cursor, options, diagnostics);
    }

    private static bool DecodeKeys(IEnumerable<JpxPacketKey> keys, HashSet<JpxPacketKey> visited, JpxTile tile, JpxCodestreamHeader header, PacketCursor cursor, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        foreach (var key in keys)
        {
            if (!visited.Add(key))
            {
                continue;
            }

            if (DecodeOnePacket(tile, header, key, cursor))
            {
                continue;
            }

            FilterDiagnostics.ReportDeviation(
                JpxDiagnosticCodes.PacketHeaderInvalid,
                $"JPXDecode: packet header malformed for tile {tile.Index} (layer {key.Layer}, resolution {key.Resolution}, component {key.Component}, precinct {key.Precinct}); remaining packets of this tile are skipped.",
                options,
                diagnostics,
                subject: null);
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Packet header + body (needs a live JpxBitReader/JpxTagTree).
    // ------------------------------------------------------------------

    private static bool DecodeOnePacket(JpxTile tile, JpxCodestreamHeader header, JpxPacketKey key, PacketCursor cursor)
    {
        var comp = tile.Components[key.Component];
        if (key.Resolution >= comp.Resolutions.Length)
        {
            return true;
        }

        var resolution = comp.Resolutions[key.Resolution];
        var coding = comp.Coding;
        var data = cursor.CurrentData.Span;

        try
        {
            var reader = new JpxBitReader(data, cursor.Offset);

            if (coding.Sop)
            {
                TrySkipMarker(ref reader, data, marker: 0xFF91, totalBytes: 6);
            }

            if (reader.ReadBit() == 0)
            {
                // Zero-length bit: no code-block in this precinct contributes to this layer.
                reader.AlignToByte();
                if (coding.Eph)
                {
                    TrySkipMarker(ref reader, data, marker: 0xFF92, totalBytes: 2);
                }

                return cursor.Advance(reader.Position);
            }

            var newSegments = new List<(JpxCodeBlock Block, int Length, int Passes)>();

            foreach (var subband in resolution.Subbands)
            {
                if (key.Precinct >= subband.Precincts.Length)
                {
                    continue;
                }

                var precinct = subband.Precincts[key.Precinct];
                var treeHeight = precinct.CodeBlocksWide > 0 ? precinct.CodeBlocks.Length / precinct.CodeBlocksWide : 0;
                precinct.Inclusion ??= new JpxTagTree(precinct.CodeBlocksWide, treeHeight);
                precinct.ZeroBitPlanes ??= new JpxTagTree(precinct.CodeBlocksWide, treeHeight);

                for (var i = 0; i < precinct.CodeBlocks.Length; i++)
                {
                    var block = precinct.CodeBlocks[i];
                    var bx = precinct.CodeBlocksWide > 0 ? i % precinct.CodeBlocksWide : 0;
                    var by = precinct.CodeBlocksWide > 0 ? i / precinct.CodeBlocksWide : 0;

                    bool included;
                    if (!block.Included)
                    {
                        included = precinct.Inclusion.DecodeInclusion(ref reader, bx, by, key.Layer + 1);
                        if (included)
                        {
                            block.Included = true;
                            block.ZeroBitPlanes = precinct.ZeroBitPlanes.DecodeValue(ref reader, bx, by);
                        }
                    }
                    else
                    {
                        included = reader.ReadBit() != 0;
                    }

                    if (!included)
                    {
                        continue;
                    }

                    var newPasses = DecodeNumberOfCodingPasses(ref reader);
                    block.Lblock = GrowLblock(block.Lblock, DecodeUnary(ref reader, MaxLblock));
                    if (block.Lblock > MaxLblock)
                    {
                        throw new JpxPacketHeaderException($"Lblock grew to {block.Lblock}; a codeword-segment length field wider than {MaxLblock} bits is malformed.");
                    }

                    foreach (var plan in PlanSegments(coding.CodeBlockStyle, block.PassesSignalled, newPasses))
                    {
                        var lengthBits = SegmentLengthFieldBits(block.Lblock, plan.Passes);
                        var length = reader.ReadBits(lengthBits); // throws past MaxFieldBits
                        newSegments.Add((block, length, plan.Passes));
                    }

                    block.PassesSignalled += newPasses;
                }
            }

            reader.AlignToByte();
            if (coding.Eph)
            {
                TrySkipMarker(ref reader, data, marker: 0xFF92, totalBytes: 2);
            }

            // Segment lengths are 31-bit fields; their running sum is accumulated in long and
            // checked against the tile-part before any of them is recorded, so a header whose
            // bodies overshoot the data can neither wrap the offset negative nor leave a
            // half-recorded packet behind.
            long offset = reader.Position;
            foreach (var (_, length, _) in newSegments)
            {
                offset += length;
            }

            if (offset > data.Length)
            {
                return false;
            }

            var segmentOffset = reader.Position;
            foreach (var (block, length, passes) in newSegments)
            {
                block.Segments.Add(new JpxSegmentRef(cursor.TilePartIndex, segmentOffset, length, passes));
                segmentOffset += length;
            }

            return cursor.Advance(segmentOffset);
        }
        catch (JpxPacketHeaderException)
        {
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Checks the two raw bytes at <paramref name="reader"/>'s current position for a marker (<c>SOP</c> 0xFF91 or <c>EPH</c> 0xFF92) and, if present, skips it — tolerating either presence or absence regardless of what <c>Scod</c> declared (real encoders disagree with their own header bit). Compared as raw bytes: a <see cref="JpxBitReader"/> would apply B.10.1's stuffing rule to the byte after the <c>0xFF</c> and read <c>0x7F91</c>, never matching.</summary>
    private static void TrySkipMarker(ref JpxBitReader reader, ReadOnlySpan<byte> data, ushort marker, int totalBytes)
    {
        var position = reader.Position;
        if (position + 1 < data.Length && data[position] == (byte)(marker >> 8) && data[position + 1] == (byte)marker)
        {
            reader = new JpxBitReader(data, position + totalBytes);
        }
    }

    /// <summary>Reads a unary code (a run of 1-bits closed by a 0-bit). A run longer than <paramref name="maxOnes"/> is a malformed header — the only unary field in a packet header is <c>Lblock</c>'s growth, bounded by <see cref="MaxLblock"/>.</summary>
    private static int DecodeUnary(ref JpxBitReader reader, int maxOnes)
    {
        var count = 0;
        while (reader.ReadBit() == 1)
        {
            count++;
            if (count > maxOnes)
            {
                throw new JpxPacketHeaderException($"a unary-coded packet-header field ran past {maxOnes} one-bits.");
            }
        }

        return count;
    }

    /// <summary>B.10.6: reads the prefix-coded number of new coding passes for one code-block, via <see cref="PassCountFromFields"/>.</summary>
    private static int DecodeNumberOfCodingPasses(ref JpxBitReader reader)
    {
        if (reader.ReadBit() == 0)
        {
            return PassCountFromFields(moreThanOne: false, moreThanTwo: false, 0, 0, 0);
        }

        if (reader.ReadBit() == 0)
        {
            return PassCountFromFields(moreThanOne: true, moreThanTwo: false, 0, 0, 0);
        }

        var twoBitField = reader.ReadBits(2);
        if (twoBitField != 3)
        {
            return PassCountFromFields(moreThanOne: true, moreThanTwo: true, twoBitField, 0, 0);
        }

        var fiveBitField = reader.ReadBits(5);
        if (fiveBitField != 31)
        {
            return PassCountFromFields(moreThanOne: true, moreThanTwo: true, 3, fiveBitField, 0);
        }

        var sevenBitField = reader.ReadBits(7);
        return PassCountFromFields(moreThanOne: true, moreThanTwo: true, 3, 31, sevenBitField);
    }

    // ------------------------------------------------------------------
    // Pure decode tables — no bit stream involved.
    // ------------------------------------------------------------------

    /// <summary>
    /// B.10.6 number-of-coding-passes table, expressed over already-extracted field values rather
    /// than a live bit stream so the table itself is directly unit-testable. The prefix code is
    /// staged: <paramref name="moreThanOne"/> is the first bit (0 → 1 pass); <paramref name="moreThanTwo"/>
    /// is the second bit, read only when the first was 1 (0 → 2 passes); <paramref name="twoBitField"/>
    /// is read only once both were 1, and terminates the code unless it is 3; <paramref name="fiveBitField"/>
    /// is read only when <paramref name="twoBitField"/> is 3, and terminates unless it is 31;
    /// <paramref name="sevenBitField"/> is read only when <paramref name="fiveBitField"/> is 31.
    /// A field not reached by the earlier ones is ignored — callers pass 0 for it. Direct
    /// enumeration of every reachable combination yields the 164 contiguous values 1..164 (the
    /// two 1-bit codes, three 2-bit-field values 3/4/5, 31 five-bit-field values 6..36, and 128
    /// seven-bit-field values 37..164) — the exhaustive test proves the bijection.
    /// </summary>
    internal static int PassCountFromFields(bool moreThanOne, bool moreThanTwo, int twoBitField, int fiveBitField, int sevenBitField)
    {
        if (!moreThanOne)
        {
            return 1;
        }

        if (!moreThanTwo)
        {
            return 2;
        }

        if (twoBitField != 3)
        {
            return 3 + twoBitField;
        }

        if (fiveBitField != 31)
        {
            return 6 + fiveBitField;
        }

        return 37 + sevenBitField;
    }

    /// <summary>B.10.7.1: <c>Lblock</c> after reading a unary growth code of <paramref name="unaryOnesRead"/> leading one-bits before the terminating zero.</summary>
    internal static int GrowLblock(int currentLblock, int unaryOnesRead) => currentLblock + unaryOnesRead;

    /// <summary>B.10.7.1: the codeword-segment length field is <c>Lblock + ⌊log₂(passes in segment)⌋</c> bits.</summary>
    internal static int SegmentLengthFieldBits(int lblock, int passesInSegment) => lblock + BitOperations.Log2((uint)passesInSegment);

    /// <summary>
    /// B.10.7's segment-count rule for the <paramref name="newPasses"/> coding passes a packet newly
    /// signals for a code-block, given the block's <paramref name="priorPasses"/> already signalled
    /// in earlier packets/layers (needed because the bypass style's pass-10 cutoff and its raw
    /// sig-prop/mag-ref pairing are both counted from the block's first pass, not from this packet's
    /// first new one) and its <c>SPcod</c>/<c>SPcoc</c> <paramref name="codeBlockStyle"/> flags:
    /// <list type="bullet">
    /// <item>neither <c>bypass</c> nor <c>termAll</c>: one MQ segment covering every new pass.</item>
    /// <item><c>termAll</c> (with or without <c>bypass</c>): one segment per new pass — MQ, unless
    /// <c>bypass</c> is also set and the pass falls past the initial 10-pass MQ run, in which case
    /// sig-prop/mag-ref passes are raw and the cleanup pass is MQ (the same coding-mode rule
    /// <c>bypass</c> alone uses; <c>termAll</c> only changes segment granularity, not coding mode).</item>
    /// <item><c>bypass</c> alone: one MQ segment through pass <see cref="BypassMqRunLength"/> (10),
    /// then per bit-plane one raw segment for its sig-prop+mag-ref pair and one MQ segment for its
    /// cleanup pass.</item>
    /// </list>
    /// Only <c>bypass</c> (0x01) and <c>termAll</c> (0x04) affect this rule; the other four
    /// <see cref="JpxCodingStyle.CodeBlockStyle"/> bits (reset, vertically-causal, predictable
    /// termination, segmentation symbols) do not change segment boundaries.
    /// </summary>
    internal static IReadOnlyList<JpxSegmentPlan> PlanSegments(byte codeBlockStyle, int priorPasses, int newPasses)
    {
        if (newPasses <= 0)
        {
            return Array.Empty<JpxSegmentPlan>();
        }

        var bypass = (codeBlockStyle & CodeBlockStyleBypass) != 0;
        var termAll = (codeBlockStyle & CodeBlockStyleTermAll) != 0;
        var plans = new List<JpxSegmentPlan>();

        if (!bypass && !termAll)
        {
            plans.Add(new JpxSegmentPlan(newPasses, JpxSegmentKind.Mq));
            return plans;
        }

        if (termAll)
        {
            for (var i = 0; i < newPasses; i++)
            {
                var passIndex = priorPasses + i + 1;
                var kind = bypass && IsRawPass(passIndex) ? JpxSegmentKind.Raw : JpxSegmentKind.Mq;
                plans.Add(new JpxSegmentPlan(1, kind));
            }

            return plans;
        }

        // bypass alone.
        var consumed = 0;
        while (consumed < newPasses)
        {
            var passIndex = priorPasses + consumed + 1;
            if (!IsRawPass(passIndex))
            {
                var start = consumed;
                while (consumed < newPasses && !IsRawPass(priorPasses + consumed + 1))
                {
                    consumed++;
                }

                plans.Add(new JpxSegmentPlan(consumed - start, JpxSegmentKind.Mq));
                continue;
            }

            // Raw region: the sig-prop/mag-ref pair shares one raw segment; the cleanup pass that
            // follows is its own MQ segment. `offset` is 0 at sig-prop, 1 at mag-ref (never 2 here —
            // a cleanup pass is never itself a raw pass).
            var offset = (passIndex - (BypassMqRunLength + 1)) % 3;
            var rawWanted = 2 - offset;
            var rawCount = Math.Min(rawWanted, newPasses - consumed);
            plans.Add(new JpxSegmentPlan(rawCount, JpxSegmentKind.Raw));
            consumed += rawCount;

            if (rawCount == rawWanted && consumed < newPasses)
            {
                plans.Add(new JpxSegmentPlan(1, JpxSegmentKind.Mq));
                consumed++;
            }
        }

        return plans;
    }

    /// <summary>Whether 1-based global pass <paramref name="passIndex"/> is raw-coded under the bypass style: past the initial <see cref="BypassMqRunLength"/>-pass MQ run, sig-prop and mag-ref passes are raw, cleanup passes stay MQ.</summary>
    private static bool IsRawPass(int passIndex) => passIndex > BypassMqRunLength && (passIndex - (BypassMqRunLength + 1)) % 3 != 2;

    // ------------------------------------------------------------------
    // B-2: progression-order iterators (B.12) — pure over tile/header data, no bit stream.
    // ------------------------------------------------------------------

    /// <summary>Dispatches to the iterator for <paramref name="order"/>, bounded to <c>[resStart, resEnd)</c> resolutions, <c>[compStart, compEnd)</c> components and <paramref name="layers"/> layers (a <c>POC</c> volume's range, or the whole tile for the COD-progression remainder).</summary>
    internal static IEnumerable<JpxPacketKey> EnumerateProgression(JpxCodestreamHeader header, JpxTile tile, JpxProgression order, int layers, int resStart, int resEnd, int compStart, int compEnd) =>
        order switch
        {
            JpxProgression.Lrcp => EnumerateLrcp(tile, resStart, resEnd, compStart, compEnd, layers),
            JpxProgression.Rlcp => EnumerateRlcp(tile, resStart, resEnd, compStart, compEnd, layers),
            JpxProgression.Rpcl => EnumerateRpcl(header, tile, resStart, resEnd, compStart, compEnd, layers),
            JpxProgression.Pcrl => EnumeratePcrl(header, tile, resStart, resEnd, compStart, compEnd, layers),
            JpxProgression.Cprl => EnumerateCprl(header, tile, resStart, resEnd, compStart, compEnd, layers),
            _ => throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown JPX progression order."),
        };

    /// <summary>Layer–resolution–component–position (Table A.16): index-ordered, no reference-grid geometry needed.</summary>
    internal static IEnumerable<JpxPacketKey> EnumerateLrcp(JpxTile tile, int resStart, int resEnd, int compStart, int compEnd, int layers)
    {
        for (var l = 0; l < layers; l++)
        {
            for (var r = resStart; r < resEnd; r++)
            {
                for (var c = compStart; c < compEnd; c++)
                {
                    foreach (var key in PrecinctsOf(tile, l, r, c))
                    {
                        yield return key;
                    }
                }
            }
        }
    }

    /// <summary>Resolution–layer–component–position (Table A.16): index-ordered.</summary>
    internal static IEnumerable<JpxPacketKey> EnumerateRlcp(JpxTile tile, int resStart, int resEnd, int compStart, int compEnd, int layers)
    {
        for (var r = resStart; r < resEnd; r++)
        {
            for (var l = 0; l < layers; l++)
            {
                for (var c = compStart; c < compEnd; c++)
                {
                    foreach (var key in PrecinctsOf(tile, l, r, c))
                    {
                        yield return key;
                    }
                }
            }
        }
    }

    private static IEnumerable<JpxPacketKey> PrecinctsOf(JpxTile tile, int layer, int r, int c)
    {
        if (r >= tile.Components[c].Resolutions.Length)
        {
            yield break;
        }

        var res = tile.Components[c].Resolutions[r];
        var count = res.PrecinctsWide * res.PrecinctsHigh;
        for (var p = 0; p < count; p++)
        {
            yield return new JpxPacketKey(layer, r, c, p);
        }
    }

    /// <summary>Resolution–position–component–layer (Table A.16, B.12.1.3): position stepped per resolution level at the finest step among the components present at that level.</summary>
    internal static IEnumerable<JpxPacketKey> EnumerateRpcl(JpxCodestreamHeader header, JpxTile tile, int resStart, int resEnd, int compStart, int compEnd, int layers)
    {
        for (var r = resStart; r < resEnd; r++)
        {
            if (!TryMinStep(header, tile, r, compStart, compEnd, out var stepX, out var stepY))
            {
                continue;
            }

            for (long y = tile.Y0; y < tile.Y1; y += stepY - (y % stepY))
            {
                for (long x = tile.X0; x < tile.X1; x += stepX - (x % stepX))
                {
                    for (var c = compStart; c < compEnd; c++)
                    {
                        var precinct = PrecinctIndexIfIncluded(header, tile, c, r, x, y);
                        if (precinct is null)
                        {
                            continue;
                        }

                        for (var l = 0; l < layers; l++)
                        {
                            yield return new JpxPacketKey(l, r, c, precinct.Value);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Position–component–resolution–layer (Table A.16, B.12.1.3): position stepped once, at the finest step across every (component, resolution) pair in range.</summary>
    internal static IEnumerable<JpxPacketKey> EnumeratePcrl(JpxCodestreamHeader header, JpxTile tile, int resStart, int resEnd, int compStart, int compEnd, int layers)
    {
        if (!TryMinStepOverRange(header, tile, resStart, resEnd, compStart, compEnd, out var stepX, out var stepY))
        {
            yield break;
        }

        for (long y = tile.Y0; y < tile.Y1; y += stepY - (y % stepY))
        {
            for (long x = tile.X0; x < tile.X1; x += stepX - (x % stepX))
            {
                for (var c = compStart; c < compEnd; c++)
                {
                    for (var r = resStart; r < resEnd; r++)
                    {
                        var precinct = PrecinctIndexIfIncluded(header, tile, c, r, x, y);
                        if (precinct is null)
                        {
                            continue;
                        }

                        for (var l = 0; l < layers; l++)
                        {
                            yield return new JpxPacketKey(l, r, c, precinct.Value);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Component–position–resolution–layer (Table A.16, B.12.1.3): position stepped per component, at the finest step across that component's resolutions in range.</summary>
    internal static IEnumerable<JpxPacketKey> EnumerateCprl(JpxCodestreamHeader header, JpxTile tile, int resStart, int resEnd, int compStart, int compEnd, int layers)
    {
        for (var c = compStart; c < compEnd; c++)
        {
            if (!TryMinStepOverRange(header, tile, resStart, resEnd, c, c + 1, out var stepX, out var stepY))
            {
                continue;
            }

            for (long y = tile.Y0; y < tile.Y1; y += stepY - (y % stepY))
            {
                for (long x = tile.X0; x < tile.X1; x += stepX - (x % stepX))
                {
                    for (var r = resStart; r < resEnd; r++)
                    {
                        var precinct = PrecinctIndexIfIncluded(header, tile, c, r, x, y);
                        if (precinct is null)
                        {
                            continue;
                        }

                        for (var l = 0; l < layers; l++)
                        {
                            yield return new JpxPacketKey(l, r, c, precinct.Value);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reference-grid precinct step at resolution <paramref name="r"/> for <paramref name="component"/>
    /// (B.12.1.3): <c>XRsiz·2^(PPx+N_L−r)</c> horizontally, the <c>Y</c> analogue vertically.
    /// Computed in <see cref="long"/>: the shift is up to <c>15 + 32 = 47</c> bits, so an
    /// <see cref="int"/> shift masked it to <c>(PPx+N_L−r) mod 32</c> — a step of zero
    /// (<c>DivideByZeroException</c>) or <c>int.MinValue</c> (a position loop that never advances)
    /// for any stream with <c>PPx + N_L − r ≥ 31</c>.
    /// </summary>
    private static (long StepX, long StepY) Step(JpxCodestreamHeader header, JpxTile tile, int component, int r)
    {
        var info = header.Siz.Components[component];
        var res = tile.Components[component].Resolutions[r];
        var nl = tile.Components[component].Coding.DecompositionLevels;
        var stepX = (long)info.XRsiz << (res.PPx + nl - r);
        var stepY = (long)info.YRsiz << (res.PPy + nl - r);
        return (stepX, stepY);
    }

    private static bool TryMinStep(JpxCodestreamHeader header, JpxTile tile, int r, int compStart, int compEnd, out long stepX, out long stepY)
    {
        stepX = 0;
        stepY = 0;
        var found = false;
        for (var c = compStart; c < compEnd; c++)
        {
            if (r >= tile.Components[c].Resolutions.Length)
            {
                continue;
            }

            var (sx, sy) = Step(header, tile, c, r);
            stepX = found ? Math.Min(stepX, sx) : sx;
            stepY = found ? Math.Min(stepY, sy) : sy;
            found = true;
        }

        return found;
    }

    private static bool TryMinStepOverRange(JpxCodestreamHeader header, JpxTile tile, int resStart, int resEnd, int compStart, int compEnd, out long stepX, out long stepY)
    {
        stepX = 0;
        stepY = 0;
        var found = false;
        for (var c = compStart; c < compEnd; c++)
        {
            var end = Math.Min(resEnd, tile.Components[c].Resolutions.Length);
            for (var r = resStart; r < end; r++)
            {
                var (sx, sy) = Step(header, tile, c, r);
                stepX = found ? Math.Min(stepX, sx) : sx;
                stepY = found ? Math.Min(stepY, sy) : sy;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// B.12.1.3's inclusion test on absolute reference-grid coordinates: point (<paramref name="x"/>,
    /// <paramref name="y"/>) starts a precinct of <paramref name="component"/>'s resolution
    /// <paramref name="r"/> when it lies on the precinct lattice (<c>y mod YRsiz·2^(PPy+N_L−r) = 0</c>),
    /// or when it is the tile's own top/left edge and the resolution's origin is not lattice-aligned
    /// (<c>y = ty0</c> and <c>try0·2^(N_L−r) mod 2^(PPy+N_L−r) ≠ 0</c>) — the first precinct row/column
    /// then starts at the tile edge, not at a lattice point. Returns the precinct's raster-order
    /// index, <c>⌊⌈x / (XRsiz·2^(N_L−r))⌉ / 2^PPx⌋ − ⌊trx0 / 2^PPx⌋</c> (and the <c>y</c> analogue),
    /// when so, else <see langword="null"/>.
    /// </summary>
    private static int? PrecinctIndexIfIncluded(JpxCodestreamHeader header, JpxTile tile, int component, int r, long x, long y)
    {
        var comp = tile.Components[component];
        if (r >= comp.Resolutions.Length)
        {
            return null;
        }

        var res = comp.Resolutions[r];
        if (res.PrecinctsWide <= 0 || res.PrecinctsHigh <= 0)
        {
            return null;
        }

        var info = header.Siz.Components[component];
        var nl = comp.Coding.DecompositionLevels;
        var (stepX, stepY) = Step(header, tile, component, r);

        // Every term below is long: the level shift (N_L − r) reaches 32 and the precinct
        // lattice shift (PPx + N_L − r) reaches 47, both past what an int shift represents.
        var yIncluded = y % stepY == 0 || (y == tile.Y0 && ((long)res.Y0 << (nl - r)) % (1L << (res.PPy + nl - r)) != 0);
        var xIncluded = x % stepX == 0 || (x == tile.X0 && ((long)res.X0 << (nl - r)) % (1L << (res.PPx + nl - r)) != 0);
        if (!yIncluded || !xIncluded)
        {
            return null;
        }

        var px = (CeilDiv(x, (long)info.XRsiz << (nl - r)) >> res.PPx) - (res.X0 >> res.PPx);
        var py = (CeilDiv(y, (long)info.YRsiz << (nl - r)) >> res.PPy) - (res.Y0 >> res.PPy);
        if (px < 0 || py < 0 || px >= res.PrecinctsWide || py >= res.PrecinctsHigh)
        {
            return null;
        }

        return (int)((py * res.PrecinctsWide) + px);
    }

    private static long CeilDiv(long a, long b) => (a + b - 1) / b;

    /// <summary>Tracks the current byte position across a tile's tile-parts (B.9): a packet never spans a tile-part boundary, so advancing past the end of one moves to the next.</summary>
    private sealed class PacketCursor(IReadOnlyList<JpxTilePart> tileParts)
    {
        private int _index;

        public int TilePartIndex => _index;

        public int Offset { get; private set; }

        public ReadOnlyMemory<byte> CurrentData => tileParts[_index].Data;

        /// <summary>Moves to byte <paramref name="newOffset"/> of the current tile-part, or the start of the next one when <paramref name="newOffset"/> lands exactly at its end. Returns <see langword="false"/> when <paramref name="newOffset"/> overshoots or moves backwards (both malformed).</summary>
        public bool Advance(int newOffset)
        {
            if (newOffset > CurrentData.Length || newOffset < Offset)
            {
                return false;
            }

            Offset = newOffset;
            if (Offset == CurrentData.Length && _index + 1 < tileParts.Count)
            {
                _index++;
                Offset = 0;
            }

            return true;
        }
    }
}
