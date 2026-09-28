using System.Globalization;
using System.Text;
using System.Xml;

namespace PlumePdf.Documents.Metadata;

/// <summary>
/// Serializes an <see cref="XmpPacket"/> to its complete RDF/XML packet bytes (ISO 16684-1),
/// the shape PlumePDF writes into a document's <c>/Metadata</c> stream (ISO 32000-1 §14.3.2)
/// via <c>PdfDocument.SetXmpMetadata</c>. Built entirely on <see cref="System.Xml.XmlWriter"/>
/// — no XML serializer or reflection-based binding (the AOT-safe-XML precedent):
/// every element is written by an explicit call, the same discipline the writer layer already
/// applies to PDF syntax itself.
/// </summary>
public static class XmpWriter
{
    // ISO 16684-1 Annex C's standard xpacket wrapper — a byte-identical marker every XMP
    // consumer recognizes, not PlumePDF-specific syntax. The begin marker's "begin" attribute
    // value is the UTF-8 BOM character itself (codepoint 0xFEFF, built from its hex codepoint
    // via char.ConvertFromUtf32 rather than embedding the literal invisible character in this
    // source file, where it would be indistinguishable from whitespace under review) — how a
    // generic XMP consumer detects the packet's encoding before any XML parsing begins.
    private static readonly string XPacketBegin =
        "<?xpacket begin=\"" + char.ConvertFromUtf32(0xFEFF) + "\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>";

    private const string XPacketEnd = "<?xpacket end=\"w\"?>";

    private const string RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string XNs = "adobe:ns:meta/";
    private const string DcNs = "http://purl.org/dc/elements/1.1/";
    private const string XmpNs = "http://ns.adobe.com/xap/1.0/";
    private const string PdfNs = "http://ns.adobe.com/pdf/1.3/";
    private const string PdfaidNs = "http://www.aiim.org/pdfa/ns/id/";
    private const string PdfuaidNs = "http://www.aiim.org/pdfua/ns/id/";

    /// <summary>
    /// Serializes <paramref name="packet"/> to a complete XMP packet (UTF-8, with the
    /// standard xpacket begin/end processing instructions) and writes it to
    /// <paramref name="output"/>.
    /// </summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="packet">The metadata to serialize.</param>
    /// <param name="options">Options controlling the write, notably <see cref="PdfOptions.MaxXmpPacketWriteBytes"/> and <see cref="PdfOptions.Deterministic"/>.</param>
    /// <exception cref="PlumePdfException">
    /// The serialized packet would exceed <see cref="PdfOptions.MaxXmpPacketWriteBytes"/>
    /// (<c>PLUME6056</c>, enforced incrementally while serializing — an over-cap packet is
    /// refused at the byte that crosses the cap, never fully materialized first); a text field
    /// contains a character XML 1.0 cannot represent, such as a control character in
    /// <see cref="XmpPacket.Title"/> (<c>PLUME6081</c>, naming the offending field); or
    /// <paramref name="packet"/> declares a <see cref="XmpPacket.Conformance"/> under
    /// <see cref="PdfOptions.Deterministic"/> without both <see cref="XmpPacket.CreateDate"/>
    /// and <see cref="XmpPacket.ModifyDate"/> supplied (<c>PLUME6058</c>).
    /// </exception>
    /// <example>
    /// <code>
    /// using var stream = new MemoryStream();
    /// XmpWriter.Write(stream, new XmpPacket { Title = "Report" }, PdfOptions.Default);
    /// </code>
    /// </example>
    public static void Write(Stream output, XmpPacket packet, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Write(ToBytes(packet, options));
    }

    /// <summary>Serializes <paramref name="packet"/> to its complete XMP packet bytes (UTF-8), without writing to a stream.</summary>
    /// <exception cref="PlumePdfException">See <see cref="Write"/>'s exceptions — identical conditions.</exception>
    public static byte[] ToBytes(XmpPacket packet, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(options);

        // PdfOptions.Deterministic promises byte-identical output across runs
        // (docs/spec.md "public and permanent"), but PDF/A mandates xmp:CreateDate/ModifyDate
        // — a value that, left to PlumePDF to invent, would have to be either a fixed fake
        // timestamp (silently wrong metadata) or the current time (silently non-deterministic).
        // Neither is acceptable; refuse instead, exactly like signing time is refused under the same conditions.
        if (packet.Conformance != PdfAConformance.None && options.Deterministic && (packet.CreateDate is null || packet.ModifyDate is null))
        {
            throw new PlumePdfException(
                "PLUME6058",
                "PDF/A metadata (XmpPacket.Conformance != None) combined with PdfOptions.Deterministic requires both XmpPacket.CreateDate and XmpPacket.ModifyDate to be supplied explicitly — PlumePDF never invents a fixed or 'now' timestamp on the caller's behalf, since either would violate one of the two guarantees silently.");
        }

        // PLUME6081 before any serialization work: XmlWriter's CheckCharacters would reject a
        // control character too, but as a bare ArgumentException mid-write (violating the
        // coded-error contract) and without saying WHICH field carried it.
        ValidateXmlRepresentable(nameof(packet.Title), packet.Title);
        ValidateXmlRepresentable(nameof(packet.Creator), packet.Creator);
        ValidateXmlRepresentable(nameof(packet.Subject), packet.Subject);
        ValidateXmlRepresentable(nameof(packet.Keywords), packet.Keywords);
        ValidateXmlRepresentable(nameof(packet.Producer), packet.Producer);

        // PLUME6056 is enforced incrementally by the bounded buffer below — the write that
        // crosses the cap throws, so an over-cap packet (e.g. a pathologically large Keywords
        // string) is never fully materialized in memory first.
        using var buffer = new BoundedMemoryStream(options.MaxXmpPacketWriteBytes);
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.None,
            CheckCharacters = true,
        };

        using (var writer = XmlWriter.Create(buffer, settings))
        {
            writer.WriteRaw(XPacketBegin);
            writer.WriteStartElement("x", "xmpmeta", XNs);
            writer.WriteStartElement("rdf", "RDF", RdfNs);
            writer.WriteStartElement("rdf", "Description", RdfNs);
            writer.WriteAttributeString("rdf", "about", RdfNs, string.Empty);

            // Every schema prefix this writer might use is declared once, up front — XmlWriter
            // requires all attributes (namespace declarations included) to precede any child
            // element on the same start tag, so a schema used only conditionally below (pdfaid/
            // pdfuaid) still needs its xmlns declared here, unconditionally. An unused xmlns
            // declaration is valid, harmless XML (RDF/XML consumers ignore it), never a
            // PDF/A conformance defect.
            writer.WriteAttributeString("xmlns", "dc", null, DcNs);
            writer.WriteAttributeString("xmlns", "xmp", null, XmpNs);
            writer.WriteAttributeString("xmlns", "pdf", null, PdfNs);
            writer.WriteAttributeString("xmlns", "pdfaid", null, PdfaidNs);
            writer.WriteAttributeString("xmlns", "pdfuaid", null, PdfuaidNs);

            if (packet.Title is { } title)
            {
                WriteLangAlt(writer, "dc", DcNs, "title", title);
            }

            if (packet.Creator is { } creator)
            {
                WriteSeq(writer, "dc", DcNs, "creator", creator);
            }

            if (packet.Subject is { } subject)
            {
                WriteLangAlt(writer, "dc", DcNs, "description", subject);
            }

            if (packet.Keywords is { } keywords)
            {
                writer.WriteElementString("pdf", "Keywords", PdfNs, keywords);
            }

            if (packet.Producer is { } producer)
            {
                writer.WriteElementString("pdf", "Producer", PdfNs, producer);
            }

            if (packet.CreateDate is { } createDate)
            {
                writer.WriteElementString("xmp", "CreateDate", XmpNs, FormatXmpDate(createDate));
            }

            if (packet.ModifyDate is { } modifyDate)
            {
                writer.WriteElementString("xmp", "ModifyDate", XmpNs, FormatXmpDate(modifyDate));
            }

            if (packet.Conformance != PdfAConformance.None)
            {
                writer.WriteElementString("pdfaid", "part", PdfaidNs, packet.Conformance == PdfAConformance.A1b ? "1" : "2");
                writer.WriteElementString("pdfaid", "conformance", PdfaidNs, "B");
            }

            if (packet.DeclarePdfUa)
            {
                writer.WriteElementString("pdfuaid", "part", PdfuaidNs, "1");
            }

            writer.WriteEndElement(); // rdf:Description
            writer.WriteEndElement(); // rdf:RDF
            writer.WriteEndElement(); // x:xmpmeta
            writer.WriteRaw(XPacketEnd);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Rejects a field value carrying a character XML 1.0 cannot represent (control characters
    /// other than tab/LF/CR, unpaired surrogates — the same set <see cref="XmlWriterSettings.CheckCharacters"/>
    /// enforces) with a coded refusal naming the field, instead of letting the writer surface
    /// it later as a bare <see cref="ArgumentException"/>.
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME6081</c> — see above.</exception>
    private static void ValidateXmlRepresentable(string fieldName, string? value)
    {
        if (value is null)
        {
            return;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (XmlConvert.IsXmlChar(c))
            {
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], c))
            {
                i++; // a valid astral-plane pair — fine in XML 1.0.
                continue;
            }

            throw new PlumePdfException(
                "PLUME6081",
                $"XmpPacket.{fieldName} contains U+{(int)c:X4} at index {i}, a character XML 1.0 cannot represent (control characters and unpaired surrogates are not expressible in an XMP packet, even escaped). Remove or replace it before setting the metadata.");
        }
    }

    /// <summary>
    /// A <see cref="MemoryStream"/> that refuses (<c>PLUME6056</c>) the moment the serialized
    /// packet crosses <see cref="PdfOptions.MaxXmpPacketWriteBytes"/> — enforcement during
    /// serialization, not a check over an already fully-materialized over-cap buffer.
    /// </summary>
    private sealed class BoundedMemoryStream(long maxBytes) : MemoryStream
    {
        private readonly long _maxBytes = maxBytes;

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfOverCap(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfOverCap(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            ThrowIfOverCap(1);
            base.WriteByte(value);
        }

        private void ThrowIfOverCap(int incomingBytes)
        {
            if (Length + incomingBytes > _maxBytes)
            {
                throw new PlumePdfException(
                    "PLUME6056",
                    $"Serialized XMP packet exceeds PdfOptions.MaxXmpPacketWriteBytes ({_maxBytes}). Trim the packet's fields or raise the cap.");
            }
        }
    }

    private static void WriteLangAlt(XmlWriter writer, string prefix, string ns, string localName, string value)
    {
        writer.WriteStartElement(prefix, localName, ns);
        writer.WriteStartElement("rdf", "Alt", RdfNs);
        writer.WriteStartElement("rdf", "li", RdfNs);
        writer.WriteAttributeString("xml", "lang", null, "x-default");
        writer.WriteString(value);
        writer.WriteEndElement(); // rdf:li
        writer.WriteEndElement(); // rdf:Alt
        writer.WriteEndElement(); // the schema element
    }

    private static void WriteSeq(XmlWriter writer, string prefix, string ns, string localName, string value)
    {
        writer.WriteStartElement(prefix, localName, ns);
        writer.WriteStartElement("rdf", "Seq", RdfNs);
        writer.WriteElementString("rdf", "li", RdfNs, value);
        writer.WriteEndElement(); // rdf:Seq
        writer.WriteEndElement(); // the schema element
    }

    /// <summary>Formats a date per the XMP value-serialization spec (ISO 8601, always with an explicit UTC-offset designator): <c>yyyy-MM-ddTHH:mm:sszzz</c>.</summary>
    private static string FormatXmpDate(DateTimeOffset value) =>
        value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
}
