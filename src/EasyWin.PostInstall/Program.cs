using EasyWin.Core.Catalogs;
using EasyWin.Core.Manifests;
using EasyWin.Core.Logging;
using EasyWin.Core.Processes;
using EasyWin.Core.Security;
using EasyWin.Core.Serialization;
using EasyWin.Core.Validation;
using EasyWin.Deployment.Boot;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Orchestration;
using EasyWin.Deployment.PostInstall;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EasyWin");
    Directory.CreateDirectory(root);
    string log = Path.Combine(root, "postinstall.log");
    try
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var setup = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup");
        if (args.Contains("--defer-until-setup-complete", StringComparer.OrdinalIgnoreCase) &&
            (Convert.ToInt32(setup?.GetValue("SystemSetupInProgress", 0), System.Globalization.CultureInfo.InvariantCulture) != 0 ||
             Convert.ToInt32(setup?.GetValue("OOBEInProgress", 0), System.Globalization.CultureInfo.InvariantCulture) != 0)) return 2;
        using var executionLock = PostInstallBootstrap.AcquireLock(root);
        string manifestPath = Value(args, "--manifest") ?? Path.Combine(root, "manifest.json");
        var json = new SystemTextJsonSerializer();
        var hashes = new Sha256HashService();
        var manifestService = new ManifestService(json, hashes);
        var candidate = await json.DeserializeFileAsync<EasyWin.Core.Models.DeploymentManifest>(manifestPath).ConfigureAwait(false);
        await manifestService.ValidateAsync(candidate, Path.GetDirectoryName(manifestPath)!, false).ConfigureAwait(false);
        string planLogs = PathValidator.ResolveUnderRoot(root, $"Logs/{candidate.PlanId:N}");
        Directory.CreateDirectory(planLogs);
        log = Path.Combine(planLogs, "postinstall.log");
        using var structuredLogger = new DeploymentFileLogger(planLogs);
        var runner = new LoggingProcessRunner(new ProcessRunner(), structuredLogger);
        var disks = new PhysicalDiskService(runner);
        var bcd = new BcdService(runner);
        var diskPart = new DiskPartService(runner);
        var orchestrator = new PostInstallOrchestrator(
            json,
            manifestService,
            disks,
            diskPart,
            new CatalogService(json),
            new ApplicationInstaller(runner, hashes),
            new DriverInstaller(runner, hashes),
            new ProfileApplicator(runner),
            new WindowsActivationService(runner),
            new CleanupService(disks, diskPart, new DiskPartScriptBuilder(), bcd, runner, new DiskIdentityValidator()));
        var progress = new Progress<EasyWin.Core.Models.DeploymentProgress>(value => File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} {value.Stage} {value.Percentage:0.#}% {value.Message}{Environment.NewLine}"));
        await orchestrator.RunAsync(manifestPath, progress).ConfigureAwait(false);
        var removeTask = await runner.RunAsync(new CommandSpec("schtasks.exe", ["/Delete", "/TN", PostInstallBootstrap.TaskName, "/F"], requiresElevation: true)).ConfigureAwait(false);
        if (!removeTask.Succeeded)
            await File.AppendAllTextAsync(log, "Bootstrap task removal will be retried on the next launch.\r\n").ConfigureAwait(false);
        else
        {
            File.Delete(Path.Combine(root, "RegisterPostInstall.cmd"));
            File.Delete(Path.Combine(root, "PostInstallTask.xml"));
        }
        await File.AppendAllTextAsync(log, $"{DateTimeOffset.UtcNow:O} Completed{Environment.NewLine}").ConfigureAwait(false);
        return 0;
    }
    catch (Exception exception)
    {
        await File.AppendAllTextAsync(log, $"{DateTimeOffset.UtcNow:O} FATAL {exception}{Environment.NewLine}").ConfigureAwait(false);
        return 1;
    }
}

static string? Value(string[] values, string name)
{
    int index = Array.FindIndex(values, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? Path.GetFullPath(values[index + 1]) : null;
}
