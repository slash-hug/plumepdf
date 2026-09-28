# PLUME8004 — table offset/length runs past the end of the file, or a fixed-layout table is truncated

**Cause:** Either (a) a table directory entry's declared `offset + length` extends past the actual end of the font file, or (b) a fixed-layout table (`head`, `hhea`, `maxp`, `OS/2`, `post`, `loca`) is shorter than the minimum byte length its format requires.

**Example:** A hostile font whose directory entry claims a table extends 10 MiB past a 200 KiB file; a font truncated mid-download that cuts a table off partway through its header.

**Fix:** Re-fetch or re-export the font file from a trusted source; a font that fails this check is not a well-formed SFNT file regardless of how it was produced.

**Recovery attempted:** None — an out-of-bounds table offset makes every byte PlumePDF might otherwise read from it untrustworthy.
