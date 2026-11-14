using System.Text;
using PlumePdf.Documents;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Forms;

/// <summary>
/// <see cref="AcroFormMerger.MergeInto"/>: the form fields of a page copied into an opened
/// document join that document's form with the destination as the base. Existing fields,
/// <c>/DR</c> entries and form-level keys stay as they are; incoming names that collide are
/// renamed; only fields placed on the copied page come along; nothing is mutated until the plan
/// is applied.
/// </summary>
public class AcroFormMergeIntoTests
{
    private static readonly PdfName Fields = PdfName.Get("Fields");
    private static readonly PdfName T = PdfName.Get("T");
    private static readonly PdfName DR = PdfName.Get("DR");
    private static readonly PdfName Font = PdfName.Get("Font");
    private static readonly PdfName DA = PdfName.Get("DA");
    private static readonly PdfName DS = PdfName.Get("DS");
    private static readonly PdfName Q = PdfName.Get("Q");
    private static readonly PdfName Kids = PdfName.Get("Kids");
    private static readonly PdfName Opt = PdfName.Get("Opt");
    private static readonly PdfName NeedAppearances = PdfName.Get("NeedAppearances");
    private static readonly PdfName SigFlags = PdfName.Get("SigFlags");
    private static readonly PdfName CO = PdfName.Get("CO");
    private static readonly PdfName XFA = PdfName.Get("XFA");
    private static readonly PdfName AcroForm = PdfName.Get("AcroForm");

    private const string DestinationForm = "<< /Fields [5 0 R] /DR << /Font << /Helv 6 0 R /F1 7 0 R >> >> /DA (/Helv 0 Tf 0 g) /SigFlags 0 >>";

    [Fact]
    public void Planning_ChangesNothingInTheDestination()
    {
        using var run = Run(Destination(acroForm: "4 0 R", form: DestinationForm), apply: false);

        Assert.Single(FieldsOf(run));
        Assert.Empty(run.Destination.Objects.DirtyObjects);
        Assert.NotEmpty(run.Plan.Appends);
    }

    [Fact]
    public void ExistingFields_AreUntouched_AndIncomingFieldsAreAppended()
    {
        using var run = Run(Destination(acroForm: "4 0 R", form: DestinationForm));

        var fields = FieldsOf(run);
        Assert.Equal(new IndirectReference(5, 0), ((PdfReference)fields[0]).Target);
        Assert.Equal(["Name", "Name~2", "Choice", "Sig"], fields.Select(f => Text(run.Resolve(f), T)));
    }

    [Fact]
    public void ACollidingTopLevelName_IsRenamed_AndReported()
    {
        using var run = Run(Destination(acroForm: "4 0 R", form: DestinationForm));

        Assert.Contains(("Name", "Name~2"), run.Plan.Renames);
    }

    [Fact]
    public void RadioOptions_StayAlignedWithTheirWidgets()
    {
        using var run = Run(Destination(acroForm: "4 0 R", form: DestinationForm));

        var radio = (PdfDictionary)run.Resolve(FieldsOf(run).Single(f => Text(run.Resolve(f), T) == "Choice"));
        var kids = (PdfArray)radio[Kids];
        Assert.Equal(2, kids.Count);
        Assert.Equal(2, ((PdfArray)radio[Opt]).Count);
    }

    [Fact]
    public void OnlyFieldsPlacedOnTheCopiedPage_ComeAlong()
    {
        using var run = Run(Destination(acroForm: "4 0 R", form: DestinationForm));

        var names = FieldsOf(run).Select(f => Text(run.Resolve(f), T)).ToList();
        Assert.DoesNotContain("Hidden", names); // placed on no page
        Assert.DoesNotContain("Other", names);  // placed on the source's other page
    }

    [Fact]
    public void ADifferentFontUnderATakenName_GetsAFreshName_AndTheCopiedDaStringsFollowIt()
    {
        using var run = Run(Destination(acroForm: "4 0 R", form: DestinationForm));

        var fonts = (PdfDictionary)((PdfDictionary)run.Resolve(run.AcroForm[DR]))[Font];
        Assert.Equal(new IndirectReference(7, 0), ((PdfReference)fonts[PdfName.Get("F1")]).Target); // the destination's own, untouched
        Assert.True(fonts.ContainsKey(PdfName.Get("F1~2")));
        Assert.Equal("Courier", ((PdfName)((PdfDictionary)run.Resolve(fonts[PdfName.Get("F1~2")]))[PdfName.Get("BaseFont")]).Value);

        var name2 = (PdfDictionary)run.Resolve(FieldsOf(run).Single(f => Text(run.Resolve(f), T) == "Name~2"));
        Assert.Equal("/F1~2 10 Tf 0 g", Text(name2, DA));
        Assert.Contains(("F1", "F1~2"), run.Plan.ResourceRenames);
    }

    [Fact]
    public void TheSameStandardFontUnderTheSameName_IsShared_NotRenamed()
    {
        using var run = Run(Destination(acroForm: "4 0 R", form: DestinationForm));

        var fonts = (PdfDictionary)((PdfDictionary)run.Resolve(run.AcroForm[DR]))[Font];
        Assert.Equal(new IndirectReference(6, 0), ((PdfReference)fonts[PdfName.Get("Helv")]).Target);
        Assert.DoesNotContain(fonts, static e => e.Key.Value.StartsWith("Helv~", StringComparison.Ordinal));
    }

    [Fact]
    public void RichTextStyles_AreLeftAsTheyAre()
    {
        using var run = Run(Destination(acroForm: "4 0 R", form: DestinationForm));

        var name2 = (PdfDictionary)run.Resolve(FieldsOf(run).Single(f => Text(run.Resolve(f), T) == "Name~2"));
        Assert.Equal("font: F1 12pt", Text(name2, DS));
    }

    [Fact]
    public void TheSourcesFormLevelDefaults_AreWrittenOntoIncomingFieldsThatLackTheirOwn()
    {
        using var run = Run(Destination(acroForm: "4 0 R", form: DestinationForm));

        var radio = (PdfDictionary)run.Resolve(FieldsOf(run).Single(f => Text(run.Resolve(f), T) == "Choice"));
        Assert.Equal("/F1~2 9 Tf 0 g", Text(radio, DA)); // the source's form-level /DA, its font renamed
        Assert.Equal(1, ((PdfNumber)radio[Q]).ToInt32());
        Assert.Equal("/Helv 0 Tf 0 g", Text(run.AcroForm, DA)); // the destination's own form-level /DA, untouched
    }

    [Fact]
    public void NeedAppearances_IsOred_AndSigFlagsNeverGainsAppendOnly()
    {
        using var run = Run(Destination(acroForm: "4 0 R", form: DestinationForm));

        Assert.Equal(PdfBoolean.True, run.AcroForm[NeedAppearances]);
        var flags = ((PdfNumber)run.AcroForm[SigFlags]).ToInt32();
        Assert.Equal(1, flags); // SignaturesExist (a signature field came along, empty); never AppendOnly (2)
    }

    [Fact]
    public void CalculationOrder_CarriesTheIncomingFieldsThatCameAlong()
    {
        using var run = Run(Destination(acroForm: "4 0 R", form: DestinationForm));

        var co = (PdfArray)run.AcroForm[CO];
        var only = Assert.Single(co);
        Assert.Equal("Choice", Text(run.Resolve(only), T));
    }

    [Fact]
    public void AnXfaEntry_IsLeftAsItIs()
    {
        using var run = Run(Destination(acroForm: "4 0 R", form: "<< /Fields [5 0 R] /XFA 8 0 R >>", extra: [(8, "<< /Length 0 >>\nstream\n\nendstream")]));

        Assert.Equal(new IndirectReference(8, 0), ((PdfReference)run.AcroForm[XFA]).Target);
    }

    [Theory]
    [InlineData("inline")]
    [InlineData("missing")]
    [InlineData("no-fields")]
    [InlineData("indirect-fields")]
    public void EveryShapeOfDestinationForm_TakesTheIncomingFields(string shape)
    {
        var destination = shape switch
        {
            "inline" => Destination(acroForm: DestinationForm, form: null),
            "missing" => Destination(acroForm: null, form: null),
            "no-fields" => Destination(acroForm: "4 0 R", form: "<< /DA (/Helv 0 Tf 0 g) >>"),
            _ => Destination(acroForm: "4 0 R", form: "<< /Fields 8 0 R /DR << /Font << /Helv 6 0 R /F1 7 0 R >> >> >>", extra: [(8, "[5 0 R]")]),
        };

        using var run = Run(destination);

        var names = FieldsOf(run).Select(f => Text(run.Resolve(f), T)).ToList();
        Assert.Contains("Choice", names);
        Assert.Contains("Sig", names);
        Assert.Equal(shape is "inline" or "indirect-fields", names.Contains("Name~2"));
    }

    [Fact]
    public void TheObjectsToMarkDirty_AreTheDestinationObjectsThatChanged()
    {
        using var indirect = Run(Destination(acroForm: "4 0 R", form: DestinationForm), apply: false);
        using var inline = Run(Destination(acroForm: DestinationForm, form: null), apply: false);
        using var indirectFields = Run(Destination(acroForm: "4 0 R", form: "<< /Fields 8 0 R >>", extra: [(8, "[5 0 R]")]), apply: false);

        Assert.Contains(new IndirectReference(4, 0), indirect.Plan.Dirty);
        Assert.DoesNotContain(new IndirectReference(1, 0), indirect.Plan.Dirty);
        Assert.Contains(new IndirectReference(1, 0), inline.Plan.Dirty);
        Assert.Contains(new IndirectReference(8, 0), indirectFields.Plan.Dirty);
    }

    // --- harness ---------------------------------------------------------------------------

    private sealed class MergeRun(PdfDocument destination, PdfDocument source, StagingTarget staging, FormMergePlan plan) : IDisposable
    {
        public PdfDocument Destination { get; } = destination;
        public StagingTarget Staging { get; } = staging;
        public FormMergePlan Plan { get; } = plan;

        public PdfDictionary AcroForm
        {
            get
            {
                var catalog = (PdfDictionary)Destination.Objects[new IndirectReference(1, 0)];
                return (PdfDictionary)Resolve(catalog[AcroFormMergeIntoTests.AcroForm]);
            }
        }

        public PdfObject Resolve(PdfObject value) => value is PdfReference r
            ? (StagingTarget.IsProvisional(r.Target) ? Staging.Objects[r.Target.Number] : Destination.Objects[r.Target])
            : value;

        public void Dispose()
        {
            Destination.Dispose();
            source.Dispose();
        }
    }

    private static MergeRun Run(byte[] destinationBytes, bool apply = true)
    {
        var destination = PdfDocument.Open(destinationBytes);
        var sourceDocument = PdfDocument.Open(Source());
        var page = sourceDocument.Pages[0];
        var source = new ImportSource(sourceDocument, 0);
        var staging = new StagingTarget();
        var copier = new PageGraphCopier(staging, PageGraphCopier.Prepare([(source, page.Reference, page.Dictionary)]), ImportPolicy.PageAdd);
        copier.ReservePage(source, page.Reference);
        var plan = AcroFormMerger.MergeInto(destination, source, page.Reference, page.Dictionary, copier, staging);
        copier.Drain();
        if (apply)
        {
            plan.Apply();
        }

        return new MergeRun(destination, sourceDocument, staging, plan);
    }

    private static PdfArray FieldsOf(MergeRun run) => (PdfArray)run.Resolve(run.AcroForm[Fields]);

    private static string Text(PdfObject dictionary, PdfName key) =>
        Encoding.Latin1.GetString(((PdfString)((PdfDictionary)dictionary)[key]).Bytes.Span) is var raw && raw.StartsWith("þÿ", StringComparison.Ordinal)
            ? Encoding.BigEndianUnicode.GetString(((PdfString)((PdfDictionary)dictionary)[key]).Bytes.Span[2..])
            : raw;

    /// <summary>A one-page destination: field 5 "Name" (a text widget), fonts 6 (Helvetica as /Helv) and 7 (Times as /F1).</summary>
    private static byte[] Destination(string? acroForm, string? form, (int, string)[]? extra = null)
    {
        var objects = new List<(int, string)>
        {
            (1, acroForm is null ? "<< /Type /Catalog /Pages 2 0 R >>" : $"<< /Type /Catalog /Pages 2 0 R /AcroForm {acroForm} >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>"),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /P 3 0 R /Rect [0 0 50 20] >>"),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>"),
        };
        if (form is not null)
        {
            objects.Add((4, form));
        }

        objects.AddRange(extra ?? []);
        return PdfFixtureBuilder.BuildClassicXrefPdf([.. objects.OrderBy(static o => o.Item1)], rootObjectNumber: 1);
    }

    /// <summary>
    /// A two-page source. Page 3 places: field 5 "Name" (collides; its /DA uses /F1, a different
    /// font from the destination's /F1), radio "Choice" (8, widgets 12 and 13, inheriting the
    /// form-level /DA and /Q), and signature field 9 "Sig". Field 10 "Hidden" is placed on no
    /// page; field 11 "Other" is placed on page 4. Form level: /DA uses /F1, /Q 1,
    /// /NeedAppearances true, /SigFlags 3 (SignaturesExist | AppendOnly), /CO [8 0 R 11 0 R].
    /// </summary>
    private static byte[] Source() => PdfFixtureBuilder.BuildClassicXrefPdf(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 14 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R 12 0 R 13 0 R 9 0 R] >>"),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [11 0 R] >>"),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /P 3 0 R /Rect [0 0 50 20] /DA (/F1 10 Tf 0 g) /DS (font: F1 12pt) >>"),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>"),
            (8, "<< /FT /Btn /Ff 49152 /T (Choice) /Opt [(a) (b)] /Kids [12 0 R 13 0 R] >>"),
            (9, "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig) /P 3 0 R /Rect [0 30 50 50] >>"),
            (10, "<< /FT /Tx /T (Hidden) >>"),
            (11, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Other) /P 4 0 R /Rect [0 0 50 20] >>"),
            (12, "<< /Type /Annot /Subtype /Widget /Parent 8 0 R /P 3 0 R /Rect [60 0 70 10] /AP << /N << /a 15 0 R /Off 15 0 R >> >> >>"),
            (13, "<< /Type /Annot /Subtype /Widget /Parent 8 0 R /P 3 0 R /Rect [80 0 90 10] /AP << /N << /b 15 0 R /Off 15 0 R >> >> >>"),
            (14, "<< /Fields [5 0 R 8 0 R 9 0 R 10 0 R 11 0 R] /DR << /Font << /Helv 6 0 R /F1 7 0 R >> >> /DA (/F1 9 Tf 0 g) /Q 1 /NeedAppearances true /SigFlags 3 /CO [8 0 R 11 0 R] >>"),
            (15, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 0 >>\nstream\n\nendstream"),
        ],
        rootObjectNumber: 1);
}
