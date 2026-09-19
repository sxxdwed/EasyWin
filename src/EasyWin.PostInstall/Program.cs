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
        string manifestPath = Value(args, "--manifest") ?? Path.Combine(root, "manifest.json");
        using var structuredLogger = new DeploymentFileLogger(Path.Combine(root, "Logs"));
        var runner = new LoggingProcessRunner(new ProcessRunner(), structuredLogger);
        var json = new SystemTextJsonSerializer();
        var hashes = new Sha256HashService();
        var disks = new PhysicalDiskService(runner);
        var bcd = new BcdService(runner);
        var diskPart = new DiskPartService(runner);
        var orchestrator = new PostInstallOrchestrator(
            json,
            new ManifestService(json, hashes),
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
