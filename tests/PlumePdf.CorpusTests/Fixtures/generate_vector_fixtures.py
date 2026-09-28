#!/usr/bin/env python3
"""
Generator for the anti-aliasing oracle fixture:
`vector-edges.pdf`, a single hand-built US-Letter page (612x792 pt) with no embedded
font, whose content stream exercises exactly the geometry `RasterOracleTests`' AA-off leg
(`VectorEdges_AntiAliasOff_MatchesPdfiumAliased`) needs a hard "before/after" comparison
against — the shapes the pinned PDFium shim's `--render-aliased` verb (`FPDF_RENDER_NO_SMOOTHPATH
| FPDF_RENDER_NO_SMOOTHTEXT`) measurably changes (fills and strokes, per a chromium/8009
measurement) alongside a shape it measurably does NOT (the text line — glyph
aliasing is a declared oracle gap here, unit-asserted elsewhere):

  - Three straight diagonal strokes of differing line widths (1/3/6 pt) — a fill/stroke edge at
    three different anti-aliasing footprints.
  - A Bezier-approximated circle (four cubic segments, the standard k=0.5522847498 control-point
    fraction) — a curved fill edge, not just straight diagonals.
  - A triangular clip over a solid fill — exercises the clip-coverage path too (clip coverage is
    always anti-aliased, a declared scope exclusion the pipeline tests pin directly; included
    here only so the fixture has one, not because this leg's SSIM isolates it).
  - One line of Standard-14 Helvetica text (no /FontFile*) — the declared glyph-aliasing gap.

Provenance: original content-stream authorship from ISO 32000-1 (clean-room per the policy in
AGENTS.md); reuses
generate_fixtures.py's existing minimal PDF/xref serialization helpers (HEADER, indirect_obj,
stream_obj, pdf_dict, arr, name, num, ref, hex_string, layout, classic_xref_and_trailer) —
the same helpers generate_image_fixtures.py imports — so the byte-level framing this script
produces is deterministic and reproducible: `python3 generate_vector_fixtures.py` regenerates
`vector-edges.pdf` byte-for-byte.
"""
from __future__ import annotations

import hashlib
from pathlib import Path

from generate_fixtures import (
    HEADER,
    arr,
    classic_xref_and_trailer,
    hex_string,
    indirect_obj,
    layout,
    name,
    num,
    pdf_dict,
    ref,
    stream_obj,
)

HERE = Path(__file__).parent

# Standard 4-cubic-Bezier circle approximation constant (radius r, center cx,cy):
# each quadrant's two control points sit at a tangent-line distance of k*r from the on-circle
# anchor point, where k = 4/3 * (sqrt(2) - 1) ~= 0.5522847498.
_K = 0.5522847498


def _circle_path(cx: float, cy: float, r: float) -> bytes:
    kr = _K * r
    ops: list[bytes] = [f"{cx - r:.3f} {cy:.3f} m".encode()]
    # Four cubic segments, counter-clockwise starting at the leftmost point (cx - r, cy).
    segments = [
        # (c1x, c1y, c2x, c2y, ex, ey)
        (cx - r, cy + kr, cx - kr, cy + r, cx, cy + r),
        (cx + kr, cy + r, cx + r, cy + kr, cx + r, cy),
        (cx + r, cy - kr, cx + kr, cy - r, cx, cy - r),
        (cx - kr, cy - r, cx - r, cy - kr, cx - r, cy),
    ]
    for c1x, c1y, c2x, c2y, ex, ey in segments:
        ops.append(f"{c1x:.3f} {c1y:.3f} {c2x:.3f} {c2y:.3f} {ex:.3f} {ey:.3f} c".encode())
    ops.append(b"h")
    return b"\n".join(ops)


def _build_content() -> bytes:
    lines: list[bytes] = []

    # Three diagonal strokes of differing widths, black.
    lines.append(b"q 0 0 0 RG")
    lines.append(b"1 w 60 700 m 220 560 l S")
    lines.append(b"3 w 280 700 m 440 560 l S")
    lines.append(b"6 w 500 700 m 560 560 l S")
    lines.append(b"Q")

    # A Bezier-approximated circle, mid-gray fill (a curved edge, not axis-aligned).
    lines.append(b"q 0.4 0.4 0.4 rg")
    lines.append(_circle_path(cx=180, cy=350, r=90))
    lines.append(b"f")
    lines.append(b"Q")

    # A triangular clip over a solid black fill: only the triangle's interior paints, so the
    # fixture also exercises the always-anti-aliased clip-coverage path.
    lines.append(b"q 330 260 m 560 260 l 330 430 l h W n")
    lines.append(b"0 0 0 rg 300 230 300 230 re f")
    lines.append(b"Q")

    # One line of Standard-14 Helvetica text, no embedded font.
    lines.append(b"BT /F1 28 Tf 60 100 Td (PlumePDF vector-edges) Tj ET")

    return b"\n".join(lines) + b"\n"


def build_vector_edges() -> bytes:
    catalog, pages, page, font, content = 1, 2, 3, 4, 5

    objs = {
        catalog: indirect_obj(catalog, 0, pdf_dict([("Type", name("Catalog")), ("Pages", ref(pages))])),
        pages: indirect_obj(
            pages, 0, pdf_dict([("Type", name("Pages")), ("Kids", arr([ref(page)])), ("Count", num(1))])
        ),
        page: indirect_obj(
            page,
            0,
            pdf_dict(
                [
                    ("Type", name("Page")),
                    ("Parent", ref(pages)),
                    ("MediaBox", arr([num(0), num(0), num(612), num(792)])),
                    ("Resources", pdf_dict([("Font", pdf_dict([("F1", ref(font))]))])),
                    ("Contents", ref(content)),
                ]
            ),
        ),
        font: indirect_obj(
            font,
            0,
            pdf_dict([("Type", name("Font")), ("Subtype", name("Type1")), ("BaseFont", name("Helvetica"))]),
        ),
        content: stream_obj(content, 0, [], _build_content()),
    }

    buf, offsets = layout(HEADER, [(n, objs[n]) for n in sorted(objs)])
    doc_id = hashlib.md5(b"plumepdf-fixture-vector-edges").digest()
    trailer = [("Size", num(6)), ("Root", ref(catalog)), ("ID", arr([hex_string(doc_id), hex_string(doc_id)]))]
    return classic_xref_and_trailer(buf, offsets, 6, trailer)


def write(name_: str, data: bytes) -> None:
    path = HERE / name_
    path.write_bytes(data)
    print(f"wrote {name_} ({len(data)} bytes)")


if __name__ == "__main__":
    write("vector-edges.pdf", build_vector_edges())
