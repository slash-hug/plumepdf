#!/usr/bin/env python3
"""Independent re-parse of every fixture's cross-reference data — and, for
the encrypted fixtures, a full key-derivation + decrypt round-trip — kept
alongside generate_fixtures.py so fixture correctness can be re-proven any
time these files (or the algorithms above) change. Run with:
    python3 validate_fixtures.py
Exits non-zero if any check fails."""
import base64
import re
import sys
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import generate_fixtures as gf
import generate_image_fixtures as gif

HERE = Path(__file__).parent


def check(label, cond):
    status = "OK " if cond else "FAIL"
    print(f"[{status}] {label}")
    if not cond:
        global failures
        failures += 1


failures = 0


def read(name):
    return (HERE / name).read_bytes()


# --- classic-xref.pdf -------------------------------------------------------
data = read("classic-xref.pdf")
m = re.search(rb"startxref\s+(\d+)", data)
xref_off = int(m.group(1))
check("classic-xref: startxref points at 'xref'", data[xref_off : xref_off + 4] == b"xref")
lines = data[xref_off:].split(b"\n")
# lines[0]=xref lines[1]="0 6" lines[2..7]=entries
for n in range(1, 6):
    entry = lines[2 + n]
    off = int(entry[:10])
    check(f"classic-xref: object {n} offset resolves to '{n} 0 obj'", data[off : off + len(f"{n} 0 obj")] == f"{n} 0 obj".encode())

# --- xref-stream.pdf ---------------------------------------------------------
data = read("xref-stream.pdf")
m = re.search(rb"startxref\s+(\d+)", data)
xref_off = int(m.group(1))
obj_hdr = re.match(rb"(\d+) 0 obj\s*<<(.*?)>>\s*stream\r?\n", data[xref_off:], re.S)
check("xref-stream: xref object found at startxref offset", obj_hdr is not None)
dict_bytes = obj_hdr.group(2)
stream_start = xref_off + obj_hdr.end()
length_m = re.search(rb"/Length (\d+)", dict_bytes)
length = int(length_m.group(1))
compressed = data[stream_start : stream_start + length]
raw = zlib.decompress(compressed)
columns = 6  # W = [1 4 1]
rows = []
prev = bytes(columns)
for i in range(0, len(raw), columns + 1):
    filtered_row = raw[i + 1 : i + 1 + columns]
    row = bytes((filtered_row[j] + prev[j]) & 0xFF for j in range(columns))
    rows.append(row)
    prev = row
check("xref-stream: 7 records decoded", len(rows) == 7)
for n in range(1, 6):
    t, off = rows[n][0], int.from_bytes(rows[n][1:5], "big")
    check(f"xref-stream: object {n} type=1 and offset resolves", t == 1 and data[off : off + len(f"{n} 0 obj")] == f"{n} 0 obj".encode())
t6, off6 = rows[6][0], int.from_bytes(rows[6][1:5], "big")
check("xref-stream: xref object self-reference offset correct", t6 == 1 and off6 == xref_off)

# --- object-stream.pdf --------------------------------------------------------
data = read("object-stream.pdf")
m = re.search(rb"6 0 obj\s*<<(.*?)>>\s*stream\r?\n", data, re.S)
check("object-stream: objstm object 6 found", m is not None)
objstm_dict = m.group(1)
first_m = re.search(rb"/First (\d+)", objstm_dict)
n_m = re.search(rb"/N (\d+)", objstm_dict)
length_m = re.search(rb"/Length (\d+)", objstm_dict)
start = data.index(b"stream\n", m.start()) + len(b"stream\n")
compressed = data[start : start + int(length_m.group(1))]
raw = zlib.decompress(compressed)
first = int(first_m.group(1))
header = raw[:first].split()
check("object-stream: header lists objects 1,2,4", header[0::2] == [b"1", b"2", b"4"])
body = raw[first:]
check("object-stream: contains /Type/Catalog", b"/Type/Catalog" in body or b"/Type /Catalog" in body)
check("object-stream: contains /Type/Font", b"/Type/Font" in body or b"/Type /Font" in body)

# --- hybrid.pdf ----------------------------------------------------------------
data = read("hybrid.pdf")
m = re.search(rb"/XRefStm (\d+)", data)
check("hybrid: trailer carries /XRefStm", m is not None)
xrefstm_off = int(m.group(1))
check("hybrid: /XRefStm offset resolves to an xref stream object", b"/Type/XRef" in data[xrefstm_off : xrefstm_off + 300] or b"/Type /XRef" in data[xrefstm_off : xrefstm_off + 300])

# --- broken-xref.pdf -------------------------------------------------------
data = read("broken-xref.pdf")
good = gf.build_classic_xref()
present = all(re.search(rf"{n} 0 obj".encode(), data) for n in range(1, 6))
check("broken-xref: all 5 'N 0 obj' markers still present despite bad offsets", present)
m = re.search(rb"0000000001 00000 n ", data)
check("broken-xref: offsets are the deliberately-wrong value", m is not None)

# --- truncated.pdf -----------------------------------------------------------
data = read("truncated.pdf")
check("truncated: no 'xref' keyword present", b"xref" not in data)
check("truncated: no '%%EOF' present", b"%%EOF" not in data)
check("truncated: all 5 objects still present", all(re.search(rf"{n} 0 obj".encode(), data) for n in range(1, 6)))

# --- linearized-ish.pdf ------------------------------------------------------
data = read("linearized-ish.pdf")
check("linearized-ish: /Linearized 1 present as object 1", re.search(rb"1 0 obj\s*<<[^>]*/Linearized 1", data) is not None)
m = re.search(rb"/L (\d+)", data)
check("linearized-ish: /L patched to a plausible (non-zero, <= filesize) value", m is not None and 0 < int(m.group(1)) <= len(data))
check("linearized-ish: still opens as an ordinary classic-xref PDF (has trailer/startxref)", b"trailer" in data and b"startxref" in data)


# --- encrypted fixtures: re-derive keys and decrypt --------------------------
def check_rc4(filename, revision, key_len):
    data = read(filename)
    m = re.search(rb"/O <([0-9a-fA-F]+)>", data)
    o_hex = m.group(1)
    o_entry = bytes.fromhex(o_hex.decode())
    m = re.search(rb"/U <([0-9a-fA-F]+)>", data)
    u_entry_file = bytes.fromhex(m.group(1).decode())
    m = re.search(rb"/P (-?\d+)", data)
    p = int(m.group(1))
    o_expected = gf.compute_owner_entry(b"", b"", revision, key_len)
    check(f"{filename}: /O entry matches recomputation", o_entry == o_expected)
    file_key = gf.compute_file_key(b"", o_entry, p, gf.DOC_ID, key_len, revision)
    u_expected = gf.compute_user_entry(file_key, gf.DOC_ID, revision)
    check(f"{filename}: /U entry matches recomputation", u_entry_file == u_expected)
    # decrypt object 5 (content stream)
    m = re.search(rb"5 0 obj\s*<<(.*?)>>\s*stream\r?\n", data, re.S)
    length = int(re.search(rb"/Length (\d+)", m.group(1)).group(1))
    start = data.index(b"stream\n", m.start()) + len(b"stream\n")
    ciphertext = data[start : start + length]
    key = gf.object_key(file_key, 5, 0, aes=False)
    plaintext = gf.rc4(key, ciphertext)
    check(f"{filename}: decrypted content stream matches original plaintext", plaintext == gf.CONTENT_STREAM)


check_rc4("RC4-40.pdf", 2, 5)
check_rc4("RC4-128.pdf", 3, 16)


def check_aes_v4(filename):
    data = read(filename)
    m = re.search(rb"/O <([0-9a-fA-F]+)>", data)
    o_entry = bytes.fromhex(m.group(1).decode())
    m = re.search(rb"/P (-?\d+)", data)
    p = int(m.group(1))
    file_key = gf.compute_file_key(b"", o_entry, p, gf.DOC_ID, 16, 4)
    m = re.search(rb"5 0 obj\s*<<(.*?)>>\s*stream\r?\n", data, re.S)
    length = int(re.search(rb"/Length (\d+)", m.group(1)).group(1))
    start = data.index(b"stream\n", m.start()) + len(b"stream\n")
    ciphertext = data[start : start + length]
    iv, body = ciphertext[:16], ciphertext[16:]
    key = gf.object_key(file_key, 5, 0, aes=True)
    expected_iv = gf.deterministic_iv(f"{filename}:5:0".encode())
    check(f"{filename}: IV matches deterministic derivation", iv == expected_iv)
    plaintext = gf.aes_cbc_decrypt(key, iv, body, unpad=True)
    check(f"{filename}: AES-CBC decrypt of content stream matches original plaintext", plaintext == gf.CONTENT_STREAM)


check_aes_v4("AES-128.pdf")


def check_aes_v5(filename):
    data = read(filename)
    m = re.search(rb"/U <([0-9a-fA-F]+)>", data)
    u_entry = bytes.fromhex(m.group(1).decode())
    m = re.search(rb"/UE <([0-9a-fA-F]+)>", data)
    ue_entry = bytes.fromhex(m.group(1).decode())
    val_salt_u, key_salt_u = u_entry[32:40], u_entry[40:48]
    hash_check = gf.hardened_hash(b"", val_salt_u, b"")
    check(f"{filename}: /U validation hash matches empty-password recomputation", hash_check == u_entry[:32])
    intermediate_u = gf.hardened_hash(b"", key_salt_u, b"")
    # Recover the file key from /UE: AES-256-CBC-no-padding, iv=0.
    file_key = gf.aes_cbc_decrypt(intermediate_u, bytes(16), ue_entry, unpad=False)
    check(f"{filename}: file key recovered from /UE is 32 bytes", len(file_key) == 32)

    m = re.search(rb"/O <([0-9a-fA-F]+)>", data)
    o_entry = bytes.fromhex(m.group(1).decode())
    val_salt_o, key_salt_o = o_entry[32:40], o_entry[40:48]
    hash_o_check = gf.hardened_hash(b"", val_salt_o, u_entry)
    check(f"{filename}: /O validation hash matches empty-password recomputation", hash_o_check == o_entry[:32])

    m = re.search(rb"5 0 obj\s*<<(.*?)>>\s*stream\r?\n", data, re.S)
    length = int(re.search(rb"/Length (\d+)", m.group(1)).group(1))
    start = data.index(b"stream\n", m.start()) + len(b"stream\n")
    ciphertext = data[start : start + length]
    iv, body = ciphertext[:16], ciphertext[16:]
    plaintext = gf.aes_cbc_decrypt(file_key, iv, body, unpad=True)
    check(f"{filename}: AES-256 decrypt of content stream (file key from /UE) matches original plaintext", plaintext == gf.CONTENT_STREAM)


check_aes_v5("AES-256.pdf")

# --- symbol-fonts.pdf --------------------------------------------------------
data = read("symbol-fonts.pdf")
check("symbol-fonts: classic xref resolves", data[int(re.search(rb"startxref\s+(\d+)", data).group(1)):].startswith(b"xref"))
check("symbol-fonts: 12 objects in the xref", b"xref\n0 12\n" in data)
check("symbol-fonts: /Symbol font has no /Widths, /Encoding or descriptor", re.search(rb"5 0 obj\s*<<\s*/Type /Font\s*/Subtype /Type1\s*/BaseFont /Symbol\s*>>", data) is not None)
check("symbol-fonts: /ZapfDingbats page font carries the producer-stamped /WinAnsiEncoding", re.search(rb"/BaseFont /ZapfDingbats\s*/Encoding /WinAnsiEncoding", data) is not None)
check("symbol-fonts: checkbox /Yes appearance draws the ZaDb check", b"/ZaDb 14 Tf 3 3 Td (4) Tj" in data)
check("symbol-fonts: widget is on (/V /Yes /AS /Yes) and listed in /AcroForm /Fields", b"/V /Yes" in data and b"/AS /Yes" in data and b"/Fields [ 9 0 R ]" in data.replace(b"[9 0 R]", b"[ 9 0 R ]"))

# --- images/jpx-*.pdf ---------------------------------------------------------
# Byte-level re-checks only (no PDF object model, matching this script's house style):
# dictionary keys via regex on the raw object bytes, payload identity via /Length-sliced
# stream data. JP2_SIGNATURE is ISO/IEC 15444-1 Annex I.5.1's first 8 signature-box bytes.
JP2_SIGNATURE = bytes.fromhex("0000000C6A502020")


def read_image(name):
    return (HERE / "images" / name).read_bytes()


def object_dict_and_stream(data: bytes, n: int):
    """Locates 'N 0 obj << ... >>\\nstream\\n<payload>' for object N and returns
    (dict_bytes, payload_bytes), slicing the payload by its own /Length so binary
    payload bytes that happen to look like 'endstream' can't desync the parse."""
    m = re.search(rf"{n} 0 obj\s*<<(.*?)>>\s*stream\r?\n".encode(), data, re.S)
    assert m is not None, f"object {n} not found"
    dict_bytes = m.group(1)
    length = int(re.search(rb"/Length (\d+)", dict_bytes).group(1))
    return dict_bytes, data[m.end() : m.end() + length]


jpx_small = read_image("jpx-small.pdf")
d, payload = object_dict_and_stream(jpx_small, 6)
check("jpx-small: /Filter /JPXDecode present", b"/Filter /JPXDecode" in d)
check(
    "jpx-small: /Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8",
    b"/Width 8" in d and b"/Height 8" in d and b"/ColorSpace /DeviceRGB" in d and b"/BitsPerComponent 8" in d,
)
check("jpx-small: payload starts with the JP2 signature", payload[:8] == JP2_SIGNATURE)
check("jpx-small: payload is exactly the JP2_RGB_8x8 literal", payload == gif.JP2_RGB_8x8)

jpx_garbage = read_image("jpx-garbage.pdf")
d, payload = object_dict_and_stream(jpx_garbage, 6)
check("jpx-garbage: /Filter /JPXDecode present", b"/Filter /JPXDecode" in d)
check("jpx-garbage: payload is 32 zero bytes (not a JP2 signature)", payload == b"\x00" * 32)

jpx_scan = read_image("jpx-scan.pdf")
d, payload = object_dict_and_stream(jpx_scan, 6)
check(
    "jpx-scan: /Width 640 /Height 480 /ColorSpace /DeviceRGB",
    b"/Width 640" in d and b"/Height 480" in d and b"/ColorSpace /DeviceRGB" in d,
)
check("jpx-scan: payload starts with the JP2 signature", payload[:8] == JP2_SIGNATURE)
check("jpx-scan: payload is exactly base64.b64decode(JP2_SCAN_B64)", payload == base64.b64decode(gif.JP2_SCAN_B64))
check("jpx-scan: payload size in the ~45-65 KB range documented for JP2_SCAN_B64", 45_000 <= len(payload) <= 65_000)

jpx_absent = read_image("jpx-smaskindata-absent.pdf")
d, payload = object_dict_and_stream(jpx_absent, 6)
check("jpx-smaskindata-absent: no /SMaskInData key", b"/SMaskInData" not in d)
check("jpx-smaskindata-absent: payload is JP2_RGBA_8x8", payload == gif.JP2_RGBA_8x8)

jpx_one = read_image("jpx-smaskindata-1.pdf")
d, payload_one = object_dict_and_stream(jpx_one, 6)
check("jpx-smaskindata-1: /SMaskInData 1", re.search(rb"/SMaskInData 1\b", d) is not None)
check("jpx-smaskindata-1: payload is JP2_RGBA_8x8", payload_one == gif.JP2_RGBA_8x8)

jpx_two = read_image("jpx-smaskindata-2.pdf")
d, payload_two = object_dict_and_stream(jpx_two, 6)
check("jpx-smaskindata-2: /SMaskInData 2", re.search(rb"/SMaskInData 2\b", d) is not None)
check("jpx-smaskindata-2: payload is JP2_RGBA_8x8_PREMUL", payload_two == gif.JP2_RGBA_8x8_PREMUL)
diffs = [i for i in range(min(len(payload_one), len(payload_two))) if payload_one[i] != payload_two[i]]
check(
    "jpx-smaskindata-2: its payload differs from -1's only at the documented cdef Typ byte (offset 108)",
    diffs == [108] and payload_one[108] == 1 and payload_two[108] == 2,
)

jpx_prec = read_image("jpx-smask-precedence.pdf")
d, payload = object_dict_and_stream(jpx_prec, 6)
check(
    "jpx-smask-precedence: /SMaskInData 1 AND /SMask 7 0 R both present",
    re.search(rb"/SMaskInData 1\b", d) is not None and re.search(rb"/SMask 7 0 R", d) is not None,
)
d7, payload7 = object_dict_and_stream(jpx_prec, 7)
check(
    "jpx-smask-precedence: /SMask target is a fully-opaque (255) 8x8 /DeviceGray stream",
    b"/ColorSpace /DeviceGray" in d7 and zlib.decompress(payload7) == b"\xff" * 64,
)

jpx_broken = read_image("jpx-smask-broken.pdf")
d, payload = object_dict_and_stream(jpx_broken, 6)
check(
    "jpx-smask-broken: /SMaskInData 1 AND /SMask 7 0 R both present",
    re.search(rb"/SMaskInData 1\b", d) is not None and re.search(rb"/SMask 7 0 R", d) is not None,
)
check(
    "jpx-smask-broken: /SMask target (object 7) is a bare integer, not a stream",
    re.search(rb"7 0 obj\s*0\s*endobj", jpx_broken) is not None and b"7 0 obj\n<<" not in jpx_broken,
)

jpx_pref = read_image("jpx-small-prefiltered.pdf")
m = re.search(rb"6 0 obj\s*<<(.*?)>>\s*stream\r?\n", jpx_pref, re.S)
d = m.group(1)
length = int(re.search(rb"/Length (\d+)", d).group(1))
ascii85_payload = jpx_pref[m.end() : m.end() + length]
check("jpx-small-prefiltered: /Filter [/ASCII85Decode /JPXDecode]", b"/Filter [/ASCII85Decode /JPXDecode]" in d)
check("jpx-small-prefiltered: ASCII85 payload has NO leading '<~' delimiter", not ascii85_payload.startswith(b"<~"))
check("jpx-small-prefiltered: ASCII85 payload ends with the '~>' EOD marker", ascii85_payload.endswith(b"~>"))
_a85_core = ascii85_payload[:-2] if ascii85_payload.endswith(b"~>") else ascii85_payload
check(
    "jpx-small-prefiltered: base64.a85decode(payload) recovers JP2_RGB_8x8 (independent re-decode)",
    base64.a85decode(_a85_core, adobe=False) == gif.JP2_RGB_8x8,
)

jpx_inline = read_image("jpx-inline-illegal.pdf")
check(
    "jpx-inline-illegal: content stream carries a BI ... /F /JPXDecode ... ID ... EI inline image",
    b"BI /W 8 /H 8 /CS /RGB /BPC 8 /F /JPXDecode ID " in jpx_inline and b" EI Q" in jpx_inline,
)
check("jpx-inline-illegal: inline image payload is JP2_RGB_8x8", gif.JP2_RGB_8x8 in jpx_inline)
check("jpx-inline-illegal: /Resources /XObject dictionary is empty (BI is the only image)", b"/XObject <<  >>" in jpx_inline)

print()
print("FAILURES:" if failures else "ALL CHECKS PASSED", failures if failures else "")
sys.exit(1 if failures else 0)
