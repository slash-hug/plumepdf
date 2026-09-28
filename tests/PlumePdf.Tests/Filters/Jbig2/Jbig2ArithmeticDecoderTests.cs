using PlumePdf.Filters.Jbig2;
using Xunit;

namespace PlumePdf.Tests.Filters.Jbig2;

/// <summary>
/// <see cref="Jbig2ArithmeticDecoder"/> (the MQ arithmetic decoder, ITU-T T.88
/// Annex E). The five vectors below are cross-checked against an independent Python
/// transcription of the same verified algorithm structure this C# port follows (both written
/// directly from the same source-verified pseudocode, not one derived from the other) -
/// catches transliteration bugs (a swapped operand, an off-by-one shift) distinct from the
/// algorithm-level verification already performed against the porting source's own published
/// behavior. Cycles through 8 contexts (all starting at state 0/MPS 0) reading 40 bits per
/// vector, exercising renormalization, byte-stuffing (the <c>0xFF</c> vectors), and the
/// LPS/MPS exchange paths.
/// </summary>
public class Jbig2ArithmeticDecoderTests
{
    private static int[] DecodeBits(byte[] data, int count, int numContexts = 8)
    {
        var contexts = new byte[numContexts];
        var decoder = new Jbig2ArithmeticDecoder(data, 0, data.Length);
        var bits = new int[count];
        for (var i = 0; i < count; i++)
        {
            bits[i] = decoder.ReadBit(contexts, i % numContexts);
        }

        return bits;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void ReadBit_MatchesIndependentPythonTranscription(string hexInput, int[] expectedBits)
    {
        var data = Convert.FromHexString(hexInput);

        var actual = DecodeBits(data, expectedBits.Length);

        Assert.Equal(expectedBits, actual);
    }

    public static TheoryData<string, int[]> Vectors()
    {
        var data = new TheoryData<string, int[]>
        {
            {
                "0000000000000000",
                [0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0]
            },
            {
                "ffffffffffffffff",
                [1, 1, 1, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1, 0]
            },
            {
                "84c73bfce1a1430402200000",
                [0, 0, 0, 1, 1, 1, 0, 1, 0, 0, 0, 1, 0, 1, 0, 1, 1, 0, 0, 1, 0, 1, 0, 1, 1, 0, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 0, 1, 0, 1]
            },
            {
                "00ffac715500ff009933",
                [0, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 0]
            },
            {
                "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20",
                [0, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 1]
            },
        };
        return data;
    }

    [Fact]
    public void ReadBit_DoesNotThrow_WhenInputRunsOutMidStream()
    {
        // BYTEIN's own end-of-data handling (treats missing bytes as 0xFF padding, per T.88
        // Annex E) means decoding past the end of a short buffer must degrade gracefully
        // (arbitrary-but-deterministic bits), never throw or index out of range.
        var data = new byte[] { 0x12, 0x34 };
        var contexts = new byte[4];
        var decoder = new Jbig2ArithmeticDecoder(data, 0, data.Length);

        for (var i = 0; i < 500; i++)
        {
            _ = decoder.ReadBit(contexts, i % 4);
        }
    }

    [Fact]
    public void ReadBit_EmptyInput_DoesNotThrow()
    {
        var decoder = new Jbig2ArithmeticDecoder([], 0, 0);
        var contexts = new byte[1];
        for (var i = 0; i < 16; i++)
        {
            _ = decoder.ReadBit(contexts, 0);
        }
    }
}
