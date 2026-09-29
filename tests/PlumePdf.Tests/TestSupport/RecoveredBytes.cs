using System.Text;
using System.Text.RegularExpressions;
using PlumePdf.Objects;

namespace PlumePdf.Tests.TestSupport;

/// <summary>
/// The "is it physically still in the file" probe shared by the redaction and page-removal
/// confidentiality suites. Searches the raw file bytes, then brute-force parses every object
/// present in a saved file (every "N G obj" header, independent of any cross-reference table or
/// catalog reachability), filter-decodes every stream it finds — object streams included, so an
/// object packed by <see cref="PdfOptions.Optimize"/> is searched too — and checks both the
/// decoded stream bytes and every string value for a needle. Reachability alone would not catch
/// a writer bug that still emitted bytes somewhere unreferenced.
/// </summary>
/// <remarks>
/// Defensive by design: a single malformed or unparseable recovered object is skipped rather
/// than failing the scan (matching the recovery scanner's own lenient-by-default philosophy) —
/// callers care whether the needle survives <em>anywhere parseable</em>, not whether every
/// byte in the file parses as a well-formed object.
/// </remarks>
internal static class RecoveredBytes
{
    private static readonly Regex ObjectHeader = new(@"(?<![0-9])[0-9]+[ \t\r\n\f\0]+[0-9]+[ \t\r\n\f\0]+obj(?![A-Za-z])", RegexOptions.CultureInvariant);

    /// <summary>Whether any recovered object in <paramref name="fileBytes"/> contains <paramref name="needle"/>'s Latin-1 bytes.</summary>
    public static bool AnyRecoveredObjectContains(byte[] fileBytes, string needle) =>
        AnyRecoveredObjectContainsByteRun(fileBytes, Encoding.Latin1.GetBytes(needle));

    /// <summary>Whether any recovered object in <paramref name="fileBytes"/> contains the byte run <paramref name="needle"/>.</summary>
    public static bool AnyRecoveredObjectContainsByteRun(byte[] fileBytes, ReadOnlySpan<byte> needle)
    {
        // The raw bytes first: an uncompressed needle anywhere in the file (inside an object or
        // not) is recoverable by definition.
        if (fileBytes.AsSpan().IndexOf(needle) >= 0)
        {
            return true;
        }

        foreach (var offset in ObjectHeaderOffsets(fileBytes))
        {
            PdfObject value;
            try
            {
                value = ObjectParser.ParseIndirectObject(fileBytes.AsSpan(offset), PdfOptions.Default, diagnostics: null, out _);
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

    // Every "N G obj" header in the file, each one parsed where it stands. Deliberately not
    // RecoveryScanner's rebuilt table: that keeps one entry per object number (an older copy of
    // a number is never visited) and refuses a file with neither a 'trailer' keyword nor an
    // uncompressed catalog, which is exactly what an Optimize save (cross-reference stream,
    // catalog packed in an object stream) looks like.
    private static IEnumerable<int> ObjectHeaderOffsets(byte[] fileBytes)
    {
        foreach (Match match in ObjectHeader.Matches(Encoding.Latin1.GetString(fileBytes)))
        {
            yield return match.Index;
        }
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
