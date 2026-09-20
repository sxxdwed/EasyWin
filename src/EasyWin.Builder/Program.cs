using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Core.Serialization;
using EasyWin.Deployment.DryRun;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.WinPe;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    try
    {
        if (Has(args, "--acquire-apps"))
        {
            int idsAt = Array.FindIndex(args, value => value.Equals("--apps", StringComparison.OrdinalIgnoreCase));
            string[] selected = idsAt >= 0 && idsAt + 1 < args.Length ? args[idsAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
            string config = Value(args, "--config") ?? FindConfigRoot();
            string workspace = Required(args, "--workspace");
            using var http = EasyWin.Deployment.PostInstall.VerifiedAppAcquisition.CreateClient();
            var signature = new EasyWin.Deployment.PostInstall.AuthenticodeVerifier(new ProcessRunner());
            var result = await new EasyWin.Deployment.PostInstall.PreparedApplications(new(http, signature), signature, Path.Combine(workspace, "Cache")).PrepareAsync(
                selected, config, Path.GetDirectoryName(config)!, workspace, default).ConfigureAwait(false);
            foreach (var app in result.Applications) Console.WriteLine($"{app.Name}: {app.Sha256} ({app.SizeBytes})");
            Console.WriteLine(result.PayloadRoot);
            return 0;
        }
        if (Has(args, "--dry-run"))
        {
            string workspace = Value(args, "--workspace") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "dryrun");
            string config = Value(args, "--config") ?? FindConfigRoot();
            DryRunReport report = await new DryRunEngine().RunAsync(workspace, config).ConfigureAwait(false);
            Console.WriteLine(report.Message);
            Console.WriteLine($"Stages: {string.Join(" -> ", report.Stages)}");
            Console.WriteLine($"Commands recorded: {report.RecordedCommands.Count}; executed: {report.DestructiveCommandsExecuted}");
            Console.WriteLine($"Manifest: {report.ManifestPath}");
            return report.Success && !report.DestructiveCommandsExecuted ? 0 : 2;
        }

        if (Has(args, "--build-winpe"))
        {
            string adk = Required(args, "--adk");
            string payload = Required(args, "--payload");
            string output = Required(args, "--output");
            int planIndex = Array.FindIndex(args, value => value.Equals("--plan-id", StringComparison.OrdinalIgnoreCase));
            if (planIndex < 0 || planIndex + 1 >= args.Length || !Guid.TryParse(args[planIndex + 1], out Guid planId) || planId == Guid.Empty)
                throw new ArgumentException("--plan-id must identify the exact deployment plan embedded into WinPE.");
            string work = Value(args, "--workspace") ?? Path.Combine(Path.GetTempPath(), $"EasyWin-WinPE-{Guid.NewGuid():N}");
            var builder = new WinPeMediaBuilder(new ProcessRunner());
            WinPeBuildResult result = await builder.BuildAsync(new WinPeBuildRequest(
                adk, "amd64", work, output, payload,
                ["WinPE-WMI", "WinPE-NetFX", "WinPE-Scripting", "WinPE-PowerShell", "WinPE-StorageWMI"], [], PlanId: planId), ExecutionMode.Live).ConfigureAwait(false);
            Console.WriteLine($"WinPE: {result.MediaRoot}");
            return 0;
        }

        Console.Error.WriteLine("Usage: EasyWin.Builder --dry-run [--workspace PATH] [--config PATH] | --build-winpe --adk PATH --payload PATH --output PATH --plan-id GUID");
        return 64;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Builder failed: {exception.Message}");
        return 1;
    }
}

static bool Has(string[] values, string name) => values.Any(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
static string? Value(string[] values, string name)
{
    int index = Array.FindIndex(values, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? Path.GetFullPath(values[index + 1]) : null;
}
static string Required(string[] values, string name) => Value(values, name) ?? throw new ArgumentException($"Missing {name}.");
static string FindConfigRoot()
{
    string current = Environment.CurrentDirectory;
    for (int index = 0; index < 8; index++)
    {
        string candidate = Path.Combine(current, "config");
        if (Directory.Exists(candidate)) return candidate;
        DirectoryInfo? parent = Directory.GetParent(current);
        if (parent is null) break;
        current = parent.FullName;
    }

    throw new DirectoryNotFoundException("Could not locate the EasyWin config directory. Pass --config.");
}
