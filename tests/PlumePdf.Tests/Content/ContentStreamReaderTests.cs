using System.Text;
using PlumePdf.Content;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Content;

/// <summary><see cref="ContentStreamReader"/> operator/operand scanning, inline-image skip, truncated-stream and operator-count-bomb guards.</summary>
public class ContentStreamReaderTests
{
    [Fact]
    public void Read_PathPaintingSequence_PairsOperandsWithOperators()
    {
        var bytes = "q\n1 0 0 1 72 720 cm\n0 0 100 20 re\nf\nQ\n"u8.ToArray();

        var ops = ContentStreamReader.Read(bytes, PdfOptions.Default, diagnostics: null);

        Assert.Equal(["q", "cm", "re", "f", "Q"], ops.Select(o => o.Operator));
        var cm = ops.Single(o => o.Operator == "cm");
        Assert.Equal(6, cm.Operands.Count);
        Assert.Equal(72, ((PdfNumber)cm.Operands[4]).Value);
        Assert.Equal(720, ((PdfNumber)cm.Operands[5]).Value);
    }

    [Fact]
    public void Read_TextShowingOperators_CapturesStringAndArrayOperands()
    {
        var bytes = "BT\n/F1 12 Tf\n(Hello) Tj\n[(A) -120 (V)] TJ\nET\n"u8.ToArray();

        var ops = ContentStreamReader.Read(bytes, PdfOptions.Default, diagnostics: null);

        Assert.Equal(["BT", "Tf", "Tj", "TJ", "ET"], ops.Select(o => o.Operator));
        var tj = ops.Single(o => o.Operator == "TJ");
        Assert.IsType<PdfArray>(Assert.Single(tj.Operands));
    }

    [Fact]
    public void Read_InlineImage_SkipsBinaryPayloadAndResumesAfterEI()
    {
        var content = new MemoryStream();
        content.Write("q\nBI /W 2 /H 1 /BPC 8 /CS /G ID "u8);
        content.Write([0x00, 0x45, 0x49, 0xFF]); // binary payload including a byte sequence containing "EI" that is NOT whitespace-delimited
        content.Write(" EI\nQ\n"u8);
        var bytes = content.ToArray();

        var ops = ContentStreamReader.Read(bytes, PdfOptions.Default, diagnostics: null);

        Assert.Equal(["q", "BI", "Q"], ops.Select(o => o.Operator));
        var bi = ops.Single(o => o.Operator == "BI");
        Assert.True(bi.IsInlineImage);
        var dict = Assert.IsType<PdfDictionary>(Assert.Single(bi.Operands));
        Assert.True(dict.ContainsKey(PdfName.Get("W")));
    }

    /// <summary>
    /// Alongside the whole-run <see cref="ContentOperation.InlineImageSpan"/>,
    /// the reader records <see cref="ContentOperation.InlineImageDataSpan"/> — the binary payload
    /// alone (first byte after <c>ID</c>'s single whitespace, through the byte before the
    /// whitespace delimiter preceding <c>EI</c>) — so the render path can slice it verbatim into
    /// a synthesized <c>PdfStream</c> for the image resolver.
    /// </summary>
    [Fact]
    public void Read_InlineImage_RecordsBothWholeRunAndPayloadSpans()
    {
        var prefix = "q\nBI /W 2 /H 1 /BPC 8 /CS /G ID "u8.ToArray();
        byte[] payload = [0x00, 0x45, 0x49, 0xFF]; // contains a non-delimited "EI" byte pair
        var content = new MemoryStream();
        content.Write(prefix);
        content.Write(payload);
        content.Write(" EI\nQ\n"u8);
        var bytes = content.ToArray();

        var ops = ContentStreamReader.Read(bytes, PdfOptions.Default, diagnostics: null);
        var bi = ops.Single(o => o.Operator == "BI");

        Assert.NotNull(bi.InlineImageSpan);
        Assert.NotNull(bi.InlineImageDataSpan);

        var (dataStart, dataEnd) = bi.InlineImageDataSpan!.Value;
        Assert.Equal(prefix.Length, dataStart); // first byte after "ID "'s single whitespace
        Assert.Equal(prefix.Length + payload.Length, dataEnd); // excludes the " " delimiter before EI
        Assert.Equal(payload, bytes[dataStart..dataEnd]);

        // The payload span nests strictly inside the whole-run span.
        var (runStart, runEnd) = bi.InlineImageSpan!.Value;
        Assert.True(runStart < dataStart && dataEnd < runEnd);
    }

    [Fact]
    public void Read_TruncatedStream_RecordsDiagnosticAndReturnsOperatorsParsedSoFar()
    {
        var bytes = "q\n1 0 0 1 72 720 cm\n0 0 100"u8.ToArray(); // ends mid-operand, no closing operator
        var diagnostics = new DiagnosticCollection();

        var ops = ContentStreamReader.Read(bytes, PdfOptions.Default, diagnostics);

        Assert.Equal(["q", "cm"], ops.Select(o => o.Operator));
        Assert.Contains(diagnostics, d => d.Code == "PLUME7011");
    }

    [Fact]
    public void Read_OperatorCountBomb_ThrowsPlume7010()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            builder.Append("0 0 m\n");
        }

        var bytes = Encoding.ASCII.GetBytes(builder.ToString());

        var ex = Assert.Throws<PlumePdfException>(() => ContentStreamReader.Read(bytes, PdfOptions.Default, diagnostics: null, maxOperators: 5));
        Assert.Equal("PLUME7010", ex.Code);
    }

    [Fact]
    public void Read_StrictMode_ThrowsOnTruncatedStreamInsteadOfRecordingDiagnostic()
    {
        var bytes = "1 0 0 1 72"u8.ToArray();
        var strict = PdfOptions.Default with { Strict = true };

        Assert.Throws<PlumePdfException>(() => ContentStreamReader.Read(bytes, strict, diagnostics: null));
    }
}

/// <summary><see cref="PdfMatrix"/> composition, <see cref="GraphicsStateStack"/> q/Q, and <see cref="TextState"/> matrix math against hand-computed ISO 32000-1 §9.4.4 examples.</summary>
public class GraphicsAndTextStateTests
{
    [Fact]
    public void PdfMatrix_Multiply_TranslationThenScale_MatchesHandComputedResult()
    {
        // Translate by (10, 20), then scale by (2, 3): a point at (1, 1) should land at
        // ((1+10)*2, (1+20)*3) = (22, 63) when the translation is applied first.
        var translate = new PdfMatrix(1, 0, 0, 1, 10, 20);
        var scale = new PdfMatrix(2, 0, 0, 3, 0, 0);

        var combined = PdfMatrix.Multiply(translate, scale);
        var (x, y) = combined.Transform(1, 1);

        Assert.Equal(22, x);
        Assert.Equal(63, y);
    }

    [Fact]
    public void PdfMatrix_Multiply_Rotation90Degrees_MapsUnitXToUnitY()
    {
        // A 90-degree counter-clockwise rotation matrix: (1,0) -> (0,1).
        var rotate = new PdfMatrix(0, 1, -1, 0, 0, 0);
        var (x, y) = rotate.Transform(1, 0);

        Assert.Equal(0, x, precision: 10);
        Assert.Equal(1, y, precision: 10);
    }

    [Fact]
    public void GraphicsStateStack_SaveAndRestore_RestoresPriorCtm()
    {
        var stack = new GraphicsStateStack();
        stack.Concatenate(new PdfMatrix(1, 0, 0, 1, 5, 5));
        stack.Save();
        stack.Concatenate(new PdfMatrix(2, 0, 0, 2, 0, 0));

        // CTM after both concatenations: cm's matrix is applied first per "cm's new matrix
        // concatenates onto the CTM" (CTM' = M_cm x CTM_old) — scale(2,2) x translate(5,5)
        // composes to (2,0,0,2,5,5), so (1,1) maps to (1*2+5, 1*2+5) = (7,7).
        Assert.Equal((7.0, 7.0), stack.CurrentTransform.Transform(1, 1));

        var restored = stack.Restore();

        Assert.True(restored);
        Assert.Equal((6.0, 6.0), stack.CurrentTransform.Transform(1, 1));
    }

    [Fact]
    public void GraphicsStateStack_RestoreWithNothingSaved_ReturnsFalse()
    {
        var stack = new GraphicsStateStack();
        Assert.False(stack.Restore());
    }

    [Fact]
    public void TextState_BeginTextObject_ResetsTextAndLineMatrices()
    {
        var state = new TextState();
        state.SetTextMatrix(new PdfMatrix(2, 0, 0, 2, 100, 100));

        state.BeginTextObject();

        Assert.Equal(PdfMatrix.Identity, state.TextMatrix);
        Assert.Equal(PdfMatrix.Identity, state.TextLineMatrix);
    }

    [Fact]
    public void TextState_MoveToNextLine_AccumulatesOnTextLineMatrix()
    {
        var state = new TextState();
        state.BeginTextObject();

        state.MoveToNextLine(10, 0);
        state.MoveToNextLine(0, -14);

        var (x, y) = state.TextMatrix.Transform(0, 0);
        Assert.Equal(10, x);
        Assert.Equal(-14, y);
    }

    [Fact]
    public void TextState_ComputeRenderingMatrix_KnownFontSizeAndOrigin_MatchesHandComputedResult()
    {
        var state = new TextState { FontSize = 12 };
        state.SetTextMatrix(new PdfMatrix(1, 0, 0, 1, 72, 700));

        // Trm = [Tfs 0 0; 0 Tfs 0; 0 0 1] x Tm x CTM(identity): a glyph-space origin (0,0)
        // should land at the text matrix's translation, scaled by nothing (origin is (0,0)).
        var trm = state.ComputeRenderingMatrix(PdfMatrix.Identity);
        var origin = trm.Transform(0, 0);

        Assert.Equal(72, origin.X);
        Assert.Equal(700, origin.Y);

        // A glyph-space point at (1, 0) - one em to the right - should advance by Tfs=12.
        var oneEmRight = trm.Transform(1, 0);
        Assert.Equal(84, oneEmRight.X);
        Assert.Equal(700, oneEmRight.Y);
    }

    [Fact]
    public void TextState_Advance_MovesTextMatrixByGlyphWidthTimesFontSize()
    {
        var state = new TextState { FontSize = 10 };
        state.BeginTextObject();

        state.Advance(0.5, isWordSpaceCode: false); // half an em at font size 10 = 5 units

        var (x, _) = state.TextMatrix.Transform(0, 0);
        Assert.Equal(5, x);
    }
}
