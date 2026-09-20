using EasyWin.Core.Processes;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.PostInstall;

public sealed class DriverBackupService(IProcessRunner runner)
{
    public async Task<IReadOnlyList<string>> ExportAsync(string destination, CancellationToken token)
    {
        if (Directory.Exists(destination)) throw new DeploymentSafetyException("drivers.export.exists", "Driver export directory must be new.");
        Directory.CreateDirectory(destination);
        var result = await runner.RunAsync(new CommandSpec("dism.exe", ["/Online", "/Export-Driver", "/Destination:" + Path.GetFullPath(destination)], requiresElevation: true, timeout: TimeSpan.FromMinutes(20)), token).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed("Export current third-party drivers", result);
        return Directory.EnumerateFiles(destination, "*.inf", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
