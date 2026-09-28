namespace PlumePdf.Filters.Jpx;

/// <summary>
/// Tier-1 EBCOT block decoder (T.800 Annex D): the 4-row stripe scan, the three coding passes
/// (significance propagation, magnitude refinement, cleanup with run-length/uniform run mode),
/// context formation (Tables D.1-D.4), and the six code-block style flags. Fills
/// <see cref="JpxCodeBlock.Coefficients"/> (sign-magnitude) and <see cref="JpxCodeBlock.DecodedPlanes"/>.
/// </summary>
/// <remarks>
/// The <c>Decode</c> signature is a frozen contract: it carries no
/// <see cref="PdfOptions"/> or <see cref="IndirectReference"/>, so the two deviations this file
/// mints (<c>PLUME3710</c>, <c>PLUME3711</c>) are appended to the <see cref="DiagnosticCollection"/>
/// directly at <see cref="DiagnosticSeverity.Warning"/> rather than through
/// <c>FilterDiagnostics.ReportDeviation</c> (which needs both of those to honour
/// <see cref="PdfOptions.Strict"/>) — every other JPX report site elsewhere in this codec does have
/// <see cref="PdfOptions"/> in hand and uses that helper.
/// </remarks>
internal static class JpxBlockDecoder
{
    private enum PassType
    {
        SigProp,
        MagRef,
        Cleanup,
    }

    /// <summary>Decodes <paramref name="block"/>'s codeword segments; <paramref name="mb"/> is the subband's <c>M_b</c>, <paramref name="orientation"/> its 0 LL / 1 HL / 2 LH / 3 HH code.</summary>
    public static void Decode(JpxCodeBlock block, IReadOnlyList<JpxTilePart> tileParts, JpxCodingStyle coding, int orientation, int mb, DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(tileParts);
        ArgumentNullException.ThrowIfNull(coding);

        var width = block.Width;
        var height = block.Height;
        var count = width * height;
        block.Coefficients = count > 0 ? new int[count] : [];
        block.DecodedPlanes = 0;

        var codedPlanes = mb - block.ZeroBitPlanes;
        if (count == 0 || codedPlanes <= 0 || block.Segments.Count == 0)
        {
            return;
        }

        var style = coding.CodeBlockStyle;
        var bypass = (style & 0x01) != 0;
        var resetPerPass = (style & 0x02) != 0;
        var termAll = (style & 0x04) != 0;
        var vCausal = (style & 0x08) != 0;
        var segSymbol = (style & 0x20) != 0;

        // Style bits 0x04 (termall) and 0x10 (predictable termination) need no bespoke code
        // here: termall is fully expressed by tier-2's own segment split (each JpxSegmentRef
        // already ends exactly where its codeword terminates — this decoder just follows the
        // segment list it is handed), and predictable termination only constrains the
        // ENCODER's FLUSH padding pattern; this decoder accepts it without
        // re-verifying the pad bits ("accepted, unverified").

        // Pass schedule (T.800 D.3): the first coded (most-significant) plane has only a
        // cleanup pass; every later plane has significance-propagation, magnitude-refinement,
        // then cleanup, in that order.
        var schedule = new (int Plane, PassType Type)[1 + (3 * (codedPlanes - 1))];
        schedule[0] = (0, PassType.Cleanup);
        var next = 1;
        for (var p = 1; p < codedPlanes; p++)
        {
            schedule[next++] = (p, PassType.SigProp);
            schedule[next++] = (p, PassType.MagRef);
            schedule[next++] = (p, PassType.Cleanup);
        }

        var totalSignalled = 0;
        foreach (var segment in block.Segments)
        {
            totalSignalled += segment.Passes;
        }

        // Tier-2 may legitimately signal MORE passes than the 1 + 3·(M_b − P) schedule holds.
        // OpenJPEG's encoder derives a block's pass count from the block's ACTUAL magnitude
        // bit-planes (t1.c `opj_t1_encode_cblk`: numbps = floorlog2(max)+1, uncapped) and
        // signals P = band->numbps − cblk->numbps into the tag tree (t2.c
        // `opj_t2_encode_packet`); when the coefficients outgrow the subband's M_b (too few
        // guard bits for the data — e.g. out-of-range source samples, or 5/3 worst-case growth)
        // that P is negative and the tag tree codes it as 0 (tgt.c `opj_tgt_encode`: low ≥ value
        // at the first threshold emits the terminating 1). The decoder side (t2.c
        // `opj_t2_read_packet_header`: cblk->numbps = band->numbps + 1 − i, i.e. M_b − P) then
        // sees M_b planes and, in t1.c `opj_t1_decode_cblk`, runs its pass loop under
        // `(passno < seg->real_num_passes) && (bpno_plus_one >= 1)` — every pass past the last
        // plane is silently skipped, with no event message. This decoder does exactly the same
        // (clamp, decode the top M_b − P planes, ignore the rest) so its output stays bit-exact
        // with the reference decoder on such streams (Fixtures/jpx/depth-12u.j2k is one:
        // OpenJPEG-encoded, ~10 of its blocks signal 3 surplus passes). The surplus is NOT a
        // PLUME3710 fact — it is a real-encoder artefact both decoders tolerate identically, and
        // reporting it turned every 12-bit JPX page into a spurious PLUME7744. The unread passes cost nothing (the loop never visits them), so no cap on the
        // surplus is needed for the ledger/DoS posture either.
        var availablePasses = Math.Min(totalSignalled, schedule.Length);

        // T.800 B.10.7: outside the termAll style, a code-block's arithmetically-coded passes
        // form ONE continuous MQ codeword — and its raw (bypass) sig-prop/mag-ref run, when
        // split across more than one, forms one continuous raw bit stream — even though tier-2
        // hands the bytes over piecemeal, one packet/layer contribution (JpxSegmentRef) at a
        // time. Concatenate consecutive same-kind segments into one physical buffer BEFORE
        // decoding starts (mirroring how a reference decoder gathers a code-block's chunks
        // up front) so the arithmetic/raw reader's own byte-oriented state (and the MQ
        // decoder's one-byte marker-avoidance lookahead) never has to treat a tier-2 layer
        // boundary — a bookkeeping split, not a real codeword boundary — as end-of-stream.
        // termAll segments are never merged: each pass there IS its own independently
        // terminated codeword by construction, and must restart INITDEC/raw framing exactly
        // once per pass.
        // Two passes: group the segments into runs and total each run's length first, then
        // allocate every run's buffer once and copy each segment in — a re-copy of the growing
        // buffer per appended segment was O(n²) in the segment count (a 10-layer stream hands a
        // block ten contributions). The bytes are the input's own,
        // so the buffers are bounded by the codestream's length, not by any header field.
        var runs = new List<(int FirstSegment, int SegmentCount, int Length, int Passes, bool Raw)>();
        var passCursor = 0;

        // The one byte-level fact tier-1 CAN report (see the PLUME3710 rule below the decode
        // loop): a segment reference whose declared Length runs past the bytes its tile-part
        // actually holds. Recorded once per block, at the first such segment.
        string? segmentOverflow = null;
        for (var s = 0; s < block.Segments.Count; s++)
        {
            var segment = block.Segments[s];
            if (segment.Passes <= 0)
            {
                continue;
            }

            var segRaw = false;
            if (passCursor < schedule.Length)
            {
                var (_, type0) = schedule[passCursor];
                segRaw = type0 != PassType.Cleanup && bypass && passCursor + 1 >= 11;
            }

            var length = GetSegmentBytes(tileParts, segment).Length;
            if (length < segment.Length)
            {
                segmentOverflow ??= $"codeword segment declares {segment.Length} bytes but only {length} remain in tile-part {segment.TilePartIndex}";
            }
            if (!termAll && runs.Count > 0 && runs[^1].Raw == segRaw)
            {
                var prev = runs[^1];
                runs[^1] = (prev.FirstSegment, s + 1 - prev.FirstSegment, prev.Length + length, prev.Passes + segment.Passes, segRaw);
            }
            else
            {
                runs.Add((s, 1, length, segment.Passes, segRaw));
            }

            passCursor += segment.Passes;
        }

        var mergedSegments = new List<(byte[] Bytes, int Passes, bool Raw)>(runs.Count);
        foreach (var (firstSegment, segmentCount, length, passes, isRaw) in runs)
        {
            var combined = new byte[length];
            var written = 0;
            for (var s = firstSegment; s < firstSegment + segmentCount; s++)
            {
                var segment = block.Segments[s];
                if (segment.Passes <= 0)
                {
                    continue;
                }

                var bytes = GetSegmentBytes(tileParts, segment);
                bytes.CopyTo(combined.AsSpan(written));
                written += bytes.Length;
            }

            mergedSegments.Add((combined, passes, isRaw));
        }

        var state = new BlockState(width, height, vCausal, orientation);

        var segmentIndex = 0;
        var passesLeftInSegment = mergedSegments.Count > 0 ? mergedSegments[0].Passes : 0;
        JpxMqDecoder? mq = null;
        var raw = default(RawBitReader);
        var currentIsRaw = false;
        var segmentShortfall = false;

        void OpenSegment(int index)
        {
            var (bytes, _, wantRaw) = mergedSegments[index];
            if (wantRaw)
            {
                raw = new RawBitReader(bytes);
            }
            else if (mq is null)
            {
                mq = new JpxMqDecoder(bytes);
            }
            else
            {
                mq.Reinitialise(bytes);
            }

            currentIsRaw = wantRaw;
        }

        int DecodeBit(int context) => currentIsRaw ? raw.ReadBit() : mq!.Decode(context);

        void SignificancePropagationPass(int plane)
        {
            var weight = 1 << (codedPlanes - 1 - plane);
            for (var stripeStart = 0; stripeStart < height; stripeStart += 4)
            {
                state.SetStripe(stripeStart);
                var rows = Math.Min(4, height - stripeStart);
                for (var x = 0; x < width; x++)
                {
                    for (var ry = 0; ry < rows; ry++)
                    {
                        var y = stripeStart + ry;
                        var idx = (y * width) + x;
                        if (state.Significant[idx])
                        {
                            continue;
                        }

                        var ctx = state.ZeroCodingContext(x, y);
                        if (ctx == 0)
                        {
                            // SPP only visits samples with at least one significant neighbour
                            // right now; a zero context leaves the sample for the cleanup pass.
                            continue;
                        }

                        var bit = DecodeBit(ctx);
                        state.AttemptedThisPlane[idx] = true;
                        if (bit == 1)
                        {
                            state.Significant[idx] = true;
                            state.NewThisPlane[idx] = true;
                            state.Magnitude[idx] |= weight;
                            int signBit;
                            if (currentIsRaw)
                            {
                                signBit = raw.ReadBit();
                            }
                            else
                            {
                                var (sctx, sxor) = state.SignContext(x, y);
                                signBit = mq!.Decode(sctx) ^ sxor;
                            }

                            state.Sign[idx] = signBit == 1;
                        }
                    }
                }
            }
        }

        void MagnitudeRefinementPass(int plane)
        {
            var weight = 1 << (codedPlanes - 1 - plane);
            for (var stripeStart = 0; stripeStart < height; stripeStart += 4)
            {
                state.SetStripe(stripeStart);
                var rows = Math.Min(4, height - stripeStart);
                for (var x = 0; x < width; x++)
                {
                    for (var ry = 0; ry < rows; ry++)
                    {
                        var y = stripeStart + ry;
                        var idx = (y * width) + x;

                        // Only samples significant BEFORE this plane's own SPP are refined —
                        // NewThisPlane marks the ones SPP (or, for plane 0, nothing) just found.
                        if (!state.Significant[idx] || state.NewThisPlane[idx])
                        {
                            continue;
                        }

                        var firstRefinement = !state.Refined[idx];
                        var ctx = state.MagRefContext(x, y, firstRefinement);
                        var bit = DecodeBit(ctx);
                        state.Refined[idx] = true;
                        if (bit == 1)
                        {
                            state.Magnitude[idx] |= weight;
                        }
                    }
                }
            }
        }

        var segSymbolMismatch = false;

        void CleanupPass(int plane)
        {
            // Cleanup is always arithmetically coded (T.800 D.4) — bypass never applies here.
            var weight = 1 << (codedPlanes - 1 - plane);
            for (var stripeStart = 0; stripeStart < height; stripeStart += 4)
            {
                state.SetStripe(stripeStart);
                var rows = Math.Min(4, height - stripeStart);
                for (var x = 0; x < width; x++)
                {
                    var startRow = 0;

                    // Run-length mode (T.800 D.4): a full 4-row column group, none of whose
                    // members were touched yet this plane and every one of whose zero-coding
                    // contexts is currently 0, is tested with a single decision instead of four.
                    if (rows == 4)
                    {
                        var eligible = true;
                        for (var ry = 0; ry < 4 && eligible; ry++)
                        {
                            var y = stripeStart + ry;
                            var idx = (y * width) + x;
                            if (state.Significant[idx] || state.AttemptedThisPlane[idx] || state.ZeroCodingContext(x, y) != 0)
                            {
                                eligible = false;
                            }
                        }

                        if (eligible)
                        {
                            var runBit = mq!.Decode(JpxTier1Contexts.RunLength);
                            if (runBit == 0)
                            {
                                // All four resolved insignificant — none of them need the
                                // per-sample fallback loop below either.
                                for (var ry = 0; ry < 4; ry++)
                                {
                                    state.AttemptedThisPlane[((stripeStart + ry) * width) + x] = true;
                                }

                                continue;
                            }

                            var hi = mq.Decode(JpxTier1Contexts.Uniform);
                            var lo = mq.Decode(JpxTier1Contexts.Uniform);
                            var firstRow = (hi << 1) | lo;

                            // Rows before firstRow are now known insignificant (the group test
                            // already proved that); firstRow itself is resolved significant
                            // below. Rows AFTER firstRow are NOT resolved by the group decision
                            // — the homogeneity assumption run mode relies on only covers "are
                            // all four still insignificant", not what happens past the first
                            // hit — so they must NOT be marked attempted here, or the per-sample
                            // fallback loop would wrongly skip testing them.
                            for (var ry = 0; ry <= firstRow; ry++)
                            {
                                state.AttemptedThisPlane[((stripeStart + ry) * width) + x] = true;
                            }

                            var firstY = stripeStart + firstRow;
                            var firstIdx = (firstY * width) + x;
                            state.Significant[firstIdx] = true;
                            state.NewThisPlane[firstIdx] = true;
                            state.Magnitude[firstIdx] |= weight;
                            var (sctx, sxor) = state.SignContext(x, firstY);
                            state.Sign[firstIdx] = (mq.Decode(sctx) ^ sxor) == 1;
                            startRow = firstRow + 1;
                        }
                    }

                    for (var ry = startRow; ry < rows; ry++)
                    {
                        var y = stripeStart + ry;
                        var idx = (y * width) + x;
                        if (state.Significant[idx] || state.AttemptedThisPlane[idx])
                        {
                            continue;
                        }

                        state.AttemptedThisPlane[idx] = true;
                        var ctx = state.ZeroCodingContext(x, y);
                        var bit = mq!.Decode(ctx);
                        if (bit == 1)
                        {
                            state.Significant[idx] = true;
                            state.NewThisPlane[idx] = true;
                            state.Magnitude[idx] |= weight;
                            var (sctx, sxor) = state.SignContext(x, y);
                            state.Sign[idx] = (mq.Decode(sctx) ^ sxor) == 1;
                        }
                    }
                }
            }

            if (segSymbol)
            {
                var b0 = mq!.Decode(JpxTier1Contexts.Uniform);
                var b1 = mq.Decode(JpxTier1Contexts.Uniform);
                var b2 = mq.Decode(JpxTier1Contexts.Uniform);
                var b3 = mq.Decode(JpxTier1Contexts.Uniform);
                if (b0 != 1 || b1 != 0 || b2 != 1 || b3 != 0)
                {
                    segSymbolMismatch = true;
                }
            }
        }

        if (mergedSegments.Count == 0)
        {
            return;
        }

        OpenSegment(0); // pass 1 is always plane 0's cleanup pass — always arithmetic.

        var lastCompletedPlane = -1;
        var lastPass = (Plane: -1, Type: PassType.Cleanup);
        for (var i = 0; i < availablePasses; i++)
        {
            var (plane, type) = schedule[i];

            if (i > 0 && passesLeftInSegment <= 0)
            {
                segmentIndex++;
                if (segmentIndex >= mergedSegments.Count)
                {
                    // Fewer segments than the summed Passes promised — an inconsistent tier-2
                    // hand-off, distinct from a segment simply running out of bytes. Stop here
                    // rather than index out of range; reported below with its own message.
                    segmentShortfall = true;
                    break;
                }

                passesLeftInSegment = mergedSegments[segmentIndex].Passes;
                OpenSegment(segmentIndex);
            }

            if (type == PassType.SigProp || (type == PassType.Cleanup && plane == 0))
            {
                state.ResetPlaneFlags();
            }

            switch (type)
            {
                case PassType.SigProp:
                    SignificancePropagationPass(plane);
                    break;
                case PassType.MagRef:
                    MagnitudeRefinementPass(plane);
                    break;
                default:
                    CleanupPass(plane);
                    lastCompletedPlane = plane;
                    break;
            }

            lastPass = (plane, type);
            passesLeftInSegment--;
            if (resetPerPass)
            {
                mq?.ResetContexts();
            }
        }

        block.DecodedPlanes = lastCompletedPlane + 1;

        // A block whose passes stop part-way through a bit-plane (after its sig-prop or mag-ref
        // pass, before that plane's cleanup) leaves its samples at two different depths: the
        // ones this plane's passes visited know their bit here, the rest do not. DecodedPlanes
        // counts only COMPLETED planes, so dequantisation (E.1.1.2, JpxWavelet) adds its
        // reconstruction half at this plane's full weight to every non-zero sample — right for
        // the unvisited ones, one half-weight too much for the visited ones, whose own
        // reconstruction point is half of THIS plane's weight below their decoded bit. Fold
        // that difference into the magnitude now (the low bits are ours to set: nothing below
        // this plane was decoded) so the uniform rule reconstructs every sample exactly where
        // the standard's (and the OpenJPEG oracle's) per-sample rule does. Not needed after a
        // cleanup pass (every sample is at the same depth), and a no-op at the last plane
        // (weight 1 has no integer half).
        if (lastPass.Type != PassType.Cleanup && lastPass.Plane >= 0)
        {
            var halfWeight = (1 << (codedPlanes - 1 - lastPass.Plane)) >> 1;
            if (halfWeight > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    // After sig-prop only the samples it just made significant were visited;
                    // after mag-ref every significant sample was (by sig-prop or by mag-ref).
                    if (state.Significant[i] && (lastPass.Type == PassType.MagRef || state.NewThisPlane[i]))
                    {
                        state.Magnitude[i] -= halfWeight;
                    }
                }
            }
        }

        for (var i = 0; i < count; i++)
        {
            block.Coefficients[i] = state.Sign[i] ? -state.Magnitude[i] : state.Magnitude[i];
        }

        // PLUME3710 is a STRUCTURAL diagnostic. It fires on exactly two facts, each a
        // disagreement between what tier-2 signalled and what tier-1 was handed:
        //   1. segmentShortfall — fewer codeword segments arrived than the summed Passes promised;
        //   2. segmentOverflow  — a JpxSegmentRef's Offset + Length runs past its tile-part's data.
        //
        // Signalled passes EXCEEDING the 1 + 3·(M_b − P) schedule are deliberately not a third
        // fact: see the comment at `availablePasses` above — OpenJPEG's own encoder emits such
        // streams whenever a block's magnitude outgrows M_b, and OpenJPEG's decoder skips the
        // surplus passes silently (t1.c `opj_t1_decode_cblk`, `bpno_plus_one >= 1`); this decoder
        // mirrors that, bit-exactly and without a diagnostic.
        //
        // Tier-1 OVER-READ is never a diagnostic. T.800 C.3.4 (BYTEIN) defines what a decoder
        // does when it reaches the end of a codeword segment: it feeds itself implied 0xFF bytes
        // and keeps decoding — and D.4.2 (length calculation / near-optimal termination) lets an
        // encoder drop every trailing byte the decoder can regenerate that way, so a conformant
        // codeword's real end may sit an unbounded-by-constant number of bytes before the last
        // decision it encodes (Kakadu, the encoder behind most Adobe-produced JPX, terminates
        // this tightly). Any byte-count threshold on the over-read is therefore either a false
        // positive on a real encoder or blind to a small cut, and no other decoder diagnoses it
        // (OpenJPEG and PDFium do not). Genuine byte exhaustion is already caught one layer up,
        // structurally: JpxPacketDecoder rejects a packet whose declared segment lengths overshoot
        // the tile-part (PLUME3709) before any segment is recorded, and the codestream layer
        // records a truncated tile-part as PLUME3701 — so a cut file reaching this decoder with a
        // consistent Length/Data pair is indistinguishable from an optimally terminated one, and
        // is decoded as far as it goes.
        //
        // Nor is this a comparison against how many bit-planes the schedule COULD hold
        // (DecodedPlanes vs. codedPlanes): a single quality layer legitimately stops short of
        // that ceiling at its rate-distortion truncation point (veraPDF 6.2.8.3-t02-fail-a, a
        // real-world single-layer JP2, signals exactly one pass short of the full schedule on
        // most of its blocks).
        if (segmentShortfall || segmentOverflow is not null)
        {
            var reason = segmentOverflow ?? "fewer codeword segments than the summed signalled passes promised";

            diagnostics?.Add(new PdfDiagnostic(
                JpxDiagnosticCodes.CodeBlockTruncated,
                DiagnosticSeverity.Warning,
                $"JPX: code-block tier-1 decode: {reason}; decoded {block.DecodedPlanes} of {codedPlanes} coded bit-plane(s)."));
        }

        if (segSymbolMismatch)
        {
            diagnostics?.Add(new PdfDiagnostic(
                JpxDiagnosticCodes.SegmentationSymbolMismatch,
                DiagnosticSeverity.Warning,
                "JPX: code-block segmentation symbol after a cleanup pass did not match the expected 1010 sequence; decoded coefficients kept."));
        }
    }

    /// <summary>The byte range one <see cref="JpxSegmentRef"/> names into its tile-part's data, clamped to that tile-part's actual length so a malformed length never indexes out of range. A slice shorter than <see cref="JpxSegmentRef.Length"/> is the second structural <c>PLUME3710</c> fact — <see cref="Decode"/> compares the two and reports it once per block; the reader itself just decodes off implied <c>0xFF</c> past the slice (T.800 C.3.4), which is never a diagnostic.</summary>
    private static ReadOnlySpan<byte> GetSegmentBytes(IReadOnlyList<JpxTilePart> tileParts, JpxSegmentRef segment)
    {
        var data = tileParts[segment.TilePartIndex].Data.Span;
        var start = Math.Clamp(segment.Offset, 0, data.Length);
        var end = Math.Clamp(segment.Offset + segment.Length, start, data.Length);
        return data[start..end];
    }

    /// <summary>
    /// Raw (bypass) bit reader for the significance-propagation and magnitude-refinement
    /// passes' <c>bypass</c> style (T.800 D.7): bits come MSB-first out of each byte, and a
    /// byte that reads as <c>0xFF</c> is always followed by a byte contributing only 7 bits —
    /// its own top bit position is skipped rather than read, the same marker-avoidance
    /// convention <see cref="JpxMqDecoder"/>'s <c>BYTEIN</c> uses for the arithmetic stream.
    /// A private, decoder-local type rather than the frozen <c>JpxBitReader</c> contract (the
    /// packet-header reader) — keeping this file buildable and testable without a dependency on it.
    /// </summary>
    private struct RawBitReader(byte[] data)
    {
        private readonly byte[] _data = data;
        private int _pos;
        private int _bitsLeft;
        private int _current;
        private bool _prevWasFf;

        public int ReadBit()
        {
            if (_bitsLeft == 0)
            {
                // Past the real end the reader feeds implied 0xFF bytes, the same convention
                // T.800 C.3.4's BYTEIN gives the arithmetic stream — never a diagnostic (see
                // Decode's PLUME3710 rule).
                var b = _pos < _data.Length ? _data[_pos] : (byte)0xFF;
                _pos++;
                _current = b;
                _bitsLeft = _prevWasFf ? 7 : 8;
                _prevWasFf = b == 0xFF;
            }

            _bitsLeft--;
            return (_current >> _bitsLeft) & 1;
        }
    }

    /// <summary>
    /// Per-block decode state: significance/refinement/sign bookkeeping over the block's
    /// samples, plus the T.800 Tables D.1-D.4 context-formation rules, which all read that
    /// same live state. <see cref="SetStripe"/> records which 4-row stripe the sample being
    /// coded belongs to, purely so the <c>verticallyCausal</c> style can cut off a neighbour
    /// lookup into the next (not-yet-decoded-this-pass) stripe.
    /// </summary>
    private sealed class BlockState(int width, int height, bool verticallyCausal, int orientation)
    {
        public bool[] Significant { get; } = new bool[width * height];

        public bool[] Refined { get; } = new bool[width * height];

        /// <summary><see langword="true"/> for a negative coefficient.</summary>
        public bool[] Sign { get; } = new bool[width * height];

        public int[] Magnitude { get; } = new int[width * height];

        /// <summary>Samples the significance-propagation or cleanup pass has already decided this plane — reset at the start of each plane, read by cleanup to skip what SPP already handled.</summary>
        public bool[] AttemptedThisPlane { get; } = new bool[width * height];

        /// <summary>Samples that became significant during THIS plane — magnitude refinement only touches samples significant before it, never ones SPP/cleanup just found.</summary>
        public bool[] NewThisPlane { get; } = new bool[width * height];

        private int _stripeStart;

        public void SetStripe(int stripeStart) => _stripeStart = stripeStart;

        public void ResetPlaneFlags()
        {
            Array.Clear(AttemptedThisPlane);
            Array.Clear(NewThisPlane);
        }

        /// <summary>Zero-coding context (T.800 Table D.1), 0-8, selected by <paramref name="x"/>/<paramref name="y"/>'s eight-neighbour significance pattern and the subband's orientation.</summary>
        public int ZeroCodingContext(int x, int y)
        {
            var h = (IsSignificant(x - 1, y) ? 1 : 0) + (IsSignificant(x + 1, y) ? 1 : 0);
            var v = (IsSignificant(x, y - 1) ? 1 : 0) + (IsSignificant(x, y + 1) ? 1 : 0);
            var d = (IsSignificant(x - 1, y - 1) ? 1 : 0) + (IsSignificant(x + 1, y - 1) ? 1 : 0)
                  + (IsSignificant(x - 1, y + 1) ? 1 : 0) + (IsSignificant(x + 1, y + 1) ? 1 : 0);

            return orientation switch
            {
                1 => HorizontalVerticalTable(v, h, d), // HL: the horizontal/vertical roles swap.
                3 => DiagonalTable(h, v, d), // HH.
                _ => HorizontalVerticalTable(h, v, d), // LL (0) and LH (2) share the table.
            };
        }

        /// <summary>Sign context and expected-bit XOR (T.800 D.2/D.3): the coded bit XORed with this value gives the actual sign bit (0 = positive, 1 = negative).</summary>
        public (int Context, int Xor) SignContext(int x, int y)
        {
            var h = Math.Clamp(SignContribution(x - 1, y) + SignContribution(x + 1, y), -1, 1);
            var v = Math.Clamp(SignContribution(x, y - 1) + SignContribution(x, y + 1), -1, 1);

            if (h > 0)
            {
                return v switch { > 0 => (13, 0), 0 => (12, 0), _ => (11, 0) };
            }

            if (h == 0)
            {
                return v switch { > 0 => (10, 0), 0 => (9, 0), _ => (10, 1) };
            }

            return v switch { > 0 => (11, 1), 0 => (12, 1), _ => (13, 1) };
        }

        /// <summary>Magnitude-refinement context (T.800 Table D.4): a coefficient's first refinement uses one of two contexts depending on whether it has a significant neighbour right now; every later refinement of the same coefficient uses the third.</summary>
        public int MagRefContext(int x, int y, bool firstRefinement)
        {
            if (!firstRefinement)
            {
                return JpxTier1Contexts.MagRefBase + 2;
            }

            var anySignificantNeighbour =
                IsSignificant(x - 1, y - 1) || IsSignificant(x, y - 1) || IsSignificant(x + 1, y - 1) ||
                IsSignificant(x - 1, y) || IsSignificant(x + 1, y) ||
                IsSignificant(x - 1, y + 1) || IsSignificant(x, y + 1) || IsSignificant(x + 1, y + 1);

            return anySignificantNeighbour ? JpxTier1Contexts.MagRefBase + 1 : JpxTier1Contexts.MagRefBase;
        }

        private bool IsSignificant(int x, int y)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            {
                return false;
            }

            if (verticallyCausal && y >= _stripeStart + 4)
            {
                // The "vertically causal" style (SPcod 0x08) keeps context formation from
                // looking into the stripe below the one currently being coded, even though
                // that stripe may already carry significance state from an earlier bit-plane.
                return false;
            }

            return Significant[(y * width) + x];
        }

        private int SignContribution(int x, int y)
        {
            if (!IsSignificant(x, y))
            {
                return 0;
            }

            return Sign[(y * width) + x] ? -1 : 1;
        }

        // T.800 Table D.1, LL/LH orientation (and HL with h/v swapped by the caller): the
        // horizontal count dominates, then vertical, then diagonal.
        private static int HorizontalVerticalTable(int h, int v, int d)
        {
            if (h == 2)
            {
                return 8;
            }

            if (h == 1)
            {
                return v >= 1 ? 7 : d >= 1 ? 6 : 5;
            }

            if (v == 2)
            {
                return 4;
            }

            if (v == 1)
            {
                return 3;
            }

            return d >= 2 ? 2 : d == 1 ? 1 : 0;
        }

        // T.800 Table D.1, HH orientation: the diagonal count dominates.
        private static int DiagonalTable(int h, int v, int d)
        {
            var hv = h + v;
            if (d >= 3)
            {
                return 8;
            }

            if (d == 2)
            {
                return hv >= 1 ? 7 : 6;
            }

            if (d == 1)
            {
                return hv >= 2 ? 5 : hv == 1 ? 4 : 3;
            }

            return hv >= 2 ? 2 : hv == 1 ? 1 : 0;
        }
    }
}
