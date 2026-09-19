using System.Text.RegularExpressions;
using EasyWin.Core.Processes;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Environment;

public interface IBitLockerService
{
    Task<BitLockerVolumeStatus> GetStatusAsync(string volumeRoot, CancellationToken cancellationToken = default);
}

public sealed partial class BitLockerService(IProcessRunner processRunner) : IBitLockerService
{
    public async Task<BitLockerVolumeStatus> GetStatusAsync(
        string volumeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        var letter = GetVolumeLetter(volumeRoot);
        var command = new CommandSpec(
            "manage-bde.exe",
            ["-status", $"{letter}:"],
            timeout: TimeSpan.FromMinutes(1));
        var result = await processRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed("BitLocker status", result);

        var conversion = GetValue(result.StandardOutput, "Conversion Status") ?? "Unknown";
        var protection = GetValue(result.StandardOutput, "Protection Status") ?? "Unknown";
        var encrypted = !conversion.Contains("Fully Decrypted", StringComparison.OrdinalIgnoreCase) &&
                        !conversion.Contains("0.0%", StringComparison.OrdinalIgnoreCase);
        var protectionEnabled = protection.Contains("Protection On", StringComparison.OrdinalIgnoreCase);
        return new BitLockerVolumeStatus($"{letter}:\\", conversion, protection, encrypted, protectionEnabled);
    }

    private static char GetVolumeLetter(string root)
    {
        var pathRoot = Path.GetPathRoot(Path.GetFullPath(root));
        if (pathRoot is null || pathRoot.Length < 2 || pathRoot[1] != ':')
        {
            throw new DeploymentSafetyException("bitlocker.volume.invalid", "BitLocker checks require a drive-letter volume.");
        }

        return DeploymentGuard.DriveLetter(pathRoot[0]);
    }

    private static string? GetValue(string output, string name)
    {
        var match = Regex.Match(
            output,
            $"(?im)^\\s*{Regex.Escape(name)}\\s*:\\s*(?<value>.+?)\\s*$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }
}
