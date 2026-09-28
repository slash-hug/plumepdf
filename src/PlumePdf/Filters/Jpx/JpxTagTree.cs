namespace PlumePdf.Filters.Jpx;

/// <summary>
/// Packet-header bit reader (T.800 B.10.1): reads bits MSB-first with the bit-stuffing rule — a
/// fully-consumed byte whose raw value is <c>0xFF</c> is always followed, in the underlying
/// stream, by a byte whose top bit is a fixed stuffing bit (never real data): the next byte
/// contributes only its low 7 bits. One instance decodes exactly one packet header: construct at
/// the header's first byte, call <see cref="ReadBit"/>/<see cref="ReadBits"/> as the header format
/// dictates, then <see cref="AlignToByte"/> once to land <see cref="Position"/> on the packet
/// body's first byte (skipping the stuffed byte, whole, when the header's last-loaded raw byte was
/// <c>0xFF</c>). Reading past the end of the underlying data throws
/// <see cref="JpxPacketHeaderException"/>: a packet header that needs more bits than its
/// tile-part holds is malformed (B.9: a packet never spans a tile-part boundary), and the
/// packet decoder turns the throw into its <c>PLUME3709</c> deviation. It must never feed zero
/// bits instead — a unary-coded field (tag-tree node, <c>Lblock</c> growth) read against an
/// endless supply of zeros never terminates, so a one-byte truncated packet became a
/// 2^31-iteration CPU burn per code-block.
/// </summary>
internal ref struct JpxBitReader
{
    /// <summary>The widest field any B.10 packet-header syntax element can occupy (a codeword-segment length of <c>Lblock + ⌊log₂ passes⌋</c> bits, capped at 31 by <see cref="JpxPacketDecoder"/>); a wider request is a malformed header, not a reader capability.</summary>
    public const int MaxFieldBits = 31;

    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    /// <summary>The most recently loaded raw byte, or -1 before the first byte has been fetched.</summary>
    private int _currentByte;

    /// <summary>Bits of <see cref="_currentByte"/> not yet returned by <see cref="ReadBit"/>.</summary>
    private int _bitsRemaining;

    /// <summary>Positions the reader at byte <paramref name="start"/> of <paramref name="data"/>.</summary>
    public JpxBitReader(ReadOnlySpan<byte> data, int start)
    {
        _data = data;
        _position = start;
        _currentByte = -1;
        _bitsRemaining = 0;
    }

    /// <summary>Byte offset of the next unread byte.</summary>
    public readonly int Position => _position;

    /// <summary>Bytes remaining from <see cref="Position"/> to the end of the span.</summary>
    public readonly int Remaining => Math.Max(0, _data.Length - _position);

    /// <summary>Reads one bit (0 or 1), MSB-first, applying B.10.1 bit-stuffing across byte boundaries.</summary>
    public int ReadBit()
    {
        if (_bitsRemaining == 0)
        {
            FetchByte();
        }

        _bitsRemaining--;
        return (_currentByte >> _bitsRemaining) & 1;
    }

    /// <summary>Reads <paramref name="count"/> bits MSB-first, most significant bit first, as a single non-negative integer. <paramref name="count"/> above <see cref="MaxFieldBits"/> (or negative) is a malformed header (<see cref="JpxPacketHeaderException"/>), never an <see cref="int"/> overflow.</summary>
    public int ReadBits(int count)
    {
        if ((uint)count > MaxFieldBits)
        {
            throw new JpxPacketHeaderException($"a packet-header field of {count} bits is not representable (maximum {MaxFieldBits}).");
        }

        var value = 0;
        for (var i = 0; i < count; i++)
        {
            value = (value << 1) | ReadBit();
        }

        return value;
    }

    /// <summary>
    /// Ends the packet header: discards any unread bits of the current byte and, when that byte's
    /// raw value was <c>0xFF</c>, skips the mandatory stuffed byte that follows it in the stream —
    /// whether or not any of its bits were actually consumed — so <see cref="Position"/> lands
    /// exactly on the packet body's first real byte.
    /// </summary>
    public void AlignToByte()
    {
        if (_currentByte == 0xFF)
        {
            _position++;

            // The skipped byte's top bit is, by construction, always the fixed stuffing 0 (a
            // stuffed byte can never itself be 0xFF), so its exact value never matters — only
            // that the *next* FetchByte's "was the previous byte 0xFF" check sees something
            // other than 0xFF here. Leaving _currentByte at 0xFF would make that next fetch
            // wrongly re-apply the 7-bit stuffing rule to the byte after the one just skipped.
            _currentByte = 0;
        }

        _bitsRemaining = 0;
    }

    private void FetchByte()
    {
        if (_position >= _data.Length)
        {
            throw new JpxPacketHeaderException($"the packet header needs more bits than remain in its tile-part ({_data.Length} byte(s)).");
        }

        var wasFF = _currentByte == 0xFF;
        _currentByte = _data[_position];
        _position++;
        _bitsRemaining = wasFF ? 7 : 8;
    }
}

/// <summary>
/// Raised by <see cref="JpxBitReader"/> and <see cref="JpxTagTree"/> when a packet header cannot be
/// decoded from the bytes it has — it ran off the end of the tile-part, asked for an impossibly
/// wide field, or a tag-tree value exceeded what T.800 permits. Internal control flow only:
/// <see cref="JpxPacketDecoder"/> catches it at the packet boundary and reports
/// <c>PLUME3709</c> (the remaining packets of that tile are skipped); it never escapes the decoder.
/// </summary>
internal sealed class JpxPacketHeaderException(string message) : Exception(message);

/// <summary>
/// Tag-tree decoder (T.800 B.10.2) for the inclusion and zero-bit-planes information of a
/// precinct's code-blocks: a quad-tree over the leaf grid where each internal node's value is the
/// minimum of its children (built once, bottom-up, at construction; leaf values are never known
/// upfront during decode — they are established incrementally from the bitstream). The inclusion
/// tree is resumable: <see cref="DecodeInclusion"/> may be called repeatedly at increasing
/// thresholds (one per layer) and each node's partial state (its current lower bound, and whether
/// its exact value has been finalised) persists across calls, so no bit already consumed is ever
/// re-read. <see cref="DecodeValue"/> (the zero-bit-planes tree) queries with an effectively
/// infinite threshold, so it fully resolves every node on the path at first inclusion.
/// </summary>
internal sealed class JpxTagTree
{
    /// <summary>
    /// The largest value a fully-decoded (zero bit-planes) tag-tree leaf may take: the number of
    /// missing most-significant bit-planes <c>P</c> is at most <c>M_b = G + ε_b − 1</c> (E-2), and
    /// with <c>G ≤ 7</c> (three bits) and <c>ε_b ≤ 31</c> (five bits) that is 37. A node whose
    /// unary run passes this without terminating is malformed
    /// (<see cref="JpxPacketHeaderException"/>) — bounding the walk here, not only at the bit
    /// reader's end of data, keeps a long run of zero bytes from being read as a 2^31-plane value.
    /// </summary>
    public const int MaxValue = 37;

    private sealed class Node
    {
        public int Value;
        public bool Finalized;
        public Node? Parent;
    }

    private readonly Node[] _leaves;

    /// <summary>A tree over a <paramref name="width"/> × <paramref name="height"/> leaf grid.</summary>
    public JpxTagTree(int width, int height)
    {
        Width = width;
        Height = height;

        var levelWidth = Math.Max(1, width);
        var levelHeight = Math.Max(1, height);
        var level = new Node[levelWidth * levelHeight];
        for (var i = 0; i < level.Length; i++)
        {
            level[i] = new Node();
        }

        _leaves = level;

        while (levelWidth > 1 || levelHeight > 1)
        {
            var parentWidth = (levelWidth + 1) / 2;
            var parentHeight = (levelHeight + 1) / 2;
            var parentLevel = new Node[parentWidth * parentHeight];
            for (var i = 0; i < parentLevel.Length; i++)
            {
                parentLevel[i] = new Node();
            }

            for (var y = 0; y < levelHeight; y++)
            {
                for (var x = 0; x < levelWidth; x++)
                {
                    level[(y * levelWidth) + x].Parent = parentLevel[((y / 2) * parentWidth) + (x / 2)];
                }
            }

            level = parentLevel;
            levelWidth = parentWidth;
            levelHeight = parentHeight;
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Inclusion query for leaf (<paramref name="x"/>, <paramref name="y"/>): <see langword="true"/>
    /// when the leaf's value is below <paramref name="threshold"/> (i.e. the code-block was included
    /// by layer <c>threshold − 1</c>). Reads only as many bits as are needed to resolve the
    /// question — none at all if an earlier, higher-threshold call (or this leaf's own ancestor)
    /// already proved the answer.
    /// </summary>
    public bool DecodeInclusion(ref JpxBitReader reader, int x, int y, int threshold) =>
        Walk(ref reader, _leaves[(y * Width) + x], threshold);

    /// <summary>Fully decodes leaf (<paramref name="x"/>, <paramref name="y"/>)'s value (zero bit-planes, decoded completely the first time it is queried). A value above <see cref="MaxValue"/> is a malformed header (<see cref="JpxPacketHeaderException"/>).</summary>
    public int DecodeValue(ref JpxBitReader reader, int x, int y)
    {
        var leaf = _leaves[(y * Width) + x];
        if (!Walk(ref reader, leaf, MaxValue + 1))
        {
            throw new JpxPacketHeaderException($"a tag-tree value exceeds {MaxValue}, the largest number of missing bit-planes T.800 permits.");
        }

        return leaf.Value;
    }

    /// <summary>
    /// Walks the path from the root down to <paramref name="leaf"/>, resolving each node against
    /// <paramref name="threshold"/> in root-to-leaf order (a node gates its descendants: the
    /// min-of-children invariant means once a node's value is known to be at least
    /// <paramref name="threshold"/>, every descendant is too, and no more bits belonging to this
    /// query remain to be read). Returns whether the leaf's value is below <paramref name="threshold"/>.
    /// </summary>
    /// <remarks>
    /// T.800 B.10.2 (and every reference codec's <c>tgt_decode</c>/<c>tgt_encode</c>) signals
    /// each node as a unary distance from its PARENT's already-resolved value, not from zero: a
    /// child's value can never be lower than its parent's (the min-of-children invariant), so
    /// once a node resolves to a value, every descendant's own unary code starts counting up
    /// from that same value rather than from 0. The local <c>low</c> carries the
    /// most-recently-resolved ancestor's value down the path; a node whose own (possibly
    /// partially-resolved, cross-call) <see cref="Node.Value"/> is still behind that bound is
    /// bumped up to it before decoding continues — never bumped down, since a node already
    /// known to exceed its parent's bound from an earlier call remains valid.
    /// </remarks>
    private static bool Walk(ref JpxBitReader reader, Node leaf, int threshold)
    {
        var low = 0;
        foreach (var node in PathFromRoot(leaf))
        {
            if (node.Value < low)
            {
                node.Value = low;
            }

            while (!node.Finalized && node.Value < threshold)
            {
                if (reader.ReadBit() != 0)
                {
                    node.Finalized = true;
                }
                else
                {
                    node.Value++;
                }
            }

            low = node.Value;

            if (node.Value >= threshold)
            {
                return false;
            }
        }

        return true;
    }

    private static List<Node> PathFromRoot(Node leaf)
    {
        var path = new List<Node>();
        for (var node = leaf; node is not null; node = node.Parent)
        {
            path.Add(node);
        }

        path.Reverse();
        return path;
    }
}
