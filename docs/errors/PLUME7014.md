# PLUME7014 — 'Q' with no matching 'q' to restore (diagnostic)

**Cause:** A content stream's `Q` operator was encountered with nothing on the graphics-state
stack to restore — a graphics-state stack underflow. Unlike the write side's `ContentStreamBuilder`
(which throws `PLUME7001` for the same shape of error, since a document PlumePDF itself is
generating should never be malformed), the read side tolerates this under lenient reading:
a document PlumePDF did not produce is untrusted input, and a stray `Q` is a
recoverable deviation, not a reason to abandon extraction of the rest of the page.

**Example:** A content stream with an extra `Q` left over from a producer bug, e.g. `q ... Q Q`.

**Fix:** None required — the stray `Q` is ignored (the current transformation matrix is left
unchanged) and extraction continues.

**Recovery attempted:** The `Q` is treated as a no-op; extraction continues with the CTM (and
text-state parameters) it had before the stray `Q`.
