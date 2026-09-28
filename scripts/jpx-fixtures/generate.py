#!/usr/bin/env python3
"""
Generator for the JPX (JPEG 2000) fixture matrix: every entry under
tests/PlumePdf.CorpusTests/Fixtures/jpx/ is produced HERE, either by driving a pinned
OpenJPEG 2.5.4 CLI (opj_compress/opj_decompress) over a deterministic, self-generated
source image, or -- for the nine "hand_built" entries -- by direct byte-level construction
of JP2 boxes / codestream marker segments (clean-room, ISO/IEC 15444-1 Annex A and Annex I,
no third-party code copied).

Determinism: every pixel source is a linear congruential generator seeded with a fixed
integer (never `os.urandom`/`random` without a seed, never a timestamp). opj_compress
embeds one non-deterministic-looking but actually-constant string -- a COM marker reading
"Created by OpenJPEG version 2.5.4" -- which is fine ("committed fixture bytes encode the
oracle version; a future bump regenerates and re-diffs, it is not a regression"). Running
this script twice must produce byte-identical output (`git status` clean the second time);
it never rewrites any file with e.g. a current-time comment.

Structural facts asserted in MANIFEST.json (marker presence, SOT counts, SIZ/COD/QCD
fields, the >=11-passes fact for the cblksty fixtures) are computed by markers.py's real
marker-segment walker, never guessed or hand-transcribed from a single sample run -- see
markers.py's own docstring for why a byte scan is unsafe here.

Usage:
  python3 generate.py             # regenerate the full fixture matrix + MANIFEST.json
  python3 generate.py --check     # re-run only the structural assertions against the
                                   # already-committed fixture bytes and MANIFEST.json
                                   # (no OpenJPEG invocation, no regeneration)
"""
from __future__ import annotations

import os

import argparse
import hashlib
import json
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import markers  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[2]
FIXTURES_DIR = REPO_ROOT / "tests" / "PlumePdf.CorpusTests" / "Fixtures" / "jpx"
MANIFEST_PATH = FIXTURES_DIR / "MANIFEST.json"
TOLERANCE_PATH = FIXTURES_DIR / "jpx-tolerance.json"
README_PATH = FIXTURES_DIR / "README.md"

REFUSAL_CODES = {
    "part2": "PLUME3702",
    "rgn": "PLUME3703",
    "ppm": "PLUME3704",
    "precision17": "PLUME3705",
}
TRUNCATION_CODE = "PLUME3701"


# =========================================================================================
# OpenJPEG CLI resolution (prefer ~/jpx-oracle/bin, else PATH).
# =========================================================================================


def _portable_command(argv, build_dir):
    """Renders an opj_* invocation for MANIFEST.json with the tool's basename instead of the
    resolved absolute binary path (which is machine-specific — a different $HOME would make
    every regeneration a spurious 22-line manifest diff) and with the ephemeral build
    directory stripped, so the recorded command is byte-identical across machines."""
    rendered = []
    for i, a in enumerate(argv):
        a = str(a)
        if i == 0 and os.path.basename(a).startswith("opj_"):
            a = os.path.basename(a)
        rendered.append(a.replace(str(build_dir) + "/", ""))
    return " ".join(rendered)

def _resolve_opj() -> dict:
    candidates = [Path.home() / "jpx-oracle" / "bin"]
    tools = {}
    for name in ("opj_compress", "opj_decompress", "opj_dump"):
        found = None
        for d in candidates:
            p = d / name
            if p.exists():
                found = str(p)
                break
        if found is None:
            found = shutil.which(name)
        if found is None:
            print(f"generate.py: '{name}' not found in ~/jpx-oracle/bin or PATH.",
                  file=sys.stderr)
            sys.exit(1)
        tools[name] = found
    return tools


def _run(cmd: list) -> str:
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(
            f"command failed ({result.returncode}): {' '.join(cmd)}\n"
            f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
        )
    return result.stdout + result.stderr


# =========================================================================================
# Deterministic source generation (LCG: x = (x*1103515245 + 12345) mod 2^31, seeded).
# =========================================================================================

def _lcg(seed: int, n: int) -> bytes:
    s = seed & 0xFFFFFFFF
    out = bytearray(n)
    for i in range(n):
        s = (s * 1103515245 + 12345) & 0x7FFFFFFF
        out[i] = (s >> 16) & 0xFF
    return bytes(out)


def write_ppm(path: Path, w: int, h: int, seed: int):
    """Deterministic RGB source: R/G ramps (spatial gradient) + a fixed-seed-LCG blue
    channel, so every fixture's source spans the full 0..255 range in every channel
    (needed, e.g., so the cblksty fixtures' finest-resolution code-blocks have enough
    coding passes -- what threatens this is low dynamic range in the
    source)."""
    noise = _lcg(seed, w * h)
    data = bytearray(w * h * 3)
    i = 0
    n = 0
    for y in range(h):
        gy = (y * 255) // max(1, h - 1)
        for x in range(w):
            data[i] = (x * 255) // max(1, w - 1)
            data[i + 1] = gy
            data[i + 2] = noise[n]
            i += 3
            n += 1
    path.write_bytes(b"P6\n%d %d\n255\n" % (w, h) + bytes(data))


def write_pgm(path: Path, w: int, h: int, seed: int):
    """Deterministic gray source: fixed-seed LCG over the full 0..255 byte range."""
    path.write_bytes(b"P5\n%d %d\n255\n" % (w, h) + _lcg(seed, w * h))


def write_scan_ppm(path: Path, w: int, h: int, seed: int):
    """Deterministic scan-like RGB source (the shape of a typical scanned form page: a
    near-white sheet with dark text-like strokes, ruled boxes and a little sensor noise) --
    so the 9/7 + ICT fixture at a real compression ratio (-r 20) carries the sharp-edge
    ringing and near-saturated background a synthetic gradient never produces. Every
    decision below comes from the fixed-seed LCG, never from the clock or the platform."""
    noise = _lcg(seed, w * h * 3)
    strokes = _lcg_values(seed + 1, 4096, 0, 0xFFFF)
    data = bytearray(w * h * 3)
    # Paper: 236..247 with per-pixel noise, a slight warm cast.
    i = 0
    n = 0
    for _y in range(h):
        for _x in range(w):
            base = 236 + (noise[n] % 12)
            data[i] = min(255, base + 4)
            data[i + 1] = base
            data[i + 2] = max(0, base - 6)
            i += 3
            n += 3

    def dark(x, y, r, g, b):
        if 0 <= x < w and 0 <= y < h:
            o = (y * w + x) * 3
            data[o], data[o + 1], data[o + 2] = r, g, b

    # Ruled form boxes: 1-px dark lines.
    for k, (x0, y0, bw, bh) in enumerate(((24, 20, w - 48, 40), (24, 80, (w - 48) // 2 - 6, 60),
                                          (24 + (w - 48) // 2 + 6, 80, (w - 48) // 2 - 6, 60),
                                          (24, 160, w - 48, h - 200))):
        for x in range(x0, x0 + bw):
            dark(x, y0, 40, 40, 48)
            dark(x, y0 + bh, 40, 40, 48)
        for y in range(y0, y0 + bh + 1):
            dark(x0, y, 40, 40, 48)
            dark(x0 + bw, y, 40, 40, 48)
    # Text-like strokes: short runs of 2-px-tall dark pixels on 16-px line pitch, with
    # LCG-chosen run lengths and gaps, plus a few blue-ink runs.
    si = 0
    for line_y in range(30, h - 30, 16):
        x = 32
        while x < w - 40:
            run = 2 + (strokes[si % len(strokes)] % 9)
            gap = 2 + ((strokes[si % len(strokes)] >> 8) % 6)
            ink_blue = (strokes[si % len(strokes)] >> 14) == 3
            si += 1
            for dx in range(run):
                for dy in range(2 + (dx % 2)):
                    if ink_blue:
                        dark(x + dx, line_y + dy, 30, 40, 120)
                    else:
                        dark(x + dx, line_y + dy, 24, 24, 28)
            x += run + gap
    path.write_bytes(b"P6\n%d %d\n255\n" % (w, h) + bytes(data))


def _lcg_values(seed: int, n: int, lo: int, hi: int) -> list:
    """n deterministic integers spanning [lo, hi] (inclusive), via the same LCG, scaled --
    used for >8-bit raw sources so precision fixtures exercise their full declared range."""
    span = hi - lo + 1
    s = seed & 0xFFFFFFFF
    out = []
    for _ in range(n):
        s = (s * 1103515245 + 12345) & 0x7FFFFFFF
        out.append(lo + ((s >> 8) % span))
    return out


def write_raw_gray(path: Path, w: int, h: int, seed: int, precision: int, signed: bool):
    """Raw little-endian samples for `opj_compress -F w,h,1,precision,{s|u}` (>8-bit
    precision fixtures use raw input, never PGX -- PGX derives precision from the data
    range and always loses one bit for signed data)."""
    if signed:
        lo, hi = -(1 << (precision - 1)), (1 << (precision - 1)) - 1
    else:
        lo, hi = 0, (1 << precision) - 1
    vals = _lcg_values(seed, w * h, lo, hi)
    fmt = "<h" if signed else "<H"
    if precision <= 8:
        fmt = "<b" if signed else "<B"
    path.write_bytes(b"".join(struct.pack(fmt, v) for v in vals))


def write_raw_multiplane(path: Path, w: int, h: int, n_comp: int, seed: int):
    """Raw 8-bit unsigned samples, one full-resolution plane per component, concatenated --
    for `opj_compress -F w,h,n_comp,8,u` (the CMYK fixture)."""
    data = bytearray()
    for c in range(n_comp):
        data += _lcg(seed + c, w * h)
    path.write_bytes(bytes(data))


def write_raw_mixed_subsampled(path: Path, w: int, h: int, seed: int):
    """4:2:0-style raw source for `-F w,h,3,8,u@1x1:2x2:2x2`: one full-resolution plane,
    then two planes at half resolution in both dimensions."""
    data = bytearray(_lcg(seed, w * h))
    cw, ch = w // 2, h // 2
    data += _lcg(seed + 1, cw * ch)
    data += _lcg(seed + 2, cw * ch)
    path.write_bytes(bytes(data))


def write_png_rgba(path: Path, w: int, h: int, seed: int):
    """Minimal, dependency-free RGBA PNG writer (stdlib zlib only): IHDR + one IDAT (filter
    type 0 per scanline, i.e. no filtering) + IEND. Deterministic zlib.compress output for
    a fixed input + fixed level is stable across runs on the same zlib version, which is
    all this repo's determinism check (`git status` clean on a second run) requires."""
    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(
            ">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    noise = _lcg(seed, w * h)
    raw = bytearray()
    n = 0
    for y in range(h):
        raw.append(0)  # filter type 0 (None) for this scanline
        gy = (y * 255) // max(1, h - 1)
        for x in range(w):
            raw += bytes([(x * 255) // max(1, w - 1), gy, noise[n], 200])
            n += 1
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)  # color type 6 = RGBA
    idat = zlib.compress(bytes(raw), 9)
    data = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", idat)
            + chunk(b"IEND", b""))
    path.write_bytes(data)


# =========================================================================================
# JP2 box construction helpers (Annex I.5) -- for the hand-built fixtures.
# =========================================================================================

JP2_SIGNATURE = struct.pack(">I", 12) + b"jP  " + b"\x0d\x0a\x87\x0a"


def jp2_box(type_: bytes, payload: bytes) -> bytes:
    return struct.pack(">I", 8 + len(payload)) + type_ + payload


def jp2_ftyp() -> bytes:
    return jp2_box(b"ftyp", b"jp2 " + struct.pack(">I", 0) + b"jp2 ")


def jp2_ihdr(h: int, w: int, nc: int, bpc: int) -> bytes:
    # bpc encodes precision-1 in the low 7 bits, sign in bit 7 (same convention as SIZ's
    # Ssiz). C (compression type) is fixed 7 for JPEG 2000; UnkC/IPR both 0 for our
    # fixtures (colourspace known, no IP rights box).
    return jp2_box(b"ihdr", struct.pack(">IIHBBBB", h, w, nc, bpc, 7, 0, 0))


def jp2_colr_enum(enum_cs: int) -> bytes:
    return jp2_box(b"colr", bytes([1, 0, 0]) + struct.pack(">I", enum_cs))


def jp2_pclr(entries: list) -> bytes:
    payload = struct.pack(">HB", len(entries), 3) + bytes([7, 7, 7])
    for r, g, b in entries:
        payload += bytes([r, g, b])
    return jp2_box(b"pclr", payload)


def jp2_cmap(n_out: int) -> bytes:
    payload = b"".join(struct.pack(">HBB", 0, 1, i) for i in range(n_out))
    return jp2_box(b"cmap", payload)


def jp2_res_resd(dpi: float) -> bytes:
    """A `res ` superbox with one `resd` (default display resolution) subbox, per
    Annex I.5.3.7-8: (VRcN, VRcD, HRcN, HRcD, VRcE, HRcE) encode vertical/horizontal
    resolution as (numerator/denominator) * 10^exponent, in pixels per metre. Chosen as
    numerator=11811, denominator=1, exponent=0 -> 11811 px/m == 299.9994 dpi (~300 dpi,
    the nearest exact-ish integer-pixels-per-metre value); opj_compress emits no `res `
    box at all, so this DPI is pinned entirely by construction -- there is
    no oracle to corroborate it against."""
    del dpi  # documented constant above; parameter kept for call-site clarity
    inner = jp2_box(b"resd", struct.pack(">HHHHBB", 11811, 1, 11811, 1, 0, 0))
    return jp2_box(b"res ", inner)


def wrap_jp2(codestream: bytes, jp2h_children: bytes) -> bytes:
    return JP2_SIGNATURE + jp2_ftyp() + jp2_box(b"jp2h", jp2h_children) + jp2_box(b"jp2c", codestream)


# =========================================================================================
# Fixture bookkeeping.
# =========================================================================================

class Matrix:
    def __init__(self, build_dir: Path):
        self.build_dir = build_dir
        self.entries = []  # list of dict, MANIFEST order

    def add(self, entry: dict):
        self.entries.append(entry)


def _copy_into(build_dir: Path, name: str) -> Path:
    return build_dir / name


def _decompress_refs(opj: dict, src: Path, ref_stem: str, build_dir: Path,
                      extra_args: list = None) -> list:
    """Runs opj_decompress -upsample (+ extra_args) and returns the list of committed
    ref_<c>.pgx filenames (renamed from opj_decompress's own <stem>_<c>.pgx output)."""
    extra_args = extra_args or []
    out_prefix = build_dir / (ref_stem + "_tmp.pgx")
    _run([opj["opj_decompress"], "-i", str(src), "-o", str(out_prefix), "-upsample"]
         + extra_args)
    produced = sorted(build_dir.glob(ref_stem + "_tmp_*.pgx"))
    refs = []
    for p in produced:
        comp = p.stem.rsplit("_", 1)[-1]
        dest_name = f"{ref_stem}_ref_{comp}.pgx"
        (build_dir / dest_name).write_bytes(p.read_bytes())
        p.unlink()
        refs.append(dest_name)
    return refs


def _sha256_hex(path: Path) -> str:
    """SHA-256 of a fixture or reference file's exact bytes, for MANIFEST.json's
    integrity fields (`sha256` / `referencesSha256`) -- --check hashes the COMMITTED
    file, so a corrupted or hand-edited reference (garbage bytes, a swapped PGX) fails
    even though the file still merely "exists" (the prior --check only checked
    existence, not content)."""
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _codestream_bytes(path: Path) -> bytes:
    """Returns the raw marker-segment codestream, whether `path` is a bare .j2k or a .jp2
    container (in which case the jp2c box's payload is returned)."""
    data = path.read_bytes()
    if data[:4] == b"\x00\x00\x00\x0c" and data[4:8] == b"jP  ":
        boxes = markers.walk_jp2_boxes(data)
        return markers.find_jp2c(boxes).payload
    return data


def _siz_cod_qcd(cs: markers.Codestream):
    siz = markers.parse_siz(markers.segments_by_marker(cs.main_header_segments, markers.SIZ)[0].payload)
    cod = markers.parse_cod(markers.segments_by_marker(cs.main_header_segments, markers.COD)[0].payload)
    qcd_segs = markers.segments_by_marker(cs.main_header_segments, markers.QCD)
    qcd = markers.parse_qcd(qcd_segs[0].payload) if qcd_segs else None
    return siz, cod, qcd


def _marker_summary(cs: markers.Codestream) -> dict:
    main = cs.main_header_segments
    tp0 = cs.tile_parts[0]
    tp0_headers = tp0.header_segments
    plt_loc = None
    if markers.has_marker(tp0_headers, markers.PLT):
        plt_loc = "tile"
    elif markers.has_marker(main, markers.PLT):
        plt_loc = "main"  # never legal, but recorded rather than assumed
    return {
        "POC": markers.has_marker(tp0_headers, markers.POC) or markers.has_marker(main, markers.POC),
        "RGN": markers.has_marker(tp0_headers, markers.RGN) or markers.has_marker(main, markers.RGN),
        "PPM": markers.has_marker(main, markers.PPM),
        "PPT": markers.has_marker(tp0_headers, markers.PPT),
        "PLT": {"present": plt_loc is not None, "location": plt_loc},
        "TLM": {"present": markers.has_marker(main, markers.TLM), "location": "main" if markers.has_marker(main, markers.TLM) else None},
        "SOP": bool(_siz_cod_qcd(cs)[1].scod & 0x02),
        "EPH": bool(_siz_cod_qcd(cs)[1].scod & 0x04),
    }


def _basic_assert(cs: markers.Codestream, siz, cod, qcd) -> dict:
    ppx_r0, ppy_r0 = (15, 15)
    if cod.precinct_sizes:
        ppx_r0, ppy_r0 = cod.precinct_sizes[0]
    return {
        "sotCount": len(cs.tile_parts),
        "markers": _marker_summary(cs),
        "componentsPrecision": siz.precision,
        "componentsSigned": siz.signed,
        "componentsXRsiz": siz.xrsiz,
        "componentsYRsiz": siz.yrsiz,
        "cblkStyle": cod.cblksty,
        "transform": "5-3" if cod.transform == 1 else "9-7",
        "ppxAtR0": ppx_r0,
        "ppyAtR0": ppy_r0,
        # Every resolution level's (PPx, PPy), r = 0..N_L, when Scod bit 0 is set; [] for the
        # default (15, 15) case. A multi-precinct grid is what the position-driven
        # progressions (RPCL/PCRL/CPRL) are actually sensitive to, so the fixtures that claim
        # one pin the whole list, not just r = 0.
        "precinctSizes": [[ppx, ppy] for ppx, ppy in cod.precinct_sizes],
        "sqcdStyle": qcd.style if qcd else None,
        "numLayers": cod.num_layers,
        "progressionOrder": cod.prog_order,
        "mct": cod.mct,
    }


def _recompute_assert(fixture_path: Path, saved_assert: dict) -> dict:
    """Recomputes, purely structurally from the committed file (and, for
    'originalSotCount', one sibling fixture it was truncated from), every fact this
    generator's `assert` blocks ever record. Used by --check; deliberately computes the
    full set every time rather than only the keys a given fixture happens to assert, so
    `_check_fixture` can tell "genuinely not recomputable" (a bug in this function) apart
    from "this fixture doesn't claim that fact" (fine)."""
    data = fixture_path.read_bytes()
    out = {}
    codestream_bytes = data
    if data[:4] == b"\x00\x00\x00\x0c" and data[4:8] == b"jP  ":
        boxes = markers.walk_jp2_boxes(data)
        jp2h_children = markers.find_box(boxes, b"jp2h").children
        colr_boxes = markers.find_all_boxes(jp2h_children, b"colr")
        if colr_boxes:
            enum_values = [struct.unpack(">I", b.payload[3:7])[0] for b in colr_boxes]
            out["colorEnumCS"] = enum_values if len(enum_values) > 1 else enum_values[0]
        cdef_boxes = markers.find_all_boxes(jp2h_children, b"cdef")
        if cdef_boxes:
            n = struct.unpack(">H", cdef_boxes[0].payload[:2])[0]
            out["cdef"] = [
                dict(zip(("Cn", "Typ", "Asoc"),
                         struct.unpack(">HHH", cdef_boxes[0].payload[2 + i * 6:8 + i * 6])))
                for i in range(n)
            ]
        pclr_boxes = markers.find_all_boxes(jp2h_children, b"pclr")
        if pclr_boxes:
            out["pclrEntryCount"] = struct.unpack(">H", pclr_boxes[0].payload[:2])[0]
        out["hasResBox"] = len(markers.find_all_boxes(jp2h_children, b"res ")) > 0
        ihdr_box = markers.find_box(jp2h_children, b"ihdr")
        if ihdr_box:
            _, _, nc, _bpc = struct.unpack(">IIHB", ihdr_box.payload[:11])
            out["ihdrComponentCount"] = nc
        codestream_bytes = markers.find_jp2c(boxes).payload

    try:
        cs = markers.walk_codestream(codestream_bytes)
    except (ValueError, IndexError, struct.error):
        # The refusal/malformed fixtures (PPM/Part2/17-bit) are all still well-formed
        # enough to walk (only SIZ/PPM fields are patched/inserted) -- a real parse
        # failure here means this function itself needs a fixture-specific case, not
        # that the fixture is "supposed" to fail structurally.
        return out

    if cs.main_header_segments and markers.has_marker(cs.main_header_segments, markers.SIZ):
        siz = markers.parse_siz(markers.segments_by_marker(cs.main_header_segments, markers.SIZ)[0].payload)
        out["componentsPrecision"] = siz.precision
        out["componentsSigned"] = siz.signed
        out["componentsXRsiz"] = siz.xrsiz
        out["componentsYRsiz"] = siz.yrsiz
        out["rsizBit15Set"] = bool(siz.rsiz & 0x8000)
        n_tiles_x = -(-(siz.xsiz - siz.xtosiz) // siz.xtsiz)
        n_tiles_y = -(-(siz.ysiz - siz.ytosiz) // siz.ytsiz)
        out["tileGrid"] = {"tw": n_tiles_x, "th": n_tiles_y}
    out["sotCount"] = len(cs.tile_parts)
    out["markers"] = {"PPM": markers.has_marker(cs.main_header_segments, markers.PPM)}

    if markers.has_marker(cs.main_header_segments, markers.COD):
        cod = markers.parse_cod(markers.segments_by_marker(cs.main_header_segments, markers.COD)[0].payload)
        qcd_segs = markers.segments_by_marker(cs.main_header_segments, markers.QCD)
        qcd = markers.parse_qcd(qcd_segs[0].payload) if qcd_segs else None
        out.update(_basic_assert(cs, siz, cod, qcd))  # supersedes the partial fields above

        if cs.tile_parts:
            tp0 = cs.tile_parts[0]
            poc_segs = markers.segments_by_marker(tp0.header_segments, markers.POC)
            if poc_segs:
                entry_size = 7 if siz.csiz < 257 else 9  # 1- vs 2-byte component fields
                out["pocEntryCountInTile0Tp0"] = len(poc_segs[0].payload) // entry_size
            plt_segs = markers.segments_by_marker(tp0.header_segments, markers.PLT)
            if plt_segs and "finestResolutionCodeBlockPasses" in saved_assert:
                pkt_lengths = markers.parse_plt_lengths(plt_segs[0].payload)
                packet_start = tp0.body_offset + sum(pkt_lengths[:cod.num_decomp_levels])
                out["finestResolutionCodeBlockPasses"] = markers.decode_first_codeblock_num_passes(
                    codestream_bytes, packet_start)
            out["truncatedAfterTileIndex"] = tp0.isot

    if "originalSotCount" in saved_assert:
        sibling = FIXTURES_DIR / "tiles-3x2-partial.j2k"
        if sibling.exists():
            sibling_cs = markers.walk_codestream(sibling.read_bytes())
            out["originalSotCount"] = len(sibling_cs.tile_parts)

    return out


def _check_fixture(entry: dict) -> list:
    """Returns a list of mismatch strings (empty if every fact the fixture's committed
    `assert` block claims still matches what markers.py derives from the committed bytes
    right now, AND every committed file's content still matches its pinned sha256).
    Only claimed `assert` keys are checked -- a key `_recompute_assert` can derive but
    this fixture never asserted is not a failure; a claimed key `_recompute_assert`
    cannot produce at all IS a failure (a gap in this checker, not in the fixture).

    The sha256 checks are deliberately independent of the structural `assert` checks:
    `assert` only pins facts this generator bothered to name (marker presence, SIZ/COD
    fields, ...), so a corrupted-but-structurally-plausible reference file -- e.g. a PGX
    whose header is untouched but whose sample data is garbage -- would pass every
    `assert` check while being useless as an oracle reference. Hashing the exact
    committed bytes closes that gap for both the fixture file and every reference."""
    problems = []
    path = FIXTURES_DIR / entry["file"]
    if not path.exists():
        return [f"{entry['file']}: file missing"]

    expected_sha = entry.get("sha256")
    if not expected_sha:
        problems.append(f"{entry['file']}: MANIFEST entry has no 'sha256' field")
    else:
        actual_sha = _sha256_hex(path)
        if actual_sha != expected_sha:
            problems.append(
                f"{entry['file']}: sha256 mismatch -- MANIFEST says {expected_sha}, "
                f"committed file hashes to {actual_sha}"
            )

    recomputed = _recompute_assert(path, entry["assert"])
    for key, expected in entry["assert"].items():
        if key not in recomputed:
            problems.append(f"{entry['file']}: assert['{key}'] = {expected!r} but this checker "
                             f"cannot recompute '{key}' at all")
            continue
        actual = recomputed[key]
        if actual != expected:
            problems.append(f"{entry['file']}: assert['{key}'] = {expected!r}, recomputed = {actual!r}")

    ref_hashes = entry.get("referencesSha256", {})
    for ref in entry.get("references", []):
        ref_path = FIXTURES_DIR / ref
        if not ref_path.exists():
            problems.append(f"{entry['file']}: reference {ref} missing")
            continue
        expected_ref_sha = ref_hashes.get(ref)
        if not expected_ref_sha:
            problems.append(f"{entry['file']}: reference {ref} has no entry in 'referencesSha256'")
            continue
        actual_ref_sha = _sha256_hex(ref_path)
        if actual_ref_sha != expected_ref_sha:
            problems.append(
                f"{entry['file']}: reference {ref} sha256 mismatch -- MANIFEST says "
                f"{expected_ref_sha}, committed file hashes to {actual_ref_sha}"
            )
    # A reference hash recorded for a file no longer listed in "references" would
    # otherwise go unchecked -- catch a stale/renamed entry too.
    for ref in ref_hashes:
        if ref not in entry.get("references", []):
            problems.append(f"{entry['file']}: referencesSha256 has an entry for '{ref}' "
                             f"which is not in 'references'")
    return problems


# =========================================================================================
# The fixture matrix itself.
# =========================================================================================

def build_matrix(opj: dict, build_dir: Path) -> Matrix:
    m = Matrix(build_dir)

    # --- deterministic sources -----------------------------------------------------------
    gray_64x48 = build_dir / "src_gray_64x48.pgm"
    write_pgm(gray_64x48, 64, 48, seed=11)
    rgb_64x48 = build_dir / "src_rgb_64x48.ppm"
    write_ppm(rgb_64x48, 64, 48, seed=7)
    rgb_97x61 = build_dir / "src_rgb_97x61.ppm"
    write_ppm(rgb_97x61, 97, 61, seed=31)
    rgb_640x480 = build_dir / "src_rgb_640x480.ppm"
    write_ppm(rgb_640x480, 640, 480, seed=41)

    def compress(args: list, out_name: str) -> Path:
        # `args` already starts with opj["opj_compress"] at every call site (so the
        # MANIFEST `command` string built from the same list reads naturally).
        out_path = build_dir / out_name
        _run(args + ["-o", str(out_path)])
        return out_path

    def load_cs(path: Path):
        cs_bytes = _codestream_bytes(path)
        cs = markers.walk_codestream(cs_bytes)
        siz, cod, qcd = _siz_cod_qcd(cs)
        return cs, siz, cod, qcd

    def simple_entry(name: str, file_name: str, command: list, out_name: str,
                      tolerance_class: str, description: str,
                      decompress_extra: list = None, extra_assert: dict = None,
                      skip_reference: bool = False):
        path = compress(command, out_name)
        cs, siz, cod, qcd = load_cs(path)
        assert_block = _basic_assert(cs, siz, cod, qcd)
        if extra_assert:
            assert_block.update(extra_assert)
        refs = [] if skip_reference else _decompress_refs(opj, path, name, build_dir, decompress_extra or [])
        final_name = f"{name}{Path(out_name).suffix}"
        (build_dir / final_name).write_bytes(path.read_bytes())
        final_refs = []
        for r in refs:
            new_r = r.replace(f"{name}_ref", f"{name}.ref")
            (build_dir / new_r).write_bytes((build_dir / r).read_bytes())
            final_refs.append(new_r)
        m.add({
            "file": final_name,
            "description": description,
            "hand_built": False,
            # Display form only: strip the ephemeral per-run temp build directory from
            # every arg (it differs run to run) so MANIFEST.json is byte-identical across
            # two regenerations.
            "command": _portable_command(command + ["-o", out_name], build_dir),
            "references": final_refs,
            "toleranceClass": tolerance_class,
            "assert": assert_block,
        })
        return path, cs, siz, cod, qcd

    # 1. Baseline 5/3, single tile, default precincts -- both raw .j2k and JP2-wrapped.
    simple_entry(
        "baseline-53", "baseline-53.j2k",
        [opj["opj_compress"], "-i", str(gray_64x48), "-r", "1"],
        "baseline-53.j2k", "bit-exact",
        "5/3 (reversible) wavelet, single tile, default (huge) precincts, 1 layer, "
        "lossless. Doubles as the '1 layer' fixture.",
    )
    simple_entry(
        "baseline-53-jp2", "baseline-53.jp2",
        [opj["opj_compress"], "-i", str(gray_64x48), "-r", "1"],
        "baseline-53.jp2", "bit-exact",
        "Same encode as baseline-53.j2k, JP2-wrapped -- the 'raw .j2k and .jp2' pair.",
    )

    # 2. 9/7 (irreversible) wavelet.
    simple_entry(
        "wavelet-97", "wavelet-97.jp2",
        [opj["opj_compress"], "-i", str(gray_64x48), "-I", "-q", "40"],
        "wavelet-97.jp2", "tolerance-a",
        "9/7 (irreversible) wavelet at a fixed quality target. Sqcd style asserted; "
        "measured 2 (scalar EXPOUNDED -- an explicit (mantissa,exponent) pair per "
        "subband, verified via opj_dump) for opj_compress's default 9/7 output both "
        "with and without an explicit -q, not style 1 ('scalar derived', a single "
        "reference value with the rest formula-derived) -- a correction to this "
        "generator's own first assumption, not a recipe change.",
    )

    # 2b. 9/7 + ICT on a 3-component source -- the production path (typical scanned forms are
    #     RGB, single tile, 9/7 + ICT) and the one combination the matrix never exercised
    #     before this fixture: wavelet-97 is single-component (no MCT), every RGB fixture is
    #     5/3 (RCT). opj_compress selects the irreversible colour transform whenever -I meets a
    #     3-component source (mct=1 asserted, alongside transform 9-7).
    simple_entry(
        "wavelet-97-ict", "wavelet-97-ict.jp2",
        [opj["opj_compress"], "-i", str(rgb_64x48), "-I", "-r", "1"],
        "wavelet-97-ict.jp2", "tolerance-a",
        "9/7 (irreversible) wavelet with the irreversible colour transform (ICT): a 3-component "
        "RGB source under -I. The production path of a typical scanned form; the only fixture "
        "combining 9/7 with mct=1 (wavelet-97 has one component; every other mct=1 fixture is "
        "5/3, i.e. RCT). Both steps are irreversible, hence tolerance-a.",
    )

    # 2c. The same path at a real compression ratio on a scan-like 640x480 page (dark strokes
    #     and ruled boxes on near-white paper): -r 20 leaves genuine quantisation ringing at
    #     every edge, which a lossless-rate fixture cannot show. Single tile, default 6 levels.
    scan_640x480 = build_dir / "src_scan_640x480.ppm"
    write_scan_ppm(scan_640x480, 640, 480, seed=53)
    simple_entry(
        "scan-97-ict-640x480", "scan-97-ict-640x480.jp2",
        [opj["opj_compress"], "-i", str(scan_640x480), "-I", "-r", "20"],
        "scan-97-ict-640x480.jp2", "tolerance-a",
        "640x480 scan-like RGB page (near-white paper, dark text-like strokes, ruled boxes, "
        "sensor noise -- this generator's write_scan_ppm) encoded 9/7 + ICT at -r 20, the "
        "shape and ratio of a typical scanned form. The matrix's second large "
        "fixture; the rate keeps it small.",
    )

    # 3. Tiled, 3x2 grid with partial edge tiles; also the RPCL progression fixture and
    #    the source the truncation fixture is cut from (spec: "the multi-tile RPCL
    #    fixture truncated at a tile boundary").
    tiles_path, tiles_cs, tiles_siz, tiles_cod, tiles_qcd = simple_entry(
        "tiles-3x2-partial", "tiles-3x2-partial.j2k",
        [opj["opj_compress"], "-i", str(rgb_97x61), "-t", "33,31", "-p", "RPCL", "-n", "3", "-r", "1"],
        "tiles-3x2-partial.j2k", "bit-exact",
        "97x61 (odd-sized) RGB, 33x31 tiles -> 3x2 grid with partial edge tiles on both "
        "axes; RPCL progression (also satisfies the 'five progressions' RPCL case). "
        "-n 3 (3 resolution levels): opj_compress's default of 6 fails "
        "('Number of resolutions is too high') against a 33x31 tile.",
        extra_assert={"tileGrid": {"tw": 3, "th": 2}},
    )

    # 4. Explicit precincts.
    simple_entry(
        "precincts-explicit", "precincts-explicit.jp2",
        [opj["opj_compress"], "-i", str(gray_64x48), "-c", "[128,128],[64,64],[32,32]", "-r", "1"],
        "precincts-explicit.jp2", "bit-exact",
        "Explicit precinct sizes, extrapolated by opj_compress across all 6 resolution "
        "levels (measured; the short 3-entry list does not map 1:1 onto PPx=7 == "
        "log2(128)). Measured PPx=PPy=2 at r=0 -- the B.7 code-block clamp is load-bearing "
        "here (xcb'=min(xcb,PPx)=min(6,2)=2 at r=0).",
    )

    # 5-8. Four of the five progressions on a single default tile (RPCL is fixture 3).
    for prog, code in (("LRCP", 0), ("RLCP", 1), ("PCRL", 3), ("CPRL", 4)):
        simple_entry(
            f"prog-{prog.lower()}", f"prog-{prog.lower()}.jp2",
            [opj["opj_compress"], "-i", str(gray_64x48), "-p", prog, "-r", "1"],
            f"prog-{prog.lower()}.jp2", "bit-exact",
            f"{prog} progression order (Sec B.12), single default tile.",
        )

    # 9. POC on a 640x480, 2x2-tiled, RGB stream.
    simple_entry(
        "poc-640x480", "poc-640x480.j2k",
        [opj["opj_compress"], "-i", str(rgb_640x480), "-t", "320,240",
         "-POC", "T1=0,0,1,5,3,CPRL/T1=5,0,1,6,3,CPRL", "-r", "30"],
        "poc-640x480.j2k", "tolerance-a",
        "POC (progression changes) on a 640x480 RGB, 2x2-tiled stream. CE=3 in both "
        "entries requires an actual 3-component image (a grayscale source silently gets "
        "CE clamped to 1, verified). -r 30 keeps this, the matrix's one large fixture, "
        "small; the rate-limited single layer is genuinely lossy, hence tolerance-a "
        "despite the 5/3 wavelet. T1 is 1-based (T1 == tile index 0); the POC segment "
        "lands in tile 0's FIRST tile-part header, which is asserted directly.",
        extra_assert={"pocEntryCountInTile0Tp0": 2},
    )

    # 10-16. Each cblksty bit alone, and all six -- with -PLT added (deviation, see
    # README/manifest note) so the >=11-passes assertion is checkable via a minimal
    # single-packet header read instead of a full multi-packet Tier-2 skip-decode.
    for mval in (1, 2, 4, 8, 16, 32, 63):
        path = compress(
            [opj["opj_compress"], "-i", str(gray_64x48), "-M", str(mval), "-PLT", "-r", "1"],
            f"cblksty-m{mval}.j2k")
        cs, siz, cod, qcd = load_cs(path)
        assert_block = _basic_assert(cs, siz, cod, qcd)
        markers.assert_single_codeblock_finest_subband(siz, cod)
        tp0 = cs.tile_parts[0]
        plt_seg = markers.segments_by_marker(tp0.header_segments, markers.PLT)[0]
        pkt_lengths = markers.parse_plt_lengths(plt_seg.payload)
        finest_packet_index = cod.num_decomp_levels  # r = N_L is the last packet (LRCP)
        packet_start = tp0.body_offset + sum(pkt_lengths[:finest_packet_index])
        num_passes = markers.decode_first_codeblock_num_passes(_codestream_bytes(path), packet_start)
        assert_block["finestResolutionCodeBlockPasses"] = num_passes
        assert num_passes >= 11, f"cblksty-m{mval}: expected >=11 passes, measured {num_passes}"
        final_name = f"cblksty-m{mval}.j2k"
        (build_dir / final_name).write_bytes(path.read_bytes())
        m.add({
            "file": final_name,
            "description": (
                f"cblksty=0x{mval:02x} (opj_compress -M {mval}), single-tile 64x48 gray, "
                "default (huge) precincts and 64x64 code-blocks so every subband has "
                "exactly one code-block (verified via markers.assert_single_codeblock_"
                "finest_subband, not assumed). -PLT added (deviation from the literal "
                "-M-only recipe): PLT's packet-length list lets the generator "
                "skip straight to the finest resolution's packet and read only its first "
                "code-block's number-of-new-passes field, instead of a full multi-packet "
                "Tier-2 decode with cblksty-dependent segmentation (which, for the "
                "'selective bypass' bit alone, has non-trivial per-bit-plane grouping "
                "past pass 10). Measured >=11 passes on every cblksty value (this LCG "
                "source's blue/noise channel spans the full 0-255 range)."
            ),
            "hand_built": False,
            "command": f"opj_compress -i src_gray_64x48.pgm -M {mval} -PLT -r 1 -o cblksty-m{mval}.j2k",
            "references": [r.replace(f"cblksty-m{mval}_ref", f"cblksty-m{mval}.ref")
                            for r in _decompress_refs(opj, path, f"cblksty-m{mval}", build_dir)],
            "toleranceClass": "bit-exact",
            "assert": assert_block,
        })
        # _decompress_refs already wrote "<stem>_ref_<comp>.pgx" into build_dir; copy each
        # to the dotted name recorded above (the convention used by every other fixture).
        for old_name in _glob_refs(build_dir, f"cblksty-m{mval}"):
            new_name = old_name.replace(f"cblksty-m{mval}_ref", f"cblksty-m{mval}.ref")
            (build_dir / new_name).write_bytes((build_dir / old_name).read_bytes())

    # 17-18. 3 and 10 layers (1 layer is baseline-53).
    simple_entry(
        "layers-3", "layers-3.jp2",
        [opj["opj_compress"], "-i", str(gray_64x48), "-r", "20,10,1"],
        "layers-3.jp2", "bit-exact",
        "3 quality layers, final layer lossless (-r ...,1) -- all layers decoded, no "
        "colour step, 5/3 wavelet: bit-exact per the tolerance policy.",
    )
    simple_entry(
        "layers-10", "layers-10.jp2",
        [opj["opj_compress"], "-i", str(gray_64x48), "-r", "100,80,60,40,30,20,15,10,5,1"],
        "layers-10.jp2", "bit-exact",
        "10 quality layers, final layer lossless -- all layers decoded is bit-exact; "
        "JpxOracleTests additionally decodes layers 1..k for the self-consistency "
        "monotonic-error fact, not asserted by this generator.",
    )

    # 19. Uniform 2x2 subsampling (PPM source), explicit colr, JP2 only.
    simple_entry(
        "subsample-uniform-2x2", "subsample-uniform-2x2.jp2",
        [opj["opj_compress"], "-i", str(rgb_64x48), "-s", "2,2"],
        "subsample-uniform-2x2.jp2", "tolerance-a",
        "Uniform 2x2 subsampling via -s on a PPM source. Reference-grid size becomes "
        "127x95 (Xsiz=(w-1)*XRsiz+1, measured). opj_decompress force-attempts an sYCC "
        "conversion for any 3-component image with comps[0].dx==comps[0].dy && "
        "comps[1].dx!=1, and for the UNIFORM case bails with 'CAN NOT CONVERT' on "
        "stderr (exit 0), writing unconverted planes -- not a decode failure.",
        decompress_extra=[],
    )

    # 20. Mixed 4:2:0 subsampling (raw source), colr auto-written EnumCS 18.
    mixed_raw = build_dir / "src_mixed420.raw"
    write_raw_mixed_subsampled(mixed_raw, 64, 48, seed=53)
    simple_entry(
        "subsample-mixed-420", "subsample-mixed-420.jp2",
        [opj["opj_compress"], "-i", str(mixed_raw), "-F", "64,48,3,8,u@1x1:2x2:2x2"],
        "subsample-mixed-420.jp2", "tolerance-a",
        "Mixed 4:2:0-style subsampling via raw -F. opj_compress writes colr EnumCS 18 "
        "(sYCC) automatically for this shape (measured) -- PlumePDF's EnumCS-18 rule and "
        "the oracle's own forced conversion agree (max measured |delta| 2).",
    )

    # 21-23. 12-bit unsigned, 16-bit unsigned, 16-bit signed, all via raw -F.
    for prec, signed, tag in ((12, False, "depth-12u"), (16, False, "depth-16u"), (16, True, "depth-16s")):
        raw_path = build_dir / f"src_{tag}.raw"
        write_raw_gray(raw_path, 64, 48, seed=61, precision=prec, signed=signed)
        sign_flag = "s" if signed else "u"
        simple_entry(
            tag, f"{tag}.j2k",
            [opj["opj_compress"], "-i", str(raw_path), "-F", f"64,48,1,{prec},{sign_flag}", "-r", "1"],
            f"{tag}.j2k", "bit-exact",
            f"{prec}-bit {'signed' if signed else 'unsigned'} via raw -F (honours the "
            "declared precision exactly, unlike the PGX route -- verified).",
        )

    # 23b. Signed 16-bit under the 9/7 wavelet: the irreversible path over a signed component
    #      (every other signed/deep fixture is 5/3). tolerance-a per the 9/7 rule; the
    #      component is compared signed (opj_decompress writes signed PGX).
    raw_16s_97 = build_dir / "src_depth-16s-97.raw"
    write_raw_gray(raw_16s_97, 64, 48, seed=67, precision=16, signed=True)
    simple_entry(
        "depth-16s-97", "depth-16s-97.j2k",
        [opj["opj_compress"], "-i", str(raw_16s_97), "-F", "64,48,1,16,s", "-I", "-r", "1"],
        "depth-16s-97.j2k", "tolerance-a",
        "16-bit signed via raw -F under the 9/7 (irreversible) wavelet -- the only signed "
        "fixture on the irreversible path (depth-16s is 5/3). Compared signed, tolerance-a.",
    )

    # 24. cdef alpha via RGBA PNG.
    alpha_png = build_dir / "src_alpha.png"
    write_png_rgba(alpha_png, 64, 48, seed=71)
    simple_entry(
        "alpha-cdef", "alpha-cdef.jp2",
        [opj["opj_compress"], "-i", str(alpha_png), "-r", "1"],
        "alpha-cdef.jp2", "bit-exact",
        "RGBA PNG source -> JP2 with an auto-written cdef box (4 channel defs, alpha at "
        "component index 3 with Typ=1 'unassociated opacity', measured). mct=1 with the "
        "5/3 wavelet selects RCT (exact, reversible) for components 0-2, not ICT, so "
        "this is bit-exact despite mct=1 (RCT is exact; only sYCC/ICT are not).",
    )

    # 25. SOP/EPH.
    simple_entry(
        "sop-eph", "sop-eph.jp2",
        [opj["opj_compress"], "-i", str(gray_64x48), "-SOP", "-EPH", "-r", "1"],
        "sop-eph.jp2", "bit-exact",
        "SOP and EPH both enabled (Scod bits 1 and 2).",
    )

    # 26. Standalone PLT (tile-part header).
    simple_entry(
        "plt-marker", "plt-marker.jp2",
        [opj["opj_compress"], "-i", str(gray_64x48), "-PLT", "-r", "1"],
        "plt-marker.jp2", "bit-exact",
        "Standalone -PLT (packet lengths, tile-part header) -- distinct from the "
        "incidental -PLT usage on the cblksty-m* fixtures.",
    )

    # 27. Standalone TLM (main header).
    simple_entry(
        "tlm-marker", "tlm-marker.jp2",
        [opj["opj_compress"], "-i", str(gray_64x48), "-TLM", "-r", "1"],
        "tlm-marker.jp2", "bit-exact",
        "-TLM (tile-part lengths, main header).",
    )

    # 28. -TP R (tile-parts per resolution change).
    simple_entry(
        "tileparts-tpr", "tileparts-tpr.jp2",
        [opj["opj_compress"], "-i", str(gray_64x48), "-TP", "R", "-r", "1"],
        "tileparts-tpr.jp2", "bit-exact",
        "-TP R: a new tile-part at every resolution-level change -- 6 tile-parts for "
        "this single tile's 6 resolution levels (measured, SOT count asserted).",
    )

    # 28b/28c. Position-driven progressions over a REAL precinct grid with layers: RPCL and
    # CPRL with 16x16 precincts (a 4x3 grid at the finest resolution, 2x2 at the next --
    # precinctSizes asserted) and three layers, final layer lossless (bit-exact). The
    # single-precinct prog-* fixtures above cannot tell a decoder that visits only the first
    # precinct of each (layer, resolution, component) from a correct one; these can (needed
    # to catch a bug in JpxPacketDecoder's visited-triple de-duplication).
    for prog in ("RPCL", "CPRL"):
        simple_entry(
            f"prog-{prog.lower()}-precincts-layers", f"prog-{prog.lower()}-precincts-layers.jp2",
            [opj["opj_compress"], "-i", str(gray_64x48), "-p", prog, "-c", "[16,16],[16,16],[16,16]",
             "-r", "20,10,1"],
            f"prog-{prog.lower()}-precincts-layers.jp2", "bit-exact",
            f"{prog} progression over an explicit 16x16 precinct grid (4x3 precincts at r=5, "
            "2x2 at r=4, one below -- measured, the full per-resolution list is asserted) "
            "with 3 quality layers, final layer lossless. Exercises the B.12.1.3 position "
            "loops with a precinct index that actually varies and a layer loop nested inside "
            "it -- the packet sequence the single-precinct prog-* fixtures never produce.",
        )

    # 29. RGN refusal.
    rgn_path = compress([opj["opj_compress"], "-i", str(gray_64x48), "-ROI", "c=0,U=5"],
                         "rgn-refusal.j2k")
    rgn_cs, rgn_siz, rgn_cod, rgn_qcd = load_cs(rgn_path)
    (build_dir / "rgn-refusal.j2k").write_bytes(rgn_path.read_bytes())
    m.add({
        "file": "rgn-refusal.j2k",
        "description": "RGN (max-shift ROI, -ROI c=0,U=5) present in the main header -- "
                        "refused (PLUME3703). No PGX reference (refusal fixture).",
        "hand_built": False,
        "command": "opj_compress -i src_gray_64x48.pgm -ROI c=0,U=5 -o rgn-refusal.j2k",
        "references": [],
        "toleranceClass": f"refusal:{REFUSAL_CODES['rgn']}",
        "assert": {**_basic_assert(rgn_cs, rgn_siz, rgn_cod, rgn_qcd)},
    })

    # 30. Truncation of the multi-tile RPCL fixture, cut exactly at a tile-part boundary.
    tp0_end = tiles_cs.tile_parts[0].end_offset
    original_bytes = _codestream_bytes(tiles_path)
    truncated_bytes = original_bytes[:tp0_end]
    (build_dir / "truncated.j2k").write_bytes(truncated_bytes)
    m.add({
        "file": "truncated.j2k",
        "description": (
            "tiles-3x2-partial.j2k truncated via head -c to exactly the end of tile 0's "
            "tile-part (offset computed from that fixture's own SOT/Psot walk, not a "
            "fixed byte count) -- one complete tile, five missing, no EOC. opj_decompress "
            "requires -allow-partial to decode this at all (verified: without it, exit 1, "
            "'Stream too short', no output; with it, exit 0, tile 1/6 decoded). Not "
            "oracle-compared (no honest tolerance for a heavily truncated "
            "decode); PLUME3701 and the complete-tiles-bit-exact self-consistency fact "
            "are asserted by JpxOracleTests in C#, not by this generator."
        ),
        "hand_built": False,
        "command": f"head -c {tp0_end} tiles-3x2-partial.j2k > truncated.j2k",
        "references": [],
        "toleranceClass": f"not-compared:{TRUNCATION_CODE}",
        "assert": {
            "sotCount": 1,
            "truncatedAfterTileIndex": 0,
            "originalSotCount": len(tiles_cs.tile_parts),
        },
    })

    _add_hand_built_fixtures(opj, build_dir, m, gray_64x48, rgb_64x48)
    return m


def _glob_refs(build_dir: Path, stem: str) -> list:
    return sorted(p.name for p in build_dir.glob(f"{stem}_ref_*.pgx"))


def _add_hand_built_fixtures(opj: dict, build_dir: Path, m: Matrix, gray_64x48: Path, rgb_64x48: Path):
    def decompress_and_store(jp2_path: Path, ref_stem: str, extra_args: list = None) -> list:
        refs = _decompress_refs(opj, jp2_path, ref_stem, build_dir, extra_args or [])
        renamed = []
        for r in refs:
            new_name = r.replace(f"{ref_stem}_ref", f"{ref_stem}.ref")
            (build_dir / new_name).write_bytes((build_dir / r).read_bytes())
            renamed.append(new_name)
        return renamed

    # 31. pclr + cmap: 1-component 8-bit codestream wrapped with a 256-entry RGB palette.
    base_cs_path = build_dir / "pclr-base.j2k"
    _run([opj["opj_compress"], "-i", str(gray_64x48), "-r", "1", "-o", str(base_cs_path)])
    palette = [(i, (i * 3) % 256, 255 - i) for i in range(256)]
    jp2h_children = jp2_ihdr(48, 64, 1, 7) + jp2_pclr(palette) + jp2_cmap(3) + jp2_colr_enum(16)
    pclr_bytes = wrap_jp2(base_cs_path.read_bytes(), jp2h_children)
    pclr_path = build_dir / "pclr-cmap.jp2"
    pclr_path.write_bytes(pclr_bytes)
    m.add({
        "file": "pclr-cmap.jp2",
        "description": (
            "1-component 8-bit codestream (5/3, lossless) hand-wrapped with a 256-entry "
            "RGB pclr + cmap (component 0 -> palette columns 0,1,2). opj_decompress "
            "expands the palette itself (verified: 3 output planes), so a normal "
            "3-component PGX reference exists; the palette lookup is an exact integer "
            "mapping so this stays bit-exact."
        ),
        "hand_built": True,
        "construction": "jp2_ihdr(48,64,1,7) + jp2_pclr(256 deterministic RGB entries) + "
                         "jp2_cmap(3) + jp2_colr_enum(16), wrapping a normal lossless "
                         "1-component codestream.",
        "references": decompress_and_store(pclr_path, "pclr-cmap"),
        "toleranceClass": "bit-exact",
        "assert": {"pclrEntryCount": 256, "ihdrComponentCount": 1, "hasResBox": False},
    })

    # 32. sYCC EnumCS 18: RGB source pre-converted to YCbCr, -mct 0, colr EnumCS patched.
    ycc_ppm = build_dir / "src_ycc.ppm"
    _write_ycc_from_rgb(rgb_64x48, ycc_ppm)
    sycc_base = build_dir / "sycc-base.jp2"
    _run([opj["opj_compress"], "-i", str(ycc_ppm), "-mct", "0", "-r", "1", "-o", str(sycc_base)])
    sycc_bytes = bytearray(sycc_base.read_bytes())
    boxes = markers.walk_jp2_boxes(bytes(sycc_bytes))
    colr = markers.find_box(markers.find_box(boxes, b"jp2h").children, b"colr")
    struct.pack_into(">I", sycc_bytes, colr.payload_offset + 3, 18)
    sycc_path = build_dir / "sycc-enumcs18.jp2"
    sycc_path.write_bytes(bytes(sycc_bytes))
    m.add({
        "file": "sycc-enumcs18.jp2",
        "description": (
            "An RGB source pre-converted (by this generator, ITU-R BT.601 / JFIF full-"
            "range formula) to YCbCr, encoded with -mct 0 (core MCT disabled, so the "
            "codestream literally carries YCbCr numbers), then colr EnumCS patched "
            "16 -> 18 (sYCC). opj_decompress applies its own sycc444_to_rgb on decode "
            "(measured: output pixel (0,1,193) vs source RGB (0,0,194) at (0,0) -- an "
            "integer approximation, not exact), so this is tolerance-a."
        ),
        "hand_built": True,
        "construction": "opj_compress -i ycc.ppm -mct 0 -r 1 -o sycc-base.jp2, then the "
                         "colr box's EnumCS field patched from 16 to 18 in place (no "
                         "length change).",
        "references": decompress_and_store(sycc_path, "sycc-enumcs18"),
        "toleranceClass": "tolerance-a",
        "assert": {"colorEnumCS": 18, "mct": 0},
    })

    # 33. Premultiplied alpha: alpha-cdef's cdef Typ patched 1 -> 2.
    alpha_src_jp2 = build_dir / "alpha-cdef.jp2"
    premult_bytes = bytearray(alpha_src_jp2.read_bytes())
    boxes = markers.walk_jp2_boxes(bytes(premult_bytes))
    cdef = markers.find_box(markers.find_box(boxes, b"jp2h").children, b"cdef")
    n = struct.unpack(">H", cdef.payload[:2])[0]
    patched = False
    for i in range(n):
        entry_off = cdef.payload_offset + 2 + i * 6
        typ = struct.unpack(">H", premult_bytes[entry_off + 2:entry_off + 4])[0]
        if typ == 1:
            struct.pack_into(">H", premult_bytes, entry_off + 2, 2)
            patched = True
            break
    if not patched:
        raise RuntimeError("premultiplied-alpha: no Typ=1 cdef entry found to patch")
    premult_path = build_dir / "premultiplied-alpha.jp2"
    premult_path.write_bytes(bytes(premult_bytes))
    m.add({
        "file": "premultiplied-alpha.jp2",
        "description": "alpha-cdef.jp2 with its alpha channel's cdef Typ patched from 1 "
                        "(unassociated opacity) to 2 (premultiplied opacity) -- pixel "
                        "planes are byte-identical to alpha-cdef.jp2.",
        "hand_built": True,
        "construction": "alpha-cdef.jp2's cdef box, one channel-definition entry's Typ "
                         "field patched 1 -> 2 in place.",
        "references": decompress_and_store(premult_path, "premultiplied-alpha"),
        "toleranceClass": "bit-exact",
        "assert": {
            "cdef": [
                {"Cn": 0, "Typ": 0, "Asoc": 1},
                {"Cn": 1, "Typ": 0, "Asoc": 2},
                {"Cn": 2, "Typ": 0, "Asoc": 3},
                {"Cn": 3, "Typ": 2, "Asoc": 0},  # patched: 1 (opacity) -> 2 (premultiplied)
            ],
            "ihdrComponentCount": 4,
        },
    })

    # 34. res box (metadata-only, no PGX reference).
    baseline_jp2 = build_dir / "baseline-53.jp2"
    base_boxes = markers.walk_jp2_boxes(baseline_jp2.read_bytes())
    ihdr_box = markers.find_box(markers.find_box(base_boxes, b"jp2h").children, b"ihdr")
    colr_box = markers.find_box(markers.find_box(base_boxes, b"jp2h").children, b"colr")
    jp2c_box = markers.find_jp2c(base_boxes)
    jp2h_children = (jp2_box(b"ihdr", ihdr_box.payload)
                      + jp2_box(b"colr", colr_box.payload)
                      + jp2_res_resd(300.0))
    res_bytes = wrap_jp2(jp2c_box.payload, jp2h_children)
    res_path = build_dir / "res-metadata.jp2"
    res_path.write_bytes(res_bytes)
    m.add({
        "file": "res-metadata.jp2",
        "description": (
            "baseline-53.jp2's codestream re-wrapped with a `res `/`resd` box pinning "
            "300 dpi (11811 px/m). opj_compress never emits a `res ` box and "
            "opj_decompress's PNG output carries no pHYs / opj_dump prints no "
            "resolution, so this DPI has no oracle corroboration -- pinned entirely by "
            "construction. Metadata-only: no PGX reference."
        ),
        "hand_built": True,
        "construction": "baseline-53.jp2's ihdr+colr boxes copied verbatim into a new "
                         "jp2h that also carries a hand-built res/resd box (300 dpi); "
                         "jp2c copied verbatim.",
        "references": [],
        "toleranceClass": "metadata-only",
        "assert": {"hasResBox": True, "ihdrComponentCount": 1},
    })

    # 35. Two different colr boxes (EnumCS 16 then 18) -- metadata-only.
    rgb_cs_path = build_dir / "twocolr-base.j2k"
    _run([opj["opj_compress"], "-i", str(rgb_64x48), "-r", "1", "-o", str(rgb_cs_path)])
    jp2h_children = jp2_ihdr(48, 64, 3, 7) + jp2_colr_enum(16) + jp2_colr_enum(18)
    twocolr_bytes = wrap_jp2(rgb_cs_path.read_bytes(), jp2h_children)
    twocolr_path = build_dir / "two-colr.jp2"
    twocolr_path.write_bytes(twocolr_bytes)
    m.add({
        "file": "two-colr.jp2",
        "description": (
            "Two colr boxes in one jp2h, EnumCS 16 then 18 (genuinely different, unlike "
            "the veraPDF t02-fail-a file whose two colr boxes are byte-identical and "
            "discriminate nothing) -- tests the first-recognised-wins rule. "
            "Metadata-only: no PGX reference (colr does not affect codestream decode; "
            "the point of this fixture is purely which box PlumePDF's JpxColourInfo "
            "picks)."
        ),
        "hand_built": True,
        "construction": "jp2_ihdr(48,64,3,7) + jp2_colr_enum(16) + jp2_colr_enum(18), "
                         "wrapping a normal lossless 3-component RGB codestream.",
        "references": [],
        "toleranceClass": "metadata-only",
        "assert": {"colorEnumCS": [16, 18], "ihdrComponentCount": 3},
    })

    # 36. PPM refusal: a PPM segment inserted into an existing main header.
    ppm_base_path = build_dir / "ppm-base.j2k"
    _run([opj["opj_compress"], "-i", str(gray_64x48), "-r", "1", "-o", str(ppm_base_path)])
    ppm_data = bytearray(ppm_base_path.read_bytes())
    ppm_cs = markers.walk_codestream(bytes(ppm_data))
    cod_seg = markers.segments_by_marker(ppm_cs.main_header_segments, markers.COD)[0]
    insert_at = cod_seg.offset + 2 + cod_seg.length_field
    ppm_payload = b"\x00" + b"\x00\x00\x00\x00"  # Zppm=0, Nppm=0 (no packet header data)
    ppm_segment = struct.pack(">HH", markers.PPM, 2 + len(ppm_payload)) + ppm_payload
    ppm_inserted = bytes(ppm_data[:insert_at]) + ppm_segment + bytes(ppm_data[insert_at:])
    ppm_path = build_dir / "ppm-refusal.j2k"
    ppm_path.write_bytes(ppm_inserted)
    ppm_cs2 = markers.walk_codestream(ppm_inserted)
    m.add({
        "file": "ppm-refusal.j2k",
        "description": "A syntactically well-formed, empty PPM segment (Zppm=0, Nppm=0) "
                        "spliced into an otherwise-normal main header, right after COD. "
                        "opj_compress has no flag to request PPM, so this is hand-built. "
                        "Refused (PLUME3704); no PGX reference.",
        "hand_built": True,
        "construction": "ppm-base.j2k with a hand-built PPM marker segment (FF60, Lppm=7, "
                         "Zppm=0, Nppm=0) inserted immediately after the COD segment.",
        "references": [],
        "toleranceClass": f"refusal:{REFUSAL_CODES['ppm']}",
        "assert": {"markers": _marker_summary(ppm_cs2), "sotCount": len(ppm_cs2.tile_parts)},
    })

    # 37. Part 2 refusal: Rsiz bit 15 set.
    part2_data = bytearray(ppm_base_path.read_bytes())
    part2_cs = markers.walk_codestream(bytes(part2_data))
    siz_seg = markers.segments_by_marker(part2_cs.main_header_segments, markers.SIZ)[0]
    struct.pack_into(">H", part2_data, siz_seg.offset + 4, 0x8000)
    part2_path = build_dir / "part2-refusal.j2k"
    part2_path.write_bytes(bytes(part2_data))
    m.add({
        "file": "part2-refusal.j2k",
        "description": "ppm-base.j2k (before PPM insertion) with SIZ's Rsiz field patched "
                        "to 0x8000 (bit 15 set = Part 2 codestream). Refused (PLUME3702); "
                        "no PGX reference.",
        "hand_built": True,
        "construction": "Rsiz (first 2 bytes of the SIZ segment payload) patched to "
                         "0x8000 in place.",
        "references": [],
        "toleranceClass": f"refusal:{REFUSAL_CODES['part2']}",
        "assert": {"rsizBit15Set": True},
    })

    # 38. 17-bit precision refusal: Ssiz patched to 0x10 for component 0.
    bit17_data = bytearray(ppm_base_path.read_bytes())
    bit17_cs = markers.walk_codestream(bytes(bit17_data))
    siz_seg17 = markers.segments_by_marker(bit17_cs.main_header_segments, markers.SIZ)[0]
    ssiz_off = siz_seg17.offset + 4 + 36  # 4 (marker+Lsiz) + 36 (fixed SIZ fields) = Ssiz[0]
    bit17_data[ssiz_off] = 0x10
    bit17_path = build_dir / "precision17-refusal.j2k"
    bit17_path.write_bytes(bytes(bit17_data))
    m.add({
        "file": "precision17-refusal.j2k",
        "description": "ppm-base.j2k with component 0's Ssiz byte patched to 0x10 "
                        "(precision-1=16 => 17-bit unsigned). Refused (PLUME3705); no "
                        "PGX reference.",
        "hand_built": True,
        "construction": "Ssiz byte for component 0 (SIZ payload offset 36) patched to "
                         "0x10 in place.",
        "references": [],
        "toleranceClass": f"refusal:{REFUSAL_CODES['precision17']}",
        "assert": {"componentsPrecision": [17], "componentsSigned": [False]},
    })

    # 39. EnumCS 12 CMYK: a 4-component raw codestream wrapped with colr = 12.
    cmyk_raw = build_dir / "src_cmyk.raw"
    write_raw_multiplane(cmyk_raw, 64, 48, 4, seed=83)
    cmyk_cs_path = build_dir / "cmyk-base.j2k"
    _run([opj["opj_compress"], "-i", str(cmyk_raw), "-F", "64,48,4,8,u", "-r", "1",
          "-o", str(cmyk_cs_path)])
    jp2h_children = jp2_ihdr(48, 64, 4, 7) + jp2_colr_enum(12)
    cmyk_bytes = wrap_jp2(cmyk_cs_path.read_bytes(), jp2h_children)
    cmyk_path = build_dir / "cmyk-enumcs12.jp2"
    cmyk_path.write_bytes(cmyk_bytes)
    m.add({
        "file": "cmyk-enumcs12.jp2",
        "description": "A 4-component raw codestream (5/3, lossless, no MCT -- RCT/ICT "
                        "only ever apply to the first 3 components) hand-wrapped with "
                        "colr EnumCS 12 (CMYK).",
        "hand_built": True,
        "construction": "jp2_ihdr(48,64,4,7) + jp2_colr_enum(12), wrapping a normal "
                         "lossless 4-component codestream (opj_compress's own default "
                         "colr for a bare 4-component raw input is EnumCS 16, measured; "
                         "patched to 12 by wrapping with our own colr instead).",
        # opj_decompress converts an EnumCS 12 (CMYK) image to RGB before writing its
        # output (color_cmyk_to_rgb -- three planes, the fourth freed; no opt-out for PGX),
        # so the JP2 file itself cannot be the oracle input for a four-plane compare. The
        # bare codestream inside it carries no colour box and decodes to the four untouched
        # CMYK planes -- byte-identical codestream, so the same decode. JpxFixtureFreshnessTests
        # honours "referenceSource": "codestream" the same way (it extracts the jp2c payload
        # before invoking opj_decompress).
        "references": decompress_and_store(cmyk_cs_path, "cmyk-enumcs12"),
        "referenceSource": "codestream",
        "toleranceClass": "bit-exact",
        "assert": {"colorEnumCS": 12, "ihdrComponentCount": 4},
    })


def _write_ycc_from_rgb(rgb_ppm: Path, out_ppm: Path):
    data = rgb_ppm.read_bytes()
    # Minimal PPM (P6) reader: header then raw samples -- our own writer's exact format.
    assert data[:2] == b"P6"
    idx = 2
    fields = []
    while len(fields) < 3:
        while data[idx] in b" \t\r\n":
            idx += 1
        start = idx
        while data[idx] not in b" \t\r\n":
            idx += 1
        fields.append(int(data[start:idx]))
    idx += 1
    w, h, _maxval = fields
    pixels = data[idx:idx + w * h * 3]
    out = bytearray(len(pixels))
    for i in range(0, len(pixels), 3):
        r, g, b = pixels[i], pixels[i + 1], pixels[i + 2]
        y = 0.299 * r + 0.587 * g + 0.114 * b
        cb = -0.168736 * r - 0.331264 * g + 0.5 * b + 128
        cr = 0.5 * r - 0.418688 * g - 0.081312 * b + 128
        out[i] = max(0, min(255, round(y)))
        out[i + 1] = max(0, min(255, round(cb)))
        out[i + 2] = max(0, min(255, round(cr)))
    out_ppm.write_bytes(b"P6\n%d %d\n255\n" % (w, h) + bytes(out))


# =========================================================================================
# jpx-tolerance.json / README.md (static content, still written here for determinism).
# =========================================================================================

def write_tolerance_json():
    content = {
        "$schema": "https://json-schema.org/draft/2020-12/schema#",
        "description": (
            "Per-tolerance-class thresholds for JpxOracleTests' per-sample comparison "
            "against pinned OpenJPEG 2.5.4 PGX references. Exactly two "
            "classes, both a single global pair, no per-fixture entries. Every field is "
            "'larger number = looser' so scripts/check-jpx-tolerance.sh can apply the "
            "same generic per-key governance as check-raster-perf-baseline.sh: "
            "maxAbsFraction (a hard per-sample ceiling), outlierFraction (the fraction of "
            "samples allowed to exceed tightAbsFraction), and tightAbsFraction (the "
            "per-sample bound the non-outlier samples must satisfy) -- all expressed as "
            "fractions of the component's own full-scale range at native precision."
        ),
        "classes": {
            "bit-exact": {
                "maxAbsFraction": 0.0,
                "outlierFraction": 0.0,
                "tightAbsFraction": 0.0,
            },
            "tolerance-a": {
                "maxAbsFraction": 4.0 / 255.0,
                "outlierFraction": 0.001,
                "tightAbsFraction": 2.0 / 255.0,
            },
        },
    }
    TOLERANCE_PATH.write_text(json.dumps(content, indent=2, sort_keys=False) + "\n")


def write_readme(matrix: Matrix):
    generated = [e for e in matrix.entries if not e.get("hand_built")]
    hand_built = [e for e in matrix.entries if e.get("hand_built")]
    lines = []
    lines.append("# JPX fixture matrix\n")
    lines.append(
        "Committed fixtures for the in-house JPEG 2000 decoder. Generated deterministically by "
        "`scripts/generate-jpx-fixtures.sh` (delegates to `scripts/jpx-fixtures/"
        "generate.py` + `markers.py`) against a pinned OpenJPEG 2.5.4 CLI. Never run in "
        "CI -- regenerate by hand only when the matrix itself changes.\n"
    )
    lines.append(f"**{len(matrix.entries)} fixtures** ({len(generated)} opj_compress-generated, "
                  f"{len(hand_built)} hand-built byte-level constructions).\n")
    lines.append("## Regenerating\n")
    lines.append("```\n./scripts/generate-jpx-fixtures.sh\n"
                  "./scripts/generate-jpx-fixtures.sh --check   # structural asserts only, no OpenJPEG needed\n```\n")
    lines.append(
        "`--check` re-derives every `MANIFEST.json` assert field from the committed "
        "bytes via `markers.py`'s marker-segment walker and fails loudly on any "
        "mismatch. It never calls opj_compress/opj_decompress and never regenerates a "
        "file.\n"
    )
    lines.append("## OpenJPEG behaviours this matrix works around\n")
    lines.append(
        "- **Forced sYCC on subsampled 3-component streams.** opj_decompress attempts a "
        "sYCC->RGB conversion for any 3-component image where "
        "`comps[0].dx == comps[0].dy && comps[1].dx != 1`, regardless of the `colr` box. "
        "For uniform 2x2 subsampling this bails with `CAN NOT CONVERT` on stderr (exit "
        "0) and writes unconverted planes; for mixed 4:2:0 it succeeds. Neither is a "
        "decode failure.\n"
        "- **`-POC T0` is a no-op.** `Tn` in `-POC` syntax is 1-based; `T1` addresses "
        "tile index 0.\n"
        "- **`-F` honours declared precision exactly; PGX does not.** The PGX format "
        "derives precision from the data's value range, which loses one bit for signed "
        "data (a saturating signed 16-bit source reads back as `Ssiz` 15). Every "
        ">8-bit or signed fixture in this matrix uses raw `-F` input, never a PGX "
        "source.\n"
        "- **`-allow-partial` is required to decode any truncated stream at all** -- "
        "without it, `opj_decompress` exits 1 with 'Stream too short' and writes "
        "nothing.\n"
        "- **`opj_compress` never emits a `res ` box.** The 300 dpi `res`/`resd` "
        "fixture's expected value is pinned entirely by construction; there is no "
        "oracle output (no PNG `pHYs`, no `opj_dump` resolution field) to check it "
        "against.\n"
        "- **Default (huge) precincts read back as `PPx = PPy = 15`** when `COD`'s Scod "
        "bit 0 is clear -- there is no explicit per-resolution precinct-size list to "
        "read in that case; 15 is the T.800-defined default, not a measured value.\n"
        "- **`opj_decompress` converts EnumCS 12 (CMYK) to RGB** before writing any "
        "non-TIFF output (`color_cmyk_to_rgb`: three planes out, the fourth freed, no "
        "opt-out for PGX). The `cmyk-enumcs12.jp2` references are therefore decoded from "
        "the bare codestream inside the JP2 (`\"referenceSource\": \"codestream\"` in "
        "`MANIFEST.json`, honoured by `JpxFixtureFreshnessTests` too): same bytes, same "
        "decode, four untouched CMYK planes.\n"
    )
    lines.append("## Deviations from the literal fixture recipes\n")
    lines.append(
        "- **`cblksty-m*` fixtures generated with `-PLT` added** (the natural recipe "
        "is `-M <bits>` alone). The MANIFEST assertion 'the largest code-block signals "
        ">= 11 passes in the packet header' needs either a full Tier-2 (packet-header) "
        "decoder in the generator or a way to skip straight to the target packet. "
        "Every fixture in this set is single-tile/single-component/default-precinct "
        "with exactly one code-block per subband (verified programmatically, not "
        "assumed -- `markers.assert_single_codeblock_finest_subband`), so with `-PLT`'s "
        "packet-length list the generator can jump straight to the finest resolution's "
        "packet and read only its first code-block's number-of-new-passes field. "
        "Without `-PLT` this would require replicating the 'selective bypass' style's "
        "non-trivial per-bit-plane segmentation grouping (T.800 B.10.7) purely to skip "
        "past earlier packets it never needed to interpret. Measured >= 11 passes "
        "(most fixtures measure 22) on every cblksty value.\n"
    )
    lines.append("## Inventory\n")
    lines.append("| File | Class | Tolerance | References |\n|---|---|---|---|\n")
    for e in matrix.entries:
        refs = ", ".join(e["references"]) if e["references"] else "(none)"
        lines.append(f"| `{e['file']}` | {'hand-built' if e.get('hand_built') else 'generated'} "
                      f"| {e['toleranceClass']} | {refs} |\n")
    README_PATH.write_text("".join(lines))


# =========================================================================================
# Entry point.
# =========================================================================================

def regenerate():
    opj = _resolve_opj()
    # opj_compress -h exits 1 (it is a usage dump, not a "did that succeed" signal), so
    # this is a direct subprocess call rather than the _run() helper (which raises on a
    # non-zero exit -- correct for every other invocation in this file, wrong here).
    version_probe = subprocess.run([opj["opj_compress"], "-h"], capture_output=True, text=True)
    version_output = version_probe.stdout + version_probe.stderr
    if "v2.5.4" not in version_output:
        print(f"generate.py: expected OpenJPEG v2.5.4, got:\n{version_output[:300]}", file=sys.stderr)
        sys.exit(1)

    if FIXTURES_DIR.exists():
        for p in FIXTURES_DIR.iterdir():
            if p.name not in ("README.md",):
                p.unlink() if p.is_file() else shutil.rmtree(p)
    FIXTURES_DIR.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="jpx-fixtures-") as tmp:
        build_dir = Path(tmp)
        matrix = build_matrix(opj, build_dir)
        for entry in matrix.entries:
            src = build_dir / entry["file"]
            (FIXTURES_DIR / entry["file"]).write_bytes(src.read_bytes())
            for ref in entry["references"]:
                (FIXTURES_DIR / ref).write_bytes((build_dir / ref).read_bytes())

        # Integrity fields: a sha256 of the fixture file itself, and one per reference,
        # so --check can detect a corrupted/hand-edited committed file (garbage bytes
        # substituted for a real PGX, say) that would otherwise still pass every
        # structural `assert` check and the plain "does the file exist" reference check.
        for entry in matrix.entries:
            entry["sha256"] = _sha256_hex(build_dir / entry["file"])
            entry["referencesSha256"] = {
                ref: _sha256_hex(build_dir / ref) for ref in entry["references"]
            }

        manifest = {
            "description": (
                "JPX fixture matrix manifest. Every "
                "'assert' block is machine-checked both here (at generation time) and "
                "by 'scripts/generate-jpx-fixtures.sh --check' (structurally, from the "
                "committed bytes, no OpenJPEG invocation). 'sha256' (the fixture file) "
                "and 'referencesSha256' (one per committed reference) are also verified "
                "by --check, byte-for-byte, so a structurally-plausible but corrupted "
                "committed file (e.g. a PGX with an intact header and garbage sample "
                "data) is still caught even though it would pass every 'assert' key and "
                "the plain existence check on its own."
            ),
            "fixtures": matrix.entries,
        }
        MANIFEST_PATH.write_text(json.dumps(manifest, indent=2, sort_keys=False) + "\n")
        write_tolerance_json()
        write_readme(matrix)

    print(f"generate.py: wrote {len(list(FIXTURES_DIR.iterdir()))} files to {FIXTURES_DIR}")


def check():
    if not MANIFEST_PATH.exists():
        print(f"generate.py --check: {MANIFEST_PATH} does not exist -- run without --check first.",
              file=sys.stderr)
        sys.exit(1)
    manifest = json.loads(MANIFEST_PATH.read_text())
    problems = []
    for entry in manifest["fixtures"]:
        problems.extend(_check_fixture(entry))
    if problems:
        print(f"generate.py --check: {len(problems)} problem(s):", file=sys.stderr)
        for p in problems:
            print(f"  - {p}", file=sys.stderr)
        sys.exit(1)
    print(f"generate.py --check: {len(manifest['fixtures'])} fixtures, all structural asserts match. PASS")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    if args.check:
        check()
    else:
        regenerate()


if __name__ == "__main__":
    main()
