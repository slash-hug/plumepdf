using PlumePdf.Documents.PageRemoval;
using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// <see cref="DestinationResolver"/>: which page of this document a destination or an action
/// lands on — explicit arrays (direct or indirect), destination dictionaries, names through
/// <c>/Dests</c>, strings through the <c>/Names /Dests</c> tree, and <c>GoTo</c> actions
/// (following <c>/Next</c>, where the first <c>GoTo</c> decides) — and which forms are never
/// local. Cyclic <c>/Next</c> chains, cyclic name trees and self-referencing names end the walk
/// instead of hanging.
/// </summary>
public class DestinationResolverTests
{
    private static readonly byte[] Document = CleanupFixtures.Compose(
        "/Dests 61 0 R /Names << /Dests 62 0 R >>",
        new Dictionary<int, string>
        {
            [60] = "[4 0 R /Fit]",
            [61] = "<< /two [4 0 R /Fit] /one 60 0 R /viaDict << /D [5 0 R /Fit] >> /loop /loop /indexed [1 /Fit] >>",
            [62] = "<< /Kids [63 0 R 64 0 R 62 0 R] >>",
            [63] = "<< /Names [(alpha) [3 0 R /Fit] (beta) 65 0 R] /Limits [(alpha) (beta)] >>",
            [64] = "<< /Names [(two) << /D [4 0 R /Fit] >>] /Limits [(two) (two)] >>",
            [65] = "<< /D [4 0 R /XYZ 0 0 0] >>",
            [70] = "<< /S /GoTo /D [4 0 R /Fit] >>",
            [71] = "<< /S /GoTo /D 60 0 R >>",
            [72] = "<< /S /GoTo /D /two >>",
            [73] = "<< /S /GoTo /D (beta) >>",
            [74] = "<< /S /GoToR /F (other.pdf) /D [4 0 R /Fit] >>",
            [75] = "<< /S /URI /URI (https://example.com/) /Next 70 0 R >>",
            [76] = "<< /S /GoTo /D [3 0 R /Fit] /Next 70 0 R >>",
            [77] = "<< /S /URI /URI (https://example.com/) /Next [78 0 R 70 0 R] >>",
            [78] = "<< /S /GoTo /D [5 0 R /Fit] >>",
            [79] = "<< /S /JavaScript /JS (app.alert\\(1\\)) /Next 80 0 R >>",
            [80] = "<< /S /Named /N /NextPage /Next 79 0 R >>",
            [81] = "<< /S /Launch /F (other.pdf) >>",
            [82] = "<< /S /GoToE /D [4 0 R /Fit] >>",
            [83] = "[1 /Fit]",
            [84] = "<< /S /GoTo /D /missing >>",
            [85] = "<< /S /GoTo /D /loop >>",
            [90] = "<< /Title (both) /Dest /two /A 76 0 R >>",
            [91] = "<< /Title (action) /A 70 0 R >>",
            [92] = "<< /Title (nothing) >>",
            [93] = "<< /Title (indirect dest) /Dest 60 0 R >>",
        });

    [Theory]
    [InlineData(60, 4)] // an indirect explicit array
    [InlineData(65, 4)] // a destination dictionary
    [InlineData(70, 4)] // GoTo with an explicit array
    [InlineData(71, 4)] // GoTo whose /D is indirect
    [InlineData(72, 4)] // GoTo to a name, through /Dests
    [InlineData(73, 4)] // GoTo to a string, through the name tree (a dictionary value, indirect)
    [InlineData(75, 4)] // URI, then GoTo through /Next: the first GoTo decides
    [InlineData(76, 3)] // GoTo to page 1, then GoTo to page 2: the first GoTo decides
    [InlineData(77, 5)] // /Next as an array runs in order
    public void Resolves_LocalDestinationsAndActions(int number, int expectedPage)
    {
        using var document = PdfDocument.Open(Document);
        Assert.Equal(expectedPage, ResolveObject(document, number));
    }

    [Theory]
    [InlineData(74)] // GoToR: another file
    [InlineData(79)] // JavaScript/Named in a cyclic /Next chain, no GoTo anywhere
    [InlineData(81)] // Launch
    [InlineData(82)] // GoToE: an embedded file
    [InlineData(83)] // an integer page index (documented limitation: not a reference)
    [InlineData(84)] // a name /Dests does not define
    [InlineData(85)] // a name whose /Dests value is the name itself
    public void NeverLocal(int number)
    {
        using var document = PdfDocument.Open(Document);
        Assert.Null(ResolveObject(document, number));
    }

    [Fact]
    public void Resolves_NamesThroughDests()
    {
        using var document = PdfDocument.Open(Document);
        Assert.Equal(4, Resolve(document, PdfName.Get("two")));
        Assert.Equal(4, Resolve(document, PdfName.Get("one")));
        Assert.Equal(5, Resolve(document, PdfName.Get("viaDict")));
        Assert.Null(Resolve(document, PdfName.Get("indexed")));
    }

    [Fact]
    public void Resolves_StringsThroughACyclicNameTree()
    {
        // The tree's root lists itself among its /Kids; the walk skips the revisit.
        using var document = PdfDocument.Open(Document);
        Assert.Equal(3, Resolve(document, PdfString.FromLiteral("alpha"u8.ToArray())));
        Assert.Equal(4, Resolve(document, PdfString.FromLiteral("two"u8.ToArray())));
        Assert.Null(Resolve(document, PdfString.FromLiteral("gamma"u8.ToArray())));
    }

    [Fact]
    public void Resolves_DirectExplicitArrays()
    {
        using var document = PdfDocument.Open(Document);
        Assert.Equal(5, Resolve(document, new PdfArray([new PdfReference(new IndirectReference(5, 0)), PdfName.Get("Fit")])));
        Assert.Null(Resolve(document, new PdfArray()));
        Assert.Null(Resolve(document, PdfNumber.Get(3)));
    }

    [Theory]
    [InlineData(90, 4)] // /Dest wins over /A
    [InlineData(91, 4)] // /A alone
    [InlineData(92, null)] // neither
    [InlineData(93, 4)] // an indirect /Dest
    public void ResolveTarget_ReadsDestThenAction(int number, int? expectedPage)
    {
        using var document = PdfDocument.Open(Document);
        var resolver = new DestinationResolver(document.Objects, CleanupFixtures.Catalog(document), PdfOptions.Default);
        var item = Assert.IsType<PdfDictionary>(document.Objects[new IndirectReference(number, 0)]);
        Assert.Equal(expectedPage, resolver.ResolveTarget(item));
    }

    private static int? ResolveObject(PdfDocument document, int number) =>
        Resolve(document, new PdfReference(new IndirectReference(number, 0)));

    private static int? Resolve(PdfDocument document, PdfObject value) =>
        DestinationResolver.ResolveLocalPage(document.Objects, CleanupFixtures.Catalog(document), value, PdfOptions.Default);
}
