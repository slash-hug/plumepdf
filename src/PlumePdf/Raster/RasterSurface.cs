using System.Runtime.InteropServices;

namespace PlumePdf.Raster;

/// <summary>
/// A mutable in-memory BGRA (32 bits per pixel: B, G, R, A, top-down, no row padding) raster
/// target the interpreter paints into. This is the write-side counterpart of
/// <see cref="RasterImageFrame"/>'s read-only <see cref="RasterPixelFormat.Rgba32"/> view — BGRA
/// (not RGBA) matches PDFium's and most software rasterizers' native compositing byte order, so
/// <see cref="BlendPixel"/>'s per-channel math stays a simple little-endian <c>uint</c> load on
/// every platform this pipeline runs on; the channel swap to <see cref="RasterPixelFormat.Rgba32"/>
/// happens once, at <see cref="ToRasterImageFrame"/>.
/// </summary>
/// <remarks>
/// Allocation is cap-checked <em>before</em> the pixel buffer is allocated (a repeated
/// lesson from earlier reviews: validate every document/caller-controlled dimension against
/// its cap before the allocation that dimension drives). <see cref="Create"/> is the only
/// constructor path; there is no way to obtain a <see cref="RasterSurface"/> whose byte size was
/// not checked first.
/// </remarks>
internal sealed class RasterSurface
{
    /// <summary>
    /// The default ceiling on a surface's total pixel-buffer size in bytes, used when a caller
    /// does not yet have a <c>PdfOptions.MaxRasterSurfaceBytes</c> to thread through (that cap
    /// lands on <c>PdfOptions</c> once <see cref="Rasterizer"/> can read it). Sized to the same ≈512 MB ceiling:
    /// <c>1 &lt;&lt; 27</c> pixels (the existing <c>PdfOptions.MaxImagePixels</c>
    /// default) × 4 BGRA bytes/pixel.
    /// </summary>
    public const long DefaultMaxSurfaceBytes = (1L << 27) * 4;

    private readonly byte[] _pixels;
    private readonly bool _pooled;

    private RasterSurface(int width, int height, byte[] pixels, bool pooled = false)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
        _pooled = pooled;
    }

    /// <summary>The surface's width in pixels.</summary>
    public int Width { get; }

    /// <summary>The surface's height in pixels.</summary>
    public int Height { get; }

    /// <summary>The number of bytes between the start of one row and the next — always <c>Width * 4</c> (no row padding).</summary>
    public int Stride => Width * 4;

    /// <summary>The raw BGRA pixel buffer, top-down, unpadded — exposed for the pixel-determinism regression test, which compares raw buffers rather than only encoded PNG bytes. Explicitly length-sliced: a pooled surface's backing array may be longer than <c>Width * Height * 4</c>.</summary>
    public ReadOnlySpan<byte> Pixels => _pixels.AsSpan(0, Width * Height * 4);

    /// <summary>
    /// The raw BGRA pixel buffer, mutable — used by transparency-group compositing
    /// (<see cref="Transparency.TransparencyGroup"/>) to write a rendered group's composite
    /// result directly onto the destination surface without an extra allocation/copy.
    /// </summary>
    internal Span<byte> MutablePixels => _pixels.AsSpan(0, Width * Height * 4);

    /// <summary>
    /// Overwrites the entire pixel buffer with <paramref name="source"/> — used to preload a
    /// non-isolated transparency group's offscreen buffer with its captured backdrop
    /// (<see cref="Transparency.Backdrop.Capture"/>) before painting the group's own content into
    /// it (ISO 32000-1 §11.4.7).
    /// </summary>
    /// <param name="source">Exactly <see cref="Pixels"/>' length (<c>Width * Height * 4</c>) bytes.</param>
    internal void LoadPixels(ReadOnlySpan<byte> source) => source.CopyTo(_pixels);

    /// <summary>
    /// Allocates a <paramref name="width"/> × <paramref name="height"/> BGRA surface, refusing
    /// before allocating when the resulting byte size would exceed <paramref name="maxSurfaceBytes"/>.
    /// </summary>
    /// <param name="width">The surface width in pixels. Must be positive.</param>
    /// <param name="height">The surface height in pixels. Must be positive.</param>
    /// <param name="maxSurfaceBytes">The byte-size ceiling to enforce before allocating; defaults to <see cref="DefaultMaxSurfaceBytes"/>.</param>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME7500</c> — <paramref name="width"/>/<paramref name="height"/> are non-positive, or
    /// the required buffer size (<c>width * height * 4</c>, computed in 64-bit arithmetic so an
    /// extreme-but-technically-representable dimension pair can't wrap a 32-bit product before
    /// the cap comparison ever sees it) exceeds <paramref name="maxSurfaceBytes"/>.
    /// </exception>
    public static RasterSurface Create(int width, int height, long maxSurfaceBytes = DefaultMaxSurfaceBytes)
    {
        if (width <= 0 || height <= 0)
        {
            throw new PlumePdfException("PLUME7500", $"RasterSurface.Create: width and height must both be positive; got {width}x{height}.");
        }

        // 64-bit throughout: width/height are int, but the product (before the ×4 for BGRA)
        // must not be computed in 32-bit arithmetic first, or two document/caller-supplied
        // dimensions that are each individually small could still overflow int before the cap
        // check runs (the exact Phase-7 TIFF-stride overflow class).
        var requiredBytes = (long)width * height * 4;
        if (requiredBytes > maxSurfaceBytes)
        {
            throw new PlumePdfException("PLUME7500", $"RasterSurface.Create: a {width}x{height} BGRA surface needs {requiredBytes:N0} bytes, exceeding the {maxSurfaceBytes:N0}-byte cap (PdfOptions.MaxRasterSurfaceBytes); refusing to allocate.");
        }

        return new RasterSurface(width, height, new byte[requiredBytes]);
    }

    /// <summary>
    /// Rents a surface over an <see cref="System.Buffers.ArrayPool{T}"/> buffer
    /// — for the transparency pass's strictly-scoped page-size temporaries, which used to be
    /// fresh allocations per group (and per knockout child). Same cap check as
    /// <see cref="Create"/>; the buffer's contents are UNDEFINED — the caller initializes
    /// whatever region it will actually read (<see cref="ClearRegion"/>/<see cref="LoadRegion"/>).
    /// Pair with <see cref="ReturnToPool"/>.
    /// </summary>
    internal static RasterSurface Rent(int width, int height, long maxSurfaceBytes = DefaultMaxSurfaceBytes)
    {
        if (width <= 0 || height <= 0)
        {
            throw new PlumePdfException("PLUME7500", $"RasterSurface.Rent: width and height must both be positive; got {width}x{height}.");
        }

        var requiredBytes = (long)width * height * 4;
        if (requiredBytes > maxSurfaceBytes)
        {
            throw new PlumePdfException("PLUME7500", $"RasterSurface.Rent: a {width}x{height} BGRA surface needs {requiredBytes:N0} bytes, exceeding the {maxSurfaceBytes:N0}-byte cap (PdfOptions.MaxRasterSurfaceBytes); refusing to allocate.");
        }

        return new RasterSurface(width, height, System.Buffers.ArrayPool<byte>.Shared.Rent((int)requiredBytes), pooled: true);
    }

    /// <summary>Returns a <see cref="Rent"/>ed surface's buffer to the pool. The surface must not be used afterwards. A no-op for a <see cref="Create"/>d surface.</summary>
    internal void ReturnToPool()
    {
        if (_pooled)
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(_pixels);
        }
    }

    /// <summary>Fills the <paramref name="w"/>×<paramref name="h"/> region at (<paramref name="x0"/>, <paramref name="y0"/>) with one BGRA color — the region-scoped sibling of <see cref="Clear"/>, for pooled surfaces whose unread remainder can stay uninitialized.</summary>
    internal void ClearRegion(int x0, int y0, int w, int h, byte b, byte g, byte r, byte a)
    {
        if (w <= 0 || h <= 0)
        {
            return;
        }

        if (BitConverter.IsLittleEndian)
        {
            var packed = (uint)(b | (g << 8) | (r << 16) | (a << 24));
            for (var y = y0; y < y0 + h; y++)
            {
                MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan((y * Stride) + (x0 * 4), w * 4)).Fill(packed);
            }

            return;
        }

        for (var y = y0; y < y0 + h; y++)
        {
            var row = _pixels.AsSpan((y * Stride) + (x0 * 4), w * 4);
            for (var o = 0; o < row.Length; o += 4)
            {
                row[o] = b;
                row[o + 1] = g;
                row[o + 2] = r;
                row[o + 3] = a;
            }
        }
    }

    /// <summary>Writes <paramref name="source"/> (a compact <paramref name="w"/>×<paramref name="h"/> BGRA block, e.g. <see cref="Transparency.Backdrop.Capture"/>'s result) into the region at (<paramref name="x0"/>, <paramref name="y0"/>).</summary>
    internal void LoadRegion(ReadOnlySpan<byte> source, int x0, int y0, int w, int h)
    {
        for (var y = 0; y < h; y++)
        {
            source.Slice(y * w * 4, w * 4).CopyTo(_pixels.AsSpan(((y0 + y) * Stride) + (x0 * 4), w * 4));
        }
    }

    /// <summary>Fills the entire surface with a single opaque or transparent BGRA color — the initial page background.</summary>
    public void Clear(byte b, byte g, byte r, byte a)
    {
        if (BitConverter.IsLittleEndian)
        {
            // One 32-bit store per pixel: a little-endian uint whose bytes are
            // B,G,R,A in memory order — Span<uint>.Fill is a vectorized fill, replacing the old
            // row-template + per-row copy (which also allocated the template row every call).
            var packed = (uint)(b | (g << 8) | (r << 16) | (a << 24));
            MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan(0, Width * Height * 4)).Fill(packed);
            return;
        }

        for (var o = 0; o < Width * Height * 4; o += 4)
        {
            _pixels[o] = b;
            _pixels[o + 1] = g;
            _pixels[o + 2] = r;
            _pixels[o + 3] = a;
        }
    }

    /// <summary>Reads the raw BGRA bytes of one pixel; out-of-bounds coordinates are a no-op returning zero.</summary>
    public (byte B, byte G, byte R, byte A) GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return (0, 0, 0, 0);
        }

        var o = (y * Stride) + (x * 4);
        return (_pixels[o], _pixels[o + 1], _pixels[o + 2], _pixels[o + 3]);
    }

    /// <summary>
    /// Source-over alpha-composites one BGRA source pixel onto the surface at
    /// <paramref name="x"/>,<paramref name="y"/>, scaled by <paramref name="coverage"/> (0-255,
    /// the scan converter's per-pixel antialiasing coverage). Out-of-bounds coordinates are a
    /// no-op — the scan converter clips scanlines to <c>[0, Width)</c>/<c>[0, Height)</c>
    /// itself, but a defensive bounds check here means a caller mistake degrades silently
    /// instead of throwing an <see cref="IndexOutOfRangeException"/> mid-render. Integer-only
    /// (premultiplied-alpha "over" compositing with rounding division) — no floating point, so
    /// this stays byte-identical run to run under <c>PdfOptions.Deterministic</c>, same as
    /// every other step of the scan-conversion/paint path.
    /// </summary>
    public void BlendPixel(int x, int y, byte srcB, byte srcG, byte srcR, byte srcA, int coverage)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || coverage <= 0)
        {
            return;
        }

        coverage = Math.Min(coverage, 255);

        // Effective source alpha after scan-converter coverage: srcA * coverage / 255.
        var alpha = ((srcA * coverage) + 127) / 255;
        if (alpha <= 0)
        {
            return;
        }

        var o = (y * Stride) + (x * 4);
        if (alpha >= 255)
        {
            _pixels[o] = srcB;
            _pixels[o + 1] = srcG;
            _pixels[o + 2] = srcR;
            _pixels[o + 3] = srcA;
            return;
        }

        var invAlpha = 255 - alpha;
        var dstA = _pixels[o + 3];
        _pixels[o] = (byte)((((srcB * alpha) + (_pixels[o] * invAlpha)) + 127) / 255);
        _pixels[o + 1] = (byte)((((srcG * alpha) + (_pixels[o + 1] * invAlpha)) + 127) / 255);
        _pixels[o + 2] = (byte)((((srcR * alpha) + (_pixels[o + 2] * invAlpha)) + 127) / 255);
        _pixels[o + 3] = (byte)(alpha + (((dstA * invAlpha) + 127) / 255));
    }

    /// <summary>
    /// Source-over composites a horizontal run of one constant BGRA color at one constant
    /// coverage — the span-granularity sibling of <see cref="BlendPixel"/> (the
    /// scanline sweep emits constant-coverage runs, so the alpha product, the opaque test, and
    /// the bounds clip all hoist out of the per-pixel loop; the arithmetic per pixel is
    /// byte-identical to <see cref="BlendPixel"/>'s). Out-of-bounds portions are clipped, not
    /// errors, matching <see cref="BlendPixel"/>'s defensive stance.
    /// </summary>
    public void BlendSpan(int y, int x, int length, byte srcB, byte srcG, byte srcR, byte srcA, int coverage)
    {
        if ((uint)y >= (uint)Height || coverage <= 0 || length <= 0)
        {
            return;
        }

        if (x < 0)
        {
            length += x;
            x = 0;
        }

        if (x + length > Width)
        {
            length = Width - x;
        }

        if (length <= 0)
        {
            return;
        }

        coverage = Math.Min(coverage, 255);
        var alpha = ((srcA * coverage) + 127) / 255;
        if (alpha <= 0)
        {
            return;
        }

        var row = _pixels.AsSpan((y * Stride) + (x * 4), length * 4);
        if (alpha >= 255)
        {
            if (BitConverter.IsLittleEndian)
            {
                var packed = (uint)(srcB | (srcG << 8) | (srcR << 16) | (srcA << 24));
                MemoryMarshal.Cast<byte, uint>(row).Fill(packed);
                return;
            }

            for (var o = 0; o < row.Length; o += 4)
            {
                row[o] = srcB;
                row[o + 1] = srcG;
                row[o + 2] = srcR;
                row[o + 3] = srcA;
            }

            return;
        }

        var invAlpha = 255 - alpha;
        for (var o = 0; o < row.Length; o += 4)
        {
            var dstA = row[o + 3];
            row[o] = (byte)((((srcB * alpha) + (row[o] * invAlpha)) + 127) / 255);
            row[o + 1] = (byte)((((srcG * alpha) + (row[o + 1] * invAlpha)) + 127) / 255);
            row[o + 2] = (byte)((((srcR * alpha) + (row[o + 2] * invAlpha)) + 127) / 255);
            row[o + 3] = (byte)(alpha + (((dstA * invAlpha) + 127) / 255));
        }
    }

    /// <summary>
    /// Converts this surface into a read-only <see cref="RasterImageFrame"/> (channel order
    /// swapped from BGRA to <see cref="RasterPixelFormat.Rgba32"/>'s RGBA), the shape
    /// <see cref="Rasterizer"/> hands back to the <c>Pdf.Rasterize</c>/<c>doc.Pages[i].Rasterize</c>
    /// verbs. CONSUMES the surface: the swap happens in place and the frame
    /// wraps this surface's own pixel buffer — the old copy was a second full-page allocation
    /// (~29 MB at 2400×3200) made on every render just to reorder channels, whose source was
    /// discarded the moment it returned. After this call the buffer is RGBA, so no further
    /// paint/read against this surface is valid; <see cref="Rasterizer.Rasterize"/> is the only
    /// caller and returns immediately.
    /// </summary>
    public RasterImageFrame ToRasterImageFrame(double? xDpi = null, double? yDpi = null)
    {
        var byteLength = Width * Height * 4;
        if (BitConverter.IsLittleEndian)
        {
            // BGRA -> RGBA is a swap of bytes 0 and 2 in every 32-bit lane; as little-endian
            // uints that is: keep G/A (0xFF00FF00), move B up 16, move R down 16.
            var lanes = MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan(0, byteLength));
            for (var i = 0; i < lanes.Length; i++)
            {
                var v = lanes[i];
                lanes[i] = (v & 0xFF00FF00u) | ((v & 0x000000FFu) << 16) | ((v & 0x00FF0000u) >> 16);
            }
        }
        else
        {
            for (var i = 0; i < byteLength; i += 4)
            {
                (_pixels[i], _pixels[i + 2]) = (_pixels[i + 2], _pixels[i]);
            }
        }

        return new RasterImageFrame(_pixels.AsMemory(0, byteLength), Width, Height, RasterPixelFormat.Rgba32, xDpi, yDpi);
    }
}
