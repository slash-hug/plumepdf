// FontAssetGen — converts real font files on disk into
// compiled-in `static ReadOnlySpan<byte>` FieldRVA blobs, the same shape
// `Standard14Metrics.g.cs`'s generator established for the 456-byte sRGB ICC profile, scaled up
// to the ~4.9 MB substitute-font bundle. The `static ReadOnlySpan<byte> X => new byte[] { ... }`
// property shape below is not stylistic — it is the exact pattern the Roslyn compiler recognizes
// to emit the byte data into the assembly's PE data section (a "field RVA" blob) instead of
// runtime array-initialization IL: the resulting property is allocation-free and reflection-free
// by construction, which is what keeps this AOT-safe and clear of ReflectionBanTests without any
// gate change.
//
// Run: dotnet run --project tools/FontAssetGen -- <input-dir> <output-dir> [--manifest <ClassName>] [--banner <text>]
// Every *.ttf/*.otf/*.pfb/*.cff file directly under <input-dir> becomes one <PascalName>.g.cs under
// <output-dir>, plus one manifest (default class SubstituteFontBlobs) indexing all of them by face
// key (the font file's name without extension) — a plain switch, not reflection, so
// SubstituteFontStore's lookup stays AOT/trim-safe. `--manifest` names a different manifest class
// so a second asset set (bare-CFF Foxit faces → SubstituteCffBlobs) can be generated
// WITHOUT regenerating — and thereby wiping — the Liberation manifest; `--banner` adds one
// provenance line (e.g. the pinned upstream commit) to every generated file's header.
using System.Text;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: FontAssetGen <input-dir> <output-dir> [--manifest <ClassName>] [--banner <text>]");
    return 1;
}

var inputDir = args[0];
var outputDir = args[1];
var manifestClass = "SubstituteFontBlobs";
string? banner = null;
for (var i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--manifest" when i + 1 < args.Length:
            manifestClass = args[++i];
            break;
        case "--banner" when i + 1 < args.Length:
            banner = args[++i];
            break;
        default:
            Console.Error.WriteLine($"unrecognized argument: {args[i]}");
            return 1;
    }
}

if (!Directory.Exists(inputDir))
{
    Console.Error.WriteLine($"input directory not found: {inputDir}");
    return 1;
}

Directory.CreateDirectory(outputDir);

var files = Directory.EnumerateFiles(inputDir, "*", SearchOption.TopDirectoryOnly)
    .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".pfb", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".cff", StringComparison.OrdinalIgnoreCase))
    .OrderBy(f => f, StringComparer.Ordinal)
    .ToList();

if (files.Count == 0)
{
    Console.Error.WriteLine($"no .ttf/.otf/.pfb/.cff files found directly under {inputDir}");
    return 1;
}

// The exact invocation that produced this output — recorded in every generated file's header so
// a future re-run copies the real command (including --manifest/--banner) instead of the bare
// two-positional-argument form, which would regenerate the DEFAULT manifest (SubstituteFontBlobs)
// and silently wipe out a non-default one (e.g. SubstituteCffBlobs) along with its --banner
// provenance line — exactly the failure mode --manifest/--banner exist to prevent.
//
// Paths are normalized before recording, never baked in raw: an input directory is very often a
// throwaway scratch/mktemp location (e.g. scripts/fetch-foxit-fonts.sh's default), so committing
// the operator's literal absolute path would be both non-reproducible and a machine-identifying
// leak into a permanent, checked-in file. When a path sits under the current directory (the
// documented "run at repo root" usage), its repo-relative form is genuinely reproducible and is
// recorded as-is; otherwise it is replaced with the same generic placeholder this tool's own
// usage line above uses, so the header stays a faithful, copy-pasteable template.
var invocationParts = new List<string>
{
    "dotnet run --project tools/FontAssetGen --",
    NormalizeForRecording(inputDir, "<input-dir>"),
    NormalizeForRecording(outputDir, "<output-dir>"),
};
if (manifestClass != "SubstituteFontBlobs")
{
    invocationParts.Add($"--manifest {manifestClass}");
}

if (banner is not null)
{
    invocationParts.Add($"--banner \"{banner}\"");
}

var invocation = string.Join(' ', invocationParts);

var entries = new List<(string FaceKey, string TypeName, long ByteCount, string RelativeSource)>();

foreach (var file in files)
{
    var faceKey = Path.GetFileNameWithoutExtension(file);
    var typeName = ToPascalCase(faceKey) + "Blob";
    var bytes = File.ReadAllBytes(file);

    var outputPath = Path.Combine(outputDir, ToPascalCase(faceKey) + ".g.cs");
    WriteBlobFile(outputPath, typeName, faceKey, file, bytes, banner, invocation);

    entries.Add((faceKey, typeName, bytes.LongLength, file));
    Console.WriteLine($"{faceKey} -> {typeName} ({bytes.LongLength:N0} bytes)");
}

WriteManifestFile(Path.Combine(outputDir, manifestClass + ".g.cs"), manifestClass, entries, banner);

Console.WriteLine($"Generated {entries.Count} blob(s) + manifest into {outputDir}");
return 0;

static void WriteBlobFile(string path, string typeName, string faceKey, string sourcePath, byte[] bytes, string? banner, string invocation)
{
    var sb = new StringBuilder(bytes.Length * 6);
    sb.AppendLine("// <auto-generated>");
    sb.AppendLine($"// Generated by tools/FontAssetGen from '{Path.GetFileName(sourcePath)}' — do not edit by hand.");
    sb.AppendLine($"// Re-run: {invocation}");
    if (banner is not null)
    {
        sb.AppendLine($"// {banner}");
    }

    sb.AppendLine("// </auto-generated>");
    sb.AppendLine();
    sb.AppendLine("namespace PlumePdf.Fonts.Substitute;");
    sb.AppendLine();
    sb.AppendLine($"/// <summary>Compiled-in font-file bytes for the \"{faceKey}\" substitute face ({bytes.Length:N0} bytes) — a FieldRVA blob: allocation-free, reflection-free, NativeAOT-safe by construction.</summary>");
    sb.AppendLine($"internal static class {typeName}");
    sb.AppendLine("{");
    sb.AppendLine($"    /// <summary>The \"{faceKey}\" face's raw font-file bytes, verbatim.</summary>");
    sb.AppendLine("    public static ReadOnlySpan<byte> Data =>");
    sb.AppendLine("        new byte[]");
    sb.AppendLine("        {");

    const int perLine = 20;
    for (var i = 0; i < bytes.Length; i += perLine)
    {
        sb.Append("            ");
        var end = Math.Min(i + perLine, bytes.Length);
        for (var j = i; j < end; j++)
        {
            sb.Append("0x").Append(bytes[j].ToString("X2")).Append(", ");
        }

        sb.AppendLine();
    }

    sb.AppendLine("        };");
    sb.AppendLine("}");

    File.WriteAllText(path, sb.ToString());
}

static void WriteManifestFile(string path, string manifestClass, List<(string FaceKey, string TypeName, long ByteCount, string RelativeSource)> entries, string? banner)
{
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated>");
    sb.AppendLine("// Generated by tools/FontAssetGen — do not edit by hand.");
    if (banner is not null)
    {
        sb.AppendLine($"// {banner}");
    }

    sb.AppendLine("// Indexes every compiled-in substitute-font blob by face key by name (a plain switch, not");
    sb.AppendLine("// reflection/Assembly.GetTypes — SubstituteFontStore's lookup stays AOT/trim-safe and clear");
    sb.AppendLine("// of ReflectionBanTests without any gate change.");
    sb.AppendLine("// </auto-generated>");
    sb.AppendLine();
    sb.AppendLine("namespace PlumePdf.Fonts.Substitute;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>Name-keyed lookup over every compiled-in substitute-font blob this build generated.</summary>");
    sb.AppendLine($"internal static class {manifestClass}");
    sb.AppendLine("{");
    sb.AppendLine("    /// <summary>Every bundled face key this build compiled in, e.g. for AOT-smoke enumeration.</summary>");
    sb.Append("    public static readonly string[] FaceKeys = [");
    sb.Append(string.Join(", ", entries.Select(e => $"\"{e.FaceKey}\"")));
    sb.AppendLine("];");
    sb.AppendLine();
    sb.AppendLine("    /// <summary>Looks up a compiled-in face's raw font bytes by key (the source file's name without extension, e.g. <c>\"LiberationSans-Regular\"</c>). No reflection — a plain switch over the generated set.</summary>");
    sb.AppendLine("    public static bool TryGetBlob(string faceKey, out ReadOnlySpan<byte> data)");
    sb.AppendLine("    {");
    sb.AppendLine("        switch (faceKey)");
    sb.AppendLine("        {");
    foreach (var (faceKey, typeName, _, _) in entries)
    {
        sb.AppendLine($"            case \"{faceKey}\": data = {typeName}.Data; return true;");
    }

    sb.AppendLine("            default: data = default; return false;");
    sb.AppendLine("        }");
    sb.AppendLine("    }");
    sb.AppendLine("}");

    File.WriteAllText(path, sb.ToString());
}

static string NormalizeForRecording(string path, string placeholder)
{
    var cwd = Directory.GetCurrentDirectory();
    var full = Path.GetFullPath(path);
    var relative = Path.GetRelativePath(cwd, full);

    // Path.GetRelativePath falls back to returning `full` unchanged when there's no relative
    // route (different drive on Windows); ".." walking out of the repo root is the POSIX
    // equivalent for a path outside the current directory — either way it's not a reproducible,
    // repo-relative reference, so record the generic placeholder instead of leaking it.
    var isOutsideCwd = relative == full || relative.StartsWith("..", StringComparison.Ordinal);
    return isOutsideCwd ? placeholder : relative.Replace(Path.DirectorySeparatorChar, '/');
}

static string ToPascalCase(string faceKey)
{
    // Face keys are already PascalCase-ish source file names (e.g. "LiberationSans-Regular");
    // strip separators so the generated type name is a valid, conventional C# identifier.
    var sb = new StringBuilder(faceKey.Length);
    var upperNext = true;
    foreach (var c in faceKey)
    {
        if (c is '-' or '_' or ' ' or '.')
        {
            upperNext = true;
            continue;
        }

        sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
        upperNext = false;
    }

    return sb.ToString();
}
