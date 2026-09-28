# PLUME4005 — AES decryption failed

**Cause:** `Aes.CreateDecryptor().TransformFinalBlock` threw a `CryptographicException` - the ciphertext is corrupt, or the derived key is wrong (which itself usually means authentication actually failed in a way `PLUME4002`'s checks didn't catch, or the object's data was damaged after encryption).

**Example:** A per-object encrypted payload with corrupted ciphertext or padding.

**Fix:** Inspect the source object; if this happens for every object in an otherwise-authenticating document, the file may be corrupted post-encryption.

**Recovery attempted:** None for that object - decryption of that string/stream fails; caught by `ObjectResolver` and recorded as `PLUME4006` rather than failing the whole document.
