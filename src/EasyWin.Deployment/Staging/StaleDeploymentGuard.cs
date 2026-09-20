using EasyWin.Core.Localization;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Deployment.Boot;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Staging;

public static class StaleDeploymentGuard
{
    public static async Task ValidateAsync(IPhysicalDiskService disks, IProcessRunner runner, string language, CancellationToken token)
    {
        var snapshots = await disks.GetDisksAsync(token).ConfigureAwait(false);
        bool found = snapshots.Any(d => d.Partitions.Any(p => p.Role == PartitionRole.Deployment));
        foreach (var volume in snapshots.SelectMany(d => d.Partitions).Where(p => p.DriveLetter is { Length: 1 }))
        {
            string root = volume.DriveLetter + ":\\EasyWin-Deployment";
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) found = true;
        }
        var bcd = await runner.RunAsync(BcdCommandFactory.Enumerate("all"), token).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed("Inspect existing BCD entries", bcd);
        found |= bcd.StandardOutput.Contains("EasyWin Deployment", StringComparison.OrdinalIgnoreCase);
        if (found) throw new DeploymentSafetyException("deployment.stale", DeploymentStrings.Get("StaleDeployment", language));
    }
}
