using System.Security.Cryptography;
using System.Text;
using PlumePdf.IO;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// <see cref="SigningWriteSession"/>: placeholder-driven <c>/Contents</c>/
/// <c>/ByteRange</c> reservation, offset computation, and the "patch leaves every other byte
/// identical" guarantee the whole two-pass digital-signature technique depends on. Builds its
/// object graph directly against <see cref="ObjectRegistry"/>/<see cref="InMemoryObjectSource"/>
/// (the same low-level approach as <c>IncrementalUpdateWriterEncryptionIdTests</c>) rather
/// than through <c>PdfDocument</c>, since this seam is layer-2 (Objects) plumbing that
/// <c>PdfDocument</c> (layer 5) merely calls into.
/// </summary>
public class SigningWriteSessionTests
{
    private const string SourceText = "%PDF-1.7\n%source-standing-in-for-a-real-document\n%%EOF";

    private static (ObjectRegistry Registry, IndirectReference SignatureReference) BuildRegistryWithSignaturePlaceholder(int reservationBytes = 256, int byteRangePadWidth = 12)
    {
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(2));
        trailer.Set(PdfName.Root, new PdfReference(new IndirectReference(1, 0)));

        var objectsDict = new Dictionary<int, PdfObject> { [1] = new PdfDictionary() };
        var registry = new ObjectRegistry(new InMemoryObjectSource(trailer, objectsDict), nextObjectNumber: 2);

        var sigRef = registry.AllocateNumber();
        var sigDict = new PdfDictionary();
        sigDict.Set(PdfName.Type, PdfName.Get("Sig"));
        sigDict.Set(PdfName.Get("Filter"), PdfName.Get("Adobe.PPKLite"));
        sigDict.Set(PdfName.Get("SubFilter"), PdfName.Get("adbe.pkcs7.detached"));
        sigDict.Set(PdfName.Get("Contents"), new PdfContentsPlaceholder(reservationBytes));
        sigDict.Set(PdfName.Get("ByteRange"), new PdfByteRangePlaceholder(byteRangePadWidth));
        registry.RegisterNew(sigRef, sigDict);

        return (registry, sigRef);
    }

    private static ByteSource MakeSource() => new StreamByteSource(new ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes(SourceText)));

    [Fact]
    public void Create_ReportsSelfConsistentOffsets()
    {
        var (registry, _) = BuildRegistryWithSignaturePlaceholder(reservationBytes: 128, byteRangePadWidth: 10);
        using var source = MakeSource();

        var session = SigningWriteSession.Create(source, registry, pagesTreeDirty: false, topPagesReference: null, pages: [], previousStartXrefOffset: 0, PdfOptions.Default);

        // ContentsLength is the full '<...>' span: two delimiters plus twice the reservation.
        Assert.Equal(2 + (128 * 2), session.ContentsLength);
        Assert.True(session.ContentsOffset >= source.Length, "ContentsOffset must fall inside the appendix, never inside the copied source.");
        Assert.True(session.ByteRangeOffset >= source.Length, "ByteRangeOffset must fall inside the appendix, never inside the copied source.");
        Assert.NotEqual(session.ContentsOffset, session.ByteRangeOffset);
    }

    [Fact]
    public void Create_PlaceholderWidthsAreStableAcrossReservationSizes()
    {
        // The whole two-pass technique depends on the placeholder's *lexical width* never
        // changing between Create() (which fixes every byte offset downstream) and
        // PatchContents (which only ever overwrites bytes already reserved) — assert the
        // written appendix is exactly the length the reservation predicts, for a few
        // different widths, rather than trusting one hard-coded size.
        foreach (var reservationBytes in new[] { 1, 16, 256, 4096 })
        {
            var (registry, _) = BuildRegistryWithSignaturePlaceholder(reservationBytes);
            using var source = MakeSource();

            var session = SigningWriteSession.Create(source, registry, pagesTreeDirty: false, topPagesReference: null, pages: [], previousStartXrefOffset: 0, PdfOptions.Default);

            Assert.Equal(2 + (reservationBytes * 2), session.ContentsLength);
        }
    }

    [Fact]
    public void PatchContents_LeavesEveryOtherByteIdentical()
    {
        var (registry, _) = BuildRegistryWithSignaturePlaceholder(reservationBytes: 64, byteRangePadWidth: 10);
        using var source = MakeSource();

        var session = SigningWriteSession.Create(source, registry, pagesTreeDirty: false, topPagesReference: null, pages: [], previousStartXrefOffset: 0, PdfOptions.Default);

        using var before = new MemoryStream();
        // WriteTo requires PatchContents first (by design — see WriteTo's own test below), so
        // capture the pre-patch appendix bytes via a throwaway signature of the reserved
        // all-zero width instead, to compare against the post-patch bytes at the same offsets.
        session.PatchContents(new byte[64]); // all-zero "signature" — establishes the baseline.
        session.WriteTo(before);
        var beforeBytes = before.ToArray();

        var cms = RandomNumberGenerator.GetBytes(64);
        session.PatchContents(cms);
        using var after = new MemoryStream();
        session.WriteTo(after);
        var afterBytes = after.ToArray();

        Assert.Equal(beforeBytes.Length, afterBytes.Length);

        var hexStart = (int)session.ContentsOffset + 1; // skip '<'
        var hexEnd = hexStart + (64 * 2);
        var expectedHex = Convert.ToHexString(cms); // upper-hex, matching PatchContents's own alphabet.
        Assert.Equal(expectedHex, Encoding.ASCII.GetString(afterBytes, hexStart, hexEnd - hexStart));

        for (var i = 0; i < afterBytes.Length; i++)
        {
            if (i >= hexStart && i < hexEnd)
            {
                continue;
            }

            Assert.True(beforeBytes[i] == afterBytes[i], $"byte at offset {i} changed outside the /Contents reservation.");
        }
    }

    [Fact]
    public void HashDocument_CoversByteRangeSegmentsOnly()
    {
        var (registry, _) = BuildRegistryWithSignaturePlaceholder(reservationBytes: 32, byteRangePadWidth: 10);
        using var source = MakeSource();

        var session = SigningWriteSession.Create(source, registry, pagesTreeDirty: false, topPagesReference: null, pages: [], previousStartXrefOffset: 0, PdfOptions.Default);
        session.PatchContents(new byte[32]);

        using var output = new MemoryStream();
        session.WriteTo(output);
        var fileBytes = output.ToArray();

        using var sessionHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        session.HashDocument(sessionHash);
        var sessionDigest = sessionHash.GetHashAndReset();

        // Independently recompute the expected digest straight from the finished file's own
        // bytes, using ContentsOffset/ContentsLength as the sole source of truth for where the
        // gap is — if these ever drift apart, this test (not just eyeballing) catches it.
        using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        expectedHash.AppendData(fileBytes, 0, (int)session.ContentsOffset);
        var afterGap = (int)(session.ContentsOffset + session.ContentsLength);
        expectedHash.AppendData(fileBytes, afterGap, fileBytes.Length - afterGap);
        var expectedDigest = expectedHash.GetHashAndReset();

        Assert.Equal(expectedDigest, sessionDigest);
    }

    [Fact]
    public void PatchContents_SignatureLargerThanReservation_ThrowsOverflowCode()
    {
        var (registry, _) = BuildRegistryWithSignaturePlaceholder(reservationBytes: 8, byteRangePadWidth: 10);
        using var source = MakeSource();

        var session = SigningWriteSession.Create(source, registry, pagesTreeDirty: false, topPagesReference: null, pages: [], previousStartXrefOffset: 0, PdfOptions.Default);

        var ex = Assert.Throws<PlumePdfException>(() => session.PatchContents(new byte[9]));
        Assert.Equal("PLUME5011", ex.Code);
    }

    [Fact]
    public void WriteTo_BeforePatchContents_Throws()
    {
        var (registry, _) = BuildRegistryWithSignaturePlaceholder();
        using var source = MakeSource();

        var session = SigningWriteSession.Create(source, registry, pagesTreeDirty: false, topPagesReference: null, pages: [], previousStartXrefOffset: 0, PdfOptions.Default);

        using var output = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => session.WriteTo(output));
    }

    [Fact]
    public void Create_MissingContentsPlaceholder_ThrowsInvariantCode()
    {
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(2));
        trailer.Set(PdfName.Root, new PdfReference(new IndirectReference(1, 0)));

        var objectsDict = new Dictionary<int, PdfObject> { [1] = new PdfDictionary() };
        var registry = new ObjectRegistry(new InMemoryObjectSource(trailer, objectsDict), nextObjectNumber: 2);

        // A dirty object with no signature dictionary at all — the session must refuse rather
        // than silently produce an unsignable appendix.
        registry.MarkDirty(new IndirectReference(1, 0));

        using var source = MakeSource();

        var ex = Assert.Throws<PlumePdfException>(() => SigningWriteSession.Create(source, registry, pagesTreeDirty: false, topPagesReference: null, pages: [], previousStartXrefOffset: 0, PdfOptions.Default));
        Assert.Equal("PLUME5013", ex.Code);
    }

    [Fact]
    public void WriteTo_ProducesSourceBytesFollowedByAppendix()
    {
        var (registry, _) = BuildRegistryWithSignaturePlaceholder(reservationBytes: 16, byteRangePadWidth: 10);
        using var source = MakeSource();

        var session = SigningWriteSession.Create(source, registry, pagesTreeDirty: false, topPagesReference: null, pages: [], previousStartXrefOffset: 0, PdfOptions.Default);
        session.PatchContents(new byte[16]);

        using var output = new MemoryStream();
        session.WriteTo(output);
        var fileBytes = output.ToArray();

        var expectedSourceBytes = Encoding.ASCII.GetBytes(SourceText);
        Assert.Equal(expectedSourceBytes, fileBytes[..expectedSourceBytes.Length]);
        Assert.Contains("/Type/Sig", Encoding.ASCII.GetString(fileBytes).Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.EndsWith("%%EOF", Encoding.ASCII.GetString(fileBytes), StringComparison.Ordinal);
    }
}
