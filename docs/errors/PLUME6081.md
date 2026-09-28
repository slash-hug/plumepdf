# PLUME6081 — XMP field contains a character XML cannot represent

**Cause:** a field of the `XmpPacket` handed to `PdfDocument.SetXmpMetadata` (or serialized
directly via `XmpWriter`) contains a character XML 1.0 cannot represent at all — a control
character other than tab/LF/CR (e.g. `U+0001`), or an unpaired surrogate. XMP packets are
XML; such a character cannot be written even in escaped form, so the packet is refused with
the offending field and character position named, instead of surfacing later as a bare
`ArgumentException` from the underlying `XmlWriter` (which would violate the coded-error
contract and not say which field carried the character).

**Example:**

```csharp
var packet = new XmpPacket { Title = "Q3Report" };
document.SetXmpMetadata(packet); // throws PLUME6081 naming XmpPacket.Title and U+0001
```

**Fix:** Remove or replace the offending character before setting the metadata — control
characters in a title/author/keywords string are almost always an upstream data bug (e.g. a
raw database field pasted through). `char.IsControl` filtering, or a targeted
`string.Replace`, both work:

```csharp
var clean = new string(raw.Where(static c => !char.IsControl(c)).ToArray());
```

**Recovery attempted:** None — silently dropping or substituting characters inside metadata
the caller explicitly supplied would be a silent behavior change PlumePDF does not make;
the write is refused instead.
