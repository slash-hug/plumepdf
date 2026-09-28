/*
 * pdfium_shim.c — the PDFium oracle CLI (Phase 8 and Phase 9).
 *
 * A PlumePdf-authored variant of bblanchon/pdfium-binaries' own reference shim
 * (github.com/bblanchon/pdfium-binaries/blob/chromium/8009/example/example.c, MIT-licensed
 * repo wrapper around BSD-3-Clause PDFium itself — see licenses/pdfium.txt in the release
 * archive), permissively adapted per the clean-room policy in AGENTS.md's porting allowance:
 * the load/render call sequence (FPDF_InitLibrary -> FPDF_LoadDocument -> FPDF_LoadPage ->
 * FPDFBitmap_Create -> FPDF_RenderPageBitmap -> FPDFBitmap_GetBuffer) is unchanged, but this
 * file replaces upstream's fixed "input output" PPM dump with the CLI contract
 * tests/PlumePdf.CorpusTests/PdfiumOracle.cs already documents and probes:
 *
 *   pdfium_shim --self-test
 *     Renders a tiny in-memory PDF end-to-end (proves the loader actually found
 *     libpdfium.so/.dylib and a real render succeeded, not just that the binary starts),
 *     THEN (Phase 9) builds a second in-memory PDF containing a single `/Tx` widget field
 *     with `/V` set and no `/AP` stream, renders it through the same form-aware path
 *     `--render-forms` uses, and asserts the resulting bitmap is non-blank — proving the
 *     form-fill environment actually synthesizes a widget appearance, not just that
 *     `FPDFDOC_InitFormFillEnvironment` returns non-NULL. Prints "PDFIUM_SELF_TEST_OK" to
 *     stdout and exits 0 only if BOTH checks pass; exits non-zero with a diagnostic on
 *     stderr otherwise. This is the exact predicate the CI install step
 *     (.github/workflows/ci.yml) and PdfiumOracle.ProbeShim() both key off of.
 *
 *   pdfium_shim --render <input.pdf> <pageIndex> <pixelWidth> <output.png>
 *     Renders the zero-based <pageIndex> page of <input.pdf> to <output.png> at
 *     <pixelWidth> pixels wide, with the height derived from the page's own aspect ratio
 *     (matching PdfRasterizeOptions' DPI-derived sizing model). Exits 0 with the PNG
 *     written on success; non-zero (no output file) on any failure. Paints existing `/AP`
 *     appearance streams (`FPDF_RenderPageBitmap(…, FPDF_ANNOT)`) but does NOT synthesize
 *     appearances for `/V`-no-`/AP` widgets or run `NeedAppearances` — this is the
 *     Phase 8 "annotation-appearance-only" oracle.
 *
 *   pdfium_shim --render-forms <input.pdf> <pageIndex> <pixelWidth> <output.png>
 *     (Phase 9.) Same contract as --render, but additionally stands up
 *     a PDFium form-fill environment (FPDFDOC_InitFormFillEnvironment) and calls
 *     FPDF_FFLDraw after the base page render, so form fields PDFium itself would
 *     synthesize on-screen — including `/V`-no-`/AP` widgets and NeedAppearances
 *     documents — are painted into the output PNG too. This is the form-aware oracle
 *     PlumePdf's own widget-appearance-synthesis path (`WidgetAppearanceSynthesizer`)
 *     is compared against. The FPDF_FORMFILLINFO callback struct and the
 *     Init/OnAfterLoadPage/FFLDraw/OnBeforeClosePage/Exit call sequence below are written
 *     directly against PDFium's own public headers (`fpdf_formfill.h`, BSD-3-Clause,
 *     bundled in the pinned bblanchon/pdfium-binaries release this shim compiles against —
 *     see install-pdfium-oracle.sh) and PDFium's own publicly documented required-callback
 *     table in that header's doc comments; every callback PDFium's header marks
 *     "Implementation Required: yes" for the non-XFA, non-V8 configuration this shim runs
 *     (FFI_Invalidate/FFI_SetCursor/FFI_SetTimer/FFI_KillTimer/FFI_GetLocalTime/FFI_GetPage/
 *     FFI_GetRotation/FFI_ExecuteNamedAction) gets a minimal no-op/stub implementation
 *     below; every other callback is left NULL. See NOTICE for the full provenance record.
 *
 * PNG (not upstream's PPM) is produced because RasterImage.Decode on the C# side only
 * sniffs PNG/JPEG/TIFF containers. The encoder below is a from-scratch, self-contained
 * writer of "stored" (uncompressed) DEFLATE blocks inside a zlib stream — implementing the
 * open RFC 1950 (zlib)/RFC 1951 (DEFLATE) and W3C PNG specifications directly, not ported
 * from any library's source (no libpng/zlib dependency; matches the "PPM output keeps the
 * shim dependency-free" reasoning that motivated the original PPM contract, just targeting
 * PNG's container instead so the C# corpus tests can decode it with the codec they already
 * have).
 */

#include <fpdfview.h>
#include <fpdf_formfill.h>
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* ---------------------------------------------------------------------------------------
 * Minimal PNG writer: 8-bit truecolor RGB, one IDAT chunk of stored (uncompressed) DEFLATE
 * blocks inside a zlib stream. CRC-32 and Adler-32 below are direct implementations of the
 * PNG spec's own CRC-32 sample algorithm and RFC 1950's Adler-32 definition respectively —
 * public, standardized bit operations, not third-party source.
 * ------------------------------------------------------------------------------------- */

static uint32_t crc32_update(uint32_t crc, const unsigned char *buf, size_t len) {
  crc = ~crc;
  for (size_t i = 0; i < len; i++) {
    crc ^= buf[i];
    for (int k = 0; k < 8; k++) {
      uint32_t mask = -(int32_t)(crc & 1u);
      crc = (crc >> 1) ^ (0xEDB88320u & mask);
    }
  }
  return ~crc;
}

static uint32_t adler32_compute(const unsigned char *buf, size_t len) {
  uint32_t a = 1, b = 0;
  const uint32_t MOD = 65521u;
  size_t i = 0;
  while (i < len) {
    size_t chunk = len - i < 4096 ? len - i : 4096; /* keep partial sums small between mods */
    for (size_t j = 0; j < chunk; j++) {
      a += buf[i + j];
      b += a;
    }
    a %= MOD;
    b %= MOD;
    i += chunk;
  }
  return (b << 16) | a;
}

static void put_be32(FILE *fp, uint32_t v) {
  unsigned char b[4] = {(unsigned char)(v >> 24), (unsigned char)(v >> 16),
                         (unsigned char)(v >> 8), (unsigned char)v};
  fwrite(b, 1, 4, fp);
}

static void write_chunk(FILE *fp, const char type[4], const unsigned char *data, uint32_t len) {
  put_be32(fp, len);
  fwrite(type, 1, 4, fp);
  if (len > 0) {
    fwrite(data, 1, len, fp);
  }
  uint32_t crc = crc32_update(0, (const unsigned char *)type, 4);
  if (len > 0) {
    crc = crc32_update(crc, data, len);
  }
  put_be32(fp, crc);
}

/* Writes `raw` (length rawLen) as a zlib stream of "stored" DEFLATE blocks into `fp`,
 * wrapped as a single IDAT chunk. Stored blocks require byte-aligned framing; since the
 * zlib header is exactly 2 bytes and every block we emit is itself byte-aligned, no
 * bit-level packing is needed at all — every field below is a whole byte. */
static void write_idat_stored(FILE *fp, const unsigned char *raw, size_t rawLen) {
  /* Build the chunk payload (zlib header + stored blocks + adler32 trailer) in memory
   * first, since write_chunk needs the final length/CRC up front. */
  size_t maxBlocks = rawLen / 65535 + 1;
  size_t cap = 2 /* zlib header */ + maxBlocks * 5 /* per-block header */ + rawLen + 4 /* adler32 */;
  unsigned char *out = (unsigned char *)malloc(cap);
  if (!out) {
    fprintf(stderr, "pdfium_shim: out of memory encoding PNG (%zu bytes)\n", cap);
    exit(1);
  }
  size_t o = 0;

  int cmf = 0x78;
  int flg = 0;
  int rem = (cmf * 256 + flg) % 31;
  if (rem != 0) {
    flg += (31 - rem);
  }
  out[o++] = (unsigned char)cmf;
  out[o++] = (unsigned char)flg;

  size_t remaining = rawLen;
  const unsigned char *p = raw;
  do {
    size_t blockLen = remaining < 65535 ? remaining : 65535;
    int last = (blockLen == remaining) ? 1 : 0;
    out[o++] = (unsigned char)last; /* BFINAL in bit0, BTYPE=00 (stored) in bits1-2 */
    out[o++] = (unsigned char)(blockLen & 0xFF);
    out[o++] = (unsigned char)((blockLen >> 8) & 0xFF);
    uint16_t nlen = (uint16_t)(~blockLen & 0xFFFF);
    out[o++] = (unsigned char)(nlen & 0xFF);
    out[o++] = (unsigned char)((nlen >> 8) & 0xFF);
    if (blockLen > 0) {
      memcpy(out + o, p, blockLen);
      o += blockLen;
      p += blockLen;
      remaining -= blockLen;
    }
  } while (remaining > 0);

  uint32_t adler = adler32_compute(raw, rawLen);
  out[o++] = (unsigned char)(adler >> 24);
  out[o++] = (unsigned char)(adler >> 16);
  out[o++] = (unsigned char)(adler >> 8);
  out[o++] = (unsigned char)adler;

  write_chunk(fp, "IDAT", out, (uint32_t)o);
  free(out);
}

/* `pixels`/`stride`: BGRx or BGRA rows as FPDFBitmap_GetBuffer returns them (top row first,
 * `stride` bytes per row — see FPDFBitmap_GetStride). Writes an 8-bit truecolor RGB PNG. */
static int write_png(const char *path, int width, int height, const unsigned char *pixels, int stride) {
  FILE *fp = fopen(path, "wb");
  if (!fp) {
    fprintf(stderr, "pdfium_shim: cannot open output '%s' for writing: %s\n", path, strerror(errno));
    return 0;
  }

  static const unsigned char SIG[8] = {0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A};
  fwrite(SIG, 1, sizeof(SIG), fp);

  unsigned char ihdr[13];
  ihdr[0] = (unsigned char)(width >> 24);
  ihdr[1] = (unsigned char)(width >> 16);
  ihdr[2] = (unsigned char)(width >> 8);
  ihdr[3] = (unsigned char)width;
  ihdr[4] = (unsigned char)(height >> 24);
  ihdr[5] = (unsigned char)(height >> 16);
  ihdr[6] = (unsigned char)(height >> 8);
  ihdr[7] = (unsigned char)height;
  ihdr[8] = 8;  /* bit depth */
  ihdr[9] = 2;  /* color type: truecolor RGB */
  ihdr[10] = 0; /* compression method */
  ihdr[11] = 0; /* filter method */
  ihdr[12] = 0; /* interlace method */
  write_chunk(fp, "IHDR", ihdr, sizeof(ihdr));

  size_t rowBytes = 1 + (size_t)width * 3; /* filter-type byte + RGB samples */
  size_t rawLen = rowBytes * (size_t)height;
  unsigned char *raw = (unsigned char *)malloc(rawLen);
  if (!raw) {
    fprintf(stderr, "pdfium_shim: out of memory building PNG scanlines (%zu bytes)\n", rawLen);
    fclose(fp);
    return 0;
  }
  for (int y = 0; y < height; y++) {
    unsigned char *dst = raw + (size_t)y * rowBytes;
    dst[0] = 0; /* filter type: None */
    const unsigned char *src = pixels + (size_t)y * (size_t)stride;
    for (int x = 0; x < width; x++) {
      unsigned char b = src[x * 4 + 0];
      unsigned char g = src[x * 4 + 1];
      unsigned char r = src[x * 4 + 2];
      dst[1 + x * 3 + 0] = r;
      dst[1 + x * 3 + 1] = g;
      dst[1 + x * 3 + 2] = b;
    }
  }

  write_idat_stored(fp, raw, rawLen);
  free(raw);

  write_chunk(fp, "IEND", NULL, 0);
  fclose(fp);
  return 1;
}

/* ---------------------------------------------------------------------------------------
 * PDFium render helpers.
 * ------------------------------------------------------------------------------------- */

/* Creates an outWidth x outHeight (derived from pixelWidth + the page's own aspect ratio)
 * white-filled bitmap and paints the page's base content + existing /AP appearance streams
 * into it (FPDF_RenderPageBitmap(…, FPDF_ANNOT)). Returns NULL (nothing to destroy) on
 * failure. Shared by the plain --render path and the --render-forms path below, which
 * layers FPDF_FFLDraw on top of the same base render.
 */
static FPDF_BITMAP render_page_base(FPDF_PAGE page, int pixelWidth, int renderFlags, int *outWidthOut, int *outHeightOut) {
  double pageWidthPts = FPDF_GetPageWidth(page);
  double pageHeightPts = FPDF_GetPageHeight(page);
  if (pageWidthPts <= 0 || pageHeightPts <= 0) {
    fprintf(stderr, "pdfium_shim: page reports non-positive size (%g x %g pt)\n", pageWidthPts, pageHeightPts);
    return NULL;
  }

  int outWidth = pixelWidth;
  int outHeight = (int)(pixelWidth * pageHeightPts / pageWidthPts + 0.5);
  if (outHeight < 1) {
    outHeight = 1;
  }

  FPDF_BITMAP bitmap = FPDFBitmap_Create(outWidth, outHeight, /*alpha=*/0);
  if (!bitmap) {
    fprintf(stderr, "pdfium_shim: FPDFBitmap_Create(%d, %d) failed\n", outWidth, outHeight);
    return NULL;
  }
  FPDFBitmap_FillRect(bitmap, 0, 0, outWidth, outHeight, 0xFFFFFFFF);
  FPDF_RenderPageBitmap(bitmap, page, 0, 0, outWidth, outHeight, /*rotate=*/0, renderFlags);

  *outWidthOut = outWidth;
  *outHeightOut = outHeight;
  return bitmap;
}

/* The plain render plus two flag variants, each its own verb so the
 * C# oracle can gate PlumePDF's AntiAlias=false and ImageResamplingMode.Point against PDFium's
 * own FPDF_RENDER_NO_SMOOTHPATH|NO_SMOOTHTEXT and FPDF_RENDER_NO_SMOOTHIMAGE output. */
static int render_page_to_png(FPDF_PAGE page, int pixelWidth, int renderFlags, const char *outputPath) {
  int outWidth, outHeight;
  FPDF_BITMAP bitmap = render_page_base(page, pixelWidth, renderFlags, &outWidth, &outHeight);
  if (!bitmap) {
    return 0;
  }

  const unsigned char *pixels = (const unsigned char *)FPDFBitmap_GetBuffer(bitmap);
  int stride = FPDFBitmap_GetStride(bitmap);
  int ok = pixels && write_png(outputPath, outWidth, outHeight, pixels, stride);

  FPDFBitmap_Destroy(bitmap);
  return ok;
}

/* ---------------------------------------------------------------------------------------
 * Form-fill environment (Phase 9): the minimal FPDF_FORMFILLINFO
 * needed to drive FPDF_FFLDraw for a one-shot, non-interactive CLI render. `base` MUST be
 * the struct's first member — every FFI_* callback receives a `FPDF_FORMFILLINFO*` back
 * from PDFium and this shim recovers the enclosing ShimFormFillInfo via that pointer being
 * numerically identical to &shim->base (guaranteed by C's no-padding-before-first-member
 * rule), the same pattern PDFium's own embedder tooling uses to attach extra state to this
 * struct. Every callback PDFium's fpdf_formfill.h marks "Implementation Required: yes" for
 * a non-XFA, non-V8 build is implemented as a minimal, correctness-preserving no-op/stub
 * below (no interactive UI, no timers, no JS platform); every other callback is left NULL.
 * ------------------------------------------------------------------------------------- */

typedef struct {
  FPDF_FORMFILLINFO base;
  FPDF_PAGE page; /* the single page this one-shot CLI render operates on */
} ShimFormFillInfo;

static void shim_ffi_invalidate(FPDF_FORMFILLINFO *pThis, FPDF_PAGE page, double left, double top, double right, double bottom) {
  (void)pThis;
  (void)page;
  (void)left;
  (void)top;
  (void)right;
  (void)bottom;
  /* One-shot CLI render: FPDF_FFLDraw below repaints the whole page unconditionally after
   * form-fill setup, so there is nothing incremental to invalidate. */
}

static void shim_ffi_set_cursor(FPDF_FORMFILLINFO *pThis, int nCursorType) {
  (void)pThis;
  (void)nCursorType;
  /* No interactive cursor in a CLI process. */
}

static int shim_ffi_set_timer(FPDF_FORMFILLINFO *pThis, int uElapse, TimerCallback lpTimerFunc) {
  (void)pThis;
  (void)uElapse;
  (void)lpTimerFunc;
  return 0; /* No timer support needed for a single synchronous render. */
}

static void shim_ffi_kill_timer(FPDF_FORMFILLINFO *pThis, int nTimerID) {
  (void)pThis;
  (void)nTimerID;
}

static FPDF_SYSTEMTIME shim_ffi_get_local_time(FPDF_FORMFILLINFO *pThis) {
  (void)pThis;
  FPDF_SYSTEMTIME t;
  memset(&t, 0, sizeof(t));
  return t; /* Header notes this callback is unused by the non-interactive render path. */
}

static FPDF_PAGE shim_ffi_get_page(FPDF_FORMFILLINFO *pThis, FPDF_DOCUMENT document, int nPageIndex) {
  (void)document;
  (void)nPageIndex;
  return ((ShimFormFillInfo *)pThis)->page; /* Single-page CLI render: always the one page. */
}

static FPDF_PAGE shim_ffi_get_current_page(FPDF_FORMFILLINFO *pThis, FPDF_DOCUMENT document) {
  (void)document;
  return ((ShimFormFillInfo *)pThis)->page;
}

static int shim_ffi_get_rotation(FPDF_FORMFILLINFO *pThis, FPDF_PAGE page) {
  (void)pThis;
  (void)page;
  return 0; /* Header notes this callback is unused by the non-interactive render path. */
}

static void shim_ffi_execute_named_action(FPDF_FORMFILLINFO *pThis, FPDF_BYTESTRING namedAction) {
  (void)pThis;
  (void)namedAction;
  /* Named actions (Print/NextPage/…) are UI-driven navigation; out of scope for a
   * single-page CLI render — deliberately not executed. */
}

static void init_shim_form_fill_info(ShimFormFillInfo *shim, FPDF_PAGE page) {
  memset(shim, 0, sizeof(*shim));
  shim->base.version = 2;
  shim->base.FFI_Invalidate = shim_ffi_invalidate;
  shim->base.FFI_SetCursor = shim_ffi_set_cursor;
  shim->base.FFI_SetTimer = shim_ffi_set_timer;
  shim->base.FFI_KillTimer = shim_ffi_kill_timer;
  shim->base.FFI_GetLocalTime = shim_ffi_get_local_time;
  shim->base.FFI_GetPage = shim_ffi_get_page;
  shim->base.FFI_GetCurrentPage = shim_ffi_get_current_page;
  shim->base.FFI_GetRotation = shim_ffi_get_rotation;
  shim->base.FFI_ExecuteNamedAction = shim_ffi_execute_named_action;
  shim->page = page;
}

/* True if any pixel in the BGRx/BGRA `pixels` buffer differs meaningfully from opaque
 * white — the non-blank predicate the self-test's form-aware fixture check (and, in
 * principle, any future caller) needs. A small per-channel tolerance (5/255) absorbs
 * antialiasing noise on an otherwise-blank page without weakening the "did anything
 * actually paint" proof. */
static int bitmap_has_ink(const unsigned char *pixels, int width, int height, int stride) {
  const int tolerance = 5;
  for (int y = 0; y < height; y++) {
    const unsigned char *row = pixels + (size_t)y * (size_t)stride;
    for (int x = 0; x < width; x++) {
      const unsigned char *px = row + (size_t)x * 4;
      if (px[0] < 255 - tolerance || px[1] < 255 - tolerance || px[2] < 255 - tolerance) {
        return 1;
      }
    }
  }
  return 0;
}

/* The form-aware render: the same base render as render_page_to_png, plus a PDFium
 * form-fill environment driving FPDF_FFLDraw so widget appearances PDFium itself would
 * synthesize on screen (including /V-no-/AP fields and NeedAppearances documents) are
 * painted too. `hadInkOut`, if non-NULL, receives bitmap_has_ink's verdict on the final
 * bitmap — used by the self-test's non-blank assertion; --render-forms itself ignores it.
 */
static int render_page_to_png_form_aware(FPDF_DOCUMENT doc, FPDF_PAGE page, int pixelWidth, const char *outputPath, int *hadInkOut) {
  int outWidth, outHeight;
  FPDF_BITMAP bitmap = render_page_base(page, pixelWidth, FPDF_ANNOT, &outWidth, &outHeight);
  if (!bitmap) {
    return 0;
  }

  ShimFormFillInfo formInfo;
  init_shim_form_fill_info(&formInfo, page);

  FPDF_FORMHANDLE form = FPDFDOC_InitFormFillEnvironment(doc, &formInfo.base);
  if (!form) {
    fprintf(stderr, "pdfium_shim: FPDFDOC_InitFormFillEnvironment failed\n");
    FPDFBitmap_Destroy(bitmap);
    return 0;
  }

  /* Init/OnAfterLoadPage/FFLDraw/OnBeforeClosePage/Exit: the call sequence PDFium's own
   * public fpdf_formfill.h documents each function's own doc comment as requiring, in this
   * order, around a form-aware render (see this file's header comment for provenance). */
  FORM_DoDocumentJSAction(form);   /* No-op on a non-V8 PDFium build or a JS-free document. */
  FORM_DoDocumentOpenAction(form); /* No-op absent an /OpenAction. */
  FORM_OnAfterLoadPage(page, form);

  FPDF_FFLDraw(form, bitmap, page, 0, 0, outWidth, outHeight, /*rotate=*/0, FPDF_ANNOT);

  const unsigned char *pixels = (const unsigned char *)FPDFBitmap_GetBuffer(bitmap);
  int stride = FPDFBitmap_GetStride(bitmap);
  int ok = 0;
  if (pixels) {
    if (hadInkOut) {
      *hadInkOut = bitmap_has_ink(pixels, outWidth, outHeight, stride);
    }
    ok = write_png(outputPath, outWidth, outHeight, pixels, stride);
  }

  FORM_OnBeforeClosePage(page, form);
  FPDFDOC_ExitFormFillEnvironment(form);
  FPDFBitmap_Destroy(bitmap);
  return ok;
}

/* Phase 9 self-test extension: builds a tiny in-memory AcroForm PDF
 * with a single /Tx (text) widget field carrying /V but deliberately NO /AP appearance
 * stream, renders it through render_page_to_png_form_aware, and asserts the result is
 * non-blank — proving the form-fill environment above actually synthesizes a widget
 * appearance (the exact case --render-forms exists for), not merely that
 * FPDFDOC_InitFormFillEnvironment returns a non-NULL handle. Uses the same
 * exactly-offset-computed, no-hand-transcribed-xref construction style as the base
 * self-test fixture above. Returns 0 on success, 1 (with a stderr diagnostic) on failure.
 *
 * The widget's /T (partial field name) is NOT optional here even though ISO 32000-1
 * §12.7.3.2 allows a field dictionary to omit it when inheriting a name from a /Parent:
 * this is a standalone top-level field with no /Parent, and empirically (verified locally
 * against this exact pinned pdfium build) PDFium's CPDFSDK_InteractiveForm silently
 * declines to associate a /T-less widget with any CPDF_FormField at all —
 * FPDFAnnot_GetFormFieldType returns -1 (unrecognized) rather than the expected
 * FPDF_FORMFIELD_TEXTFIELD, and FPDF_FFLDraw then has no field to synthesize an
 * appearance for, silently producing a blank page. Omitting /T here would make this
 * self-test check fail even though the fixture is otherwise spec-legal. */
static int run_self_test_form_fixture(void) {
  char pdf[2048];
  size_t len = 0;
  size_t off1, off2, off3, off4, off5, off6, offxref;

  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "%%PDF-1.4\n");
  off1 = len;
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len,
                           "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 6 0 R >>\nendobj\n");
  off2 = len;
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
  off3 = len;
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len,
                           "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 40] "
                           "/Resources << /Font << /Helv 5 0 R >> >> /Annots [4 0 R] >>\nendobj\n");
  off4 = len;
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len,
                           "4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Tx /T (TestField) "
                           "/Rect [5 5 95 35] /V (PlumePDF) /DA (/Helv 12 Tf 0 g) /P 3 0 R >>\nendobj\n");
  off5 = len;
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len,
                           "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
                           "/Encoding /WinAnsiEncoding >>\nendobj\n");
  off6 = len;
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len,
                           "6 0 obj\n<< /Fields [4 0 R] /DR << /Font << /Helv 5 0 R >> >> "
                           "/DA (/Helv 12 Tf 0 g) >>\nendobj\n");
  offxref = len;
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "xref\n0 7\n0000000000 65535 f \n");
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "%010zu 00000 n \n", off1);
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "%010zu 00000 n \n", off2);
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "%010zu 00000 n \n", off3);
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "%010zu 00000 n \n", off4);
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "%010zu 00000 n \n", off5);
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "%010zu 00000 n \n", off6);
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len,
                           "trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n%zu\n%%%%EOF\n", offxref);

  FPDF_DOCUMENT doc = FPDF_LoadMemDocument(pdf, (int)len, NULL);
  if (!doc) {
    fprintf(stderr, "PDFIUM_SELF_TEST_FAIL: form fixture — FPDF_LoadMemDocument failed "
                     "(last error %lu)\n",
            (unsigned long)FPDF_GetLastError());
    return 1;
  }

  FPDF_PAGE page = FPDF_LoadPage(doc, 0);
  if (!page) {
    fprintf(stderr, "PDFIUM_SELF_TEST_FAIL: form fixture — FPDF_LoadPage(0) failed\n");
    FPDF_CloseDocument(doc);
    return 1;
  }

  const char *tmpDir = getenv("TMPDIR");
  if (!tmpDir || !*tmpDir) {
    tmpDir = "/tmp";
  }
  char outPath[1200];
  snprintf(outPath, sizeof(outPath), "%s/pdfium_shim_selftest_forms_%d.png", tmpDir, (int)getpid());

  int hadInk = 0;
  int rendered = render_page_to_png_form_aware(doc, page, /*pixelWidth=*/200, outPath, &hadInk);
  remove(outPath);

  FPDF_ClosePage(page);
  FPDF_CloseDocument(doc);

  if (!rendered) {
    fprintf(stderr, "PDFIUM_SELF_TEST_FAIL: form fixture — render-forms render/PNG-encode "
                     "did not succeed\n");
    return 1;
  }

  if (!hadInk) {
    fprintf(stderr, "PDFIUM_SELF_TEST_FAIL: form fixture — a /V-no-/AP text widget rendered "
                     "through the form-fill path (FPDFDOC_InitFormFillEnvironment + "
                     "FPDF_FFLDraw) produced a blank page; the form-aware oracle is not "
                     "actually synthesizing widget appearances\n");
    return 1;
  }

  return 0;
}

static int run_self_test(void) {
  /* A tiny, exactly-offset-computed one-page PDF (blank 4x4pt page), built at runtime so
   * there is no hand-transcribed xref table to get wrong. Exercises the full pipeline —
   * library init, in-memory load, page load, bitmap render, PNG encode — end to end. */
  char pdf[1024];
  size_t len = 0;
  size_t off1, off2, off3, offxref;

  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "%%PDF-1.4\n");
  off1 = len;
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
  off2 = len;
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
  off3 = len;
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len,
                           "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 4 4] /Resources << >> >>\nendobj\n");
  offxref = len;
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "xref\n0 4\n0000000000 65535 f \n");
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "%010zu 00000 n \n", off1);
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "%010zu 00000 n \n", off2);
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len, "%010zu 00000 n \n", off3);
  len += (size_t)snprintf(pdf + len, sizeof(pdf) - len,
                           "trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n%zu\n%%%%EOF\n", offxref);

  FPDF_InitLibrary();

  FPDF_DOCUMENT doc = FPDF_LoadMemDocument(pdf, (int)len, NULL);
  if (!doc) {
    fprintf(stderr, "PDFIUM_SELF_TEST_FAIL: FPDF_LoadMemDocument failed (last error %lu) — "
                     "libpdfium did not load a well-formed in-memory PDF\n",
            (unsigned long)FPDF_GetLastError());
    FPDF_DestroyLibrary();
    return 1;
  }

  FPDF_PAGE page = FPDF_LoadPage(doc, 0);
  if (!page) {
    fprintf(stderr, "PDFIUM_SELF_TEST_FAIL: FPDF_LoadPage(0) failed\n");
    FPDF_CloseDocument(doc);
    FPDF_DestroyLibrary();
    return 1;
  }

  const char *tmpDir = getenv("TMPDIR");
  if (!tmpDir || !*tmpDir) {
    tmpDir = "/tmp";
  }
  char outPath[1200];
  snprintf(outPath, sizeof(outPath), "%s/pdfium_shim_selftest_%d.png", tmpDir, (int)getpid());

  int rendered = render_page_to_png(page, /*pixelWidth=*/4, FPDF_ANNOT, outPath);

  FPDF_ClosePage(page);
  FPDF_CloseDocument(doc);

  /* Library torn down once, below, after BOTH the base render check and the Phase 9
   * form-aware fixture check (run_self_test_form_fixture reuses this same FPDF_InitLibrary
   * call — FPDF_LoadMemDocument/FPDF_LoadPage are only valid while the library stays
   * initialized) — baseSizeOk collapses every base-check failure into one FPDF_DestroyLibrary
   * call below, rather than destroying the library twice or leaking it on a failure path. */
  int baseSizeOk = 0;
  if (!rendered) {
    fprintf(stderr, "PDFIUM_SELF_TEST_FAIL: render-to-PNG of the self-test fixture failed — "
                     "libpdfium loaded but rendering or PNG encoding did not succeed\n");
  } else {
    FILE *check = fopen(outPath, "rb");
    if (!check) {
      fprintf(stderr, "PDFIUM_SELF_TEST_FAIL: render reported success but '%s' is not readable\n", outPath);
    } else {
      fseek(check, 0, SEEK_END);
      long size = ftell(check);
      fclose(check);
      remove(outPath);
      if (size <= 0) {
        fprintf(stderr, "PDFIUM_SELF_TEST_FAIL: self-test PNG at '%s' was empty\n", outPath);
      } else {
        baseSizeOk = 1;
      }
    }
  }

  if (!baseSizeOk) {
    FPDF_DestroyLibrary();
    return 1;
  }

  /* Phase 9: the form-aware oracle self-test must also prove itself
   * before this binary is trusted as the --render-forms oracle — see
   * run_self_test_form_fixture's own doc comment. */
  int formCheckFailed = run_self_test_form_fixture();
  FPDF_DestroyLibrary();

  if (formCheckFailed) {
    return 1;
  }

  /* The flag verbs must render the same in-memory page (non-fatal to
   * regressions in libpdfium's flag handling: any failure is a hard self-test failure, since a
   * cap-less shim must never be armed for the flagged oracle legs). */
  {
    FPDF_InitLibrary();
    FPDF_DOCUMENT capsDoc = FPDF_LoadMemDocument(pdf, (int)len, NULL);
    FPDF_PAGE capsPage = capsDoc ? FPDF_LoadPage(capsDoc, 0) : NULL;
    int capsOk = capsPage != NULL;
    if (capsOk) {
      int w, h;
      FPDF_BITMAP b1 = render_page_base(capsPage, 4, FPDF_ANNOT | FPDF_RENDER_NO_SMOOTHPATH | FPDF_RENDER_NO_SMOOTHTEXT, &w, &h);
      FPDF_BITMAP b2 = render_page_base(capsPage, 4, FPDF_ANNOT | FPDF_RENDER_NO_SMOOTHIMAGE, &w, &h);
      capsOk = b1 != NULL && b2 != NULL;
      if (b1) FPDFBitmap_Destroy(b1);
      if (b2) FPDFBitmap_Destroy(b2);
      FPDF_ClosePage(capsPage);
    }
    if (capsDoc) FPDF_CloseDocument(capsDoc);
    FPDF_DestroyLibrary();
    if (!capsOk) {
      fprintf(stderr, "PDFIUM_SELF_TEST_FAIL: flagged render verbs (--render-aliased / --render-nosmooth-image) failed\n");
      return 1;
    }
  }
  printf("PDFIUM_SHIM_CAPS: render-aliased render-nosmooth-image\n");
  printf("PDFIUM_SELF_TEST_OK\n");
  return 0;
}

static int run_render(const char *inputPath, int pageIndex, int pixelWidth, int renderFlags, const char *outputPath) {
  if (pixelWidth <= 0) {
    fprintf(stderr, "pdfium_shim: pixelWidth must be positive, got %d\n", pixelWidth);
    return 1;
  }

  FPDF_InitLibrary();

  FPDF_DOCUMENT doc = FPDF_LoadDocument(inputPath, NULL);
  if (!doc) {
    fprintf(stderr, "pdfium_shim: failed to load '%s' (FPDF_GetLastError=%lu)\n", inputPath,
            (unsigned long)FPDF_GetLastError());
    FPDF_DestroyLibrary();
    return 1;
  }

  int pageCount = FPDF_GetPageCount(doc);
  if (pageIndex < 0 || pageIndex >= pageCount) {
    fprintf(stderr, "pdfium_shim: pageIndex %d out of range (document has %d page(s))\n", pageIndex, pageCount);
    FPDF_CloseDocument(doc);
    FPDF_DestroyLibrary();
    return 1;
  }

  FPDF_PAGE page = FPDF_LoadPage(doc, pageIndex);
  if (!page) {
    fprintf(stderr, "pdfium_shim: FPDF_LoadPage(%d) failed\n", pageIndex);
    FPDF_CloseDocument(doc);
    FPDF_DestroyLibrary();
    return 1;
  }

  int ok = render_page_to_png(page, pixelWidth, renderFlags, outputPath);

  FPDF_ClosePage(page);
  FPDF_CloseDocument(doc);
  FPDF_DestroyLibrary();

  return ok ? 0 : 1;
}

/* --render-forms: same contract as run_render, but drives the page through
 * render_page_to_png_form_aware (Phase 9) so PDFium's own form-fill
 * synthesis paints /V-no-/AP widgets and NeedAppearances documents into the output. */
static int run_render_forms(const char *inputPath, int pageIndex, int pixelWidth, const char *outputPath) {
  if (pixelWidth <= 0) {
    fprintf(stderr, "pdfium_shim: pixelWidth must be positive, got %d\n", pixelWidth);
    return 1;
  }

  FPDF_InitLibrary();

  FPDF_DOCUMENT doc = FPDF_LoadDocument(inputPath, NULL);
  if (!doc) {
    fprintf(stderr, "pdfium_shim: failed to load '%s' (FPDF_GetLastError=%lu)\n", inputPath,
            (unsigned long)FPDF_GetLastError());
    FPDF_DestroyLibrary();
    return 1;
  }

  int pageCount = FPDF_GetPageCount(doc);
  if (pageIndex < 0 || pageIndex >= pageCount) {
    fprintf(stderr, "pdfium_shim: pageIndex %d out of range (document has %d page(s))\n", pageIndex, pageCount);
    FPDF_CloseDocument(doc);
    FPDF_DestroyLibrary();
    return 1;
  }

  FPDF_PAGE page = FPDF_LoadPage(doc, pageIndex);
  if (!page) {
    fprintf(stderr, "pdfium_shim: FPDF_LoadPage(%d) failed\n", pageIndex);
    FPDF_CloseDocument(doc);
    FPDF_DestroyLibrary();
    return 1;
  }

  int ok = render_page_to_png_form_aware(doc, page, pixelWidth, outputPath, /*hadInkOut=*/NULL);

  FPDF_ClosePage(page);
  FPDF_CloseDocument(doc);
  FPDF_DestroyLibrary();

  return ok ? 0 : 1;
}

static void usage(const char *argv0) {
  fprintf(stderr,
          "Usage:\n"
          "  %s --self-test\n"
          "  %s --render <input.pdf> <pageIndex> <pixelWidth> <output.png>\n"
          "  %s --render-forms <input.pdf> <pageIndex> <pixelWidth> <output.png>\n"
          "  %s --render-aliased <input.pdf> <pageIndex> <pixelWidth> <output.png>\n"
          "  %s --render-nosmooth-image <input.pdf> <pageIndex> <pixelWidth> <output.png>\n",
          argv0, argv0, argv0, argv0, argv0);
}

int main(int argc, char *argv[]) {
  if (argc < 2) {
    usage(argv[0]);
    return 1;
  }

  if (strcmp(argv[1], "--self-test") == 0) {
    return run_self_test();
  }

  if (strcmp(argv[1], "--render") == 0) {
    if (argc != 6) {
      usage(argv[0]);
      return 1;
    }
    int pageIndex = atoi(argv[3]);
    int pixelWidth = atoi(argv[4]);
    return run_render(argv[2], pageIndex, pixelWidth, FPDF_ANNOT, argv[5]);
  }

  if (strcmp(argv[1], "--render-aliased") == 0) {
    if (argc != 6) {
      usage(argv[0]);
      return 1;
    }
    return run_render(argv[2], atoi(argv[3]), atoi(argv[4]), FPDF_ANNOT | FPDF_RENDER_NO_SMOOTHPATH | FPDF_RENDER_NO_SMOOTHTEXT, argv[5]);
  }

  if (strcmp(argv[1], "--render-nosmooth-image") == 0) {
    if (argc != 6) {
      usage(argv[0]);
      return 1;
    }
    return run_render(argv[2], atoi(argv[3]), atoi(argv[4]), FPDF_ANNOT | FPDF_RENDER_NO_SMOOTHIMAGE, argv[5]);
  }

  if (strcmp(argv[1], "--render-forms") == 0) {
    if (argc != 6) {
      usage(argv[0]);
      return 1;
    }
    int pageIndex = atoi(argv[3]);
    int pixelWidth = atoi(argv[4]);
    return run_render_forms(argv[2], pageIndex, pixelWidth, argv[5]);
  }

  usage(argv[0]);
  return 1;
}
