using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// Golden output for documents saved with <em>no</em> page removed, captured before the
/// removed-page barrier existed in the writers: every removed-page fixture shape plus a few
/// <see cref="WriterTestDocuments"/> documents, in all three layouts under
/// <see cref="PdfOptions.Deterministic"/>. The barrier must be a no-op when nothing is
/// removed, so these hashes never change because of it.
/// </summary>
/// <remarks>
/// A plain <see cref="PdfDocument.Save"/> and <see cref="PdfOptions.Linearize"/> compress
/// nothing, so their goldens are the SHA-256 of the raw bytes (kept per target framework).
/// <see cref="PdfOptions.Optimize"/> Flate-compresses its object and cross-reference streams
/// with the runtime's zlib, whose bytes differ between platforms, so its golden is the hash of
/// <see cref="CanonicalPdf.DecodedFormHash"/>'s decoded form instead.
/// </remarks>
public class NoRemovalByteEqualityTests
{
#if NET10_0_OR_GREATER
    private const string Framework = "net10.0";
#else
    private const string Framework = "net8.0";
#endif

    private static readonly Dictionary<string, Func<byte[]>> Documents = BuildDocuments();

    public static TheoryData<string, SaveLayout> Cases()
    {
        var data = new TheoryData<string, SaveLayout>();
        foreach (var name in Documents.Keys)
        {
            foreach (var layout in Enum.GetValues<SaveLayout>())
            {
                data.Add(name, layout);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SaveWithoutRemoval_MatchesGolden(string document, SaveLayout layout)
    {
        var key = layout == SaveLayout.Optimize ? $"{document}|{layout}" : $"{Framework}|{document}|{layout}";
        Assert.True(Goldens.TryGetValue(key, out var expected), $"No golden for {key}.");
        Assert.Equal(expected, ComputeHash(document, layout));
    }

    internal static IEnumerable<string> DocumentNames => Documents.Keys;

    internal static string FrameworkName => Framework;

    internal static string ComputeHash(string document, SaveLayout layout)
    {
        using var opened = PdfDocument.Open(Documents[document]());
        var saved = RemovedPageFixtures.SaveToBytes(opened, layout);
        return layout == SaveLayout.Optimize ? CanonicalPdf.DecodedFormHash(saved) : CanonicalPdf.Sha256(saved);
    }

    private static Dictionary<string, Func<byte[]>> BuildDocuments()
    {
        var documents = new Dictionary<string, Func<byte[]>>(StringComparer.Ordinal);
        foreach (var shape in RemovedPageFixtures.AllShapes)
        {
            documents[$"shape-{shape}"] = () => RemovedPageFixtures.Build(shape).Bytes;
        }

        documents["writer-3-pages"] = static () => WriterTestDocuments.BuildDocument(3);
        documents["writer-2-pages-info"] = static () => WriterTestDocuments.BuildDocument(2, includeInfo: true);
        documents["writer-inherited-attributes"] = WriterTestDocuments.BuildDocumentWithInheritedAttributes;
        documents["writer-indirect-contents-array"] = static () => WriterTestDocuments.BuildDocumentWithIndirectContentsArray();
        return documents;
    }

    // Captured by running this suite's hashing on the unmodified writers, once per target
    // framework. Keys: "<framework>|<document>|<layout>" for raw hashes, "<document>|Optimize"
    // for the platform-independent decoded-form hash.
    private static readonly Dictionary<string, string> Goldens = new(StringComparer.Ordinal)
    {
        ["net8.0|shape-none|Save"] = "bcaffb1c0ac7b935303209083550c0bbed183a699509e1fdd6b9887d733fc84f",
        ["net8.0|shape-none|Linearize"] = "3dae1d43399a83fe455a18ad391b99f5a56b6049d9499f4e54f3eed05ea51621",
        ["net8.0|shape-A|Save"] = "8109a6285969cb5e29bd09cbe980278e42dac972a09c00df9c1ba0b095b16946",
        ["net8.0|shape-A|Linearize"] = "64ab19d0d841439e83853a143421e4424623fa0912ad4b31d2fd892c466c0ab7",
        ["net8.0|shape-G|Save"] = "84d6e625a3983bcdde15f6ab3d9d1f532dc086b2bbe92a45c4e0d94f86088013",
        ["net8.0|shape-G|Linearize"] = "3798281cd702a97a2596e9e181183fc3e2457191b6c48983008c6d511533481d",
        ["net8.0|shape-D|Save"] = "4c8a0575ae79035d1f0d42a079f42f0a1f83f3892c8c1657668d279b8238863b",
        ["net8.0|shape-D|Linearize"] = "7ffe9ac5170f5ac0ae73b41464ac334f64044d2848e9ce66936076ea9c104134",
        ["net8.0|shape-M|Save"] = "8d63617ae0a1872e044c89130b7dc6c89e19999c18046979145a170c936573c0",
        ["net8.0|shape-M|Linearize"] = "bbea17928d37d1efcf363bceff104abced595227406d684b2c1f9d44b4c7ce15",
        ["net8.0|shape-B|Save"] = "eef709da4dedc8df6f30abbaee471bccfff37f1f8c4471c1f44563573b34bb45",
        ["net8.0|shape-B|Linearize"] = "72bef04e8e6505e721e53de532476b8be3590d245b7aa557b270b55e81ce1ea6",
        ["net8.0|shape-F|Save"] = "a2ed173d1bfe1dfced221071fe318e38023ed292de5c177f36fd1f211c062f77",
        ["net8.0|shape-F|Linearize"] = "c1ea68d28343970d5598ac67084416be489ff4b252507b755b5301cc5cdf672a",
        ["net8.0|shape-K|Save"] = "abfb65a2e96c712fd60f38683fccb800bd3fa5b72366d2613b95e0ecbf2e3e19",
        ["net8.0|shape-K|Linearize"] = "6daa4cbe33eba24c838d39c5a1ff3b02a8a4072265072d819706c2f72ecfff74",
        ["net8.0|shape-W|Save"] = "4c88aa87ee9b1e0a81b6cfc527afda55e889a621e1d4525c6de5084f430bc540",
        ["net8.0|shape-W|Linearize"] = "961a0e4de785786ef472f660bccf032b0d02db6e458eeeedff8cce8f769b7036",
        ["net8.0|shape-R|Save"] = "d95438f303017a49996b9a6b4e542483cd6baf4ca3e3a5d5de961c0618468451",
        ["net8.0|shape-R|Linearize"] = "8c3302bd1ebe1453c8d068ef1d44669e6ac3f386997cd001592d091565da4f44",
        ["net8.0|shape-L|Save"] = "dca1e5879a0ba86b1ff6b55e382c1375af05d5a79e90eb8359e39ded0697870a",
        ["net8.0|shape-L|Linearize"] = "c99ccb6342fdb63efaaeb56791777cd0d9a5ac0175a47b7d6a8488190e27df2a",
        ["net8.0|shape-O|Save"] = "6adcb371aa8de2ee08602233fa093882d6b39ad1eb92ef1ab48c0980c139d38a",
        ["net8.0|shape-O|Linearize"] = "8441bc5782b5943890fbb0f87e9fa01f1afbc8f1715b81fb44a070f3f5ac82da",
        ["net8.0|shape-P|Save"] = "f26e6890ad61a8282d09564921a91b0a58053170709cbe12fb4eb4de7194fba7",
        ["net8.0|shape-P|Linearize"] = "afc88400dea788c11813324761a5b47d47b142cada9824de21f289b0ed71c12c",
        ["net8.0|shape-S|Save"] = "7ae12d8d3b397bccb1b55711bac7b448183a9df5ce2ff57cfcd700c26b8b615e",
        ["net8.0|shape-S|Linearize"] = "97b9fa861fcfc5a61c6ec327c70da8364ea69553ef3b7f8e07e7cd9417f0b692",
        ["net8.0|shape-X|Save"] = "ac6250388e2f5fa3ffaea2cf1425a093275e0c5b0496e4c3d07408b5f5dab4d1",
        ["net8.0|shape-X|Linearize"] = "bda01e2649e5e9d6ca1a813eba78c8ad57f464b5a906efdb465150e1ea321b24",
        ["net8.0|shape-T|Save"] = "c1488680689d912d105b187f7f48bb53f7e5cdcf9ad3f42fc11bb313846ea0db",
        ["net8.0|shape-T|Linearize"] = "9bfdbfd6e19fc4404b7df85348f18ed0cbd31c9fc9ce5ffc0085f373e12cbdfb",
        ["net8.0|shape-N|Save"] = "24b6aa3a849f67a42f94d39d173d955ae5f414a27d65b957048af747a4b9099c",
        ["net8.0|shape-N|Linearize"] = "3baf0e85798ef3fb01f0f160e141a58915ec4bb4c6ec89962303d91578f4e040",
        ["net8.0|shape-I|Save"] = "1c42bd2ae706856d1ada0d3eb7de2f25bbefe595f054539fa7a9cbd19e125209",
        ["net8.0|shape-I|Linearize"] = "0677c5ce8d998d3055f903b14db09ceca11e3647358bbe62d7457d64f6f0b872",
        ["net8.0|shape-Q|Save"] = "78d1f51be65d63797d6206784301c65f197b86991875bd8331a27d58ed778d93",
        ["net8.0|shape-Q|Linearize"] = "3ed6b601e274f5276f7335c14fa2654866db8613a830b7034946c7e8a53a687d",
        ["net8.0|shape-E1|Save"] = "4b6e3a746b1e16ae217cfb40cfda49828c850d4db792a0c32b1d539344e43d28",
        ["net8.0|shape-E1|Linearize"] = "7cde61186e02ef50e3067fc6b39ced675e4904a0e0421c75af0252c1cec2d4e9",
        ["net8.0|shape-E2|Save"] = "5fc955a5ea682ec683ce25744a79caff8f7833257c07b4c2784f3bcef766c26f",
        ["net8.0|shape-E2|Linearize"] = "5d1166af9c884c7a6e002b16aa050ee05559a7dbe2078c93a94630bff85952e2",
        ["net8.0|shape-E3|Save"] = "3484a140ebaecd2eebbc08630f6e53a4e81c7c71958cf35fb485f0226cbb1413",
        ["net8.0|shape-E3|Linearize"] = "372ad7df2ad2b2db9448fcef580d2bf92179fc5873917baa46421d720e4aa93e",
        ["net8.0|shape-C|Save"] = "b5996f4dd01ce4a6a8f4f7a34c7ee47d207e344f25f35fe6b893f2e389ea3d2e",
        ["net8.0|shape-C|Linearize"] = "c8b53373358c0a01d6bf27ea2e0af9783b2a356a9d3136cd92802d2863e6876a",
        ["net8.0|writer-3-pages|Save"] = "c1f5fb25ce113a5bb5ce2392d1e60e8b5323271574205f796b4793a5068bd636",
        ["net8.0|writer-3-pages|Linearize"] = "e5bb567feb1ba179a457bd995653cbd504bd58f9974e6a19d6c600b0dc3cd27c",
        ["net8.0|writer-2-pages-info|Save"] = "81e5f4bd22998282ee41bd44dfd171aa5740f01ceb01ad04325a5eea7ad38fce",
        ["net8.0|writer-2-pages-info|Linearize"] = "9add4689d9131f3b4b1b926a380de59c71fce93c7d77215624cf597b7892b103",
        ["net8.0|writer-inherited-attributes|Save"] = "eabdf45a94783cca611d1ef07e0d677e06c01f9a9638f2a4aaaa39e4952ffd98",
        ["net8.0|writer-inherited-attributes|Linearize"] = "0718cf22d34f845f3993a82a4d2ef96e1b71de4e5070a4723ddbc10c0996bfec",
        ["net8.0|writer-indirect-contents-array|Save"] = "9c3410963cbc2f76821fd208135b9658cc3752047deecd5d0587e1c711eac88e",
        ["net8.0|writer-indirect-contents-array|Linearize"] = "c08be6fb9c833b5496d4a0e9b234aba0fc63fed941d9c11aee4c199dea0043e3",
        ["net10.0|shape-none|Save"] = "bcaffb1c0ac7b935303209083550c0bbed183a699509e1fdd6b9887d733fc84f",
        ["net10.0|shape-none|Linearize"] = "3dae1d43399a83fe455a18ad391b99f5a56b6049d9499f4e54f3eed05ea51621",
        ["net10.0|shape-A|Save"] = "8109a6285969cb5e29bd09cbe980278e42dac972a09c00df9c1ba0b095b16946",
        ["net10.0|shape-A|Linearize"] = "64ab19d0d841439e83853a143421e4424623fa0912ad4b31d2fd892c466c0ab7",
        ["net10.0|shape-G|Save"] = "84d6e625a3983bcdde15f6ab3d9d1f532dc086b2bbe92a45c4e0d94f86088013",
        ["net10.0|shape-G|Linearize"] = "3798281cd702a97a2596e9e181183fc3e2457191b6c48983008c6d511533481d",
        ["net10.0|shape-D|Save"] = "4c8a0575ae79035d1f0d42a079f42f0a1f83f3892c8c1657668d279b8238863b",
        ["net10.0|shape-D|Linearize"] = "7ffe9ac5170f5ac0ae73b41464ac334f64044d2848e9ce66936076ea9c104134",
        ["net10.0|shape-M|Save"] = "8d63617ae0a1872e044c89130b7dc6c89e19999c18046979145a170c936573c0",
        ["net10.0|shape-M|Linearize"] = "bbea17928d37d1efcf363bceff104abced595227406d684b2c1f9d44b4c7ce15",
        ["net10.0|shape-B|Save"] = "eef709da4dedc8df6f30abbaee471bccfff37f1f8c4471c1f44563573b34bb45",
        ["net10.0|shape-B|Linearize"] = "72bef04e8e6505e721e53de532476b8be3590d245b7aa557b270b55e81ce1ea6",
        ["net10.0|shape-F|Save"] = "a2ed173d1bfe1dfced221071fe318e38023ed292de5c177f36fd1f211c062f77",
        ["net10.0|shape-F|Linearize"] = "c1ea68d28343970d5598ac67084416be489ff4b252507b755b5301cc5cdf672a",
        ["net10.0|shape-K|Save"] = "abfb65a2e96c712fd60f38683fccb800bd3fa5b72366d2613b95e0ecbf2e3e19",
        ["net10.0|shape-K|Linearize"] = "6daa4cbe33eba24c838d39c5a1ff3b02a8a4072265072d819706c2f72ecfff74",
        ["net10.0|shape-W|Save"] = "4c88aa87ee9b1e0a81b6cfc527afda55e889a621e1d4525c6de5084f430bc540",
        ["net10.0|shape-W|Linearize"] = "961a0e4de785786ef472f660bccf032b0d02db6e458eeeedff8cce8f769b7036",
        ["net10.0|shape-R|Save"] = "d95438f303017a49996b9a6b4e542483cd6baf4ca3e3a5d5de961c0618468451",
        ["net10.0|shape-R|Linearize"] = "8c3302bd1ebe1453c8d068ef1d44669e6ac3f386997cd001592d091565da4f44",
        ["net10.0|shape-L|Save"] = "dca1e5879a0ba86b1ff6b55e382c1375af05d5a79e90eb8359e39ded0697870a",
        ["net10.0|shape-L|Linearize"] = "c99ccb6342fdb63efaaeb56791777cd0d9a5ac0175a47b7d6a8488190e27df2a",
        ["net10.0|shape-O|Save"] = "6adcb371aa8de2ee08602233fa093882d6b39ad1eb92ef1ab48c0980c139d38a",
        ["net10.0|shape-O|Linearize"] = "8441bc5782b5943890fbb0f87e9fa01f1afbc8f1715b81fb44a070f3f5ac82da",
        ["net10.0|shape-P|Save"] = "f26e6890ad61a8282d09564921a91b0a58053170709cbe12fb4eb4de7194fba7",
        ["net10.0|shape-P|Linearize"] = "afc88400dea788c11813324761a5b47d47b142cada9824de21f289b0ed71c12c",
        ["net10.0|shape-S|Save"] = "7ae12d8d3b397bccb1b55711bac7b448183a9df5ce2ff57cfcd700c26b8b615e",
        ["net10.0|shape-S|Linearize"] = "97b9fa861fcfc5a61c6ec327c70da8364ea69553ef3b7f8e07e7cd9417f0b692",
        ["net10.0|shape-X|Save"] = "ac6250388e2f5fa3ffaea2cf1425a093275e0c5b0496e4c3d07408b5f5dab4d1",
        ["net10.0|shape-X|Linearize"] = "bda01e2649e5e9d6ca1a813eba78c8ad57f464b5a906efdb465150e1ea321b24",
        ["net10.0|shape-T|Save"] = "c1488680689d912d105b187f7f48bb53f7e5cdcf9ad3f42fc11bb313846ea0db",
        ["net10.0|shape-T|Linearize"] = "9bfdbfd6e19fc4404b7df85348f18ed0cbd31c9fc9ce5ffc0085f373e12cbdfb",
        ["net10.0|shape-N|Save"] = "24b6aa3a849f67a42f94d39d173d955ae5f414a27d65b957048af747a4b9099c",
        ["net10.0|shape-N|Linearize"] = "3baf0e85798ef3fb01f0f160e141a58915ec4bb4c6ec89962303d91578f4e040",
        ["net10.0|shape-I|Save"] = "1c42bd2ae706856d1ada0d3eb7de2f25bbefe595f054539fa7a9cbd19e125209",
        ["net10.0|shape-I|Linearize"] = "0677c5ce8d998d3055f903b14db09ceca11e3647358bbe62d7457d64f6f0b872",
        ["net10.0|shape-Q|Save"] = "78d1f51be65d63797d6206784301c65f197b86991875bd8331a27d58ed778d93",
        ["net10.0|shape-Q|Linearize"] = "3ed6b601e274f5276f7335c14fa2654866db8613a830b7034946c7e8a53a687d",
        ["net10.0|shape-E1|Save"] = "4b6e3a746b1e16ae217cfb40cfda49828c850d4db792a0c32b1d539344e43d28",
        ["net10.0|shape-E1|Linearize"] = "7cde61186e02ef50e3067fc6b39ced675e4904a0e0421c75af0252c1cec2d4e9",
        ["net10.0|shape-E2|Save"] = "5fc955a5ea682ec683ce25744a79caff8f7833257c07b4c2784f3bcef766c26f",
        ["net10.0|shape-E2|Linearize"] = "5d1166af9c884c7a6e002b16aa050ee05559a7dbe2078c93a94630bff85952e2",
        ["net10.0|shape-E3|Save"] = "3484a140ebaecd2eebbc08630f6e53a4e81c7c71958cf35fb485f0226cbb1413",
        ["net10.0|shape-E3|Linearize"] = "372ad7df2ad2b2db9448fcef580d2bf92179fc5873917baa46421d720e4aa93e",
        ["net10.0|shape-C|Save"] = "b5996f4dd01ce4a6a8f4f7a34c7ee47d207e344f25f35fe6b893f2e389ea3d2e",
        ["net10.0|shape-C|Linearize"] = "c8b53373358c0a01d6bf27ea2e0af9783b2a356a9d3136cd92802d2863e6876a",
        ["net10.0|writer-3-pages|Save"] = "c1f5fb25ce113a5bb5ce2392d1e60e8b5323271574205f796b4793a5068bd636",
        ["net10.0|writer-3-pages|Linearize"] = "e5bb567feb1ba179a457bd995653cbd504bd58f9974e6a19d6c600b0dc3cd27c",
        ["net10.0|writer-2-pages-info|Save"] = "81e5f4bd22998282ee41bd44dfd171aa5740f01ceb01ad04325a5eea7ad38fce",
        ["net10.0|writer-2-pages-info|Linearize"] = "9add4689d9131f3b4b1b926a380de59c71fce93c7d77215624cf597b7892b103",
        ["net10.0|writer-inherited-attributes|Save"] = "eabdf45a94783cca611d1ef07e0d677e06c01f9a9638f2a4aaaa39e4952ffd98",
        ["net10.0|writer-inherited-attributes|Linearize"] = "0718cf22d34f845f3993a82a4d2ef96e1b71de4e5070a4723ddbc10c0996bfec",
        ["net10.0|writer-indirect-contents-array|Save"] = "9c3410963cbc2f76821fd208135b9658cc3752047deecd5d0587e1c711eac88e",
        ["net10.0|writer-indirect-contents-array|Linearize"] = "c08be6fb9c833b5496d4a0e9b234aba0fc63fed941d9c11aee4c199dea0043e3",
        ["shape-none|Optimize"] = "041e41173c8db2a2db1e78ad19f039195db954fd928f66ee5337657ab3500195",
        ["shape-A|Optimize"] = "9556289fcecc60965b93f7138e169e57da8ead94af87e4835680dc7c31ac1eaf",
        ["shape-G|Optimize"] = "1e15fba866bb8f10c104a56ff3337a22b1b794a40bc20ce594f6f14737ed803f",
        ["shape-D|Optimize"] = "a9a6643fd4cd61c13abffcb22eaea848f63726ef0aef0f1a71a7f242a5329eb0",
        ["shape-M|Optimize"] = "07dba209ac41a4458074de519e077fcc7c53c0407dd8248d774dc1e7e41f187f",
        ["shape-B|Optimize"] = "0fcc21c4200fe4c22ad32a9e6d0cb8b22bf8672840dd7e1061a20bcefad38fd8",
        ["shape-F|Optimize"] = "ef9a62ddc3df77e10bd634ae8354ff2c8a30243bf55a5f99b2b643ecea877cc0",
        ["shape-K|Optimize"] = "198fd537639863a6395f5e9852355961377a3bad414c248b5bbdb92193e3823b",
        ["shape-W|Optimize"] = "4b57c67b6922bb56e80876a551605193b6ec61e255d80c196ecbc63206e13590",
        ["shape-R|Optimize"] = "f5c683fa223c0ac9778392244449485e2f5260b5e2fdc71cb07a1bbd90f54da0",
        ["shape-L|Optimize"] = "ac228e9cd07384d102c47dfa10d5e69dae91e531a4f0de255c6d187b46947bb8",
        ["shape-O|Optimize"] = "592c184cff8d14bc5ea84d2440faba95e88668a39a8575551a12b5c194b5fe02",
        ["shape-P|Optimize"] = "657d0652380d65f5960ea524c0abe717fcab9d99ce2e1e8d241a2ecc4da69ba1",
        ["shape-S|Optimize"] = "f53f309a857066f972b432215f3c3f88d1685bba2619cf1f87013753564117a9",
        ["shape-X|Optimize"] = "5f448e4c1ad7de2ba6d92d1014d7b817542408754fb63746a09d4090c63b1b75",
        ["shape-T|Optimize"] = "89ad3ed001d441d23f68309afa75a2ce0c270e971374d73479007636b923dca1",
        ["shape-N|Optimize"] = "9b51f60367abbb458123eecd14e0ca9ffc1465cec4a045ca000d99c274171f1b",
        ["shape-I|Optimize"] = "4e5d5104109d3436724aab1668b175688fa0e0d507eec7ec9ef8c2207cca9a80",
        ["shape-Q|Optimize"] = "d533f64e43b896ccb62b237c1f05841ec4011cb71ef721c31f6637cabb6798b4",
        ["shape-E1|Optimize"] = "ddbaaa6e06ca8bfed4beb7a12365f36274da8925f73fe2722600b13a4b5142fc",
        ["shape-E2|Optimize"] = "5ddfc0700c1849e95add0653748fcbf56deffee04df27abae2f3fb0bd15325c4",
        ["shape-E3|Optimize"] = "32ce3386c21e65c07158af0fef524862f53f8931d87c1aea667ce31afbc463d2",
        ["shape-C|Optimize"] = "569db66b9249482b8dccc70eb6fe5ed6ca0bf447876d04eaf288cd69619dc497",
        ["writer-3-pages|Optimize"] = "7774a7cea812400fda5f4697765107e5b5d4665c4b64707d475297072ee12aae",
        ["writer-2-pages-info|Optimize"] = "aeb3e592c38b6d2b3c78e700e46d95eb7b4ec7b8bc4c841446b359f1ce3837bc",
        ["writer-inherited-attributes|Optimize"] = "bb7dadebb272c9bc8657caffa4e37618de1bc5cee55d053631a506665f7fa8f4",
        ["writer-indirect-contents-array|Optimize"] = "9319785337ae0cd294eb4397ed4191a861894f45905fc4cf9abadd36a64ea0a8",
    };
}
