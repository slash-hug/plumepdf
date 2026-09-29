using System.Globalization;
using System.Text;
using PlumePdf.IO;

namespace PlumePdf.Objects;

/// <summary>
/// Canonical <see cref="PdfObject"/>-to-bytes serialization (ISO 32000-1 §7.3), shared by
/// <see cref="IncrementalUpdateWriter"/> and <see cref="FullRewriteWriter"/>. Writes the
/// same lexical forms <see cref="PdfTokenizer"/>/<see cref="ObjectParser"/> can read back —
/// names and literal strings are escaped, numbers use <see cref="PdfNumber"/>'s own
/// invariant-culture formatting, and a stream's <c>/Length</c> is always recomputed from its
/// actual <see cref="PdfStream.RawBytes"/> rather than trusted from the source (which may
/// have been recovered via <c>endstream</c>-scanning and so may not match its declared
/// length).
/// </summary>
internal static class ObjectSerializer
{
    private static readonly byte[] Eol = "\n"u8.ToArray();

    /// <summary>
    /// Writes one indirect object's complete <c>N G obj ... endobj</c> framing to
    /// <paramref name="output"/>.
    /// </summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="number">The object number to write.</param>
    /// <param name="generation">The generation to write.</param>
    /// <param name="value">The object's value.</param>
    /// <param name="translateReference">
    /// Rewrites a <see cref="PdfReference"/> found inside <paramref name="value"/> to the
    /// <see cref="IndirectReference"/> that should actually be written — identity
    /// (<c>r =&gt; r.Target</c>) for the incremental writer (never renumbers); for the
    /// full-rewrite writer, a lookup keyed by the <em>reference instance itself</em> rather
    /// than by number alone, so a reference the writer constructs pointing at an
    /// already-final number (e.g. a fresh <c>/Pages</c> node's <c>/Kids</c> entries) can
    /// never be misread as an unrelated original object number that happens to collide with
    /// it numerically. Returning <see langword="null"/> writes the <c>null</c> object in the
    /// reference's place (the full-rewrite writers' excluded objects).
    /// </param>
    /// <param name="onPlaceholder">
    /// Invoked with the placeholder instance and the <paramref name="output"/> position at
    /// which its lexical form begins, for every <see cref="PdfContentsPlaceholder"/> or
    /// <see cref="PdfByteRangePlaceholder"/> encountered while writing <paramref name="value"/>
    /// — <see langword="null"/> for an ordinary write with no signing session
    /// involved.
    /// </param>
    public static void WriteIndirectObject(Stream output, int number, int generation, PdfObject value, Func<PdfReference, IndirectReference?>? translateReference = null, Action<PdfObject, long>? onPlaceholder = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(value);

        WriteAscii(output, number.ToString(CultureInfo.InvariantCulture));
        WriteAscii(output, " ");
        WriteAscii(output, generation.ToString(CultureInfo.InvariantCulture));
        WriteAscii(output, " obj\n");
        WriteValue(output, value, translateReference, onPlaceholder);
        WriteAscii(output, "\nendobj\n");
    }

    /// <summary>Writes a single value in its lexical PDF form.</summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="translateReference">See <see cref="WriteIndirectObject"/>'s parameter of the same name.</param>
    /// <param name="onPlaceholder">See <see cref="WriteIndirectObject"/>'s parameter of the same name.</param>
    public static void WriteValue(Stream output, PdfObject value, Func<PdfReference, IndirectReference?>? translateReference = null, Action<PdfObject, long>? onPlaceholder = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(value);
        translateReference ??= static r => r.Target;

        switch (value)
        {
            case PdfNull:
                WriteAscii(output, "null");
                break;

            case PdfBoolean boolean:
                WriteAscii(output, boolean.ToString());
                break;

            case PdfNumber number:
                WriteAscii(output, number.ToString());
                break;

            case PdfName name:
                WriteName(output, name);
                break;

            case PdfString str:
                WriteString(output, str);
                break;

            case PdfReference reference:
                {
                    // A null translation means the target is excluded from the output (a page
                    // removed before a full rewrite, and what belonged only to it): the
                    // reference is written as the null object and the target never follows.
                    if (translateReference(reference) is { } target)
                    {
                        WriteAscii(output, $"{target.Number} {target.Generation} R");
                    }
                    else
                    {
                        WriteAscii(output, "null");
                    }

                    break;
                }

            case PdfArray array:
                WriteArray(output, array, translateReference, onPlaceholder);
                break;

            case PdfStream stream:
                WriteStream(output, stream, translateReference, onPlaceholder);
                break;

            case PdfDictionary dict:
                WriteDictionary(output, dict, translateReference, onPlaceholder);
                break;

            case PdfContentsPlaceholder contents:
                WriteContentsPlaceholder(output, contents, onPlaceholder);
                break;

            case PdfByteRangePlaceholder byteRange:
                WriteByteRangePlaceholder(output, byteRange, onPlaceholder);
                break;

            default:
                throw new PlumePdfException("PLUME5010", $"The writer does not know how to serialize an object of type {value.GetType().Name}.");
        }
    }

    // Wire format: '<' + (ReservationBytes * 2) NUL-standing '0' digits + '>' — an
    // upper-hex string exactly as long as the real signature will be once
    // SigningWriteSession.PatchContents overwrites the digits in place. The
    // reported offset is the position of '<' itself, matching WriteByteRangePlaceholder's own
    // "position of the opening delimiter" convention below.
    private static void WriteContentsPlaceholder(Stream output, PdfContentsPlaceholder placeholder, Action<PdfObject, long>? onPlaceholder)
    {
        if (onPlaceholder is null)
        {
            // A null onPlaceholder means no SigningWriteSession is driving this write (an
            // ordinary Save/SaveIncremental call) — writing this placeholder's fake
            // "<0000...0000>" bytes here would produce a syntactically well-formed but
            // entirely fake /Contents value with no error at all. This only happens when a
            // signing pass failed partway through and left an unfinished
            // PdfContentsPlaceholder registered on the document (see SigningOrchestrator's
            // mutation rollback, which exists specifically to prevent that).
            throw new PlumePdfException("PLUME5015", "A PdfContentsPlaceholder reached the writer outside a SigningWriteSession pass. This is writer-internal plumbing that must never appear in an ordinary Save/SaveIncremental output — it means a prior signing call failed partway through and left the document with an unfinished, unregistered signature. Discard this PdfDocument and re-open/re-sign rather than saving it directly.");
        }

        onPlaceholder.Invoke(placeholder, output.Position);
        output.WriteByte((byte)'<');
        for (var i = 0; i < placeholder.ReservationBytes * 2; i++)
        {
            output.WriteByte((byte)'0');
        }

        output.WriteByte((byte)'>');
    }

    // Wire format: "[0" + 3x(" " + PadWidth zero digits) + "]" — the first entry is always
    // the literal integer 0 (ISO 32000-1 §12.8.1: the first byte range always starts at the
    // beginning of the file) and so is never itself a variable, patchable field; the other
    // three are zero-padded to a fixed width so SigningWriteSession can overwrite them with
    // real values later without shifting anything that comes after. The reported offset is
    // the position of '[', from which the three fields' offsets are computable purely from
    // PadWidth (SigningWriteSession's own responsibility — this method's job is only to
    // produce a stable, spec-legal lexical form, ISO 32000-1 §7.3.3 permitting an integer
    // token any number of leading zeros).
    private static void WriteByteRangePlaceholder(Stream output, PdfByteRangePlaceholder placeholder, Action<PdfObject, long>? onPlaceholder)
    {
        if (onPlaceholder is null)
        {
            // See WriteContentsPlaceholder's remark — same reasoning, for the /ByteRange twin.
            throw new PlumePdfException("PLUME5015", "A PdfByteRangePlaceholder reached the writer outside a SigningWriteSession pass. This is writer-internal plumbing that must never appear in an ordinary Save/SaveIncremental output — it means a prior signing call failed partway through and left the document with an unfinished, unregistered signature. Discard this PdfDocument and re-open/re-sign rather than saving it directly.");
        }

        onPlaceholder.Invoke(placeholder, output.Position);
        WriteAscii(output, "[0");
        for (var i = 0; i < 3; i++)
        {
            WriteAscii(output, " ");
            WriteAscii(output, new string('0', placeholder.PadWidth));
        }

        WriteAscii(output, "]");
    }

    private static void WriteArray(Stream output, PdfArray array, Func<PdfReference, IndirectReference?> translateReference, Action<PdfObject, long>? onPlaceholder = null)
    {
        WriteAscii(output, "[");
        for (var i = 0; i < array.Count; i++)
        {
            if (i > 0)
            {
                WriteAscii(output, " ");
            }

            WriteValue(output, array[i], translateReference, onPlaceholder);
        }

        WriteAscii(output, "]");
    }

    private static void WriteDictionary(Stream output, PdfDictionary dict, Func<PdfReference, IndirectReference?> translateReference, Action<PdfObject, long>? onPlaceholder = null)
    {
        WriteAscii(output, "<<");
        foreach (var (key, value) in dict)
        {
            WriteAscii(output, " ");
            WriteName(output, key);
            WriteAscii(output, " ");
            WriteValue(output, value, translateReference, onPlaceholder);
        }

        WriteAscii(output, " >>");
    }

    private static void WriteStream(Stream output, PdfStream stream, Func<PdfReference, IndirectReference?> translateReference, Action<PdfObject, long>? onPlaceholder = null)
    {
        var dict = new PdfDictionary();
        foreach (var (key, value) in stream.Dictionary)
        {
            if (!ReferenceEquals(key, PdfName.Length))
            {
                dict.Set(key, value);
            }
        }

        dict.Set(PdfName.Length, PdfNumber.Get(stream.RawBytes.Length));

        WriteDictionary(output, dict, translateReference, onPlaceholder);
        WriteAscii(output, "\nstream\n");
        output.Write(stream.RawBytes.Span);
        output.Write(Eol);
        WriteAscii(output, "endstream");
    }

    private static void WriteName(Stream output, PdfName name)
    {
        output.WriteByte((byte)'/');
        foreach (var b in Encoding.Latin1.GetBytes(name.Value))
        {
            if (IsRegularNameByte(b))
            {
                output.WriteByte(b);
            }
            else
            {
                WriteAscii(output, $"#{b:X2}");
            }
        }
    }

    private static bool IsRegularNameByte(byte b) =>
        PdfScanner.IsRegular(b) && b != (byte)'#' && b > 0x20 && b < 0x7F;

    private static void WriteString(Stream output, PdfString str)
    {
        if (str.IsHex)
        {
            output.WriteByte((byte)'<');
            foreach (var b in str.Bytes.Span)
            {
                WriteAscii(output, b.ToString("X2", CultureInfo.InvariantCulture));
            }

            output.WriteByte((byte)'>');
            return;
        }

        output.WriteByte((byte)'(');
        foreach (var b in str.Bytes.Span)
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    output.WriteByte((byte)'\\');
                    output.WriteByte(b);
                    break;

                // An unescaped CR (or CRLF) normalizes to LF on read back (§7.3.4.2), so a
                // source CR byte must be escaped or it silently turns into a different byte
                // on the next open->Save->reopen cycle. \n/\t/\b/\f use their short escapes
                // for readability; every other non-printable byte falls through to \ddd
                // (always exactly 3 octal digits, so a following literal digit 0-7 is never
                // misread as part of the same escape - PdfTokenizer.ReadLiteralString stops
                // an octal escape at 3 digits regardless).
                case (byte)'\r':
                    WriteAscii(output, "\\r");
                    break;

                case (byte)'\n':
                    WriteAscii(output, "\\n");
                    break;

                case (byte)'\t':
                    WriteAscii(output, "\\t");
                    break;

                case 0x08:
                    WriteAscii(output, "\\b");
                    break;

                case 0x0C:
                    WriteAscii(output, "\\f");
                    break;

                case < 0x20 or 0x7F:
                    WriteAscii(output, $"\\{Convert.ToString(b, 8).PadLeft(3, '0')}");
                    break;

                default:
                    output.WriteByte(b);
                    break;
            }
        }

        output.WriteByte((byte)')');
    }

    private static void WriteAscii(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));
}
