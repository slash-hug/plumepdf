using System.Formats.Asn1;
using PlumePdf.Objects.Signing;
using Xunit;

namespace PlumePdf.Tests.Signing;

public sealed class DerNormalizerTests
{
    [Fact]
    public void Normalize_AlreadyDefiniteLengthDer_IsUnchanged()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(5);
            writer.WriteInteger(7);
        }

        var der = writer.Encode();
        var normalized = DerNormalizer.Normalize(der);

        Assert.Equal(der, normalized);
    }

    [Fact]
    public void Normalize_IndefiniteLengthConstructedSequence_BecomesDefiniteLength()
    {
        // Hand-crafted BER: SEQUENCE (constructed, indefinite length 0x80) { INTEGER 5, INTEGER 7 }, end-of-contents.
        byte[] indefinite =
        [
            0x30, 0x80,
            0x02, 0x01, 0x05,
            0x02, 0x01, 0x07,
            0x00, 0x00,
        ];

        var normalized = DerNormalizer.Normalize(indefinite);

        // Definite-length equivalent, hand-computed: SEQUENCE, length 6, { INTEGER 5, INTEGER 7 }.
        byte[] expected = [0x30, 0x06, 0x02, 0x01, 0x05, 0x02, 0x01, 0x07];
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Normalize_NestedIndefiniteLengthConstructs_AreAllFlattened()
    {
        // SEQUENCE (indefinite) { SEQUENCE (indefinite) { INTEGER 1 }, INTEGER 2 }, both EOCs.
        byte[] indefinite =
        [
            0x30, 0x80,
                0x30, 0x80,
                    0x02, 0x01, 0x01,
                0x00, 0x00,
                0x02, 0x01, 0x02,
            0x00, 0x00,
        ];

        var normalized = DerNormalizer.Normalize(indefinite);

        byte[] expected =
        [
            0x30, 0x08,
                0x30, 0x03, 0x02, 0x01, 0x01,
                0x02, 0x01, 0x02,
        ];
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Normalize_ConstructedChunkedOctetString_FlattensToOnePrimitiveValue()
    {
        // OCTET STRING (constructed, indefinite) { OCTET STRING "AB", OCTET STRING "CD" }, EOC.
        byte[] chunked =
        [
            0x24, 0x80,
                0x04, 0x02, (byte)'A', (byte)'B',
                0x04, 0x02, (byte)'C', (byte)'D',
            0x00, 0x00,
        ];

        var normalized = DerNormalizer.Normalize(chunked);

        byte[] expected = [0x04, 0x04, (byte)'A', (byte)'B', (byte)'C', (byte)'D'];
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Normalize_ConstructedChunkedBitString_MergesUnusedBitsCorrectly()
    {
        // BIT STRING (constructed, indefinite): first fragment 0 unused bits over 0xAB, second
        // (last) fragment 3 unused bits over 0xE0 -> flattened content = [3, 0xAB, 0xE0].
        byte[] chunked =
        [
            0x23, 0x80,
                0x03, 0x02, 0x00, 0xAB,
                0x03, 0x02, 0x03, 0xE0,
            0x00, 0x00,
        ];

        var normalized = DerNormalizer.Normalize(chunked);

        byte[] expected = [0x03, 0x03, 0x03, 0xAB, 0xE0];
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Normalize_PreservesElementOrder_DoesNotResortSetOf()
    {
        // A two-element indefinite-length SET whose members are deliberately in an order that
        // DER's canonical SET-OF sort would reverse (0x02 0x01 0x02 sorts before 0x02 0x01 0x01
        // is false - shorter-or-equal-length same-content compares byte-by-byte; here both
        // encodings are the same length so the SECOND byte decides: 0x02 < 0x01 is false, i.e.
        // INTEGER 1 would sort before INTEGER 2 under canonical order). Input is deliberately
        // given as [2, 1] to prove DerNormalizer preserves that order rather than re-sorting.
        byte[] indefinite =
        [
            0x31, 0x80,
                0x02, 0x01, 0x02,
                0x02, 0x01, 0x01,
            0x00, 0x00,
        ];

        var normalized = DerNormalizer.Normalize(indefinite);

        byte[] expected = [0x31, 0x06, 0x02, 0x01, 0x02, 0x02, 0x01, 0x01];
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Normalize_ContextTaggedConstructedValue_PreservesItsOwnTag()
    {
        // [0] IMPLICIT (constructed, indefinite) { INTEGER 9 } — the shape SignerInfo's
        // signedAttrs/unsignedAttrs and CMS's [0]-tagged certificates use.
        byte[] indefinite = [0xA0, 0x80, 0x02, 0x01, 0x09, 0x00, 0x00];

        var normalized = DerNormalizer.Normalize(indefinite);

        byte[] expected = [0xA0, 0x03, 0x02, 0x01, 0x09];
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Normalize_EmptyInput_ThrowsCoded()
    {
        var ex = Assert.Throws<PlumePdfException>(() => DerNormalizer.Normalize([]));
        Assert.Equal("PLUME4009", ex.Code);
    }

    [Fact]
    public void Normalize_TrailingBytesAfterTopLevelValue_ThrowsCoded()
    {
        byte[] withTrailer = [0x02, 0x01, 0x05, 0xFF]; // INTEGER 5, then a stray byte.

        var ex = Assert.Throws<PlumePdfException>(() => DerNormalizer.Normalize(withTrailer));
        Assert.Equal("PLUME4009", ex.Code);
    }

    [Fact]
    public void Normalize_MalformedBer_ThrowsCoded()
    {
        byte[] garbage = [0x30, 0x7F, 0x01]; // SEQUENCE claims 127 content bytes, only 1 present.

        var ex = Assert.Throws<PlumePdfException>(() => DerNormalizer.Normalize(garbage));
        Assert.Equal("PLUME4009", ex.Code);
    }
}
