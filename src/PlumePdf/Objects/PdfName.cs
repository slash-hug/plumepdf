using System.Collections.Concurrent;
using System.Threading;

namespace PlumePdf;

/// <summary>
/// A PDF name object, e.g. <c>/Type</c> (ISO 32000-1 §7.3.5). Interned process-wide:
/// <see cref="Get(string)"/> always returns the same instance for the same
/// text, so name equality is reference equality and a <see cref="PdfDictionary"/> keyed by
/// <see cref="PdfName"/> is as cheap to look up as a hash of a reference. The text is
/// decoded with <see cref="System.Text.Encoding.Latin1"/> rather than UTF-8 — PDF names are
/// byte sequences (a <c>#XX</c> escape can encode any byte 0–255), and Latin1 is a
/// bijection between <see cref="byte"/> and <see cref="char"/> in that range, so every byte
/// round-trips exactly on re-encode. A handful of names used throughout the reading engine
/// are exposed as static properties so callers never re-intern a literal string for them.
/// </summary>
/// <remarks>
/// The intern table holds <see cref="WeakReference{T}"/>s, not strong references: a name is
/// attacker-controlled and can be arbitrarily long and arbitrarily numerous (a hostile
/// document with millions of unique <c>/Name</c>s), so a permanent, never-evicted process-wide
/// table would be an unbounded memory leak that outlives every <see cref="PdfDocument"/> that
/// ever referenced those names (the resource-limit philosophy applies here too, even though
/// it isn't a <see cref="PdfOptions"/> knob - there's no per-document scope to attach one to).
/// A name still reachable from a live document's object graph stays interned for free (its
/// own strong references keep the weak reference alive); one that becomes unreachable is
/// eligible for collection like anything else, and <see cref="Get(string)"/> periodically
/// sweeps dead entries out of the table entirely (not just letting the value die) so the
/// table's own size tracks live usage rather than growing forever. The ~30 well-known names
/// below (<see cref="Type"/>, <see cref="Filter"/>, ...) are held by static fields for the
/// process lifetime by design - that's an intentional, fixed, small set, not the unbounded
/// case this guards against.
/// </remarks>
/// <example>
/// <code>
/// PdfName filter = PdfName.Get("Filter");
/// bool same = ReferenceEquals(filter, PdfName.Filter); // true
/// </code>
/// </example>
public sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    private static readonly ConcurrentDictionary<string, WeakReference<PdfName>> Interned = new(StringComparer.Ordinal);
    private const int SweepIntervalAdds = 8192;
    private static long _addsSinceSweep;

    private PdfName(string value) => Value = value;

    /// <summary>The name's text, decoded from its raw bytes via <see cref="System.Text.Encoding.Latin1"/>.</summary>
    public string Value { get; }

    /// <summary>Returns the interned <see cref="PdfName"/> for <paramref name="value"/>, creating it on first use.</summary>
    public static PdfName Get(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        while (true)
        {
            if (Interned.TryGetValue(value, out var weak))
            {
                if (weak.TryGetTarget(out var alive))
                {
                    return alive;
                }

                // The name text is still tracked but nothing holds the instance alive any
                // more - replace the dead weak reference with a fresh live one. TryUpdate is
                // a compare-and-swap against the exact weak reference we just read, so a
                // concurrent thread doing the same replacement can't silently overwrite ours
                // (or vice versa) - the loser just retries and picks up the winner's result.
                var replacement = new PdfName(value);
                if (Interned.TryUpdate(value, new WeakReference<PdfName>(replacement), weak))
                {
                    return replacement;
                }

                continue;
            }

            var created = new PdfName(value);
            if (Interned.TryAdd(value, new WeakReference<PdfName>(created)))
            {
                SweepIfDue();
                return created;
            }
        }
    }

    /// <summary>Removes intern-table entries whose name is no longer referenced by anything, roughly every <see cref="SweepIntervalAdds"/> new entries.</summary>
    private static void SweepIfDue()
    {
        if (Interlocked.Increment(ref _addsSinceSweep) < SweepIntervalAdds)
        {
            return;
        }

        Interlocked.Exchange(ref _addsSinceSweep, 0);

        foreach (var entry in Interned)
        {
            if (!entry.Value.TryGetTarget(out _))
            {
                // Removes only if the table still maps this key to this exact (dead) weak
                // reference instance - if another thread already replaced or removed it
                // (Get's own compare-and-swap above, or a concurrent sweep), this is a no-op
                // rather than a race that could drop a just-revived live entry.
                ((ICollection<KeyValuePair<string, WeakReference<PdfName>>>)Interned).Remove(entry);
            }
        }
    }

    /// <inheritdoc/>
    public bool Equals(PdfName? other) => ReferenceEquals(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    /// <inheritdoc/>
    public override int GetHashCode() => string.GetHashCode(Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => "/" + Value;

    /// <summary>The <c>/Type</c> name.</summary>
    public static PdfName Type { get; } = Get("Type");

    /// <summary>The <c>/Subtype</c> name.</summary>
    public static PdfName Subtype { get; } = Get("Subtype");

    /// <summary>The <c>/Filter</c> name.</summary>
    public static PdfName Filter { get; } = Get("Filter");

    /// <summary>The <c>/Length</c> name.</summary>
    public static PdfName Length { get; } = Get("Length");

    /// <summary>The <c>/DecodeParms</c> name.</summary>
    public static PdfName DecodeParms { get; } = Get("DecodeParms");

    /// <summary>The <c>/Predictor</c> name.</summary>
    public static PdfName Predictor { get; } = Get("Predictor");

    /// <summary>The <c>/Columns</c> name.</summary>
    public static PdfName Columns { get; } = Get("Columns");

    /// <summary>The <c>/Colors</c> name.</summary>
    public static PdfName Colors { get; } = Get("Colors");

    /// <summary>The <c>/BitsPerComponent</c> name.</summary>
    public static PdfName BitsPerComponent { get; } = Get("BitsPerComponent");

    /// <summary>The <c>/EarlyChange</c> name.</summary>
    public static PdfName EarlyChange { get; } = Get("EarlyChange");

    /// <summary>The <c>/N</c> name (object-stream object count).</summary>
    public static PdfName N { get; } = Get("N");

    /// <summary>The <c>/First</c> name (object-stream first-object offset).</summary>
    public static PdfName First { get; } = Get("First");

    /// <summary>The <c>/Extends</c> name (object-stream chaining).</summary>
    public static PdfName Extends { get; } = Get("Extends");

    /// <summary>The <c>/Root</c> name (trailer catalog reference).</summary>
    public static PdfName Root { get; } = Get("Root");

    /// <summary>The <c>/Prev</c> name (cross-reference chain).</summary>
    public static PdfName Prev { get; } = Get("Prev");

    /// <summary>The <c>/XRefStm</c> name (hybrid-reference cross-reference stream offset).</summary>
    public static PdfName XRefStm { get; } = Get("XRefStm");

    /// <summary>The <c>/ID</c> name (file identifier array).</summary>
    public static PdfName Id { get; } = Get("ID");

    /// <summary>The <c>/Size</c> name (cross-reference size).</summary>
    public static PdfName Size { get; } = Get("Size");

    /// <summary>The <c>/Index</c> name (cross-reference stream subsection index).</summary>
    public static PdfName Index { get; } = Get("Index");

    /// <summary>The <c>/W</c> name (cross-reference stream field widths).</summary>
    public static PdfName W { get; } = Get("W");

    /// <summary>The <c>/Encrypt</c> name (trailer encryption dictionary reference).</summary>
    public static PdfName Encrypt { get; } = Get("Encrypt");

    /// <summary>The <c>/O</c> name (owner password hash, encryption dictionary).</summary>
    public static PdfName O { get; } = Get("O");

    /// <summary>The <c>/U</c> name (user password hash, encryption dictionary).</summary>
    public static PdfName U { get; } = Get("U");

    /// <summary>The <c>/P</c> name (permission flags, encryption dictionary).</summary>
    public static PdfName P { get; } = Get("P");

    /// <summary>The <c>/R</c> name (encryption standard security handler revision).</summary>
    public static PdfName R { get; } = Get("R");

    /// <summary>The <c>/V</c> name (encryption algorithm version; also, from Phase 4, an AcroForm field's current value, §12.7.3.3 — one interned name, two dictionary contexts).</summary>
    public static PdfName V { get; } = Get("V");

    /// <summary>The <c>/Info</c> name (trailer document-information reference).</summary>
    public static PdfName Info { get; } = Get("Info");

    /// <summary>The <c>/ToUnicode</c> name (a font's optional Unicode-mapping CMap stream, ISO 32000-1 §9.10.3).</summary>
    public static PdfName ToUnicode { get; } = Get("ToUnicode");

    /// <summary>The <c>/Encoding</c> name (a simple font's base encoding or encoding dictionary, or a Type0 font's CMap, §9.6.6/§9.7.5).</summary>
    public static PdfName Encoding { get; } = Get("Encoding");

    /// <summary>The <c>/Differences</c> name (an encoding dictionary's code-to-glyph-name overrides, §9.6.6.2).</summary>
    public static PdfName Differences { get; } = Get("Differences");

    /// <summary>The <c>/BaseEncoding</c> name (an encoding dictionary's named base encoding, §9.6.6.2).</summary>
    public static PdfName BaseEncoding { get; } = Get("BaseEncoding");

    /// <summary>The <c>/Widths</c> name (a simple font's per-code glyph widths array, §9.6.3).</summary>
    public static PdfName Widths { get; } = Get("Widths");

    /// <summary>The <c>/FirstChar</c> name (the first code <c>/Widths</c> covers, §9.6.3).</summary>
    public static PdfName FirstChar { get; } = Get("FirstChar");

    /// <summary>The <c>/LastChar</c> name (the last code <c>/Widths</c> covers, §9.6.3).</summary>
    public static PdfName LastChar { get; } = Get("LastChar");

    /// <summary>The <c>/DW</c> name (a Type0 font's default glyph width, §9.7.4.3).</summary>
    public static PdfName DW { get; } = Get("DW");

    /// <summary>The <c>/DescendantFonts</c> name (a Type0 font's single CIDFont array entry, §9.7.4).</summary>
    public static PdfName DescendantFonts { get; } = Get("DescendantFonts");

    /// <summary>The <c>/CIDToGIDMap</c> name (a CIDFontType2's CID-to-glyph-ID mapping, §9.7.4.3).</summary>
    public static PdfName CIDToGIDMap { get; } = Get("CIDToGIDMap");

    /// <summary>The <c>/Metadata</c> name (a document or object's XMP metadata stream reference, §14.3.2).</summary>
    public static PdfName Metadata { get; } = Get("Metadata");

    /// <summary>The <c>/Image</c> name (an XObject's <c>/Subtype</c> value for an image, §8.9.5).</summary>
    public static PdfName Image { get; } = Get("Image");

    /// <summary>The <c>/ColorSpace</c> name (an image XObject's color space, §8.9.5).</summary>
    public static PdfName ColorSpace { get; } = Get("ColorSpace");

    /// <summary>The <c>/SMask</c> name (an image XObject's soft-mask reference, §11.6.5.3).</summary>
    public static PdfName SMask { get; } = Get("SMask");

    /// <summary>The <c>/Decode</c> name (an image XObject's sample-to-color-component mapping array, §8.9.5.2).</summary>
    public static PdfName Decode { get; } = Get("Decode");

    /// <summary>The <c>/Interpolate</c> name (an image XObject's interpolation hint, §8.9.5.3).</summary>
    public static PdfName Interpolate { get; } = Get("Interpolate");

    /// <summary>The <c>/ImageMask</c> name (marks an image XObject as a stencil mask, §8.9.6.2).</summary>
    public static PdfName ImageMask { get; } = Get("ImageMask");

    // --- Phase 4 (AcroForms) --------------------------------------------------------------
    // ISO 32000-1 Table 189 (the /MK appearance-characteristics dictionary) has no "/TI"
    // key for a pushbutton widget's caption/icon text position — the real key is /TP
    // ("text position"). Minted as TextPosition/"TP" below accordingly.

    /// <summary>The <c>/AcroForm</c> name (the catalog's interactive-form dictionary root, §12.7.2).</summary>
    public static PdfName AcroForm { get; } = Get("AcroForm");

    /// <summary>The <c>/Annots</c> name (a page's annotation array, §7.7.3.3), including but not limited to widget annotations.</summary>
    public static PdfName Annots { get; } = Get("Annots");

    /// <summary>The <c>/Widget</c> name (an annotation's <c>/Subtype</c> value for a form field's on-page representation, §12.5.6.19).</summary>
    public static PdfName Widget { get; } = Get("Widget");

    /// <summary>The <c>/Fields</c> name (the AcroForm dictionary's root field array, §12.7.2).</summary>
    public static PdfName Fields { get; } = Get("Fields");

    /// <summary>The <c>/Kids</c> name (a field's child fields in the field tree, §12.7.3; also used, in a different dictionary context, by the page tree).</summary>
    public static PdfName Kids { get; } = Get("Kids");

    /// <summary>The <c>/Parent</c> name (a field's parent in the field tree, §12.7.3; also used, in a different dictionary context, by the page tree).</summary>
    public static PdfName Parent { get; } = Get("Parent");

    /// <summary>The <c>/DV</c> name (a field's default value, restored on <c>ResetForm</c>, §12.7.3.3).</summary>
    public static PdfName DV { get; } = Get("DV");

    /// <summary>The <c>/DA</c> name (a default appearance string — the AcroForm's document-wide default, or a field's own override, §12.7.3.3).</summary>
    public static PdfName DA { get; } = Get("DA");

    /// <summary>The <c>/DR</c> name (the AcroForm's default resource dictionary that <c>/DA</c> font names resolve against, §12.7.3.3).</summary>
    public static PdfName DR { get; } = Get("DR");

    /// <summary>The <c>/Ff</c> name (a field's flag bit field — e.g. multiline, required, read-only, §12.7.3.1).</summary>
    public static PdfName Ff { get; } = Get("Ff");

    /// <summary>The <c>/Q</c> name (a variable-text field's quadding/justification: 0 left, 1 center, 2 right, §12.7.3.3).</summary>
    public static PdfName Q { get; } = Get("Q");

    /// <summary>The <c>/MK</c> name (a widget's appearance-characteristics dictionary — border/background color, caption, icon, §12.5.6.19).</summary>
    public static PdfName MK { get; } = Get("MK");

    /// <summary>The <c>/AP</c> name (an annotation's appearance dictionary — <c>/N</c>/<c>/D</c>/<c>/R</c> appearance streams, §12.5.5).</summary>
    public static PdfName AP { get; } = Get("AP");

    /// <summary>The <c>/AS</c> name (a widget's currently-selected appearance state — the on-state name for a checkbox/radio field, §12.5.6.19).</summary>
    public static PdfName AS { get; } = Get("AS");

    /// <summary>The <c>/Opt</c> name (a choice field's export-value options array, §12.7.4.4).</summary>
    public static PdfName Opt { get; } = Get("Opt");

    /// <summary>The <c>/T</c> name (a field's partial name — one segment of its fully-qualified name, §12.7.3.2).</summary>
    public static PdfName T { get; } = Get("T");

    /// <summary>The <c>/TU</c> name (a field's alternate name — a tooltip/accessible-name string, §12.7.3.3).</summary>
    public static PdfName TU { get; } = Get("TU");

    /// <summary>The <c>/FT</c> name (a field's type: <see cref="Tx"/>, <see cref="Btn"/>, <see cref="Ch"/>, or <see cref="Sig"/>, §12.7.3.1).</summary>
    public static PdfName FT { get; } = Get("FT");

    /// <summary>The <c>/Tx</c> name (the <see cref="FT"/> value for a text field, §12.7.4.3).</summary>
    public static PdfName Tx { get; } = Get("Tx");

    /// <summary>The <c>/Btn</c> name (the <see cref="FT"/> value for a checkbox/radio-button/pushbutton field, §12.7.4.2).</summary>
    public static PdfName Btn { get; } = Get("Btn");

    /// <summary>The <c>/Ch</c> name (the <see cref="FT"/> value for a choice field — list box or combo box, §12.7.4.4).</summary>
    public static PdfName Ch { get; } = Get("Ch");

    /// <summary>The <c>/Sig</c> name (the <see cref="FT"/> value for a signature field, §12.7.4.5; PlumePDF reads and preserves the field, real signature semantics are Phase 5).</summary>
    public static PdfName Sig { get; } = Get("Sig");

    /// <summary>The <c>/NeedAppearances</c> name (the AcroForm flag requesting viewer-side appearance regeneration; PlumePDF generates appearances itself by default, exposing this only as an explicit fill-time option).</summary>
    public static PdfName NeedAppearances { get; } = Get("NeedAppearances");

    /// <summary>The <c>/SigFlags</c> name (the AcroForm's signature-related flags — whether the document contains signatures and whether they must be appended incrementally, §12.7.2).</summary>
    public static PdfName SigFlags { get; } = Get("SigFlags");

    /// <summary>The <c>/XFA</c> name (the AcroForm's XFA form-definition stream/array; never parsed, generated, or interpreted — permanently out of scope per <c>docs/spec.md</c>. On fill, the key is dropped from a hybrid document with a coded diagnostic).</summary>
    public static PdfName XFA { get; } = Get("XFA");

    /// <summary>The <c>/Perms</c> name (the catalog's permissions dictionary — usage rights and DocMDP, §12.8.4; see <see cref="UR3"/>).</summary>
    public static PdfName Perms { get; } = Get("Perms");

    /// <summary>The <c>/UR3</c> name (the <see cref="Perms"/> dictionary's usage-rights entry — Adobe Reader-enabling; a third-party incremental update invalidates it).</summary>
    public static PdfName UR3 { get; } = Get("UR3");

    /// <summary>The <c>/CO</c> name (the AcroForm's calculation-order array; PlumePDF reads and preserves it, never executes it, §12.7.2).</summary>
    public static PdfName CO { get; } = Get("CO");

    /// <summary>The <c>/BS</c> name (an annotation's border-style dictionary — width, dash pattern, style, §12.5.4).</summary>
    public static PdfName BS { get; } = Get("BS");

    /// <summary>The <c>/IF</c> name (a pushbutton widget's icon-fit dictionary, nested under <see cref="MK"/>, §12.5.6.19).</summary>
    public static PdfName IF { get; } = Get("IF");

    /// <summary>The <c>/TP</c> name (a pushbutton widget's caption/icon relative text position, nested under <see cref="MK"/>, §12.5.6.19).</summary>
    public static PdfName TP { get; } = Get("TP");

    /// <summary>The <c>/I</c> name (a pushbutton widget's normal-state icon stream reference, nested under <see cref="MK"/>, §12.5.6.19).</summary>
    public static PdfName I { get; } = Get("I");

    /// <summary>The <c>/Off</c> name (the conventional checkbox/radio-button "unchecked" on-state name; real forms don't always give it its own appearance sub-dictionary entry).</summary>
    public static PdfName Off { get; } = Get("Off");

    // --- Phase 5 (Digital signatures) -----------------------------------------------------
    // Each constant below is verified against
    // corpora/arlington-pdf-model-master/tsv/2.0 (fetched per scripts/fetch-corpora.sh) and
    // cited by TSV filename rather than by pasting ISO spec text (the clean-room policy in AGENTS.md).

    /// <summary>The <c>/ByteRange</c> name (a signature or document-timestamp dictionary's four-integer digest-coverage array; Arlington <c>Signature.tsv</c>/<c>DocTimeStamp.tsv</c>).</summary>
    public static PdfName ByteRange { get; } = Get("ByteRange");

    /// <summary>The <c>/SubFilter</c> name (identifies a signature's encoding format — see <see cref="AdbePkcs7Detached"/>, <see cref="ETSICAdESDetached"/>, <see cref="ETSIRFC3161"/>; Arlington <c>Signature.tsv</c>/<c>DocTimeStamp.tsv</c>).</summary>
    public static PdfName SubFilter { get; } = Get("SubFilter");

    /// <summary>The <c>adbe.pkcs7.detached</c> <see cref="SubFilter"/> value — a detached PKCS#7/CMS signature, the PAdES B-B baseline encoding; Arlington <c>Signature.tsv</c>.</summary>
    public static PdfName AdbePkcs7Detached { get; } = Get("adbe.pkcs7.detached");

    /// <summary>The <c>ETSI.CAdES.detached</c> <see cref="SubFilter"/> value — a detached CAdES signature per ETSI EN 319 142-1 (PAdES); Arlington <c>Signature.tsv</c>.</summary>
    public static PdfName ETSICAdESDetached { get; } = Get("ETSI.CAdES.detached");

    /// <summary>The <c>ETSI.RFC3161</c> <see cref="SubFilter"/> value — identifies a <see cref="DocTimeStamp"/> dictionary's RFC 3161 timestamp token; Arlington <c>DocTimeStamp.tsv</c>.</summary>
    public static PdfName ETSIRFC3161 { get; } = Get("ETSI.RFC3161");

    /// <summary>The <c>/DocTimeStamp</c> <see cref="Type"/> value — a document timestamp revision (the B-LTA machinery's standalone <c>/DocTimeStamp</c> dictionary, distinct from a <see cref="Sig"/>-typed signature dictionary); Arlington <c>DocTimeStamp.tsv</c>.</summary>
    public static PdfName DocTimeStamp { get; } = Get("DocTimeStamp");

    /// <summary>The <c>/DocMDP</c> <see cref="TransformMethod"/> value — identifies a signature reference dictionary as a modification-detection-and-prevention (DocMDP) reference; Arlington <c>SignatureReferenceDocMDP.tsv</c>.</summary>
    public static PdfName DocMDP { get; } = Get("DocMDP");

    /// <summary>The <c>/Reference</c> name (a signature dictionary's array of signature reference dictionaries — DocMDP/FieldMDP/UR/Identity; Arlington <c>Signature.tsv</c> -&gt; <c>ArrayOfSignatureReferences</c>).</summary>
    public static PdfName Reference { get; } = Get("Reference");

    /// <summary>The <c>/TransformMethod</c> name (a signature reference dictionary's transform method, e.g. <see cref="DocMDP"/>; Arlington <c>SignatureReferenceDocMDP.tsv</c>).</summary>
    public static PdfName TransformMethod { get; } = Get("TransformMethod");

    /// <summary>The <c>/TransformParams</c> name (a signature reference dictionary's transform-parameters sub-dictionary; Arlington <c>SignatureReferenceDocMDP.tsv</c> -&gt; <c>DocMDPTransformParameters</c>).</summary>
    public static PdfName TransformParams { get; } = Get("TransformParams");

    /// <summary>The <c>/DSS</c> name (the catalog's Document Security Store dictionary — and its own <see cref="Type"/> value — carrying validation-related material for LTV; Arlington <c>DSS.tsv</c>).</summary>
    public static PdfName DSS { get; } = Get("DSS");

    /// <summary>The <c>/VRI</c> name (the DSS's Validation-Related Information map, keyed by signature hash — and its own <see cref="Type"/> value on one entry; Arlington <c>DSS.tsv</c>/<c>VRI.tsv</c>).</summary>
    public static PdfName VRI { get; } = Get("VRI");

    /// <summary>The <c>/Certs</c> name (a DSS's array of DER-encoded certificate streams; Arlington <c>DSS.tsv</c>).</summary>
    public static PdfName Certs { get; } = Get("Certs");

    /// <summary>The <c>/CRLs</c> name (a DSS's array of CRL streams; Arlington <c>DSS.tsv</c>).</summary>
    public static PdfName CRLs { get; } = Get("CRLs");

    /// <summary>The <c>/OCSPs</c> name (a DSS's array of OCSP response streams; Arlington <c>DSS.tsv</c>).</summary>
    public static PdfName OCSPs { get; } = Get("OCSPs");

    /// <summary>The <c>/M</c> name (a signature dictionary's signing time — a PDF date string, distinct from the CMS signing-time attribute; Arlington <c>Signature.tsv</c>/<c>DocTimeStamp.tsv</c>).</summary>
    public static PdfName M { get; } = Get("M");

    /// <summary>The <c>/Reason</c> name (the signer-supplied reason for signing; Arlington <c>Signature.tsv</c>/<c>DocTimeStamp.tsv</c>).</summary>
    public static PdfName Reason { get; } = Get("Reason");

    /// <summary>The <c>/Location</c> name (the signer-supplied signing location; Arlington <c>Signature.tsv</c>/<c>DocTimeStamp.tsv</c>).</summary>
    public static PdfName Location { get; } = Get("Location");

    /// <summary>The <c>/ContactInfo</c> name (the signer-supplied contact information; Arlington <c>Signature.tsv</c>/<c>DocTimeStamp.tsv</c>).</summary>
    public static PdfName ContactInfo { get; } = Get("ContactInfo");

    /// <summary>The <c>/Prop_Build</c> name (a signature dictionary's build-properties sub-dictionary per the Adobe PDF Signature Build Dictionary Specification; PlumePDF reads and preserves it, never authors bespoke content. Arlington <c>Signature.tsv</c> -&gt; <c>SignatureBuildPropDict</c>).</summary>
    public static PdfName PropBuild { get; } = Get("Prop_Build");

    /// <summary>The <c>/Lock</c> name (a signature field's field-locking dictionary, naming which other fields become read-only once this field is signed; read and surfaced only, never enforced this phase — a 1.x backlog item. Arlington <c>FieldSig.tsv</c> -&gt; <c>SigFieldLock.tsv</c>).</summary>
    public static PdfName Lock { get; } = Get("Lock");

    /// <summary>The <c>/SV</c> name (a signature field's seed-value dictionary constraining acceptable signing parameters; read and surfaced only, never enforced this phase — a 1.x backlog item. Arlington <c>FieldSig.tsv</c> -&gt; <c>SigFieldSeedValue.tsv</c>).</summary>
    public static PdfName SV { get; } = Get("SV");
}
