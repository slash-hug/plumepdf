using Xunit;

namespace PlumePdf.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void PdfDiagnostic_CarriesAllFields()
    {
        var reference = new IndirectReference(7, 0);
        var diagnostic = new PdfDiagnostic("PLUME2001", DiagnosticSeverity.Warning, "Repaired a malformed offset.", byteOffset: 1234, subject: reference);

        Assert.Equal("PLUME2001", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("Repaired a malformed offset.", diagnostic.Message);
        Assert.Equal(1234, diagnostic.ByteOffset);
        Assert.Equal(reference, diagnostic.Subject);
    }

    [Fact]
    public void PdfDiagnostic_RequiresCodeAndMessage()
    {
        Assert.Throws<ArgumentException>(() => new PdfDiagnostic("", DiagnosticSeverity.Info, "message"));
        Assert.Throws<ArgumentException>(() => new PdfDiagnostic("PLUME2001", DiagnosticSeverity.Info, ""));
    }

    [Fact]
    public void DiagnosticCollection_StartsEmpty()
    {
        var diagnostics = new DiagnosticCollection();
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void DiagnosticCollection_Add_IsVisibleOnEnumeration()
    {
        var diagnostics = new DiagnosticCollection();
        diagnostics.Add(new PdfDiagnostic("PLUME2001", DiagnosticSeverity.Info, "first"));
        diagnostics.Add(new PdfDiagnostic("PLUME2002", DiagnosticSeverity.Warning, "second"));

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(["PLUME2001", "PLUME2002"], diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void DiagnosticCollection_Enumeration_IsASnapshot()
    {
        var diagnostics = new DiagnosticCollection();
        diagnostics.Add(new PdfDiagnostic("PLUME2001", DiagnosticSeverity.Info, "first"));

        var snapshot = diagnostics.ToList();
        diagnostics.Add(new PdfDiagnostic("PLUME2002", DiagnosticSeverity.Info, "second"));

        Assert.Single(snapshot);
        Assert.Equal(2, diagnostics.Count);
    }

    [Fact]
    public void DiagnosticCollection_Add_IsThreadSafeUnderConcurrentAppend()
    {
        var diagnostics = new DiagnosticCollection();
        const int perThread = 500;
        const int threads = 8;

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                diagnostics.Add(new PdfDiagnostic("PLUME2001", DiagnosticSeverity.Info, $"t{t}-{i}"));
            }
        });

        Assert.Equal(threads * perThread, diagnostics.Count);
        Assert.Equal(threads * perThread, diagnostics.Distinct().Count());
    }
}
