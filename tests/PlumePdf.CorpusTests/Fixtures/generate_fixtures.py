#!/usr/bin/env python3
"""
Self-authored generator for PlumePDF's Phase 1 hand-crafted test fixtures.

Every byte this script emits is original: the PDF object/xref/stream framing
is written directly from ISO 32000-1 (the object model + cross-reference
sections), and the AES block cipher below is implemented directly from
FIPS-197 (the public NIST standard). No third-party PDF library, and no
competitor source code, was read or consulted to write this file — see
the clean-room policy in AGENTS.md and Fixtures/README.md.

Running `python3 generate_fixtures.py` regenerates every *.pdf file in this
directory byte-for-byte (all randomness uses a fixed seed / fixed salts so
the fixtures are reproducible).

No third-party packages are imported — only the Python standard library
(zlib for Flate, hashlib for MD5/SHA-2). AES and RC4 are hand-rolled below
because generating the encrypted fixtures needs them and the stdlib has
neither; this mirrors (but is entirely independent of) what
StandardSecurityHandler.cs will later do with System.Security.Cryptography.
"""
from __future__ import annotations

import hashlib
import struct
import zlib
from pathlib import Path

HERE = Path(__file__).parent

# =============================================================================
# AES (FIPS-197) — encryption only, ECB block primitive + CBC wrapper.
# =============================================================================

# Hand-transcribing the 256-byte FIPS-197 S-box table from memory is
# error-prone, so it is built programmatically instead, straight from the
# spec's own construction: multiplicative inverse in GF(2^8) (0 maps to 0)
# followed by the fixed affine transform. Self-checked against the spec's
# published test vectors below.


def _gf_mul(a: int, b: int) -> int:
    p = 0
    for _ in range(8):
        if b & 1:
            p ^= a
        hi = a & 0x80
        a = (a << 1) & 0xFF
        if hi:
            a ^= 0x1B
        b >>= 1
    return p


def _build_sbox() -> bytes:
    # Multiplicative inverse table in GF(2^8) (0 maps to 0).
    inv = [0] * 256
    for x in range(1, 256):
        for y in range(1, 256):
            if _gf_mul(x, y) == 1:
                inv[x] = y
                break
    sbox = [0] * 256
    for x in range(256):
        b = inv[x]
        s = b
        for shift in (1, 2, 3, 4):
            s ^= ((b << shift) | (b >> (8 - shift))) & 0xFF
        s ^= 0x63
        sbox[x] = s
    return bytes(sbox)


SBOX = _build_sbox()
assert SBOX[0x00] == 0x63 and SBOX[0x01] == 0x7C and SBOX[0x53] == 0xED, "S-box construction is wrong"

RCON = [0x01]
for _ in range(13):
    v = RCON[-1] << 1
    if v & 0x100:
        v ^= 0x11B
    RCON.append(v & 0xFF)


def _key_expansion(key: bytes) -> list[bytes]:
    nk = len(key) // 4
    nr = nk + 6
    w = [key[4 * i : 4 * i + 4] for i in range(nk)]
    for i in range(nk, 4 * (nr + 1)):
        temp = bytearray(w[i - 1])
        if i % nk == 0:
            temp = bytes([temp[1], temp[2], temp[3], temp[0]])
            temp = bytes(SBOX[b] for b in temp)
            temp = bytes([temp[0] ^ RCON[i // nk - 1]]) + temp[1:]
        elif nk > 6 and i % nk == 4:
            temp = bytes(SBOX[b] for b in temp)
        w.append(bytes(a ^ b for a, b in zip(w[i - nk], temp)))
    round_keys = []
    for r in range(nr + 1):
        round_keys.append(b"".join(w[4 * r : 4 * r + 4]))
    return round_keys


def _sub_bytes(state: bytearray) -> None:
    for i in range(16):
        state[i] = SBOX[state[i]]


def _shift_rows(state: bytearray) -> None:
    # state is column-major: state[c*4 + r]
    s = state[:]
    for r in range(1, 4):
        for c in range(4):
            state[c * 4 + r] = s[((c + r) % 4) * 4 + r]


def _xtime(a: int) -> int:
    a <<= 1
    if a & 0x100:
        a ^= 0x11B
    return a & 0xFF


def _mix_columns(state: bytearray) -> None:
    for c in range(4):
        a0, a1, a2, a3 = state[c * 4 : c * 4 + 4]
        r0 = _xtime(a0) ^ (_xtime(a1) ^ a1) ^ a2 ^ a3
        r1 = a0 ^ _xtime(a1) ^ (_xtime(a2) ^ a2) ^ a3
        r2 = a0 ^ a1 ^ _xtime(a2) ^ (_xtime(a3) ^ a3)
        r3 = (_xtime(a0) ^ a0) ^ a1 ^ a2 ^ _xtime(a3)
        state[c * 4 : c * 4 + 4] = [r0, r1, r2, r3]


def _add_round_key(state: bytearray, rk: bytes) -> None:
    for i in range(16):
        state[i] ^= rk[i]


def aes_encrypt_block(key: bytes, block: bytes) -> bytes:
    assert len(block) == 16
    round_keys = _key_expansion(key)
    nr = len(round_keys) - 1
    state = bytearray(block)
    _add_round_key(state, round_keys[0])
    for rnd in range(1, nr):
        _sub_bytes(state)
        _shift_rows(state)
        _mix_columns(state)
        _add_round_key(state, round_keys[rnd])
    _sub_bytes(state)
    _shift_rows(state)
    _add_round_key(state, round_keys[nr])
    return bytes(state)


# Self-check against the FIPS-197 Appendix B / C test vectors before this
# module is used for anything, so a broken S-box/key-schedule fails loudly
# at import time rather than silently producing wrong fixtures.
_TV_KEY = bytes.fromhex("000102030405060708090a0b0c0d0e0f")
_TV_PT = bytes.fromhex("00112233445566778899aabbccddeeff")
_TV_CT = bytes.fromhex("69c4e0d86a7b0430d8cdb78070b4c55a")
assert aes_encrypt_block(_TV_KEY, _TV_PT) == _TV_CT, "AES-128 block cipher self-check failed"

_TV_KEY256 = bytes.fromhex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f")
_TV_CT256 = bytes.fromhex("8ea2b7ca516745bfeafc49904b496089")
assert aes_encrypt_block(_TV_KEY256, _TV_PT) == _TV_CT256, "AES-256 block cipher self-check failed"


def pkcs7_pad(data: bytes, block_size: int = 16) -> bytes:
    pad_len = block_size - (len(data) % block_size)
    return data + bytes([pad_len]) * pad_len


def aes_cbc_encrypt(key: bytes, iv: bytes, plaintext: bytes, pad: bool = True) -> bytes:
    assert len(iv) == 16
    data = pkcs7_pad(plaintext) if pad else plaintext
    assert len(data) % 16 == 0
    out = bytearray()
    prev = iv
    for i in range(0, len(data), 16):
        block = bytes(a ^ b for a, b in zip(data[i : i + 16], prev))
        enc = aes_encrypt_block(key, block)
        out += enc
        prev = enc
    return bytes(out)


INV_SBOX = bytes(SBOX.index(b) for b in range(256))


def _inv_sub_bytes(state: bytearray) -> None:
    for i in range(16):
        state[i] = INV_SBOX[state[i]]


def _inv_shift_rows(state: bytearray) -> None:
    s = state[:]
    for r in range(1, 4):
        for c in range(4):
            state[((c + r) % 4) * 4 + r] = s[c * 4 + r]


def _gmul(a: int, b: int) -> int:
    return _gf_mul(a, b)


def _inv_mix_columns(state: bytearray) -> None:
    for c in range(4):
        a0, a1, a2, a3 = state[c * 4 : c * 4 + 4]
        r0 = _gmul(a0, 14) ^ _gmul(a1, 11) ^ _gmul(a2, 13) ^ _gmul(a3, 9)
        r1 = _gmul(a0, 9) ^ _gmul(a1, 14) ^ _gmul(a2, 11) ^ _gmul(a3, 13)
        r2 = _gmul(a0, 13) ^ _gmul(a1, 9) ^ _gmul(a2, 14) ^ _gmul(a3, 11)
        r3 = _gmul(a0, 11) ^ _gmul(a1, 13) ^ _gmul(a2, 9) ^ _gmul(a3, 14)
        state[c * 4 : c * 4 + 4] = [r0, r1, r2, r3]


def aes_decrypt_block(key: bytes, block: bytes) -> bytes:
    """Only used by _validate_fixtures.py to round-trip-prove the cipher
    above; production decryption in PlumePDF will call
    System.Security.Cryptography.Aes, not this."""
    round_keys = _key_expansion(key)
    nr = len(round_keys) - 1
    state = bytearray(block)
    _add_round_key(state, round_keys[nr])
    for rnd in range(nr - 1, 0, -1):
        _inv_shift_rows(state)
        _inv_sub_bytes(state)
        _add_round_key(state, round_keys[rnd])
        _inv_mix_columns(state)
    _inv_shift_rows(state)
    _inv_sub_bytes(state)
    _add_round_key(state, round_keys[0])
    return bytes(state)


assert aes_decrypt_block(_TV_KEY, _TV_CT) == _TV_PT, "AES-128 inverse cipher self-check failed"


def aes_cbc_decrypt(key: bytes, iv: bytes, ciphertext: bytes, unpad: bool = True) -> bytes:
    assert len(ciphertext) % 16 == 0
    out = bytearray()
    prev = iv
    for i in range(0, len(ciphertext), 16):
        block = ciphertext[i : i + 16]
        dec = aes_decrypt_block(key, block)
        out += bytes(a ^ b for a, b in zip(dec, prev))
        prev = block
    if unpad:
        pad_len = out[-1]
        out = out[:-pad_len]
    return bytes(out)


# =============================================================================
# RC4 (ISO 32000-1 Algorithm 1 references this well-known stream cipher).
# =============================================================================


def rc4(key: bytes, data: bytes) -> bytes:
    s = list(range(256))
    j = 0
    klen = len(key)
    for i in range(256):
        j = (j + s[i] + key[i % klen]) % 256
        s[i], s[j] = s[j], s[i]
    out = bytearray(len(data))
    i = j = 0
    for n, byte in enumerate(data):
        i = (i + 1) % 256
        j = (j + s[i]) % 256
        s[i], s[j] = s[j], s[i]
        out[n] = byte ^ s[(s[i] + s[j]) % 256]
    return bytes(out)


assert rc4(b"Key", b"Plaintext") == bytes.fromhex("bbf316e8d940af0ad3"), "RC4 self-check failed"

# The 32-byte standard security handler padding string, ISO 32000-1 §7.6.3.3
# ("Algorithm 2: Computing an encryption key") — a fixed public constant
# defined by the spec, not derived from any implementation.
PAD = bytes(
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ]
)
assert len(PAD) == 32


# =============================================================================
# Standard Security Handler algorithms — ISO 32000-1 §7.6.3 (RC4/AES-128,
# revisions 2-4) and ISO 32000-2 §7.6.4 (AES-256, revision 6). Implemented
# directly from the published algorithms (a source the clean-room policy allows), only
# to *generate* self-consistent encrypted fixtures — this is independent of,
# and not a preview of, the real StandardSecurityHandler.cs.
# =============================================================================


def pad_password(pw: bytes) -> bytes:
    if len(pw) >= 32:
        return pw[:32]
    return pw + PAD[: 32 - len(pw)]


def compute_owner_entry(owner_pw: bytes, user_pw: bytes, revision: int, key_len: int) -> bytes:
    """Algorithm 3 (ISO 32000-1 §7.6.3.4): the /O entry."""
    h = hashlib.md5(pad_password(owner_pw or user_pw)).digest()
    if revision >= 3:
        for _ in range(50):
            h = hashlib.md5(h).digest()
    rc4_key = h[:key_len]
    ciphertext = rc4(rc4_key, pad_password(user_pw))
    if revision >= 3:
        for i in range(1, 20):
            round_key = bytes(b ^ i for b in rc4_key)
            ciphertext = rc4(round_key, ciphertext)
    return ciphertext


def compute_file_key(
    user_pw: bytes, owner_entry: bytes, p: int, id0: bytes, key_len: int, revision: int,
    encrypt_metadata: bool = True,
) -> bytes:
    """Algorithm 2 (ISO 32000-1 §7.6.3.3): the file encryption key."""
    ctx = hashlib.md5()
    ctx.update(pad_password(user_pw))
    ctx.update(owner_entry)
    ctx.update(struct.pack("<i", p))
    ctx.update(id0)
    if revision >= 4 and not encrypt_metadata:
        ctx.update(b"\xff\xff\xff\xff")
    h = ctx.digest()
    if revision >= 3:
        for _ in range(50):
            h = hashlib.md5(h[:key_len]).digest()
    return h[:key_len]


def compute_user_entry(file_key: bytes, id0: bytes, revision: int) -> bytes:
    """Algorithm 4 (revision 2) / Algorithm 5 (revision >= 3): the /U entry."""
    if revision == 2:
        return rc4(file_key, PAD)
    h = hashlib.md5(PAD + id0).digest()
    ciphertext = rc4(file_key, h)
    for i in range(1, 20):
        round_key = bytes(b ^ i for b in file_key)
        ciphertext = rc4(round_key, ciphertext)
    return ciphertext + bytes(16)  # trailing 16 bytes are arbitrary per spec


def object_key(file_key: bytes, obj_num: int, gen: int, aes: bool = False) -> bytes:
    """Algorithm 1 (ISO 32000-1 §7.6.2): per-object key for V1/V2/V4 (RC4 and AESV2)."""
    ctx = hashlib.md5()
    ctx.update(file_key)
    ctx.update(bytes([obj_num & 0xFF, (obj_num >> 8) & 0xFF, (obj_num >> 16) & 0xFF]))
    ctx.update(bytes([gen & 0xFF, (gen >> 8) & 0xFF]))
    if aes:
        ctx.update(bytes([0x73, 0x41, 0x6C, 0x54]))  # "sAlT", required for AESV2 per spec
    h = ctx.digest()
    n = min(len(file_key) + 5, 16)
    return h[:n]


def hardened_hash(password: bytes, salt: bytes, udata: bytes = b"") -> bytes:
    """Algorithm 2.B (ISO 32000-2 §7.6.4.3.4): the revision-6 hardened hash."""
    k = hashlib.sha256(password + salt + udata).digest()
    round_no = 0
    while True:
        k1 = (password + k + udata) * 64
        e = aes_cbc_encrypt(k[0:16], k[16:32], k1, pad=False)
        modulus = sum(e[0:16]) % 3
        if modulus == 0:
            k = hashlib.sha256(e).digest()
        elif modulus == 1:
            k = hashlib.sha384(e).digest()
        else:
            k = hashlib.sha512(e).digest()
        round_no += 1
        if round_no >= 64 and e[-1] <= round_no - 32:
            break
    return k[:32]


def compute_v5_entries(user_pw: bytes, owner_pw: bytes, file_key: bytes):
    """Algorithm 2.A (ISO 32000-2 §7.6.4.3.3): U/UE/O/OE for revision 6 (AES-256)."""
    val_salt_u = hashlib.sha256(b"plumepdf-fixture-user-validation-salt").digest()[:8]
    key_salt_u = hashlib.sha256(b"plumepdf-fixture-user-key-salt").digest()[:8]
    hash_u = hardened_hash(user_pw, val_salt_u, b"")
    u_entry = hash_u + val_salt_u + key_salt_u
    intermediate_u = hardened_hash(user_pw, key_salt_u, b"")
    ue_entry = aes_cbc_encrypt(intermediate_u, bytes(16), file_key, pad=False)

    val_salt_o = hashlib.sha256(b"plumepdf-fixture-owner-validation-salt").digest()[:8]
    key_salt_o = hashlib.sha256(b"plumepdf-fixture-owner-key-salt").digest()[:8]
    hash_o = hardened_hash(owner_pw, val_salt_o, u_entry)
    o_entry = hash_o + val_salt_o + key_salt_o
    intermediate_o = hardened_hash(owner_pw, key_salt_o, u_entry)
    oe_entry = aes_cbc_encrypt(intermediate_o, bytes(16), file_key, pad=False)

    return u_entry, ue_entry, o_entry, oe_entry


def deterministic_iv(label: bytes) -> bytes:
    """A fixed, reproducible 16-byte IV per fixture object (not secret; these
    are test fixtures, not a security boundary — reproducible generation
    matters more than IV secrecy here)."""
    return hashlib.sha256(label).digest()[:16]


def encrypt_aes(key: bytes, data: bytes, label: bytes) -> bytes:
    iv = deterministic_iv(label)
    return iv + aes_cbc_encrypt(key, iv, data, pad=True)


# =============================================================================
# Minimal PDF object/dictionary/xref serialization helpers.
# =============================================================================


def name(s: str) -> bytes:
    return ("/" + s).encode("latin1")


def ref(n: int, g: int = 0) -> bytes:
    return f"{n} {g} R".encode()


def num(x) -> bytes:
    return str(x).encode()


def arr(items: list[bytes]) -> bytes:
    return b"[" + b" ".join(items) + b"]"


def pdf_dict(entries: list[tuple[str, bytes]]) -> bytes:
    body = b" ".join(b"/" + k.encode("latin1") + b" " + v for k, v in entries)
    return b"<< " + body + b" >>"


def string_literal(s: bytes) -> bytes:
    escaped = s.replace(b"\\", b"\\\\").replace(b"(", b"\\(").replace(b")", b"\\)")
    return b"(" + escaped + b")"


def hex_string(s: bytes) -> bytes:
    return b"<" + s.hex().encode("ascii") + b">"


def indirect_obj(n: int, g: int, body: bytes) -> bytes:
    return f"{n} {g} obj\n".encode() + body + b"\nendobj\n"


def stream_obj(n: int, g: int, dict_entries: list[tuple[str, bytes]], data: bytes) -> bytes:
    d = pdf_dict(dict_entries + [("Length", num(len(data)))])
    return f"{n} {g} obj\n".encode() + d + b"\nstream\n" + data + b"\nendstream\nendobj\n"


HEADER = b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n"

CONTENT_STREAM = b"BT /F1 12 Tf 20 50 Td (Hello, PlumePDF fixture) Tj ET"

FONT_ENTRIES: list[tuple[str, bytes]] = [
    ("Type", name("Font")),
    ("Subtype", name("Type1")),
    ("BaseFont", name("Helvetica")),
]


def page_tree_objects(catalog: int, pages: int, page: int, font: int, content: int) -> dict[int, bytes]:
    """The 5 objects every fixture's minimal one-page document needs."""
    return {
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
                    ("MediaBox", arr([num(0), num(0), num(200), num(100)])),
                    ("Resources", pdf_dict([("Font", pdf_dict([("F1", ref(font))]))])),
                    ("Contents", ref(content)),
                ]
            ),
        ),
        font: indirect_obj(font, 0, pdf_dict(FONT_ENTRIES)),
        content: stream_obj(content, 0, [], CONTENT_STREAM),
    }


def layout(header: bytes, ordered: list[tuple[int, bytes]]) -> tuple[bytearray, dict[int, int]]:
    buf = bytearray(header)
    offsets: dict[int, int] = {}
    for n, body in ordered:
        offsets[n] = len(buf)
        buf += body
    return buf, offsets


def classic_xref_and_trailer(
    buf: bytearray, offsets: dict[int, int], size: int, trailer_entries: list[tuple[str, bytes]]
) -> bytes:
    xref_offset = len(buf)
    lines = [b"xref\n", f"0 {size}\n".encode(), b"0000000000 65535 f \n"]
    for n in range(1, size):
        off = offsets.get(n)
        lines.append((f"{off:010d} 00000 n \n" if off is not None else "0000000000 00000 f \n").encode())
    tail = (
        b"".join(lines)
        + b"trailer\n"
        + pdf_dict(trailer_entries)
        + f"\nstartxref\n{xref_offset}\n%%EOF".encode()
    )
    return bytes(buf) + tail


DOC_ID = hashlib.md5(b"plumepdf-fixture-id").digest()


def write(name_: str, data: bytes) -> None:
    path = HERE / name_
    path.write_bytes(data)
    print(f"wrote {name_} ({len(data)} bytes)")


# =============================================================================
# Fixture builders. Object numbers: 1=Catalog 2=Pages 3=Page 4=Font 5=Content
# unless a fixture needs a different shape.
# =============================================================================


def build_classic_xref() -> bytes:
    """A plain, well-formed classic cross-reference table (ISO 32000-1 §7.5.4)."""
    objs = page_tree_objects(catalog=1, pages=2, page=3, font=4, content=5)
    buf, offsets = layout(HEADER, [(n, objs[n]) for n in sorted(objs)])
    trailer = [("Size", num(6)), ("Root", ref(1)), ("ID", arr([hex_string(DOC_ID), hex_string(DOC_ID)]))]
    return classic_xref_and_trailer(buf, offsets, 6, trailer)


def build_symbol_fonts() -> bytes:
    """The non-embedded Standard-14 SYMBOL-font shapes, self-authored
    from ISO 32000-1 §9.6.2.2 / §9.6.6 / §12.5.6.19 / §12.7.4.2.3. Page text sets "Hello" in
    Helvetica (a sanity control), "abg" in /Symbol with NO /Widths, /Encoding or descriptor
    (α β γ — exercises the built-in-encoding rule AND the AFM-advance fix), and "4" in
    /ZapfDingbats stamped with /Encoding /WinAnsiEncoding (the producer shape whose named
    encoding must be ignored — a check mark, not "four"). One 18×18 checkbox widget at
    [150 40 168 58] carries the standard viewer appearance: its /Yes stream draws
    `/ZaDb 14 Tf (4) Tj` through a /ZapfDingbats resource, /AS /Yes, /Off empty; the catalog's
    /AcroForm lists it and its /DR font. simple-form.pdf cannot double for this: its /Yes
    appearance is a stroked rectangle (`re S`), no ZaDb glyph at all."""
    content = (
        b"BT /F1 12 Tf 10 80 Td (Hello) Tj ET\n"
        b"BT /F2 18 Tf 10 50 Td (abg) Tj ET\n"
        b"BT /F3 18 Tf 80 50 Td (4) Tj ET"
    )
    yes_appearance = b"q BT /ZaDb 14 Tf 3 3 Td (4) Tj ET Q"
    zadb_font = pdf_dict([("Type", name("Font")), ("Subtype", name("Type1")), ("BaseFont", name("ZapfDingbats"))])
    objs = {
        1: indirect_obj(
            1,
            0,
            pdf_dict(
                [
                    ("Type", name("Catalog")),
                    ("Pages", ref(2)),
                    (
                        "AcroForm",
                        pdf_dict(
                            [
                                ("Fields", arr([ref(9)])),
                                ("DR", pdf_dict([("Font", pdf_dict([("Helv", ref(4)), ("ZaDb", ref(8))]))])),  # /DA names /Helv; ISO 32000-1 §12.7.3.3 expects DA fonts in DR
                                ("DA", string_literal(b"/Helv 0 Tf 0 g")),
                            ]
                        ),
                    ),
                ]
            ),
        ),
        2: indirect_obj(2, 0, pdf_dict([("Type", name("Pages")), ("Kids", arr([ref(3)])), ("Count", num(1))])),
        3: indirect_obj(
            3,
            0,
            pdf_dict(
                [
                    ("Type", name("Page")),
                    ("Parent", ref(2)),
                    ("MediaBox", arr([num(0), num(0), num(200), num(100)])),
                    ("Resources", pdf_dict([("Font", pdf_dict([("F1", ref(4)), ("F2", ref(5)), ("F3", ref(6))]))])),
                    ("Contents", ref(7)),
                    ("Annots", arr([ref(9)])),
                ]
            ),
        ),
        4: indirect_obj(4, 0, pdf_dict(FONT_ENTRIES)),
        5: indirect_obj(5, 0, pdf_dict([("Type", name("Font")), ("Subtype", name("Type1")), ("BaseFont", name("Symbol"))])),
        6: indirect_obj(
            6,
            0,
            pdf_dict(
                [
                    ("Type", name("Font")),
                    ("Subtype", name("Type1")),
                    ("BaseFont", name("ZapfDingbats")),
                    ("Encoding", name("WinAnsiEncoding")),
                ]
            ),
        ),
        7: stream_obj(7, 0, [], content),
        8: indirect_obj(8, 0, zadb_font),
        9: indirect_obj(
            9,
            0,
            pdf_dict(
                [
                    ("Type", name("Annot")),
                    ("Subtype", name("Widget")),
                    ("FT", name("Btn")),
                    ("T", string_literal(b"cb")),
                    ("Rect", arr([num(150), num(40), num(168), num(58)])),
                    ("F", num(4)),
                    ("P", ref(3)),
                    ("V", name("Yes")),
                    ("AS", name("Yes")),
                    ("AP", pdf_dict([("N", pdf_dict([("Yes", ref(10)), ("Off", ref(11))]))])),
                ]
            ),
        ),
        10: stream_obj(
            10,
            0,
            [
                ("Type", name("XObject")),
                ("Subtype", name("Form")),
                ("BBox", arr([num(0), num(0), num(18), num(18)])),
                ("Resources", pdf_dict([("Font", pdf_dict([("ZaDb", ref(8))]))])),
            ],
            yes_appearance,
        ),
        11: stream_obj(
            11,
            0,
            [("Type", name("XObject")), ("Subtype", name("Form")), ("BBox", arr([num(0), num(0), num(18), num(18)]))],
            b"",
        ),
    }
    buf, offsets = layout(HEADER, [(n, objs[n]) for n in sorted(objs)])
    trailer = [("Size", num(12)), ("Root", ref(1)), ("ID", arr([hex_string(DOC_ID), hex_string(DOC_ID)]))]
    return classic_xref_and_trailer(buf, offsets, 12, trailer)


def apply_png_up_predictor(data: bytes, columns: int) -> bytes:
    rows = [data[i : i + columns] for i in range(0, len(data), columns)]
    out = bytearray()
    prev = bytes(columns)
    for row in rows:
        out.append(2)  # PNG filter type 2 = "Up"
        for i in range(columns):
            out.append((row[i] - prev[i]) & 0xFF)
        prev = row
    return bytes(out)


def xref_stream_records(entries: list[tuple[int, int, int]], w=(1, 4, 1)) -> bytes:
    out = bytearray()
    for t, f2, f3 in entries:
        out += t.to_bytes(w[0], "big") + f2.to_bytes(w[1], "big") + f3.to_bytes(w[2], "big")
    return bytes(out)


def build_xref_stream() -> bytes:
    """A PDF 1.5+ cross-reference stream (ISO 32000-1 §7.5.8), Flate-compressed
    with a PNG "Up" predictor (§7.4.4.4) — the shape essentially every modern
    PDF producer emits, and the shape that makes Filters a dependency of
    reading Objects at all."""
    objs = page_tree_objects(catalog=1, pages=2, page=3, font=4, content=5)
    ordered = [(n, objs[n]) for n in sorted(objs)]
    buf, offsets = layout(HEADER, ordered)

    xref_obj_num = 6
    size = 7
    w = (1, 4, 1)
    records = []
    for n in range(size):
        if n == 0:
            records.append((0, 0, 0))
        elif n == xref_obj_num:
            records.append((1, len(buf), 0))  # self-reference; offset known before serializing
        else:
            records.append((1, offsets[n], 0))
    raw = xref_stream_records(records, w)
    columns = sum(w)
    filtered = apply_png_up_predictor(raw, columns)
    compressed = zlib.compress(filtered, 9)

    xref_dict = [
        ("Type", name("XRef")),
        ("Size", num(size)),
        ("W", arr([num(x) for x in w])),
        ("Root", ref(1)),
        ("ID", arr([hex_string(DOC_ID), hex_string(DOC_ID)])),
        ("Filter", name("FlateDecode")),
        ("DecodeParms", pdf_dict([("Predictor", num(12)), ("Columns", num(columns))])),
    ]
    xref_obj = stream_obj(xref_obj_num, 0, xref_dict, compressed)
    buf += xref_obj

    tail = f"\nstartxref\n{len(buf) - len(xref_obj)}\n%%EOF".encode()
    return bytes(buf) + tail


def build_object_stream() -> bytes:
    """Objects packed into a compressed /ObjStm (ISO 32000-1 §7.5.7), indexed
    by a compressed cross-reference stream with type-2 entries."""
    # Object 1 (Catalog), 2 (Pages), 4 (Font) live compressed inside the
    # object stream; object 3 (Page) and 5 (content stream) stay uncompressed
    # (streams themselves cannot be stored inside an object stream, and this
    # exercises the "mixed" resolution path).
    catalog_body = pdf_dict([("Type", name("Catalog")), ("Pages", ref(2))])
    pages_body = pdf_dict([("Type", name("Pages")), ("Kids", arr([ref(3)])), ("Count", num(1))])
    font_body = pdf_dict(FONT_ENTRIES)

    compressed_members = [(1, catalog_body), (2, pages_body), (4, font_body)]
    header_parts = []
    data_parts = []
    running_offset = 0
    for n, body in compressed_members:
        header_parts.append(f"{n} {running_offset}".encode())
        data_parts.append(body)
        running_offset += len(body) + 1  # +1 for the separating space below
    objstm_header = b" ".join(header_parts)
    objstm_data = objstm_header + b" " + b" ".join(data_parts)
    first = len(objstm_header) + 1
    compressed_stream = zlib.compress(objstm_data, 9)

    objstm_num = 6
    page_body = pdf_dict(
        [
            ("Type", name("Page")),
            ("Parent", ref(2)),
            ("MediaBox", arr([num(0), num(0), num(200), num(100)])),
            ("Resources", pdf_dict([("Font", pdf_dict([("F1", ref(4))]))])),
            ("Contents", ref(5)),
        ]
    )
    page_obj = indirect_obj(3, 0, page_body)
    content_obj = stream_obj(5, 0, [], CONTENT_STREAM)
    objstm_obj = stream_obj(
        objstm_num,
        0,
        [
            ("Type", name("ObjStm")),
            ("N", num(len(compressed_members))),
            ("First", num(first)),
            ("Filter", name("FlateDecode")),
        ],
        compressed_stream,
    )

    ordered = [(3, page_obj), (5, content_obj), (objstm_num, objstm_obj)]
    buf, offsets = layout(HEADER, ordered)

    xref_obj_num = 7
    size = 8
    w = (1, 4, 2)
    records = []
    for n in range(size):
        if n == 0:
            records.append((0, 0, 0))
        elif n in (1, 2, 4):
            idx = [m[0] for m in compressed_members].index(n)
            records.append((2, objstm_num, idx))
        elif n == xref_obj_num:
            records.append((1, len(buf), 0))
        else:
            records.append((1, offsets[n], 0))
    raw = xref_stream_records(records, w)
    compressed_xref = zlib.compress(raw, 9)
    xref_dict = [
        ("Type", name("XRef")),
        ("Size", num(size)),
        ("W", arr([num(x) for x in w])),
        ("Root", ref(1)),
        ("ID", arr([hex_string(DOC_ID), hex_string(DOC_ID)])),
        ("Filter", name("FlateDecode")),
    ]
    xref_obj = stream_obj(xref_obj_num, 0, xref_dict, compressed_xref)
    buf += xref_obj
    tail = f"\nstartxref\n{len(buf) - len(xref_obj)}\n%%EOF".encode()
    return bytes(buf) + tail


def build_hybrid() -> bytes:
    """A hybrid-reference file (ISO 32000-1 §7.5.8.4): a classic xref table
    for most objects, plus a trailer /XRefStm pointing at a supplemental
    cross-reference stream that resolves one object (6) stored in a
    compressed object stream — lets PDF 1.4 readers ignore the XRefStm while
    PDF 1.5+ readers see every object."""
    objs = page_tree_objects(catalog=1, pages=2, page=3, font=5, content=7)
    # Object 6 (extra metadata dict, referenced from nowhere but present to
    # exercise the compressed-object path) lives inside an /ObjStm.
    extra_body = pdf_dict([("Type", name("Metadata_")), ("Note", string_literal(b"hybrid fixture"))])
    objstm_header = f"6 0".encode()
    objstm_data = objstm_header + b" " + extra_body
    first = len(objstm_header) + 1
    objstm_num = 8
    objstm_obj = stream_obj(
        objstm_num,
        0,
        [("Type", name("ObjStm")), ("N", num(1)), ("First", num(first)), ("Filter", name("FlateDecode"))],
        zlib.compress(objstm_data, 9),
    )

    ordered = [(n, objs[n]) for n in sorted(objs)] + [(objstm_num, objstm_obj)]
    buf, offsets = layout(HEADER, ordered)

    # Supplemental xref stream (covers only object 6, per §7.5.8.4 "may
    # contain only entries for the objects it needs to add").
    xrefstm_num = 9
    w = (1, 4, 2)
    records = [(2, objstm_num, 0)]
    raw = xref_stream_records(records, w)
    xrefstm_dict = [
        ("Type", name("XRef")),
        ("Size", num(10)),
        ("Index", arr([num(6), num(1)])),
        ("W", arr([num(x) for x in w])),
        ("Filter", name("FlateDecode")),
    ]
    xrefstm_offset = len(buf)
    xrefstm_obj = stream_obj(xrefstm_num, 0, xrefstm_dict, zlib.compress(raw, 9))
    buf += xrefstm_obj

    # Classic table covering objects 0-5,7-9 (skips 6, which only the hybrid
    # stream above resolves) plus a /Prev-less trailer with /XRefStm.
    classic_size = 10
    xref_offset = len(buf)
    lines = [b"xref\n"]
    for lo, hi in [(0, 6), (7, 3)]:
        lines.append(f"{lo} {hi}\n".encode())
        for n in range(lo, lo + hi):
            if n == 0:
                lines.append(b"0000000000 65535 f \n")
            else:
                off = offsets.get(n, xrefstm_offset if n == xrefstm_num else 0)
                lines.append(f"{off:010d} 00000 n \n".encode())
    trailer = [
        ("Size", num(classic_size)),
        ("Root", ref(1)),
        ("ID", arr([hex_string(DOC_ID), hex_string(DOC_ID)])),
        ("XRefStm", num(xrefstm_offset)),
    ]
    tail = b"".join(lines) + b"trailer\n" + pdf_dict(trailer) + f"\nstartxref\n{xref_offset}\n%%EOF".encode()
    return bytes(buf) + tail


def build_hybrid_freemarked() -> bytes:
    """The real-world shape of a hybrid-reference file (ISO 32000-1 §7.5.8.4)
    that build_hybrid()'s omit-the-object shortcut doesn't exercise: Acrobat
    and most other producers give every object number a classic-table entry,
    including the ones that only really live in the /XRefStm-referenced
    object stream — those get marked *free* in the classic table rather than
    omitted outright. A reader that lets the classic table's free entry win
    over /XRefStm's real one resolves object 6 to null instead of its
    dictionary. See CrossReferenceTests for the regression this guards."""
    objs = page_tree_objects(catalog=1, pages=2, page=3, font=5, content=7)
    extra_body = pdf_dict([("Type", name("Metadata_")), ("Note", string_literal(b"hybrid fixture"))])
    objstm_header = f"6 0".encode()
    objstm_data = objstm_header + b" " + extra_body
    first = len(objstm_header) + 1
    objstm_num = 8
    objstm_obj = stream_obj(
        objstm_num,
        0,
        [("Type", name("ObjStm")), ("N", num(1)), ("First", num(first)), ("Filter", name("FlateDecode"))],
        zlib.compress(objstm_data, 9),
    )

    ordered = [(n, objs[n]) for n in sorted(objs)] + [(objstm_num, objstm_obj)]
    buf, offsets = layout(HEADER, ordered)

    xrefstm_num = 9
    w = (1, 4, 2)
    records = [(2, objstm_num, 0)]
    raw = xref_stream_records(records, w)
    xrefstm_dict = [
        ("Type", name("XRef")),
        ("Size", num(10)),
        ("Index", arr([num(6), num(1)])),
        ("W", arr([num(x) for x in w])),
        ("Filter", name("FlateDecode")),
    ]
    xrefstm_offset = len(buf)
    xrefstm_obj = stream_obj(xrefstm_num, 0, xrefstm_dict, zlib.compress(raw, 9))
    buf += xrefstm_obj

    # Classic table covering EVERY object 0-9, including 6 — marked free, the
    # shape Acrobat produces — plus a /Prev-less trailer with /XRefStm.
    classic_size = 10
    xref_offset = len(buf)
    lines = [b"xref\n", f"0 {classic_size}\n".encode()]
    for n in range(classic_size):
        if n in (0, 6):
            lines.append(b"0000000000 65535 f \n" if n == 0 else b"0000000000 00000 f \n")
        else:
            off = offsets.get(n, xrefstm_offset if n == xrefstm_num else 0)
            lines.append(f"{off:010d} 00000 n \n".encode())
    trailer = [
        ("Size", num(classic_size)),
        ("Root", ref(1)),
        ("ID", arr([hex_string(DOC_ID), hex_string(DOC_ID)])),
        ("XRefStm", num(xrefstm_offset)),
    ]
    tail = b"".join(lines) + b"trailer\n" + pdf_dict(trailer) + f"\nstartxref\n{xref_offset}\n%%EOF".encode()
    return bytes(buf) + tail


def build_linearized_ish() -> bytes:
    """Carries a first-object linearization parameter dictionary (ISO 32000-1
    Annex F) so header-recognition code has something to see, WITHOUT
    implementing true linearization's first-page xref section and hint
    stream (hence "-ish") — the file is a completely ordinary, valid,
    classic-xref document underneath."""
    objs = page_tree_objects(catalog=2, pages=3, page=4, font=5, content=6)
    l_placeholder = b"0" * 10  # patched to the real file size after layout, same width
    lin_dict = pdf_dict(
        [
            ("Linearized", num(1)),
            ("L", l_placeholder),
            ("H", arr([num(0), num(0)])),
            ("O", num(4)),
            ("E", num(0)),
            ("N", num(1)),
            ("T", num(0)),
        ]
    )
    lin_obj = indirect_obj(1, 0, lin_dict)
    ordered = [(1, lin_obj)] + [(n, objs[n]) for n in sorted(objs)]
    buf, offsets = layout(HEADER, ordered)
    trailer = [("Size", num(7)), ("Root", ref(2)), ("ID", arr([hex_string(DOC_ID), hex_string(DOC_ID)]))]
    full = classic_xref_and_trailer(buf, offsets, 7, trailer)
    final_size = str(len(full)).encode().rjust(10, b"0")
    return full.replace(l_placeholder, final_size, 1)


def build_broken_xref() -> bytes:
    """A structurally well-formed classic xref table whose offsets are all
    wrong (ISO §7.5.4 syntax intact; the numbers just lie) — the recovery
    ladder's "repairable deviation" rung: every object is still present and
    findable via a brute-force `N G obj` scan."""
    good = build_classic_xref()
    marker = b"\nxref\n0 6\n0000000000 65535 f \n"
    idx = good.index(marker)
    xref_start = idx + 1  # skip the leading \n
    trailer_idx = good.index(b"trailer", xref_start)
    # Every entry from object 1..5 gets a plausible-looking but wrong offset.
    broken_lines = [b"xref\n", b"0 6\n", b"0000000000 65535 f \n"]
    for _ in range(1, 6):
        broken_lines.append(b"0000000001 00000 n \n")
    broken_section = b"".join(broken_lines)
    return good[:xref_start] + broken_section + good[trailer_idx:]


def build_truncated() -> bytes:
    """A file cut off mid-write: every object is intact, but the xref table,
    trailer, startxref and %%EOF are all missing entirely (a common shape
    for an interrupted upload/download)."""
    good = build_classic_xref()
    cut_at = good.index(b"\nxref\n")
    return good[:cut_at] + b"\n"


def build_encrypted(
    filename: str, *, revision: int, key_len: int, v: int, cf_name: str | None, aes: bool
) -> None:
    """Shared builder for the RC4/AES fixtures: same 5-object document,
    encrypted per the Standard Security Handler with an empty user AND
    empty owner password (a completely ordinary "encrypted but not
    password-protected" PDF — the common case for permission-only
    encryption)."""
    user_pw = b""
    owner_pw = b""
    p = -4  # conventional "all permissions granted" 32-bit value

    if v == 5:
        file_key = hashlib.sha256(f"plumepdf-fixture-filekey-{filename}".encode()).digest()
        u_entry, ue_entry, o_entry, oe_entry = compute_v5_entries(user_pw, owner_pw, file_key)
        encrypt_dict = [
            ("Filter", name("Standard")),
            ("V", num(5)),
            ("R", num(6)),
            ("Length", num(256)),
            ("CF", pdf_dict([("StdCF", pdf_dict([("CFM", name("AESV3")), ("AuthEvent", name("DocOpen")), ("Length", num(32))]))])),
            ("StmF", name("StdCF")),
            ("StrF", name("StdCF")),
            ("O", hex_string(o_entry)),
            ("U", hex_string(u_entry)),
            ("OE", hex_string(oe_entry)),
            ("UE", hex_string(ue_entry)),
            ("P", num(p)),
        ]

        def enc(data: bytes, objnum: int, gen: int) -> bytes:
            return encrypt_aes(file_key, data, f"{filename}:{objnum}:{gen}".encode())

    else:
        o_entry = compute_owner_entry(owner_pw, user_pw, revision, key_len)
        file_key = compute_file_key(user_pw, o_entry, p, DOC_ID, key_len, revision)
        u_entry = compute_user_entry(file_key, DOC_ID, revision)
        encrypt_dict = [
            ("Filter", name("Standard")),
            ("V", num(v)),
            ("R", num(revision)),
            ("Length", num(key_len * 8)),
            ("O", hex_string(o_entry)),
            ("U", hex_string(u_entry)),
            ("P", num(p)),
        ]
        if cf_name is not None:
            encrypt_dict[3:3] = [
                ("CF", pdf_dict([("StdCF", pdf_dict([("CFM", name(cf_name)), ("AuthEvent", name("DocOpen")), ("Length", num(key_len))]))])),
                ("StmF", name("StdCF")),
                ("StrF", name("StdCF")),
            ]

        def enc(data: bytes, objnum: int, gen: int) -> bytes:
            key = object_key(file_key, objnum, gen, aes=aes)
            return encrypt_aes(key, data, f"{filename}:{objnum}:{gen}".encode()) if aes else rc4(key, data)

    catalog_body = pdf_dict([("Type", name("Catalog")), ("Pages", ref(2))])
    pages_body = pdf_dict([("Type", name("Pages")), ("Kids", arr([ref(3)])), ("Count", num(1))])
    page_body = pdf_dict(
        [
            ("Type", name("Page")),
            ("Parent", ref(2)),
            ("MediaBox", arr([num(0), num(0), num(200), num(100)])),
            ("Resources", pdf_dict([("Font", pdf_dict([("F1", ref(4))]))])),
            ("Contents", ref(5)),
        ]
    )
    font_body = pdf_dict(FONT_ENTRIES)
    content_encrypted = enc(CONTENT_STREAM, 5, 0)

    objs = {
        1: indirect_obj(1, 0, catalog_body),
        2: indirect_obj(2, 0, pages_body),
        3: indirect_obj(3, 0, page_body),
        4: indirect_obj(4, 0, font_body),
        5: stream_obj(5, 0, [], content_encrypted),
        6: indirect_obj(6, 0, pdf_dict(encrypt_dict)),
    }
    buf, offsets = layout(HEADER, [(n, objs[n]) for n in sorted(objs)])
    trailer = [
        ("Size", num(7)),
        ("Root", ref(1)),
        ("Encrypt", ref(6)),
        ("ID", arr([hex_string(DOC_ID), hex_string(DOC_ID)])),
    ]
    write(filename, classic_xref_and_trailer(buf, offsets, 7, trailer))


def main() -> None:
    write("classic-xref.pdf", build_classic_xref())
    write("xref-stream.pdf", build_xref_stream())
    write("object-stream.pdf", build_object_stream())
    write("hybrid.pdf", build_hybrid())
    write("hybrid-freemarked.pdf", build_hybrid_freemarked())
    write("linearized-ish.pdf", build_linearized_ish())
    write("broken-xref.pdf", build_broken_xref())
    write("truncated.pdf", build_truncated())
    write("symbol-fonts.pdf", build_symbol_fonts())

    build_encrypted("RC4-40.pdf", revision=2, key_len=5, v=1, cf_name=None, aes=False)
    build_encrypted("RC4-128.pdf", revision=3, key_len=16, v=2, cf_name=None, aes=False)
    build_encrypted("AES-128.pdf", revision=4, key_len=16, v=4, cf_name="AESV2", aes=True)
    build_encrypted("AES-256.pdf", revision=6, key_len=32, v=5, cf_name=None, aes=True)


if __name__ == "__main__":
    main()
