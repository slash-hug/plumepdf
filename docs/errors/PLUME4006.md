# PLUME4006 — failed to decrypt an object; still-encrypted value used (diagnostic)

**Cause:** `ObjectResolver` caught a `PLUME4xxx`-class exception (most commonly `PLUME4005`) while transparently decrypting a resolved object's strings/streams (§7.6.2) - rather than losing the whole object, the still-encrypted value is returned as-is and this diagnostic records what happened, so it's never silent.

**Example:** A single corrupted object inside an otherwise-correctly-authenticated encrypted document.

**Fix:** Inspect the specific object at the reported offset; every other object in the document still decrypts normally.

**Recovery attempted:** Returns the object with its strings/streams still encrypted (not the plaintext) rather than substituting `PdfNull` or failing the whole `Open` - a caller that reads that specific object's raw bytes will see ciphertext, clearly distinguishable from real content.
