"""
Structural marker/box walker for JPEG 2000 fixtures (T.800 / ISO/IEC 15444-1 Annex A, and
ISO/IEC 15444-1 Annex I for the JP2 file format). Used ONLY by generate.py to compute the
`assert` block committed in MANIFEST.json and to re-check it in `--check` mode.

This is deliberately NOT a byte scan. A codestream with no POC/RGN marker segment still
contains dozens of incidental FF5F/FF5E byte pairs inside entropy-coded packet data (a
consequence of the MQ coder's output being close to random), so `b"\\xff\\x5f" in data` is
~100% false-positive on any non-trivial stream. Every fact this module reports is derived by
walking marker segments using their own declared length fields (Annex A.2's "L### is a
2-byte big-endian length of the marker segment parameters, counted from the first byte of
L### itself to the last byte of the segment, i.e. NOT including the 2-byte marker code"),
never by searching for a byte pattern anywhere in the file.

Two independent walks:
  - `walk_jp2_boxes`: the ISO base-media-like box structure of a .jp2 file (Annex I.5):
    each box is [length:4][type:4]([xlen:8] if length==1)[payload], length==0 means "to EOF".
    Superboxes (jp2h) are descended into automatically.
  - `walk_codestream`: the marker-segment structure of a raw .j2k codestream (or the payload
    of a .jp2's jp2c box): SOC, then main-header marker segments up to (not including) the
    first SOT, then per tile-part: the SOT segment itself (which gives Isot/Psot/TPsot/TNsot),
    that tile-part's own header marker segments up to SOD, then Psot is used to jump to the
    NEXT tile-part (or EOC) without reading a single byte of packet data in between. Psot==0
    ("this tile-part extends to EOC") is resolved by scanning forward for the EOC marker
    starting the search past the SOD, which is safe because EOC (0xFFD9) cannot appear inside
    packet data undetected the way FF5E/FF5F can: it is the codestream's own terminator and by
    construction only appears once at end-of-stream in every fixture this generator produces
    (none of our recipes call for a fragmented/multi-EOC stream).

A minimal Tier-2 (packet header) reader lives at the bottom for exactly one purpose: the
`-M` (cblksty) fixtures need to prove their finest-resolution code-block signals >= 11 coding
passes (asserted in MANIFEST.json's `assert` block). Doing that in general requires a
full tag-tree + segmentation-length decoder (duplicating much of the future JpxPacketDecoder).
This generator's own fixtures make that unnecessary: every `-M` fixture is single-tile,
single-component, default (huge) precincts and default 64x64 code-blocks on a 64x48 (or
97x61) image, so every subband has EXACTLY ONE code-block (see `_assert_single_codeblock_
per_subband` below, which is itself verified from SIZ/COD geometry, not assumed). With one
code-block per subband, per-layer inclusion collapses from a general tag-tree to a single
raw bit (T.800 B.10.2's tag tree over a 1x1 grid has no internal nodes), and the fixtures are
generated with `-PLT` so packet byte lengths are read directly from the PLT marker segment
instead of requiring a full multi-packet Tier-2 skip-decode to reach the finest resolution's
packet. `decode_first_codeblock_num_passes` reads only as far as the number-of-new-passes
field for the FIRST code-block of a given packet and stops -- it never reads a length field
and is not a general packet-header decoder.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass, field


# ---------------------------------------------------------------------------------------
# JP2 box walk (Annex I.5)
# ---------------------------------------------------------------------------------------

@dataclass
class Box:
    type: bytes
    offset: int          # offset of the 4-byte length field
    payload_offset: int  # offset of the first payload byte
    payload: bytes
    children: list = field(default_factory=list)  # populated only for jp2h


_SUPERBOXES = {b"jp2h"}


def walk_jp2_boxes(data: bytes) -> list[Box]:
    return _walk_boxes(data, 0, len(data))


def _walk_boxes(data: bytes, start: int, end: int) -> list[Box]:
    boxes = []
    pos = start
    while pos < end:
        if end - pos < 8:
            break
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        btype = data[pos + 4:pos + 8]
        header_len = 8
        if length == 1:
            if end - pos < 16:
                raise ValueError(f"truncated XL box header at offset {pos}")
            length = struct.unpack(">Q", data[pos + 8:pos + 16])[0]
            header_len = 16
        if length == 0:
            length = end - pos
        payload_off = pos + header_len
        payload_end = pos + length
        payload = data[payload_off:payload_end]
        box = Box(type=btype, offset=pos, payload_offset=payload_off, payload=payload)
        if btype in _SUPERBOXES:
            box.children = _walk_boxes(data, payload_off, payload_end)
        boxes.append(box)
        pos = payload_end
    return boxes


def find_box(boxes: list[Box], type_: bytes) -> Box | None:
    for b in boxes:
        if b.type == type_:
            return b
    return None


def find_all_boxes(boxes: list[Box], type_: bytes) -> list[Box]:
    return [b for b in boxes if b.type == type_]


def find_jp2c(boxes: list[Box]) -> Box:
    box = find_box(boxes, b"jp2c")
    if box is None:
        raise ValueError("no jp2c box found in JP2 container")
    return box


# ---------------------------------------------------------------------------------------
# Codestream marker segment walk (Annex A)
# ---------------------------------------------------------------------------------------

SOC, SIZ, COD, COC, TLM, PLM, PLT, QCD, QCC, RGN, POC, PPM, PPT, CRG, COM = (
    0xFF4F, 0xFF51, 0xFF52, 0xFF53, 0xFF55, 0xFF57, 0xFF58, 0xFF5C, 0xFF5D, 0xFF5E,
    0xFF5F, 0xFF60, 0xFF61, 0xFF63, 0xFF64,
)
SOT, SOP, EPH, SOD, EOC = 0xFF90, 0xFF91, 0xFF92, 0xFF93, 0xFFD9

# Marker segments with NO length field (delimiting markers only).
_NO_LENGTH = {SOC, SOP, EPH, SOD, EOC}


@dataclass
class SizInfo:
    rsiz: int
    xsiz: int
    ysiz: int
    xosiz: int
    yosiz: int
    xtsiz: int
    ytsiz: int
    xtosiz: int
    ytosiz: int
    csiz: int
    precision: list  # per component, 1..38 (Ssiz low 7 bits + 1)
    signed: list     # per component, bool
    xrsiz: list
    yrsiz: list


@dataclass
class CodInfo:
    scod: int
    prog_order: int
    num_layers: int
    mct: int
    num_decomp_levels: int
    xcb_exp: int  # code-block width exponent (already +2 applied)
    ycb_exp: int
    cblksty: int
    transform: int  # 0 = 9/7 irreversible, 1 = 5/3 reversible
    precinct_sizes: list  # list of (ppx, ppy) per resolution level, r=0..N_L, empty if Scod bit0 clear


@dataclass
class QcdInfo:
    sqcd: int
    style: int        # Sqcd & 0x1F  (0 = none, 1 = scalar derived, 2 = scalar expounded)
    guard_bits: int    # Sqcd >> 5


@dataclass
class Segment:
    marker: int
    offset: int       # offset of the marker code (0xFFxx)'s first byte
    length_field: int  # value of L### (0 for markers with no length field)
    payload: bytes


@dataclass
class TilePart:
    isot: int
    tpsot: int
    tnsot: int
    psot: int
    sot_offset: int     # offset of the SOT marker's first byte
    header_segments: list  # Segment list, tile-part header only (between SOT segment and SOD)
    sod_offset: int     # offset of the SOD marker's first byte
    body_offset: int    # first byte after SOD (start of packet data)
    end_offset: int     # first byte NOT belonging to this tile-part (next SOT or EOC)


@dataclass
class Codestream:
    main_header_segments: list  # Segment list, SOC exclusive, up to (not incl.) first SOT
    tile_parts: list             # TilePart list, in file order
    eoc_offset: int | None


def _seg_at(data: bytes, pos: int) -> Segment:
    marker = struct.unpack(">H", data[pos:pos + 2])[0]
    if marker in _NO_LENGTH:
        return Segment(marker=marker, offset=pos, length_field=0, payload=b"")
    length = struct.unpack(">H", data[pos + 2:pos + 4])[0]
    payload = data[pos + 4:pos + 2 + length]
    return Segment(marker=marker, offset=pos, length_field=length, payload=payload)


def _seg_total_bytes(seg: Segment) -> int:
    if seg.marker in _NO_LENGTH:
        return 2
    return 2 + seg.length_field


def parse_siz(payload: bytes) -> SizInfo:
    rsiz, xsiz, ysiz, xosiz, yosiz, xtsiz, ytsiz, xtosiz, ytosiz, csiz = struct.unpack(
        ">HIIIIIIIIH", payload[:36]
    )
    precision, signed, xrsiz, yrsiz = [], [], [], []
    off = 36
    for _ in range(csiz):
        ssiz = payload[off]
        precision.append((ssiz & 0x7F) + 1)
        signed.append(bool(ssiz & 0x80))
        xrsiz.append(payload[off + 1])
        yrsiz.append(payload[off + 2])
        off += 3
    return SizInfo(rsiz, xsiz, ysiz, xosiz, yosiz, xtsiz, ytsiz, xtosiz, ytosiz, csiz,
                    precision, signed, xrsiz, yrsiz)


def parse_cod(payload: bytes) -> CodInfo:
    scod = payload[0]
    prog_order = payload[1]
    num_layers = struct.unpack(">H", payload[2:4])[0]
    mct = payload[4]
    num_decomp_levels = payload[5]
    xcb_exp = payload[6] + 2
    ycb_exp = payload[7] + 2
    cblksty = payload[8]
    transform = payload[9]
    precinct_sizes = []
    if scod & 0x01:
        n = num_decomp_levels + 1
        for i in range(n):
            b = payload[10 + i]
            precinct_sizes.append((b & 0x0F, (b >> 4) & 0x0F))
    return CodInfo(scod, prog_order, num_layers, mct, num_decomp_levels, xcb_exp, ycb_exp,
                    cblksty, transform, precinct_sizes)


def parse_qcd(payload: bytes) -> QcdInfo:
    sqcd = payload[0]
    return QcdInfo(sqcd, sqcd & 0x1F, sqcd >> 5)


def parse_sot(payload: bytes):
    isot, psot, tpsot, tnsot = struct.unpack(">HIBB", payload[:8])
    return isot, psot, tpsot, tnsot


def parse_plt_lengths(payload: bytes) -> list[int]:
    """Decodes the packet-length list of a single PLT segment (T.800 B.9.2): Zplt (1 byte,
    ignored -- our fixtures never split a PLT across segments), then a sequence of
    variable-length integers, each byte contributing its low 7 bits, MSB=1 meaning
    'more bytes follow' (big-endian bit order, most-significant group first)."""
    lengths = []
    i = 1  # skip Zplt
    cur = 0
    while i < len(payload):
        b = payload[i]
        cur = (cur << 7) | (b & 0x7F)
        if not (b & 0x80):
            lengths.append(cur)
            cur = 0
        i += 1
    return lengths


def walk_codestream(data: bytes) -> Codestream:
    if len(data) < 2 or struct.unpack(">H", data[0:2])[0] != SOC:
        raise ValueError("codestream does not start with SOC (0xFF4F)")
    pos = 2
    main_segments = []
    while True:
        marker = struct.unpack(">H", data[pos:pos + 2])[0]
        if marker == SOT:
            break
        seg = _seg_at(data, pos)
        main_segments.append(seg)
        pos += _seg_total_bytes(seg)

    tile_parts = []
    eoc_offset = None
    while pos < len(data):
        marker = struct.unpack(">H", data[pos:pos + 2])[0]
        if marker == EOC:
            eoc_offset = pos
            break
        if marker != SOT:
            raise ValueError(f"expected SOT or EOC at offset {pos}, found 0x{marker:04X}")
        sot_offset = pos
        sot_seg = _seg_at(data, pos)
        isot, psot, tpsot, tnsot = parse_sot(sot_seg.payload)
        pos += _seg_total_bytes(sot_seg)

        header_segments = []
        while True:
            marker = struct.unpack(">H", data[pos:pos + 2])[0]
            if marker == SOD:
                break
            seg = _seg_at(data, pos)
            header_segments.append(seg)
            pos += _seg_total_bytes(seg)
        sod_offset = pos
        body_offset = pos + 2  # SOD has no length field

        if psot == 0:
            # "This tile-part extends to EOC" (T.800 A.4.2). Every fixture this generator
            # writes is a single contiguous, non-fragmented stream, so it is safe to scan
            # forward from body_offset for the next SOT or EOC to find this tile-part's end
            # -- unlike FF5E/FF5F, SOT/EOC are structural markers whose reappearance
            # legitimately delimits the next unit; nothing in packet data before that
            # reappearance is interpreted.
            scan = body_offset
            while scan < len(data) - 1:
                if data[scan] == 0xFF and data[scan + 1] in (SOT & 0xFF, EOC & 0xFF):
                    break
                scan += 1
            end_offset = scan if scan < len(data) - 1 else len(data)
        else:
            end_offset = sot_offset + psot

        tile_parts.append(TilePart(isot, tpsot, tnsot, psot, sot_offset, header_segments,
                                    sod_offset, body_offset, end_offset))
        pos = end_offset

    return Codestream(main_segments, tile_parts, eoc_offset)


def segments_by_marker(segments: list, marker: int) -> list:
    return [s for s in segments if s.marker == marker]


def has_marker(segments: list, marker: int) -> bool:
    return any(s.marker == marker for s in segments)


# ---------------------------------------------------------------------------------------
# Minimal Tier-2 packet-header reader (scope: see module docstring).
# ---------------------------------------------------------------------------------------

class _BitReader:
    """T.800 B.10.1 bit-stuffed reader: after a 0xFF byte, the next byte contributes only
    its low 7 bits (bit 7 is a stuffed 0, not data)."""

    def __init__(self, data: bytes, start: int):
        self._data = data
        self._pos = start
        self._byte = 0
        self._bits_left = 0
        self._prev_was_ff = False

    def read_bit(self) -> int:
        if self._bits_left == 0:
            self._byte = self._data[self._pos]
            was_ff = self._byte == 0xFF
            self._bits_left = 7 if self._prev_was_ff else 8
            self._prev_was_ff = was_ff
            self._pos += 1
        self._bits_left -= 1
        return (self._byte >> self._bits_left) & 1

    def read_bits(self, n: int) -> int:
        v = 0
        for _ in range(n):
            v = (v << 1) | self.read_bit()
        return v

    def align(self):
        self._bits_left = 0


def resolution_dims(siz: SizInfo, component: int, num_decomp_levels: int) -> list:
    """T.800 Annex B.5, single-tile fixtures only (tile origin == image origin, tile size ==
    image size -- true for every `-M` fixture): resolution r's width/height for the given
    component, r = 0 (coarsest) .. num_decomp_levels (finest, == full component resolution).
    """
    xr, yr = siz.xrsiz[component], siz.yrsiz[component]
    tcx0, tcy0 = -(-siz.xosiz // xr), -(-siz.yosiz // yr)   # ceil division
    tcx1, tcy1 = -(-siz.xsiz // xr), -(-siz.ysiz // yr)
    dims = []
    for r in range(num_decomp_levels + 1):
        shift = num_decomp_levels - r
        w = -(-tcx1 >> shift) - -(-tcx0 >> shift) if shift else tcx1 - tcx0
        h = -(-tcy1 >> shift) - -(-tcy0 >> shift) if shift else tcy1 - tcy0
        dims.append((w, h))
    return dims


def finest_subband_dims(siz: SizInfo, cod: CodInfo, component: int = 0):
    """HL/LH/HH subband dimensions at the finest resolution (r = N_L), which by
    construction are always equal in size (T.800 B.5) -- see caller for why this makes
    'the largest code-block' unambiguous without comparing every subband in the image."""
    dims = resolution_dims(siz, component, cod.num_decomp_levels)
    if cod.num_decomp_levels == 0:
        raise ValueError("no wavelet decomposition (num_decomp_levels == 0); no HL/LH/HH band")
    (w1, h1), (w0, h0) = dims[-1], dims[-2]
    hl = (w1 - w0, h0)
    lh = (w0, h1 - h0)
    hh = (w1 - w0, h1 - h0)
    return hl, lh, hh


def assert_single_codeblock_finest_subband(siz: SizInfo, cod: CodInfo, component: int = 0):
    """Verifies (never assumes) the precondition `decode_first_codeblock_num_passes` and its
    docstring rely on: the finest-resolution HL/LH/HH subbands are each covered by exactly
    one code-block, i.e. subband dims <= code-block dims AND the codestream uses default
    (no explicit, small) precincts so a single precinct also covers the whole subband
    (T.800 B.7/B.9: a code-block never straddles a precinct boundary, so if precincts were
    small this could split a subband's coefficients across more than one code-block even
    when the subband itself is small)."""
    hl, lh, hh = finest_subband_dims(siz, cod, component)
    cbw, cbh = 1 << cod.xcb_exp, 1 << cod.ycb_exp
    for name, (w, h) in (("HL", hl), ("LH", lh), ("HH", hh)):
        if w > cbw or h > cbh:
            raise ValueError(
                f"{name} subband at finest resolution is {w}x{h}, larger than the "
                f"{cbw}x{cbh} code-block -- more than one code-block per subband, "
                f"decode_first_codeblock_num_passes's degenerate-tag-tree precondition "
                f"does not hold for this fixture"
            )
    if cod.precinct_sizes:
        ppx, ppy = cod.precinct_sizes[-1]
        if (1 << ppx) < cbw * 2 or (1 << ppy) < cbh * 2:
            raise ValueError(
                "explicit precinct sizes present and not clearly >= the code-block size "
                "at the finest resolution -- a subband could be split across precincts"
            )


def _decode_num_new_passes(br: _BitReader) -> int:
    """T.800 B.10.6, transcribed from the well-known getnumpasses shape (identical in every
    independent JPEG 2000 implementation the generator's author has read, e.g. OpenJPEG's
    opj_t2_getnumpasses in t2.c): a short prefix code, not a fixed-width field."""
    if br.read_bit() == 0:
        return 1
    if br.read_bit() == 0:
        return 2
    n = br.read_bits(2)
    if n != 3:
        return 3 + n
    n = br.read_bits(5)
    if n != 31:
        return 6 + n
    n = br.read_bits(7)
    return 37 + n


def decode_first_codeblock_num_passes(data: bytes, packet_start: int) -> int:
    """Decodes ONLY as far as the number-of-new-coding-passes field of the FIRST code-block
    of the packet starting at `packet_start` (a byte offset already known to be byte-aligned
    and correct, e.g. via PLT-reported packet lengths -- see module docstring). Precondition
    (checked by the caller via `is_single_codeblock_per_subband`): the subband this packet's
    first code-block belongs to contains exactly one code-block, so:
      - the inclusion tag tree over that 1x1 grid is a single degenerate node: T.800 B.10.2's
        general multi-level tag tree collapses to reading raw bits directly against the
        node's own running (value, known) state; for a code-block being queried for the
        FIRST time (guaranteed here -- see below) at threshold=1 (0-based layer 0, so
        threshold = layer_index + 1 = 1), the loop degenerates to exactly one bit read
        (1 => included at layer 0; 0 => not included, which cannot happen for the fixtures
        this is used on -- their finest-resolution subbands are never all-zero for a
        full-dynamic-range LCG source, and the caller does not rely on that never
        happening: it raises if it does).
      - the zero-bit-plane count (also a degenerate 1x1 tag tree, B.10.5) is therefore a
        plain unary code: count of 0-bits before the first 1-bit.
    Every fixture this is used on is single-layer, so "queried for the first time" is always
    true for every code-block in the stream -- there is no cross-layer resumption to model.
    """
    br = _BitReader(data, packet_start)
    packet_nonempty = br.read_bit()
    if packet_nonempty != 1:
        raise ValueError("expected a non-empty packet (zero-length-packet bit was 0)")
    included_bit = br.read_bit()
    if included_bit != 1:
        raise ValueError(
            "first code-block's degenerate inclusion tag tree resolved to 'not included' "
            "-- the fixture's finest subband is unexpectedly all-zero for this source"
        )
    zero_bitplanes = 0
    while br.read_bit() == 0:
        zero_bitplanes += 1
    return _decode_num_new_passes(br)
