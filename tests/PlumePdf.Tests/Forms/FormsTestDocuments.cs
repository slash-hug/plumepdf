using System.Text;

namespace PlumePdf.Tests.Forms;

/// <summary>
/// Hand-rolled classic-xref AcroForm fixtures for the forms test suites — independent of
/// PlumePDF's own reader/writer (the thing under test), the same approach
/// <c>WriterTestDocuments</c> uses for the non-forms writer suites (ISO 32000-1's
/// object/xref-table grammar directly, a source allowed under the clean-room policy in
/// AGENTS.md).
/// </summary>
internal static class FormsTestDocuments
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");

    /// <summary>
    /// A single-page document with an <c>/AcroForm</c>: a merged text field (7), a merged
    /// checkbox with <c>/AP /N</c> Yes/Off states (8, appearances 9/10), a merged radio-flag
    /// button with a <c>/1</c>/<c>Off</c> state pair (11, appearances 12/13), a choice field
    /// with three <c>/Opt</c> entries (14, field-only, no widget), and a nested nameless-path
    /// field <c>topmostSubform[0].Page1[0].c1_01[0]</c> (15/16/17) for nested-field-path lookup tests. The
    /// page's <c>/Annots</c> lists the three widgets (7, 8, 11).
    /// </summary>
    public static byte[] Build()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        void WriteStream(int num, string dictBody, string content)
        {
            offsets[num] = buffer.Count;
            var bytes = Encoding.ASCII.GetBytes(content);
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n<< {dictBody} /Length {bytes.Length} >>\nstream\n"));
            buffer.AddRange(bytes);
            buffer.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        }

        const int totalObjects = 20;

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< /Fields [7 0 R 8 0 R 11 0 R 14 0 R 15 0 R] /DR << /Font << /Helv 6 0 R >> >> >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /Helv 6 0 R >> >> /Contents 5 0 R /Annots [7 0 R 8 0 R 11 0 R] >>");

        var pageContent = "BT /Helv 10 Tf 10 100 Td (Body) Tj ET";
        WriteStream(5, string.Empty, pageContent);

        WriteObject(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        // 7: merged text field + widget, with a (blank) normal appearance already baked in —
        // the common real-world case for a text field that has been rendered by a viewer at
        // least once. Points at 18.
        WriteObject(7, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [10 10 100 30] /AP << /N 18 0 R >> /V () >>");

        // 8: merged checkbox field + widget, on-states discovered from /AP /N (never /Yes assumed by the code — the fixture happens to use /Yes as an arbitrary example name).
        WriteObject(8, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (Agree) /Rect [10 40 30 60] /AP << /N << /Yes 9 0 R /Off 10 0 R >> >> /AS /Off /V /Off >>");
        WriteStream(9, "/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "1 0 0 1 0 0 cm");
        WriteStream(10, "/Type /XObject /Subtype /Form /BBox [0 0 20 20]", string.Empty);

        // 11: merged radio-flagged button (bit 16 = 1<<15 = 32768) with a /1 on-state.
        WriteObject(11, "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 32768 /T (Pick) /Rect [10 70 30 90] /AP << /N << /1 12 0 R /Off 13 0 R >> >> /AS /Off /V /Off >>");
        WriteStream(12, "/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0 0 1 rg 0 0 20 20 re f");
        WriteStream(13, "/Type /XObject /Subtype /Form /BBox [0 0 20 20]", string.Empty);

        // 14: choice field, field-only (no widget) — /Opt-based AllowedValues.
        WriteObject(14, "<< /FT /Ch /T (Color) /Opt [(Red) (Green) (Blue)] /V (Red) >>");

        // 15 -> 16 -> 17: nested field-tree path for trailing-segment lookup.
        WriteObject(15, "<< /T (topmostSubform[0]) /Kids [16 0 R] >>");
        WriteObject(16, "<< /T (Page1[0]) /Kids [17 0 R] >>");
        WriteObject(17, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (c1_01[0]) /Rect [10 100 100 120] /AP << /N 19 0 R >> /V () >>");

        WriteStream(18, "/Type /XObject /Subtype /Form /BBox [0 0 90 20]", "BT /Helv 8 Tf 2 5 Td (x) Tj ET");
        WriteStream(19, "/Type /XObject /Subtype /Form /BBox [0 0 90 20]", "BT /Helv 8 Tf 2 5 Td (x) Tj ET");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    /// <summary>
    /// The clean two-field sample committed as <c>tests/PlumePdf.CorpusTests/Fixtures/simple-form.pdf</c>
    /// (self-authored; regenerate by re-running the dump described in that file's provenance
    /// entry): a letter-size page with a merged text field <c>FullName</c> and a merged
    /// checkbox <c>Subscribe</c> (on-state <c>/Yes</c>), a form-level <c>/DA</c> and
    /// <c>/DR</c> — the cookbook's fill/flatten recipes run against it.
    /// </summary>
    public static byte[] BuildCookbookSample()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        void WriteStream(int num, string dictBody, string content)
        {
            offsets[num] = buffer.Count;
            var bytes = Encoding.ASCII.GetBytes(content);
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n<< {dictBody} /Length {bytes.Length} >>\nstream\n"));
            buffer.AddRange(bytes);
            buffer.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        }

        const int totalObjects = 10;

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< /Fields [7 0 R 8 0 R] /DA (/Helv 11 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >> >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /Helv 6 0 R >> >> /Contents 5 0 R /Annots [7 0 R 8 0 R] >>");
        WriteStream(5, string.Empty, "BT /Helv 14 Tf 72 740 Td (Sample form) Tj ET");
        WriteObject(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        WriteObject(7, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (FullName) /Rect [72 690 420 714] /MK << /BC [0] /BG [0.95] >> /V () >>");
        WriteObject(8, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (Subscribe) /Rect [72 650 90 668] /AP << /N << /Yes 9 0 R /Off 10 0 R >> >> /AS /Off /V /Off >>");
        WriteStream(9, "/Type /XObject /Subtype /Form /BBox [0 0 18 18]", "0 0 18 18 re S");
        WriteStream(10, "/Type /XObject /Subtype /Form /BBox [0 0 18 18]", string.Empty);

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects + 1}\n0000000000 65535 f \n"));
        for (var n = 1; n <= totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    /// <summary>
    /// Two field-tree branches, <c>Group1[0].dup[0]</c> and <c>Group2[0].dup[0]</c>, whose
    /// trailing segment (<c>dup</c>) is intentionally ambiguous — for the
    /// ambiguous-partial-match throw.
    /// </summary>
    public static byte[] BuildWithAmbiguousNames()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        const int totalObjects = 9;
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< /Fields [5 0 R 7 0 R] >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>");
        WriteObject(5, "<< /T (Group1[0]) /Kids [6 0 R] >>");
        WriteObject(6, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (dup[0]) /Rect [0 0 10 10] /V () >>");
        WriteObject(7, "<< /T (Group2[0]) /Kids [8 0 R] >>");
        WriteObject(8, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (dup[0]) /Rect [0 0 10 10] /V () >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    /// <summary>
    /// Same shape as <see cref="Build"/>'s text field, but the AcroForm carries <c>/XFA</c>
    /// (an XFA-hybrid form) and the catalog carries <c>/Perms /UR3</c> — for its
    /// fill-time diagnostics.
    /// </summary>
    public static byte[] BuildXfaHybridWithUsageRights()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        const int totalObjects = 7;
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R /Perms << /UR3 6 0 R >> >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< /Fields [5 0 R] /XFA (fake-xfa-packet) >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>");
        WriteObject(5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [0 0 10 10] /V () >>");
        WriteObject(6, "<< /Type /Sig >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    /// <summary>
    /// Two merged field+widgets with no <c>/AP</c> at all: "Filled" carries
    /// <c>/V (Hello)</c> plus a <c>/DA</c> and the AcroForm ships <c>/DR</c> Helvetica — the
    /// synthesizable case flatten must generate an appearance for (generated or
    /// pre-existing); "Empty" is an unsigned <c>/FT /Sig</c> field — a kind the generator has
    /// no synthesized state for — so it is the per-widget degradation case (<c>PLUME6036</c>
    /// Warning, widget kept live). (A <c>/V</c>-less TEXT widget is not the degrade case:
    /// <c>FormFiller.BuildFieldValue</c> treats a missing text value as empty-string, so it
    /// synthesizes an empty appearance — the same nothing-painted outcome Rasterize gives it.)
    /// </summary>
    public static byte[] BuildWithVOnlyAndValuelessWidgets()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        const int totalObjects = 8;
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< /Fields [5 0 R 6 0 R] /DR << /Font << /Helv 7 0 R >> >> /DA (/Helv 12 Tf 0 g) >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [5 0 R 6 0 R] >>");
        WriteObject(5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Filled) /V (Hello) /DA (/Helv 12 Tf 0 g) /Rect [10 150 190 170] >>");
        WriteObject(6, "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Empty) /Rect [10 100 190 120] >>");
        WriteObject(7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    /// <summary>A single field, merged field+widget, with no <c>/AP</c> at all — historically a coded refusal (<c>PLUME6036</c>); now the synthesize-or-degrade path.</summary>
    public static byte[] BuildWithFieldMissingAppearance()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        const int totalObjects = 6;
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< /Fields [5 0 R] >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [5 0 R] >>");
        WriteObject(5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (NoAppearance) /Rect [0 0 10 10] /V () >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    /// <summary>
    /// A single-field document whose <c>/AcroForm</c> carries form-level defaults — <c>/DA</c>,
    /// <c>/Q</c>, and a <c>/CO</c> naming the one field — for testing that
    /// <see cref="PlumePdf.Documents.AcroFormMerger"/> carries them through a merge instead of
    /// dropping them.
    /// </summary>
    public static byte[] BuildWithFormLevelDefaults()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        const int totalObjects = 6;
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< /Fields [5 0 R] /DA (/Helv 10 Tf 0 g) /Q 1 /CO [5 0 R] >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [5 0 R] >>");
        WriteObject(5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [0 0 10 10] /V () >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    /// <summary>A document whose <c>/AcroForm</c> is missing entirely — the "no form" lenient-read case.</summary>
    public static byte[] BuildWithoutAcroForm()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        const int totalObjects = 5;
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /Helv 3 0 R >> >> >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    /// <summary>
    /// A shape lifted from the IRS fillable W-9: a pure grouping node
    /// (<c>Boxes3a-b_ReadOrder[0]</c>, no <c>/FT</c>, no <c>/V</c>) whose kids are three
    /// independent named checkbox fields <c>c1_1[0..2]</c>, each with a single on-state in
    /// <c>/AP /N</c> (<c>/1</c>, <c>/2</c>, <c>/3</c> — <c>/Off</c> appears only under <c>/D</c>,
    /// exactly as the IRS file lays it out) and each carrying the group's selected export value
    /// <c>/V /3</c> — the state a filled copy of the form was found in. Only <c>c1_1[2]</c> owns the
    /// <c>/3</c> appearance, so only it is checked. Two standalone checkboxes ride along:
    /// <c>Agree</c> (<c>/N</c> carries <c>/Yes</c> AND <c>/Off</c>, <c>/V /Yes</c>) proves an
    /// <c>/Off</c> key among the discovered states cannot flip a checked box, and
    /// <c>Bare</c> (no <c>/AP</c> at all, <c>/V /Yes</c>) pins the no-discovered-on-state fallback.
    /// </summary>
    public static byte[] BuildWithGroupedCheckboxesSharingValue()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        void WriteStream(int num, string dictBody, string content)
        {
            offsets[num] = buffer.Count;
            var bytes = Encoding.ASCII.GetBytes(content);
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n<< {dictBody} /Length {bytes.Length} >>\nstream\n"));
            buffer.AddRange(bytes);
            buffer.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        }

        const int totalObjects = 16;

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< /Fields [5 0 R 9 0 R 10 0 R] >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 11 0 R /Annots [6 0 R 7 0 R 8 0 R 9 0 R 10 0 R] >>");

        // 5: the grouping node — /T only, no /FT, no /V, no /Ff (NOT a radio group).
        WriteObject(5, "<< /T (Boxes3a-b_ReadOrder[0]) /Kids [6 0 R 7 0 R 8 0 R] >>");

        // 6-8: named kid checkboxes; every one carries the group's selected value /V /3, but
        // only kid 8 has a /3 appearance state. /Off lives under /D only, as in the W-9.
        WriteObject(6, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (c1_1[0]) /Parent 5 0 R /Rect [10 10 20 20] /AP << /N << /1 12 0 R >> /D << /1 12 0 R /Off 13 0 R >> >> /AS /Off /V /3 >>");
        WriteObject(7, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (c1_1[1]) /Parent 5 0 R /Rect [30 10 40 20] /AP << /N << /2 12 0 R >> /D << /2 12 0 R /Off 13 0 R >> >> /AS /Off /V /3 >>");
        WriteObject(8, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (c1_1[2]) /Parent 5 0 R /Rect [50 10 60 20] /AP << /N << /3 12 0 R >> /D << /3 12 0 R /Off 13 0 R >> >> /AS /3 /V /3 >>");

        // 9: standalone checked checkbox whose /N ALSO lists /Off.
        WriteObject(9, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (Agree) /Rect [10 40 20 50] /AP << /N << /Yes 12 0 R /Off 13 0 R >> >> /AS /Yes /V /Yes >>");

        // 10: standalone checkbox with no /AP at all but a non-Off /V.
        WriteObject(10, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (Bare) /Rect [30 40 40 50] /V /Yes >>");

        WriteStream(11, string.Empty, string.Empty);
        WriteStream(12, "/Type /XObject /Subtype /Form /BBox [0 0 10 10]", "0 0 1 rg 0 0 10 10 re f");
        WriteStream(13, "/Type /XObject /Subtype /Form /BBox [0 0 10 10]", string.Empty);
        WriteObject(14, "<< >>");
        WriteObject(15, "<< >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }
}
