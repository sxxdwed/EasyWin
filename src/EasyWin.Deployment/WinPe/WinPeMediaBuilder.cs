using System.Text;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.WinPe;

public interface IWinPeMediaBuilder
{
    Task<WinPeBuildResult> BuildAsync(WinPeBuildRequest request, ExecutionMode mode, CancellationToken cancellationToken = default);
}

public sealed class WinPeMediaBuilder(IProcessRunner runner) : IWinPeMediaBuilder
{
    public async Task<WinPeBuildResult> BuildAsync(WinPeBuildRequest request, ExecutionMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string work = DeploymentGuard.AbsolutePath(request.WorkingDirectory, "WinPE working directory");
        string output = DeploymentGuard.AbsolutePath(request.OutputDirectory, "WinPE output directory");
        if (Directory.Exists(work))
        {
            throw new DeploymentSafetyException("winpe.work.not_empty", "WinPE working directory already exists.");
        }

        Directory.CreateDirectory(work);
        Directory.CreateDirectory(output);
        string media = Path.Combine(work, "media");
        string mount = Path.Combine(work, "mount");
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(mount);
        string bootWim = Path.Combine(media, "sources", "boot.wim");
        string bootSdi = Path.Combine(media, "boot", "boot.sdi");
        string startup = Path.Combine(mount, "Windows", "System32", "winpeshl.ini");

        if (mode.IsDryRun())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(bootWim)!);
            Directory.CreateDirectory(Path.GetDirectoryName(bootSdi)!);
            await File.WriteAllTextAsync(bootWim, "EASYWIN DRYRUN BOOT WIM", cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(bootSdi, "EASYWIN DRYRUN BOOT SDI", cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(startup)!);
            await File.WriteAllTextAsync(startup, StartupIni(), cancellationToken).ConfigureAwait(false);
            CopyDirectory(media, output);
            return new WinPeBuildResult(output, Path.Combine(output, "sources", "boot.wim"), Path.Combine(output, "boot", "boot.sdi"), startup);
        }

        string adk = DeploymentGuard.AbsolutePath(request.AdkRoot, "Windows ADK WinPE root", true);
        string architecture = request.Architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase) ? "amd64" : throw new DeploymentSafetyException("winpe.arch.invalid", "Only amd64 WinPE is supported.");
        string architectureRoot = Path.Combine(adk, architecture);
        string sourceMedia = Path.Combine(architectureRoot, "Media");
        string sourceWim = Path.Combine(architectureRoot, "en-us", "winpe.wim");
        if (!Directory.Exists(sourceMedia) || !File.Exists(sourceWim))
        {
            throw new FileNotFoundException("The ADK WinPE add-on is incomplete. Media and en-us\\winpe.wim are required.");
        }

        CopyDirectory(sourceMedia, media);
        Directory.CreateDirectory(Path.GetDirectoryName(bootWim)!);
        File.Copy(sourceWim, bootWim, true);
        await RunAsync(new CommandSpec("dism.exe", ["/Mount-Image", $"/ImageFile:{bootWim}", "/Index:1", $"/MountDir:{mount}"], requiresElevation: true, timeout: TimeSpan.FromMinutes(10)), "Mount WinPE", cancellationToken).ConfigureAwait(false);
        bool commit = false;
        try
        {
            foreach (string component in request.OptionalComponents)
            {
                string safe = DeploymentGuard.SafeIdentifier(component, "WinPE optional component");
                string package = Path.Combine(architectureRoot, "WinPE_OCs", $"{safe}.cab");
                if (!File.Exists(package))
                {
                    throw new FileNotFoundException($"Required WinPE optional component is missing: {safe}.", package);
                }

                await RunAsync(new CommandSpec("dism.exe", [$"/Image:{mount}", "/Add-Package", $"/PackagePath:{package}"], requiresElevation: true, timeout: TimeSpan.FromMinutes(10)), $"Add WinPE component {safe}", cancellationToken).ConfigureAwait(false);
                string languagePackage = Path.Combine(architectureRoot, "WinPE_OCs", "en-us", $"{safe}_en-us.cab");
                if (File.Exists(languagePackage))
                {
                    await RunAsync(new CommandSpec("dism.exe", [$"/Image:{mount}", "/Add-Package", $"/PackagePath:{languagePackage}"], requiresElevation: true, timeout: TimeSpan.FromMinutes(10)), $"Add WinPE language component {safe}", cancellationToken).ConfigureAwait(false);
                }
            }

            // DISM verifies driver catalogs; never use /ForceUnsigned. Injection completes before any disk erase.
            foreach (string inf in request.DriverInfPaths)
            {
                string driver = DeploymentGuard.AbsolutePath(inf, "Exported driver INF", true);
                if (!Path.GetExtension(driver).Equals(".inf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Expected an INF driver package.");
                await RunAsync(new CommandSpec("dism.exe", [$"/Image:{mount}", "/Add-Driver", $"/Driver:{driver}"], requiresElevation: true, timeout: TimeSpan.FromMinutes(10)), "Inject verified driver into WinPE", cancellationToken).ConfigureAwait(false);
            }
            string app = DeploymentGuard.AbsolutePath(request.WinPeExecutablePath, "WinPE executable", true);
            string destination = Path.Combine(mount, "Windows", "System32", "EasyWin");
            Directory.CreateDirectory(destination);
            if (Directory.Exists(app))
            {
                CopyDirectory(app, destination);
            }
            else
            {
                File.Copy(app, Path.Combine(destination, Path.GetFileName(app)), true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(startup)!);
            await File.WriteAllTextAsync(startup, StartupIni(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            if (request.PlanId.HasValue)
                await File.WriteAllTextAsync(Path.Combine(destination, "plan-id.txt"), request.PlanId.Value.ToString("D"), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(mount, "Windows", "System32", "startnet.cmd"), "@echo off\r\nwpeinit\r\n", Encoding.ASCII, cancellationToken).ConfigureAwait(false);
            commit = true;
        }
        finally
        {
            string action = commit ? "/Commit" : "/Discard";
            await RunAsync(new CommandSpec("dism.exe", ["/Unmount-Image", $"/MountDir:{mount}", action], requiresElevation: true, timeout: TimeSpan.FromMinutes(15)), "Unmount WinPE", CancellationToken.None).ConfigureAwait(false);
        }

        CopyDirectory(media, output);
        if (!File.Exists(Path.Combine(output, "sources", "boot.wim")) || !File.Exists(Path.Combine(output, "boot", "boot.sdi")))
        {
            throw new InvalidDataException("Built WinPE media is incomplete.");
        }

        return new WinPeBuildResult(output, Path.Combine(output, "sources", "boot.wim"), Path.Combine(output, "boot", "boot.sdi"), startup);
    }

    private async Task RunAsync(CommandSpec command, string stage, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed(stage, result);
    }

    private static string StartupIni() => "[LaunchApp]\r\nAppPath = %SYSTEMROOT%\\System32\\EasyWin\\EasyWin.WinPE.exe\r\n";

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
