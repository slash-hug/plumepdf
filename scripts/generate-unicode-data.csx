#!/usr/bin/env dotnet-script
// Dev-time generator: fetches a pinned Unicode Character Database (UCD) release and emits
// one range-compressed, reflection-free, AOT-safe C# table:
//   src/PlumePdf/Fonts/Shaping/UnicodeShapingData.g.cs
//
// This script is NOT run in CI and is NOT shipped — it is a one-time (or on-UNICODE_VERSION-
// bump) authoring tool, exactly like scripts/generate-standard14.csx. The checked-in .g.cs
// file is the actual build input; nothing in src/ ever parses UCD text or touches
// System.Globalization.CharUnicodeInfo at run time (CharUnicodeInfo tracks the host
// runtime's own bundled UCD version, which varies across patch levels and would silently
// break PdfOptions.Deterministic byte-identity for shaped output). Run it with:
//   dotnet script scripts/generate-unicode-data.csx
//
// Provenance: the Unicode Character Database, © Unicode, Inc., distributed under the Unicode
// License (https://www.unicode.org/license.txt) — permissive, attribution-required. This
// script transcribes UCD *data* (property values, one codepoint/range per line) into tables;
// it does not reproduce any Unicode Standard *spec prose*. See NOTICE and
// docs/spec-sources.md for the attribution record.
//
// UNICODE_VERSION is the single source of truth, pinned exactly like scripts/fetch-corpora.sh's
// VERAPDF_VERSION-style pins elsewhere in this repo. Bumping it is a documented, deliberate
// minor-version behavior change (shaped output may change across a bump; it must never change
// within one) — never a routine "pick up latest UCD" edit.
using System.Net.Http;
using System.Text;

const string UNICODE_VERSION = "16.0.0";

var repoRoot = Directory.GetCurrentDirectory();
var cacheDir = Path.Combine(repoRoot, "corpora", "ucd", UNICODE_VERSION);
var outDir = Path.Combine(repoRoot, "src", "PlumePdf", "Fonts", "Shaping");
var baseUrl = $"https://www.unicode.org/Public/{UNICODE_VERSION}/ucd";

Directory.CreateDirectory(cacheDir);
Directory.CreateDirectory(outDir);

var http = new HttpClient();

async Task<string[]> FetchLinesAsync(string relativePath)
{
    var cachePath = Path.Combine(cacheDir, relativePath.Replace('/', '_'));
    if (!File.Exists(cachePath))
    {
        Console.WriteLine($"fetching {relativePath}…");
        var text = await http.GetStringAsync($"{baseUrl}/{relativePath}");
        File.WriteAllText(cachePath, text);
    }
    else
    {
        Console.WriteLine($"{relativePath}: already cached");
    }

    return File.ReadAllLines(cachePath);
}

// --- Generic UCD range-list parsing -----------------------------------------------
// Every UCD data file used here shares one shape: optional leading whitespace, a code point
// or "start..end" range, a ';'-separated field list, then an optional '#' comment to end of
// line. This walks that shared shape once; each property's own parsing below only decides
// which field(s) it wants and how to turn them into a value string/enum member name.
IEnumerable<(int Start, int End, string[] Fields)> ParseUcdLines(IEnumerable<string> lines)
{
    foreach (var raw in lines)
    {
        var line = raw;
        var hashIndex = line.IndexOf('#');
        if (hashIndex >= 0)
        {
            line = line[..hashIndex];
        }

        line = line.Trim();
        if (line.Length == 0)
        {
            continue;
        }

        var fields = line.Split(';');
        for (var i = 0; i < fields.Length; i++)
        {
            fields[i] = fields[i].Trim();
        }

        var codeField = fields[0];
        int start, end;
        var rangeSep = codeField.IndexOf("..", StringComparison.Ordinal);
        if (rangeSep >= 0)
        {
            start = int.Parse(codeField[..rangeSep], System.Globalization.NumberStyles.HexNumber);
            end = int.Parse(codeField[(rangeSep + 2)..], System.Globalization.NumberStyles.HexNumber);
        }
        else
        {
            start = end = int.Parse(codeField, System.Globalization.NumberStyles.HexNumber);
        }

        yield return (start, end, fields[1..]);
    }
}

// Merges adjacent/overlapping same-value entries into the smallest sorted, non-overlapping
// range list that still answers every lookup identically — this is the actual "range
// compression": a source file with one line per Unicode block still collapses to one row
// per genuine property-value transition.
List<(int Start, int End, string Value)> Compress(IEnumerable<(int Start, int End, string Value)> entries)
{
    var sorted = entries.OrderBy(e => e.Start).ToList();
    var result = new List<(int Start, int End, string Value)>();
    foreach (var e in sorted)
    {
        if (result.Count > 0)
        {
            var last = result[^1];
            if (string.Equals(last.Value, e.Value, StringComparison.Ordinal) && e.Start <= last.End + 1)
            {
                if (e.End > last.End)
                {
                    result[^1] = (last.Start, e.End, last.Value);
                }

                continue;
            }
        }

        result.Add(e);
    }

    return result;
}

string SanitizeIdentifier(string value) => value.Replace('-', '_').Replace(' ', '_');

// --- Fetch every source file -------------------------------------------------------
var scriptsLines = await FetchLinesAsync("Scripts.txt");
var arabicShapingLines = await FetchLinesAsync("ArabicShaping.txt");
var indicSyllabicLines = await FetchLinesAsync("IndicSyllabicCategory.txt");
var indicPositionalLines = await FetchLinesAsync("IndicPositionalCategory.txt");
var bidiClassLines = await FetchLinesAsync("extracted/DerivedBidiClass.txt");
var bidiMirroringLines = await FetchLinesAsync("BidiMirroring.txt");
var combiningClassLines = await FetchLinesAsync("extracted/DerivedCombiningClass.txt");
var graphemeBreakLines = await FetchLinesAsync("auxiliary/GraphemeBreakProperty.txt");

// --- Parse each property ------------------------------------------------------------

// Script (run itemization / ScriptSegmenter).
var scriptEntries = ParseUcdLines(scriptsLines).Select(e => (e.Start, e.End, Value: SanitizeIdentifier(e.Fields[0]))).ToList();
var scriptRanges = Compress(scriptEntries);
var scriptValues = scriptRanges.Select(r => r.Value).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).Append("Unknown").Distinct(StringComparer.Ordinal).ToList();

// Joining_Type + Joining_Group (Arabic shaper). ArabicShaping.txt lists
// single code points only (field 1 = schematic name/comment, field 2 = Joining_Type short
// code, field 3 = Joining_Group). The short codes (R/L/D/C/U/T) are transcribed verbatim —
// they are Unicode's own official abbreviations (UAX #44 §5.7.4), not an invented shorthand.
var joiningTypeEntries = ParseUcdLines(arabicShapingLines).Select(e => (e.Start, e.End, Value: e.Fields[1])).ToList();
var joiningTypeRanges = Compress(joiningTypeEntries);
var joiningGroupEntries = ParseUcdLines(arabicShapingLines).Select(e => (e.Start, e.End, Value: SanitizeIdentifier(e.Fields[2]))).ToList();
var joiningGroupRanges = Compress(joiningGroupEntries);
var joiningGroupValues = joiningGroupRanges.Select(r => r.Value).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).Append("No_Joining_Group").Distinct(StringComparer.Ordinal).ToList();

// Indic_Syllabic_Category + Indic_Positional_Category (Devanagari shaper).
var indicSyllabicEntries = ParseUcdLines(indicSyllabicLines).Select(e => (e.Start, e.End, Value: SanitizeIdentifier(e.Fields[0]))).ToList();
var indicSyllabicRanges = Compress(indicSyllabicEntries);
var indicSyllabicValues = indicSyllabicRanges.Select(r => r.Value).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).Append("Other").Distinct(StringComparer.Ordinal).ToList();

var indicPositionalEntries = ParseUcdLines(indicPositionalLines).Select(e => (e.Start, e.End, Value: SanitizeIdentifier(e.Fields[0]))).ToList();
var indicPositionalRanges = Compress(indicPositionalEntries);
var indicPositionalValues = indicPositionalRanges.Select(r => r.Value).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).Append("NA").Distinct(StringComparer.Ordinal).ToList();

// Bidi_Class (UAX #9 bidi algorithm). extracted/DerivedBidiClass.txt is
// already fully default-expanded across all of Unicode (including unassigned code points),
// so no @missing-range handling is needed here.
var bidiClassEntries = ParseUcdLines(bidiClassLines).Select(e => (e.Start, e.End, Value: e.Fields[0])).ToList();
var bidiClassRanges = Compress(bidiClassEntries);
var bidiClassValues = bidiClassRanges.Select(r => r.Value).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();

// Bidi_Mirroring (UAX #9 L4, glyph mirroring under RTL). Sparse single-codepoint pairs, not
// range-compressible the same way (each mirror target differs) — kept as an exact-match table.
var mirrorPairs = ParseUcdLines(bidiMirroringLines)
    .Select(e => (Codepoint: e.Start, Mirror: int.Parse(e.Fields[0], System.Globalization.NumberStyles.HexNumber)))
    .OrderBy(p => p.Codepoint)
    .ToList();

// Canonical_Combining_Class (UAX #9 bidi + subsequent normalization-adjacent work). Numeric
// (0-254), so no enum — the raw byte value round-trips through the range table directly.
var cccEntries = ParseUcdLines(combiningClassLines).Select(e => (e.Start, e.End, Value: e.Fields[0])).ToList();
var cccRanges = Compress(cccEntries);

// Grapheme_Cluster_Break (UAX #29, cluster-safe line-wrap boundaries). Not
// default-expanded in the source file; codepoints absent from it default to "Other" per UAX #29.
var graphemeBreakEntries = ParseUcdLines(graphemeBreakLines).Select(e => (e.Start, e.End, Value: SanitizeIdentifier(e.Fields[0]))).ToList();
var graphemeBreakRanges = Compress(graphemeBreakEntries);
var graphemeBreakValues = graphemeBreakRanges.Select(r => r.Value).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).Append("Other").Distinct(StringComparer.Ordinal).ToList();

// --- Emit C# -------------------------------------------------------------------------
var sb = new StringBuilder();

void EmitEnum(string name, IEnumerable<string> values, string summary)
{
    sb.AppendLine($"/// <summary>{summary}</summary>");
    sb.AppendLine($"internal enum {name}");
    sb.AppendLine("{");
    foreach (var v in values)
    {
        sb.AppendLine($"    {v},");
    }

    sb.AppendLine("}");
    sb.AppendLine();
}

void EmitRangeTable(string fieldName, string enumOrPrimitiveType, IEnumerable<(int Start, int End, string Value)> ranges, bool valueIsEnum)
{
    sb.AppendLine($"    private static readonly (int Start, int End, {enumOrPrimitiveType} Value)[] {fieldName} =");
    sb.AppendLine("    {");
    foreach (var (start, end, value) in ranges)
    {
        var literal = valueIsEnum ? $"{enumOrPrimitiveType}.{value}" : value;
        sb.AppendLine($"        (0x{start:X}, 0x{end:X}, {literal}),");
    }

    sb.AppendLine("    };");
    sb.AppendLine();
}

sb.AppendLine("// <auto-generated>");
sb.AppendLine($"// Generated by scripts/generate-unicode-data.csx from the Unicode Character Database {UNICODE_VERSION}.");
sb.AppendLine("// Source: the Unicode Character Database, (c) Unicode, Inc., distributed under the Unicode");
sb.AppendLine("// License (https://www.unicode.org/license.txt). See NOTICE and docs/spec-sources.md for");
sb.AppendLine("// the attribution record. Do not edit by hand — re-run the generator.");
sb.AppendLine("// </auto-generated>");
sb.AppendLine();
sb.AppendLine("namespace PlumePdf.Fonts.Shaping;");
sb.AppendLine();

EmitEnum("UnicodeScript", scriptValues, $"The Unicode <c>Script</c> property (UAX #24), {UNICODE_VERSION}. Drives run itemization (<c>ScriptSegmenter</c>) and script-tier dispatch.");
EmitEnum("JoiningType", ["R", "L", "D", "C", "U", "T"], "The Unicode <c>Joining_Type</c> property (UAX #44 §5.7.4), transcribed as Unicode's own official short codes: R=Right_Joining, L=Left_Joining, D=Dual_Joining, C=Join_Causing, U=Non_Joining, T=Transparent.");
EmitEnum("JoiningGroup", joiningGroupValues, "The Unicode <c>Joining_Group</c> property (UAX #44 §5.7.5) — which cursive-joining shape family a character belongs to (e.g. Beh, Feh, Ain).");
EmitEnum("IndicSyllabicCategory", indicSyllabicValues, "The Unicode <c>Indic_Syllabic_Category</c> property (UTR #53) — an Indic character's structural role in syllable formation.");
EmitEnum("IndicPositionalCategory", indicPositionalValues, "The Unicode <c>Indic_Positional_Category</c> property (UTR #53) — where a dependent vowel sign (matra) visually attaches relative to its base consonant.");
EmitEnum("BidiClass", bidiClassValues, "The Unicode <c>Bidi_Class</c> property (UAX #9), fully default-expanded across all of Unicode — the input to the bidirectional algorithm's weak/neutral/implicit resolution rules.");
EmitEnum("GraphemeClusterBreak", graphemeBreakValues, "The Unicode <c>Grapheme_Cluster_Break</c> property (UAX #29) — codepoints absent from the source data default to <see cref=\"Other\"/> per the standard.");

sb.AppendLine("/// <summary>");
sb.AppendLine($"/// Range-compressed Unicode Character Database properties ({UNICODE_VERSION}), generated");
sb.AppendLine("/// by <c>scripts/generate-unicode-data.csx</c> — Script, Joining_Type/Group, Indic_Syllabic/");
sb.AppendLine("/// Positional_Category, Bidi_Class, Bidi_Mirroring, Canonical_Combining_Class, and");
sb.AppendLine("/// Grapheme_Cluster_Break. Every lookup is a binary search over a sorted, non-overlapping range");
sb.AppendLine("/// array — no runtime ICU/<see cref=\"System.Globalization.CharUnicodeInfo\"/> dependency (which");
sb.AppendLine("/// tracks the host runtime's own UCD version and would silently break");
sb.AppendLine("/// <see cref=\"PlumePdf.PdfOptions.Deterministic\"/> byte-identity across .NET patch levels), no");
sb.AppendLine("/// reflection, no embedded-resource loading — reflection-free and NativeAOT-safe throughout.");
sb.AppendLine("/// </summary>");
sb.AppendLine("internal static class UnicodeShapingData");
sb.AppendLine("{");

EmitRangeTable("ScriptRanges", "UnicodeScript", scriptRanges, valueIsEnum: true);
EmitRangeTable("JoiningTypeRanges", "JoiningType", joiningTypeRanges, valueIsEnum: true);
EmitRangeTable("JoiningGroupRanges", "JoiningGroup", joiningGroupRanges, valueIsEnum: true);
EmitRangeTable("IndicSyllabicCategoryRanges", "IndicSyllabicCategory", indicSyllabicRanges, valueIsEnum: true);
EmitRangeTable("IndicPositionalCategoryRanges", "IndicPositionalCategory", indicPositionalRanges, valueIsEnum: true);
EmitRangeTable("BidiClassRanges", "BidiClass", bidiClassRanges, valueIsEnum: true);
EmitRangeTable("CombiningClassRanges", "byte", cccRanges.Select(r => (r.Start, r.End, Value: r.Value)), valueIsEnum: false);
EmitRangeTable("GraphemeClusterBreakRanges", "GraphemeClusterBreak", graphemeBreakRanges, valueIsEnum: true);

sb.AppendLine("    private static readonly (int Codepoint, int Mirror)[] BidiMirrorPairs =");
sb.AppendLine("    {");
foreach (var (codepoint, mirror) in mirrorPairs)
{
    sb.AppendLine($"        (0x{codepoint:X}, 0x{mirror:X}),");
}

sb.AppendLine("    };");
sb.AppendLine();

sb.AppendLine("    /// <summary>Binary-searches a sorted, non-overlapping range table for the value covering <paramref name=\"codepoint\"/>, or <paramref name=\"fallback\"/> if none does.</summary>");
sb.AppendLine("    private static TValue Lookup<TValue>((int Start, int End, TValue Value)[] ranges, int codepoint, TValue fallback)");
sb.AppendLine("    {");
sb.AppendLine("        var lo = 0;");
sb.AppendLine("        var hi = ranges.Length - 1;");
sb.AppendLine("        while (lo <= hi)");
sb.AppendLine("        {");
sb.AppendLine("            var mid = lo + (hi - lo) / 2;");
sb.AppendLine("            var (start, end, value) = ranges[mid];");
sb.AppendLine("            if (codepoint < start) { hi = mid - 1; }");
sb.AppendLine("            else if (codepoint > end) { lo = mid + 1; }");
sb.AppendLine("            else { return value; }");
sb.AppendLine("        }");
sb.AppendLine();
sb.AppendLine("        return fallback;");
sb.AppendLine("    }");
sb.AppendLine();

sb.AppendLine("    /// <summary>The Unicode <c>Script</c> property of <paramref name=\"codepoint\"/>; <see cref=\"UnicodeScript.Unknown\"/> if unassigned/unlisted.</summary>");
sb.AppendLine("    public static UnicodeScript GetScript(int codepoint) => Lookup(ScriptRanges, codepoint, UnicodeScript.Unknown);");
sb.AppendLine();
sb.AppendLine("    /// <summary>The Unicode <c>Joining_Type</c> of <paramref name=\"codepoint\"/>; <see cref=\"JoiningType.U\"/> (Non_Joining) if unlisted — Unicode's own default for characters outside the cursive-joining scripts.</summary>");
sb.AppendLine("    public static JoiningType GetJoiningType(int codepoint) => Lookup(JoiningTypeRanges, codepoint, JoiningType.U);");
sb.AppendLine();
sb.AppendLine("    /// <summary>The Unicode <c>Joining_Group</c> of <paramref name=\"codepoint\"/>; <see cref=\"JoiningGroup.No_Joining_Group\"/> if unlisted.</summary>");
sb.AppendLine("    public static JoiningGroup GetJoiningGroup(int codepoint) => Lookup(JoiningGroupRanges, codepoint, JoiningGroup.No_Joining_Group);");
sb.AppendLine();
sb.AppendLine("    /// <summary>The Unicode <c>Indic_Syllabic_Category</c> of <paramref name=\"codepoint\"/>; <see cref=\"IndicSyllabicCategory.Other\"/> if unlisted.</summary>");
sb.AppendLine("    public static IndicSyllabicCategory GetIndicSyllabicCategory(int codepoint) => Lookup(IndicSyllabicCategoryRanges, codepoint, IndicSyllabicCategory.Other);");
sb.AppendLine();
sb.AppendLine("    /// <summary>The Unicode <c>Indic_Positional_Category</c> of <paramref name=\"codepoint\"/>; <see cref=\"IndicPositionalCategory.NA\"/> if unlisted.</summary>");
sb.AppendLine("    public static IndicPositionalCategory GetIndicPositionalCategory(int codepoint) => Lookup(IndicPositionalCategoryRanges, codepoint, IndicPositionalCategory.NA);");
sb.AppendLine();
sb.AppendLine("    /// <summary>The Unicode <c>Bidi_Class</c> of <paramref name=\"codepoint\"/> (UAX #9) — the source data is fully default-expanded, so every codepoint in 0..10FFFF resolves to a real value.</summary>");
sb.AppendLine("    public static BidiClass GetBidiClass(int codepoint) => Lookup(BidiClassRanges, codepoint, BidiClass.L);");
sb.AppendLine();
sb.AppendLine("    /// <summary>The Unicode Canonical_Combining_Class of <paramref name=\"codepoint\"/>; 0 (not reordered) if unlisted, matching UAX #9/UAX #15's default.</summary>");
sb.AppendLine("    public static byte GetCombiningClass(int codepoint) => Lookup(CombiningClassRanges, codepoint, (byte)0);");
sb.AppendLine();
sb.AppendLine("    /// <summary>The Unicode <c>Grapheme_Cluster_Break</c> of <paramref name=\"codepoint\"/> (UAX #29); <see cref=\"GraphemeClusterBreak.Other\"/> if unlisted.</summary>");
sb.AppendLine("    public static GraphemeClusterBreak GetGraphemeClusterBreak(int codepoint) => Lookup(GraphemeClusterBreakRanges, codepoint, GraphemeClusterBreak.Other);");
sb.AppendLine();

sb.AppendLine("    /// <summary>Unicode <c>Bidi_Mirroring_Glyph</c> (UAX #9 L4): the mirrored codepoint to substitute when <paramref name=\"codepoint\"/> is drawn in a right-to-left run, or <see langword=\"false\"/> if it has none.</summary>");
sb.AppendLine("    public static bool TryGetBidiMirror(int codepoint, out int mirror)");
sb.AppendLine("    {");
sb.AppendLine("        var lo = 0;");
sb.AppendLine("        var hi = BidiMirrorPairs.Length - 1;");
sb.AppendLine("        while (lo <= hi)");
sb.AppendLine("        {");
sb.AppendLine("            var mid = lo + (hi - lo) / 2;");
sb.AppendLine("            var (pairCodepoint, pairMirror) = BidiMirrorPairs[mid];");
sb.AppendLine("            if (codepoint < pairCodepoint) { hi = mid - 1; }");
sb.AppendLine("            else if (codepoint > pairCodepoint) { lo = mid + 1; }");
sb.AppendLine("            else { mirror = pairMirror; return true; }");
sb.AppendLine("        }");
sb.AppendLine();
sb.AppendLine("        mirror = 0;");
sb.AppendLine("        return false;");
sb.AppendLine("    }");
sb.AppendLine("}");

var outPath = Path.Combine(outDir, "UnicodeShapingData.g.cs");
File.WriteAllText(outPath, sb.ToString());
Console.WriteLine($"wrote UnicodeShapingData.g.cs: {scriptRanges.Count} script ranges, {joiningTypeRanges.Count} joining-type ranges, " +
    $"{joiningGroupRanges.Count} joining-group ranges, {indicSyllabicRanges.Count} Indic syllabic ranges, " +
    $"{indicPositionalRanges.Count} Indic positional ranges, {bidiClassRanges.Count} bidi-class ranges, " +
    $"{cccRanges.Count} CCC ranges, {graphemeBreakRanges.Count} grapheme-break ranges, {mirrorPairs.Count} mirror pairs.");
