using System.Text.Json;
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
        string script = $$"""
            $ErrorActionPreference='Stop'
            $volumes = @(Get-CimInstance -Namespace 'root/CIMV2/Security/MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume -Filter "DriveLetter='{{letter}}:'")
            if ($volumes.Count -ne 1) { throw 'BitLocker volume missing or ambiguous' }
            $conversion = Invoke-CimMethod -InputObject $volumes[0] -MethodName GetConversionStatus
            $protection = Invoke-CimMethod -InputObject $volumes[0] -MethodName GetProtectionStatus
            if ($conversion.ReturnValue -ne 0 -or $protection.ReturnValue -ne 0) { throw 'BitLocker status query failed' }
            [pscustomobject]@{ ConversionStatus=[int]$conversion.ConversionStatus; ProtectionStatus=[int]$protection.ProtectionStatus; EncryptionPercentage=[int]$conversion.EncryptionPercentage } | ConvertTo-Json -Compress
            """;
        var command = new CommandSpec("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))], requiresElevation: true);
        var result = await processRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed("BitLocker status", result);

        return ParseStatus($"{letter}:\\", result.StandardOutput);
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

    public static BitLockerVolumeStatus ParseStatus(string root, string output)
    {
        using var json = JsonDocument.Parse(output);
        int conversion = json.RootElement.GetProperty("ConversionStatus").GetInt32();
        int protection = json.RootElement.GetProperty("ProtectionStatus").GetInt32();
        int percentage = json.RootElement.GetProperty("EncryptionPercentage").GetInt32();
        if (conversion is < 0 or > 5 || protection is < 0 or > 1 || percentage is < 0 or > 100)
            throw new DeploymentSafetyException("bitlocker.unknown", "BitLocker returned an unknown state.");
        return new(root, conversion.ToString(System.Globalization.CultureInfo.InvariantCulture), protection.ToString(System.Globalization.CultureInfo.InvariantCulture), conversion != 0 || percentage != 0, protection != 0);
    }
}
