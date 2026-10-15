namespace PlumePdf.Filters;

/// <summary>
/// Estimates the IJG quality setting a JPEG was encoded at, from its quantization tables.
/// </summary>
internal static class JpegQualityEstimator
{
    /// <summary>
    /// Reads the <c>DQT</c> segments before the first scan of <paramref name="jpeg"/> and
    /// recovers the quality (1–100) whose scaled standard tables match them. False when the
    /// tables aren't scaled standard tables or the header is unreadable; never throws.
    /// </summary>
    public static bool TryEstimate(ReadOnlySpan<byte> jpeg, out int quality) =>
        throw new NotImplementedException();
}
