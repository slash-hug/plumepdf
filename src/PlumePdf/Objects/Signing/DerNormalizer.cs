using System.Formats.Asn1;

namespace PlumePdf.Objects.Signing;

/// <summary>
/// Re-encodes an arbitrary BER/CER-encoded ASN.1 value (a CMS blob, an RFC 3161 timestamp
/// token, an OCSP response, ...) as definite-length DER.
/// </summary>
/// <remarks>
/// <para>
/// <c>System.Security.Cryptography.Pkcs.SignedCms.Encode()</c> is documented (and empirically
/// confirmed) to emit indefinite-length CER-style PKCS#7
/// blobs on macOS, while Windows and Linux emit definite-length DER for the same call — a
/// real, silent cross-platform divergence. PAdES/CAdES require DER. Rather than branch on
/// platform, every CMS blob this codebase produces or re-embeds (a built signature, a fetched
/// RFC 3161 token, an OCSP response destined for <c>/DSS</c>) is normalized through here
/// unconditionally, which also makes the platform conditional disappear entirely.
/// </para>
/// <para>
/// This is a structural re-framing pass only: definite-length conversion and BER/CER's
/// "constructed chunked primitive" encoding (used for <c>OCTET STRING</c>/<c>BIT STRING</c>
/// values whose content was split across several TLVs) collapsed into a single primitive DER
/// value. It does <b>not</b> reorder <c>SET OF</c> elements — the input already came from a
/// legitimate CMS-producing implementation (the OS crypto stack, a TSA, an OCSP responder), so
/// element order is trusted as-is; only the length-encoding form changes.
/// </para>
/// </remarks>
internal static class DerNormalizer
{
    /// <summary>Re-encodes <paramref name="encoded"/> as definite-length DER.</summary>
    /// <param name="encoded">A single top-level BER/CER (or already-DER) ASN.1 value.</param>
    /// <returns>The same value, re-encoded with definite lengths throughout and every chunked primitive flattened.</returns>
    /// <exception cref="PlumePdfException">
    /// <paramref name="encoded"/> is empty, contains trailing bytes after its first top-level
    /// value, or contains malformed ASN.1 that cannot be parsed as BER (<c>PLUME4009</c>).
    /// </exception>
    public static byte[] Normalize(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty)
        {
            throw new PlumePdfException("PLUME4009", "Cannot DER-normalize empty input.");
        }

        try
        {
            var reader = new AsnReader(encoded.ToArray(), AsnEncodingRules.BER);
            var writer = new AsnWriter(AsnEncodingRules.DER);
            NormalizeValue(reader, writer);

            if (reader.HasData)
            {
                throw new PlumePdfException("PLUME4009", "Input contains trailing bytes after its first top-level ASN.1 value.");
            }

            return writer.Encode();
        }
        catch (AsnContentException ex)
        {
            throw new PlumePdfException("PLUME4009", $"Input is not well-formed BER/CER ASN.1: {ex.Message}", ex);
        }
    }

    private static void NormalizeValue(AsnReader reader, AsnWriter writer)
    {
        var tag = reader.PeekTag();

        if (!tag.IsConstructed)
        {
            // Already a definite-length primitive (BER/CER primitives are always definite
            // length — only constructed values can use CER's indefinite-length or chunked
            // form) — copy the TLV through unchanged.
            writer.WriteEncodedValue(reader.ReadEncodedValue().Span);
            return;
        }

        if (tag.TagClass == TagClass.Universal && (UniversalTagNumber)tag.TagValue == UniversalTagNumber.OctetString)
        {
            writer.WriteOctetString(reader.ReadOctetString());
            return;
        }

        if (tag.TagClass == TagClass.Universal && (UniversalTagNumber)tag.TagValue == UniversalTagNumber.BitString)
        {
            var content = reader.ReadBitString(out var unusedBitCount);
            writer.WriteBitString(content, unusedBitCount);
            return;
        }

        // A structural container: SEQUENCE, SET (OF), or a context/application/private-class
        // wrapper ([0] EXPLICIT/IMPLICIT and similar). Both AsnReader and AsnWriter refuse a
        // UNIVERSAL-class tag that doesn't match the method's own built-in tag number (reading
        // a UNIVERSAL SET via ReadSequence, or writing one via PushSequence, both throw) — and
        // AsnWriter's matching PushSetOf unconditionally re-sorts its children by encoded
        // bytes, which this method deliberately never does (see remarks: element order is
        // trusted as-is). So children are recursed into and re-encoded generically under a
        // scratch SEQUENCE wrapper (any tag accepts that shape — the wrapper tag itself is
        // discarded), then re-wrapped with the value's own original tag by hand, sidestepping
        // both restrictions uniformly for every constructed tag this method encounters.
        var inner = tag.TagClass == TagClass.Universal && (UniversalTagNumber)tag.TagValue == UniversalTagNumber.SetOf
            ? reader.ReadSetOf(tag)
            : reader.ReadSequence(tag);

        var scratch = new AsnWriter(AsnEncodingRules.DER);
        using (scratch.PushSequence())
        {
            while (inner.HasData)
            {
                NormalizeValue(inner, scratch);
            }
        }

        var scratchContent = new AsnReader(scratch.Encode(), AsnEncodingRules.DER).PeekContentBytes();
        writer.WriteEncodedValue(WrapWithTag(tag, scratchContent.Span));
    }

    /// <summary>
    /// Manually assembles a DER tag-and-length header around <paramref name="content"/>, for
    /// the one case (a UNIVERSAL-class tag other than SEQUENCE) neither <see cref="AsnReader"/>
    /// nor <see cref="AsnWriter"/> will do generically — see the call site's remarks. Shared
    /// with <see cref="CmsSignatureBuilder"/>'s <c>EncodeSetOf</c>, which hits the exact same
    /// restriction writing a UNIVERSAL SET without <see cref="AsnWriter.PushSetOf"/>'s
    /// unwanted auto-sort.
    /// </summary>
    internal static byte[] WrapWithTag(Asn1Tag tag, ReadOnlySpan<byte> content)
    {
        if (tag.TagValue > 30)
        {
            // High-tag-number form (X.690 §8.1.2.4) never appears in the CMS/RFC 3161/OCSP
            // shapes this normalizer processes — failing loudly beats silently mis-encoding.
            throw new PlumePdfException("PLUME4009", $"DerNormalizer does not support ASN.1 tag numbers above 30 (found {tag.TagValue}).");
        }

        var firstByte = (byte)((byte)tag.TagClass | (tag.IsConstructed ? 0x20 : 0x00) | (byte)tag.TagValue);

        byte[] lengthBytes;
        if (content.Length < 128)
        {
            lengthBytes = [(byte)content.Length];
        }
        else
        {
            var trimmed = BitConverter.GetBytes((uint)content.Length);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(trimmed);
            }

            var firstSignificant = Array.FindIndex(trimmed, b => b != 0);
            var significantBytes = trimmed[firstSignificant..];
            lengthBytes = [(byte)(0x80 | significantBytes.Length), .. significantBytes];
        }

        return [firstByte, .. lengthBytes, .. content];
    }
}
