namespace PlumePdf.Fonts.Reading;

/// <summary>
/// Font-name helpers shared by the read-side factories (moved verbatim out of
/// <see cref="RenderFontFactory"/> so the extraction side's Standard-14 metric
/// lookup and the substitute-font map can apply the same subset-prefix rule:
/// <c>ABCDEF+Symbol</c> must resolve like <c>Symbol</c> everywhere).
/// </summary>
internal static class FontNames
{
    /// <summary>
    /// Strips an ISO 32000-1 §9.6.4 subset tag (<c>ABCDEF+</c> — exactly six uppercase ASCII
    /// letters and a plus sign) from a <c>/BaseFont</c> value; any other name is returned as-is.
    /// </summary>
    /// <param name="name">The raw <c>/BaseFont</c> value.</param>
    public static string StripSubsetPrefix(string name) =>
        name.Length > 7 && name[6] == '+' && IsSubsetTag(name) ? name[7..] : name;

    private static bool IsSubsetTag(string name)
    {
        for (var i = 0; i < 6; i++)
        {
            if (name[i] is < 'A' or > 'Z')
            {
                return false;
            }
        }

        return true;
    }
}
