using System.Text;
using PlumePdf.Documents;
using PlumePdf.IO;
using PlumePdf.Objects;

namespace PlumePdf.Tests.TestSupport;

/// <summary>
/// The "is it physically still in the file" probe shared by the redaction and page-removal
/// confidentiality suites. Brute-force reconstructs every object present in a saved file
/// (<see cref="RecoveryScanner"/>'s "N G obj" scan, independent of any cross-reference table or
/// catalog reachability), filter-decodes every stream it finds — object streams included, so an
/// object packed by <see cref="PdfOptions.Optimize"/> is searched too — and checks both the
/// decoded stream bytes and every string value for a needle. Reachability alone would not catch
/// a writer bug that still emitted bytes somewhere unreferenced.
/// </summary>
/// <remarks>
/// Defensive by design: a single malformed or unparseable recovered object is skipped rather
/// than failing the scan (matching <see cref="RecoveryScanner"/>'s own lenient-by-default
/// philosophy) — callers care whether the needle survives <em>anywhere parseable</em>, not
/// whether every byte in the file parses as a well-formed object.
/// </remarks>
internal static class RecoveredBytes
{
    /// <summary>Whether any recovered object in <paramref name="fileBytes"/> contains <paramref name="needle"/>'s Latin-1 bytes.</summary>
    public static bool AnyRecoveredObjectContains(byte[] fileBytes, string needle) =>
        AnyRecoveredObjectContainsByteRun(fileBytes, Encoding.Latin1.GetBytes(needle));

    /// <summary>Whether any recovered object in <paramref name="fileBytes"/> contains the byte run <paramref name="needle"/>.</summary>
    public static bool AnyRecoveredObjectContainsByteRun(byte[] fileBytes, ReadOnlySpan<byte> needle)
    {
        using var source = new StreamByteSource(fileBytes.AsMemory());
        var table = RecoveryScanner.Scan(source, PdfOptions.Default, diagnostics: null);

        foreach (var (_, entry) in table.EntriesByObjectNumber)
        {
            if (entry.Kind != CrossReferenceEntryKind.InFile || entry.ByteOffset >= fileBytes.Length)
            {
                continue;
            }

            PdfObject value;
            try
            {
                var slice = fileBytes.AsSpan((int)entry.ByteOffset).ToArray();
                value = ObjectParser.ParseIndirectObject(slice, PdfOptions.Default, diagnostics: null, out _);
            }
            catch (PlumePdfException)
            {
                continue;
            }

            if (ContainsByteRun(value, needle))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsByteRun(PdfObject value, ReadOnlySpan<byte> needle)
    {
        switch (value)
        {
            case PdfString s:
                return s.Bytes.Span.IndexOf(needle) >= 0;

            case PdfStream stream:
                if (stream.RawBytes.Span.IndexOf(needle) >= 0)
                {
                    return true;
                }

                try
                {
                    var decoded = stream.GetDecodedBytes(PdfFilterRegistry.Default, PdfOptions.Default);
                    return decoded.AsSpan().IndexOf(needle) >= 0;
                }
                catch (PlumePdfException)
                {
                    return false;
                }

            case PdfDictionary dict:
                foreach (var (_, entryValue) in dict)
                {
                    if (ContainsByteRun(entryValue, needle))
                    {
                        return true;
                    }
                }

                return false;

            case PdfArray array:
                foreach (var element in array)
                {
                    if (ContainsByteRun(element, needle))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }
}
