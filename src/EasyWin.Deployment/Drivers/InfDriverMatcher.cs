using System.Text.RegularExpressions;
using EasyWin.Core.Models;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Drivers;

public interface IInfDriverMatcher
{
    Task<IReadOnlyList<DriverPackage>> DiscoverAsync(string driverRoot, CancellationToken cancellationToken = default);

    IReadOnlyList<DriverMatch> Match(
        IEnumerable<string> detectedHardwareIds,
        IEnumerable<DriverPackage> candidates);
}

public sealed partial class InfDriverMatcher : IInfDriverMatcher
{
    private const int MaximumInfBytes = 4 * 1024 * 1024;

    public async Task<IReadOnlyList<DriverPackage>> DiscoverAsync(
        string driverRoot,
        CancellationToken cancellationToken = default)
    {
        var root = DeploymentGuard.AbsolutePath(driverRoot, "Driver repository", true);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        var result = new List<DriverPackage>();
        foreach (var infPath in Directory.EnumerateFiles(root, "*.inf", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(infPath);
            if (info.Length > MaximumInfBytes)
            {
                continue;
            }

            var text = await File.ReadAllTextAsync(infPath, cancellationToken).ConfigureAwait(false);
            var ids = HardwareIdRegex().Matches(text)
                .Select(match => NormalizeHardwareId(match.Groups["id"].Value))
                .Where(id => id.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ids.Count == 0)
            {
                continue;
            }

            result.Add(new DriverPackage
            {
                Id = Path.GetFileNameWithoutExtension(infPath),
                Name = ReadDirective(text, "Provider") ?? Path.GetFileName(infPath),
                Category = ParseCategory(ReadDirective(text, "Class")),
                InfPath = infPath,
                HardwareIds = ids.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                CompatibleIds = Array.Empty<string>()
            });
        }

        return result;
    }

    public IReadOnlyList<DriverMatch> Match(
        IEnumerable<string> detectedHardwareIds,
        IEnumerable<DriverPackage> candidates)
    {
        ArgumentNullException.ThrowIfNull(detectedHardwareIds);
        ArgumentNullException.ThrowIfNull(candidates);

        var normalizedDevices = detectedHardwareIds
            .Select(NormalizeHardwareId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var driverList = candidates.ToArray();
        var matches = new List<DriverMatch>();

        foreach (var deviceId in normalizedDevices)
        {
            var ranked = driverList
                .Select(driver => new DriverMatch(deviceId, driver, BestRank(deviceId, driver.HardwareIds.Concat(driver.CompatibleIds))))
                .Where(match => match.Rank >= 0)
                .OrderByDescending(match => match.Rank)
                .ThenBy(match => match.Driver.InfPath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (ranked is not null)
            {
                matches.Add(ranked);
            }
        }

        return matches;
    }

    private static int BestRank(string deviceId, IEnumerable<string> supportedIds)
    {
        var best = -1;
        foreach (var supportedId in supportedIds)
        {
            if (deviceId.Equals(supportedId, StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Max(best, 10_000 + supportedId.Length);
                continue;
            }

            // Compatible IDs may be less specific than the detected ID.  Only match on a
            // component boundary; arbitrary substring matches can install a wrong driver.
            if (deviceId.StartsWith(supportedId + "&", StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Max(best, 1_000 + supportedId.Length);
            }
        }

        return best;
    }

    private static string NormalizeHardwareId(string value) =>
        value.Trim().Trim('"').Replace('/', '\\').ToUpperInvariant();

    private static string? ReadDirective(string text, string name)
    {
        var match = Regex.Match(
            text,
            $"(?im)^\\s*{Regex.Escape(name)}\\s*=\\s*(?<value>[^;\\r\\n]+)",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups["value"].Value.Trim().Trim('"') : null;
    }

    private static DriverCategory ParseCategory(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "system" => DriverCategory.Chipset,
            "display" => DriverCategory.Gpu,
            "net" => DriverCategory.Lan,
            "media" => DriverCategory.Audio,
            _ => DriverCategory.Other
        };
    }

    [GeneratedRegex("(?im)^[^;\\r\\n=]+\\s*=\\s*[^,\\r\\n]+,\\s*(?<id>(?:PCI|USB|HDAUDIO|ACPI|SWD|ROOT)\\\\[^,;\\r\\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex HardwareIdRegex();
}
