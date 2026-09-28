using System.Diagnostics;
using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// <c>ObjectRegistry.AllocateNumber</c>: allocation order, free-list generation
/// reuse, and collision-safety against a source whose declared <c>/Size</c> undercounts its
/// real highest object number. Also covers the single-writer debug guard, which
/// lives in the same allocator/mutation infrastructure.
/// </summary>
public class AllocatorTests
{
    private static ObjectRegistry BuildRegistry(int nextObjectNumber, IReadOnlyCollection<int>? freeObjectNumbers = null)
    {
        var trailer = new PdfDictionary();
        var source = new InMemoryObjectSource(trailer, new Dictionary<int, PdfObject>());
        return new ObjectRegistry(source, nextObjectNumber, freeObjectNumbers);
    }

    [Fact]
    public void AllocateNumber_NoFreeList_ReturnsAscendingNumbersFromSeed_AtGenerationZero()
    {
        var registry = BuildRegistry(nextObjectNumber: 5);

        var first = registry.AllocateNumber();
        var second = registry.AllocateNumber();
        var third = registry.AllocateNumber();

        Assert.Equal(new IndirectReference(5, 0), first);
        Assert.Equal(new IndirectReference(6, 0), second);
        Assert.Equal(new IndirectReference(7, 0), third);
    }

    [Fact]
    public void AllocateNumber_FreeList_ReusedSmallestFirst_AtGenerationOne()
    {
        // §7.5.4: a freed object number is reused with an incremented generation. The reader
        // doesn't preserve the freed entry's own stored next-generation value (CrossReferenceReader
        // discards it), so the allocator's conservative reading is "generation 1" for any reused
        // number — see ObjectRegistry.AllocateNumber's remarks.
        var registry = BuildRegistry(nextObjectNumber: 10, freeObjectNumbers: [7, 3]);

        var first = registry.AllocateNumber();
        var second = registry.AllocateNumber();

        Assert.Equal(new IndirectReference(3, 1), first);
        Assert.Equal(new IndirectReference(7, 1), second);
    }

    [Fact]
    public void AllocateNumber_FreeListExhausted_ExtendsBeyondSeed_NeverRevisitingFreeNumbers()
    {
        var registry = BuildRegistry(nextObjectNumber: 10, freeObjectNumbers: [3]);

        var reused = registry.AllocateNumber();
        var extended1 = registry.AllocateNumber();
        var extended2 = registry.AllocateNumber();

        Assert.Equal(new IndirectReference(3, 1), reused);
        Assert.Equal(new IndirectReference(10, 0), extended1);
        Assert.Equal(new IndirectReference(11, 0), extended2);
    }

    [Fact]
    public void AllocateNumber_ExcludesObjectZero_EvenWhenPassedInFreeList()
    {
        // Object 0 is always the free-list head/terminator (§7.5.4) — never a real, allocatable
        // object, even if a caller (or a lenient/recovered table) reports it as "free".
        var registry = BuildRegistry(nextObjectNumber: 4, freeObjectNumbers: [0]);

        var allocated = registry.AllocateNumber();

        Assert.Equal(new IndirectReference(4, 0), allocated);
    }

    [Fact]
    public void AllocateNumber_RepeatedCalls_AreDeterministicAcrossFreshRegistries()
    {
        // R-m: the same starting state produces the exact same allocation sequence every time —
        // the guarantee PdfOptions.Deterministic's writer output depends on.
        var freeNumbers = new[] { 9, 2, 5 };
        var registryA = BuildRegistry(nextObjectNumber: 20, freeNumbers);
        var registryB = BuildRegistry(nextObjectNumber: 20, freeNumbers);

        var sequenceA = new[] { registryA.AllocateNumber(), registryA.AllocateNumber(), registryA.AllocateNumber(), registryA.AllocateNumber() };
        var sequenceB = new[] { registryB.AllocateNumber(), registryB.AllocateNumber(), registryB.AllocateNumber(), registryB.AllocateNumber() };

        Assert.Equal(sequenceA, sequenceB);
    }

    [Fact]
    public void AllocateNumber_OnOpenedDocument_NeverCollidesWithAnObjectNumberDeclaredSizeUndercounts()
    {
        // A hand-built, deliberately malformed document: four real objects (1-4) but a trailer
        // /Size that only claims three exist (0-2). CrossReferenceReader parses every xref
        // subsection entry regardless of what /Size later claims, so PdfDocument.OpenCore must
        // seed the allocator from the actual highest entry it saw, not from /Size alone, or
        // AllocateNumber here would collide with the real object 4.
        var path = WriteUndersizedDocument();
        try
        {
            using var document = PdfDocument.Open(path);

            var allocated = document.Objects.AllocateNumber();

            Assert.True(allocated.Number > 4, $"expected a number beyond the real highest object (4), got {allocated.Number}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteUndersizedDocument()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"));
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteObject(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R >>");
        var content = "BT ET";
        offsets[4] = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes("xref\n0 5\n0000000000 65535 f \n"));
        for (var n = 1; n < 5; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        // Deliberately understated: only 3 of the 5 declared xref entries "officially" exist.
        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size 3 /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-undersized-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, [.. buffer]);
        return path;
    }

#if DEBUG
    [Fact]
    public void Mutation_WhileReadInProgress_TripsSingleWriterGuard()
    {
        // Deterministic, not racy: rather than trying to actually race two threads (which can
        // pass "by luck" on a fast machine), directly hold a mutation open via EnterExternalMutation
        // (never paired with ExitExternalMutation in this test) and then read — the indexer's
        // Debug.Assert must trip immediately and unconditionally, exactly as it would if a real
        // mutation call overlapped a real concurrent read from parallel page extraction.
        // The exact exception type is host-dependent (the xunit/vstest test host installs its
        // own TraceListener translating a failed Debug.Assert into one of its own before
        // ThrowingAssertListener below ever gets a turn) — ThrowsAny<Exception> asserts only
        // that Debug.Assert actually failed, not which listener happened to report it.
        var registry = BuildRegistry(nextObjectNumber: 1);
        var listener = new ThrowingAssertListener();
        Trace.Listeners.Add(listener);
        try
        {
            registry.EnterExternalMutation();
            Assert.ThrowsAny<Exception>(() => _ = registry[new IndirectReference(0, 0)]);
        }
        finally
        {
            registry.ExitExternalMutation();
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void Mutation_WithoutOverlappingRead_NeverTripsTheGuard()
    {
        // The negative case: ordinary, non-overlapping single-threaded mutation (allocate,
        // register, read back) must never trip the guard, however many times it happens.
        var registry = BuildRegistry(nextObjectNumber: 1);
        var listener = new ThrowingAssertListener();
        Trace.Listeners.Add(listener);
        try
        {
            var reference = registry.AllocateNumber();
            registry.RegisterNew(reference, PdfNumber.Get(1));
            registry.MarkDirty(reference);
            Assert.Equal(PdfNumber.Get(1), registry[reference]);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    /// <summary>Converts a <see cref="Debug.Assert(bool,string)"/> failure into a thrown exception so it's observable from a test — the default <see cref="TraceListener"/> only logs and continues.</summary>
    private sealed class ThrowingAssertListener : TraceListener
    {
        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
        }

        public override void Fail(string? message) => throw new InvalidOperationException($"Debug.Assert failed: {message}");

        public override void Fail(string? message, string? detailMessage) => throw new InvalidOperationException($"Debug.Assert failed: {message} {detailMessage}");
    }
#endif
}
