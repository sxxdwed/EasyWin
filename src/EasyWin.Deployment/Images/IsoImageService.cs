using EasyWin.Core.Processes;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;
using System.Text;

namespace EasyWin.Deployment.Images;

public interface IIsoImageService
{
    Task<MountedIso> MountAsync(string isoOrMediaPath, CancellationToken cancellationToken = default);

    string LocateInstallImage(string mediaRoot);

    Task UnmountAsync(MountedIso mountedIso, CancellationToken cancellationToken = default);
}

public sealed class IsoImageService(IProcessRunner processRunner) : IIsoImageService
{
    private const string MountOperation = "$i=Get-DiskImage -ImagePath $p -ErrorAction SilentlyContinue; if(-not $i -or -not $i.Attached){$i=Mount-DiskImage -ImagePath $p -PassThru}; ($i | Get-Volume).Path";
    private const string DismountOperation = "$i=Get-DiskImage -ImagePath $p -ErrorAction SilentlyContinue; if($i -and $i.Attached){Dismount-DiskImage -ImagePath $p}";

    public async Task<MountedIso> MountAsync(string isoOrMediaPath, CancellationToken cancellationToken = default)
    {
        var normalized = DeploymentGuard.AbsolutePath(isoOrMediaPath, "Windows ISO or media directory", true);
        if (Directory.Exists(normalized))
        {
            return new MountedIso(normalized, normalized, false);
        }

        if (!Path.GetExtension(normalized).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            throw new DeploymentSafetyException("iso.extension.invalid", "The selected media must be an ISO file or extracted media directory.");
        }

        var command = CreatePowerShellCommand(MountOperation, normalized);
        var result = await processRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed("Mount Windows ISO", result);
        var root = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw new InvalidOperationException("Windows mounted the ISO but did not return a readable volume root.");
        }

        return new MountedIso(normalized, Path.GetFullPath(root), true);
    }

    public string LocateInstallImage(string mediaRoot)
    {
        var root = DeploymentGuard.AbsolutePath(mediaRoot, "Windows media root", true);
        var candidates = new[]
        {
            Path.Combine(root, "sources", "install.wim"),
            Path.Combine(root, "sources", "install.esd")
        }.Where(File.Exists).ToArray();

        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new FileNotFoundException("The media does not contain sources\\install.wim or sources\\install.esd."),
            _ => throw new DeploymentSafetyException("image.ambiguous", "The media contains both install.wim and install.esd.")
        };
    }

    public async Task UnmountAsync(MountedIso mountedIso, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mountedIso);
        if (!mountedIso.MountedByEasyWin)
        {
            return;
        }

        var command = CreatePowerShellCommand(DismountOperation, mountedIso.ImagePath);
        var result = await processRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed("Dismount Windows ISO", result);
    }

    private static CommandSpec CreatePowerShellCommand(string operation, string imagePath)
    {
        // Encode both the path and the script so PowerShell cannot reinterpret an ISO path as command text.
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(imagePath));
        var script = $"$ErrorActionPreference='Stop'; $p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedPath}')); {operation}";
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return new CommandSpec(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-EncodedCommand", encodedScript],
            timeout: TimeSpan.FromMinutes(2));
    }
}
