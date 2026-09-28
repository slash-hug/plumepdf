# PLUME3031 — ASCII85Decode: 'z' shortcut used mid-group (diagnostic)

**Cause:** A stream declared `/ASCII85Decode` (or `/A85`) and its payload used the `z`
shortcut (which stands for a whole 4-byte zero group) after one or more digits of the
current group had already been read. `z` is only valid as the first character of a group.

**Example:** A stream whose payload includes `!z...` — `z` appears after one digit (`!`)
of a group instead of at its start.

**Fix:** No action needed if this is an isolated stray byte from a non-conformant producer.

**Recovery attempted:** The `z` is ignored and digit scanning continues for the current
group; under `PdfOptions.Strict` this throws instead of tolerating it.
