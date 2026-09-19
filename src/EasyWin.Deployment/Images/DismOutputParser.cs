using System.Globalization;
using System.Text.RegularExpressions;
using EasyWin.Core.Models;

namespace EasyWin.Deployment.Images;

public static partial class DismOutputParser
{
    public static IReadOnlyList<WindowsImageInfo> ParseImageInfo(string output, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        var indexes = IndexLineRegex().Matches(normalized);
        var editions = new List<WindowsImageInfo>(indexes.Count);
        for (var i = 0; i < indexes.Count; i++)
        {
            var start = indexes[i].Index;
            var end = i + 1 < indexes.Count ? indexes[i + 1].Index : normalized.Length;
            var fields = FieldLineRegex().Matches(normalized[start..end])
                .Cast<Match>()
                .ToDictionary(
                    static match => match.Groups["key"].Value.Trim(),
                    static match => match.Groups["value"].Value.Trim(),
                    StringComparer.OrdinalIgnoreCase);
            var index = ParseRequiredInt(RequiredValue(fields, "Index"), "index");
            var name = RequiredValue(fields, "Name");
            var description = OptionalValue(fields, "Description") ?? name;
            var architecture = OptionalValue(fields, "Architecture") ?? "unknown";
            var version = OptionalValue(fields, "Version") ?? string.Empty;
            var size = ParseLocalizedSize(OptionalValue(fields, "Size"));
            editions.Add(new WindowsImageInfo
            {
                SourcePath = sourcePath,
                ImageIndex = index,
                Name = name,
                Description = description,
                EditionId = ParseEditionId(name),
                Architecture = ParseArchitecture(architecture),
                Version = version,
                SizeBytes = size,
                Container = Path.GetExtension(sourcePath).Equals(".esd", StringComparison.OrdinalIgnoreCase)
                    ? WindowsImageContainer.Esd
                    : WindowsImageContainer.Wim
            });
        }

        if (editions.Count == 0)
        {
            throw new FormatException("DISM output did not contain any Windows image indexes.");
        }

        return editions;
    }

    private static ProcessorArchitecture ParseArchitecture(string value) => value.Trim().ToLowerInvariant() switch
    {
        "x64" or "amd64" or "9" => ProcessorArchitecture.X64,
        "x86" or "0" => ProcessorArchitecture.X86,
        "arm64" or "12" => ProcessorArchitecture.Arm64,
        _ => ProcessorArchitecture.Unknown
    };

    private static string ParseEditionId(string name)
    {
        var normalized = name.Trim();
        var windows = normalized.LastIndexOf("Windows ", StringComparison.OrdinalIgnoreCase);
        if (windows >= 0)
        {
            normalized = normalized[(windows + "Windows ".Length)..];
        }

        return normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    public static int? ParseProgressPercent(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var match = ProgressRegex().Match(line);
        if (!match.Success || !decimal.TryParse(match.Groups["percent"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var percent))
        {
            return null;
        }

        return Math.Clamp((int)Math.Round(percent, MidpointRounding.AwayFromZero), 0, 100);
    }

    private static string RequiredValue(IReadOnlyDictionary<string, string> fields, string field)
    {
        var value = OptionalValue(fields, field);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FormatException($"DISM image block is missing {field}.");
    }

    private static string? OptionalValue(IReadOnlyDictionary<string, string> fields, string field) =>
        fields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int ParseRequiredInt(string text, string field) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new FormatException($"DISM returned an invalid {field}.");

    private static long ParseLocalizedSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    [GeneratedRegex("(?im)^\\s*Index\\s*:\\s*\\d+\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex IndexLineRegex();

    [GeneratedRegex("(?im)^\\s*(?<key>Index|Name|Description|Size|Architecture|Version)\\s*:\\s*(?<value>[^\\n]+?)\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldLineRegex();

    [GeneratedRegex("(?<percent>\\d{1,3}(?:[.,]\\d+)?)%", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressRegex();
}
