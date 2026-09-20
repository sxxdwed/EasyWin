using EasyWin.Core.Manifests;
using EasyWin.Core.Logging;
using EasyWin.Core.Processes;
using EasyWin.Core.Security;
using EasyWin.Core.Serialization;
using EasyWin.Core.Validation;
using EasyWin.Deployment.Boot;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Configuration;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Images;
using EasyWin.Deployment.Orchestration;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    string logPath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "X:\\", "EasyWin", "boot.log");
    try
    {
        var initialized = await new ProcessRunner().RunAsync(new CommandSpec("wpeinit.exe", [], requiresElevation: true)).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed("Initialize WinPE", initialized);
        string manifestPath = ResolveManifest(args);
        logPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "Logs", "boot.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.UtcNow:O} WinPE started; manifest={manifestPath}{Environment.NewLine}").ConfigureAwait(false);
        using var structuredLogger = new DeploymentFileLogger(Path.Combine(Path.GetDirectoryName(manifestPath)!, "Logs"));
        var runner = new LoggingProcessRunner(new ProcessRunner(), structuredLogger);
        var disks = new PhysicalDiskService(runner);
        var orchestrator = new WinPeDeploymentOrchestrator(
            new ManifestService(new SystemTextJsonSerializer(), new Sha256HashService()),
            disks,
            new DiskPartService(runner),
            new DiskPartScriptBuilder(),
            new WindowsImageService(runner),
            new BootFilesService(runner),
            new UnattendGenerator(),
            new DiskIdentityValidator(),
            runner);
        var progress = new Progress<EasyWin.Core.Models.DeploymentProgress>(value =>
        {
            string line = $"{DateTimeOffset.UtcNow:O} {value.Stage} {value.Percentage:0.#}% {value.Message}";
            Console.WriteLine(line);
            File.AppendAllText(logPath, line + Environment.NewLine);
        });
        await orchestrator.RunAsync(manifestPath, progress).ConfigureAwait(false);
        return 0;
    }
    catch (Exception exception)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.UtcNow:O} FATAL {exception}{Environment.NewLine}").ConfigureAwait(false);
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static string ResolveManifest(string[] values)
{
    string planFile = Path.Combine(AppContext.BaseDirectory, "plan-id.txt");
    if (!File.Exists(planFile) || !Guid.TryParse(File.ReadAllText(planFile).Trim(), out Guid expectedPlan))
        throw new InvalidDataException("The boot image has no valid deployment PlanId.");
    int index = Array.FindIndex(values, value => value.Equals("--manifest", StringComparison.OrdinalIgnoreCase));
    var candidates = new List<string>();
    if (index >= 0 && index + 1 < values.Length && File.Exists(values[index + 1])) candidates.Add(Path.GetFullPath(values[index + 1]));
    foreach (DriveInfo drive in DriveInfo.GetDrives().Where(static drive => drive.IsReady))
    {
        try
        {
            if (drive.VolumeLabel.Equals("EASYWIN_DEPLOY", StringComparison.OrdinalIgnoreCase))
            {
                string path = Path.Combine(drive.RootDirectory.FullName, "manifest.json");
                if (File.Exists(path)) candidates.Add(path);
            }
            string parent = Path.Combine(drive.RootDirectory.FullName, "EasyWin-Deployment");
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0)
                foreach (string folder in Directory.EnumerateDirectories(parent))
                    if (Guid.TryParseExact(Path.GetFileName(folder), "N", out _) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0 && File.Exists(Path.Combine(folder, "manifest.json")))
                        candidates.Add(Path.Combine(folder, "manifest.json"));
        }
        catch (IOException)
        {
        }
    }

    var matching = new List<string>();
    foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        var manifest = new ManifestService(new SystemTextJsonSerializer(), new Sha256HashService()).LoadAndValidateAsync(path, false).GetAwaiter().GetResult();
        if (manifest.PlanId == expectedPlan) matching.Add(path);
    }
    return matching.Count == 1 ? matching[0] : throw new InvalidDataException("Current EasyWin deployment manifest is missing or ambiguous.");
}
