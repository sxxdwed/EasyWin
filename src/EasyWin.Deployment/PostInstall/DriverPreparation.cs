using System.Text.RegularExpressions;
using EasyWin.Core.Localization;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Core.Security;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.PostInstall;

public sealed record PreparedDrivers(string Root, IReadOnlyList<ManifestFileEntry> Files, IReadOnlyList<string> WinPeInfs, bool NetworkFound)
{
    public long Bytes => Files.Sum(f => f.LengthBytes);
    public async Task ValidateAsync(CancellationToken token)
    {
        var hashes = new Sha256HashService();
        var actual = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase);
        if (!actual.SequenceEqual(Files.Select(f => f.RelativePath).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(DeploymentStrings.Get("DriverVerificationFailed"));
        foreach (var file in Files)
            if (!(await hashes.VerifyFileAsync(PathValidator.ResolveUnderRoot(Root, file.RelativePath, true), file.Sha256, file.LengthBytes, token).ConfigureAwait(false)).IsValid)
                throw new InvalidDataException(DeploymentStrings.Get("DriverVerificationFailed"));
    }
}

public static class DriverPreparation
{
    public static bool IsBootStorage(string inf) => Regex.IsMatch(inf, @"(?im)^\s*Class\s*=\s*(SCSIAdapter|HDC)\s*(;.*)?$", RegexOptions.CultureInvariant);
    public static bool IsNetwork(string inf) => Regex.IsMatch(inf, @"(?im)^\s*Class\s*=\s*Net\s*(;.*)?$", RegexOptions.CultureInvariant);

    public static string FindSignTool()
    {
        string? configured = System.Environment.GetEnvironmentVariable("EASYWIN_SIGNTOOL");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && File.Exists(configured)) return configured;
        string kits = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin");
        string? found = Directory.Exists(kits) ? Directory.EnumerateDirectories(kits).OrderDescending(StringComparer.OrdinalIgnoreCase)
            .Select(folder => Path.Combine(folder, "x64", "signtool.exe")).FirstOrDefault(File.Exists) : null;
        return found ?? throw new FileNotFoundException(DeploymentStrings.Get("DriverSignToolMissing"));
    }

    public static async Task<PreparedDrivers> InspectAsync(string root, string signTool, IProcessRunner runner, IReadOnlySet<string> hardware, DiskBusType targetBus, CancellationToken token)
    {
        var pe = new List<string>(); bool network = false;
        foreach (string inf in Directory.EnumerateFiles(root, "*.inf", SearchOption.AllDirectories))
        {
            _ = PathValidator.ResolveUnderRoot(root, Path.GetRelativePath(root, inf), true);
            string text = await File.ReadAllTextAsync(inf, token).ConfigureAwait(false);
            var section = Regex.Match(text, @"(?ims)^\s*\[Version\]\s*$(.*?)(?=^\s*\[|\z)");
            var catalogs = Regex.Matches(section.Groups[1].Value, @"(?im)^\s*CatalogFile(?:\.NT(?:AMD64)?)?\s*=\s*([^;\r\n]+)")
                .Select(m => m.Groups[1].Value.Trim().Trim('"')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (catalogs.Length != 1) throw new InvalidDataException(DeploymentStrings.Get("DriverVerificationFailed"));
            string catalog = PathValidator.ResolveUnderRoot(Path.GetDirectoryName(inf)!, catalogs[0], true);
            var verified = await runner.RunAsync(new CommandSpec(signTool, ["verify", "/kp", "/c", catalog, inf], timeout: TimeSpan.FromMinutes(2)), token).ConfigureAwait(false);
            if (!verified.Succeeded) throw new InvalidDataException(DeploymentStrings.Get("DriverVerificationFailed"));
            foreach (string binary in Directory.EnumerateFiles(Path.GetDirectoryName(inf)!, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".sys" or ".dll" or ".exe"))
            {
                _ = PathValidator.ResolveUnderRoot(root, Path.GetRelativePath(root, binary), true);
                var member = await runner.RunAsync(new CommandSpec(signTool, ["verify", "/kp", "/c", catalog, binary], timeout: TimeSpan.FromMinutes(2)), token).ConfigureAwait(false);
                if (!member.Succeeded) throw new InvalidDataException(DeploymentStrings.Get("DriverVerificationFailed"));
            }
            bool compatible = hardware.Any(id => text.Contains(id, StringComparison.OrdinalIgnoreCase));
            if (compatible && IsBootStorage(section.Value)) pe.Add(inf);
            network |= compatible && IsNetwork(section.Value);
        }
        if (targetBus == DiskBusType.Raid && pe.Count == 0)
            throw new DeploymentSafetyException("drivers.storage.missing", DeploymentStrings.Get("DriverStorageMissing"));
        var files = new List<ManifestFileEntry>(); var hashes = new Sha256HashService();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            _ = PathValidator.ResolveUnderRoot(root, relative, true);
            files.Add(new() { RelativePath = relative, LengthBytes = new FileInfo(file).Length, Sha256 = await hashes.ComputeSha256Async(file, token).ConfigureAwait(false), Kind = ManifestFileKind.Driver });
        }
        return new(root, files, pe, network);
    }
}
