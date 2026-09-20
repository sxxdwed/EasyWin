using EasyWin.Core.Models;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;
using EasyWin.Deployment.Staging;

namespace EasyWin.Deployment.PostInstall;

public static class FirstBootGuard
{
    public static void Validate(DeploymentManifest manifest, PhysicalDiskSnapshot target, string windowsDirectory, bool windowsExists, bool isWinPe)
    {
        string? root = Path.GetPathRoot(windowsDirectory);
        if (isWinPe || !windowsExists || !target.Identity.IsBootDisk || !StagingSelection.SameDisk(manifest.TargetDisk, target.Identity) ||
            !manifest.State.CompletedStages.Contains(DeploymentStage.AwaitingFirstBoot) || root is null ||
            !target.Partitions.Any(p => p.DriveLetter is { Length: 1 } && root.Equals(p.DriveLetter + ":\\", StringComparison.OrdinalIgnoreCase)))
            throw new DeploymentSafetyException("postinstall.firstboot", "The running Windows installation is not the confirmed deployment target. Staging is preserved.");
    }

    public static void ValidateCurrent(DeploymentManifest manifest, PhysicalDiskSnapshot target)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var miniNt = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
        string windows = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
        Validate(manifest, target, windows, File.Exists(Path.Combine(windows, "System32", "ntoskrnl.exe")), miniNt is not null);
    }
}
