namespace PlumePdf.Filters.Jpx;

/// <summary>
/// The decoder's working-memory ledger for one <see cref="JpxImageDecoder.Decode"/> call: every
/// allocation whose size is driven by header fields rather than by the input's byte count —
/// component planes, upsampled planes, palette output planes, the per-tile coefficient and
/// wavelet buffers, and the precinct/code-block object graph — is charged here BEFORE it is made,
/// and released when the tile that needed it is done. The ceiling is
/// <see cref="BytesPerReferencePixel"/> bytes per <c>PdfOptions.MaxImagePixels</c> pixel, so the
/// one documented cap (no second cap, no new <c>PdfOptions</c> knob) also bounds the
/// decoder's peak memory: a header that would need more is refused with <c>PLUME3718</c> before a
/// byte of it is allocated, instead of the amplification an earlier review measured
/// (64 sub-sampled components upsampled to a full grid, a 255-column palette over an 8192² index
/// plane, a 1×1-precinct partition over a 4096² tile — each a few dozen header bytes).
/// </summary>
/// <remarks>
/// The ceiling is calibrated to the legitimate worst case at the cap, not to typical use: the
/// decoder holds at most the native-width planes (1–2 B/px/component) plus, for the tile in
/// flight, one component's tier-1 coefficients (4 B/px), its reconstructed samples (4 B/px) and
/// the two live wavelet levels (≈ 8 B/px) — three components at once when the MCT is on. A
/// five-component 16-bit image with MCT at exactly the cap needs about 40 B/px at its peak;
/// <see cref="BytesPerReferencePixel"/> leaves that headroom without letting the pathological
/// shapes above through (each of them charges several hundred bytes per pixel). Per-instance,
/// never static — the decoder is stateless and concurrent callers each get their own ledger.
/// </remarks>
internal sealed class JpxDecodeBudget
{
    /// <summary>Bytes the decoder may hold live per <c>MaxImagePixels</c> reference-grid pixel (≈ 6.4 GiB at the default cap of 2^27 pixels).</summary>
    public const int BytesPerReferencePixel = 48;

    /// <summary>
    /// Output planes the decoder will produce, as a multiple of <c>MaxImagePixels</c> samples:
    /// every plane the caller receives is a full reference-grid plane (sub-sampled components are
    /// upsampled, a palette expands to one plane per column), so an image's output is
    /// <c>planes × reference pixels</c> samples however small its components are on their own
    /// grids. ISO 32000-1 §7.4.9 images carry 1, 3 or 4 colour channels plus at most one opacity
    /// channel; eight planes' worth leaves headroom for that at the cap while refusing the
    /// 64-sub-sampled-component and 255-column-palette shapes,
    /// each of which is dozens of full planes from a few header bytes.
    /// </summary>
    public const int OutputPlanesPerReferencePixel = 8;

    /// <summary>
    /// Approximate live size of one <see cref="JpxCodeBlock"/> charged when a tile's partition is
    /// built: the object, its bounds and its empty segment list (≈ 160 B), plus its share of the
    /// precinct's two tag trees (<see cref="JpxTagTree"/>: ≈ 4/3 nodes per leaf per tree at
    /// ≈ 32 B a node plus an 8 B array slot each, two trees — ≈ 128 B). The trees are created
    /// lazily by the packet decoder, per code-block count, so their cost is carried here rather
    /// than in <see cref="BytesPerPrecinct"/>.
    /// </summary>
    public const int BytesPerCodeBlock = 288;

    /// <summary>Approximate live size of one <see cref="JpxPrecinct"/> (the object, its code-block array header and the two <see cref="JpxTagTree"/> objects — whose nodes are counted per code-block in <see cref="BytesPerCodeBlock"/>) charged when a tile's partition is built.</summary>
    public const int BytesPerPrecinct = 128;

    private long _used;

    /// <summary>A ledger allowing <see cref="BytesPerReferencePixel"/> × <paramref name="maxImagePixels"/> live bytes.</summary>
    public JpxDecodeBudget(long maxImagePixels)
    {
        Limit = Math.Max(0, maxImagePixels) * BytesPerReferencePixel;
        OutputSampleLimit = Math.Max(0, maxImagePixels) * OutputPlanesPerReferencePixel;
    }

    /// <summary>The ceiling, in bytes.</summary>
    public long Limit { get; }

    /// <summary>The most output samples (planes × reference-grid pixels) a decode may produce: <see cref="OutputPlanesPerReferencePixel"/> × <c>MaxImagePixels</c>.</summary>
    public long OutputSampleLimit { get; }

    /// <summary>Refuses (<c>PLUME3718</c>) an image whose <paramref name="planes"/> full-grid output planes of <paramref name="referencePixels"/> samples each exceed <see cref="OutputSampleLimit"/>.</summary>
    public void RequireOutputWithinLimit(long planes, long referencePixels, string what)
    {
        var samples = planes * referencePixels;
        if (planes < 0 || referencePixels < 0 || samples / Math.Max(1, referencePixels) != planes || samples > OutputSampleLimit)
        {
            throw new PlumePdfException(
                JpxDiagnosticCodes.ImageTooLarge,
                $"JPEG 2000 image would produce {planes} full-grid plane(s) of {referencePixels} samples for {what} ({(planes < 0 || referencePixels < 0 ? "unrepresentable" : samples.ToString())} samples), exceeding the {OutputSampleLimit}-sample output ceiling PdfOptions.MaxImagePixels implies ({OutputPlanesPerReferencePixel} planes per permitted pixel).");
        }
    }

    /// <summary>Bytes currently charged.</summary>
    public long Used => _used;

    /// <summary>Charges <paramref name="bytes"/> (for <paramref name="what"/>), throwing <c>PLUME3718</c> when the ceiling would be exceeded. A negative or overflowing request is treated as exceeding it.</summary>
    public void Charge(long bytes, string what)
    {
        if (bytes < 0 || bytes > Limit - _used)
        {
            throw new PlumePdfException(
                JpxDiagnosticCodes.ImageTooLarge,
                $"JPEG 2000 decode would need {(bytes < 0 ? "an unrepresentable amount of" : bytes.ToString())} byte(s) for {what} on top of {_used} already in use, exceeding the {Limit}-byte working-memory ceiling PdfOptions.MaxImagePixels implies ({BytesPerReferencePixel} bytes per permitted pixel).");
        }

        _used += bytes;
    }

    /// <summary>Releases <paramref name="bytes"/> previously charged (a finished tile's buffers). A negative amount is a bookkeeping error, never a way to raise <see cref="Used"/>: like <see cref="Charge"/>'s own negative guard, it is rejected rather than applied.</summary>
    public void Release(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        _used = Math.Max(0, _used - bytes);
    }
}
