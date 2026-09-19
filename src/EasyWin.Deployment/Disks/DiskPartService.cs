using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Safety;
using EasyWin.Deployment.Models;

namespace EasyWin.Deployment.Disks;

public interface IDiskPartService
{
    IReadOnlyList<string> PlannedScripts { get; }

    Task ExecuteAsync(string script, string workingDirectory, ExecutionMode mode, CancellationToken cancellationToken = default);

    Task PrepareSeparateTargetAsync(PhysicalDiskSnapshot target, PhysicalDiskSnapshot staging, string workingDirectory, ExecutionMode mode, CancellationToken cancellationToken = default);
}

public sealed class DiskPartService(IProcessRunner processRunner) : IDiskPartService
{
    private readonly List<string> _plannedScripts = [];

    public IReadOnlyList<string> PlannedScripts => _plannedScripts.AsReadOnly();

    public Task PrepareSeparateTargetAsync(PhysicalDiskSnapshot target, PhysicalDiskSnapshot staging, string workingDirectory, ExecutionMode mode, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(new DiskPartScriptBuilder().PrepareTargetWithSeparateStaging(target, staging), workingDirectory, mode, true, cancellationToken);

    public Task ExecuteAsync(string script, string workingDirectory, ExecutionMode mode, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(script, workingDirectory, mode, false, cancellationToken);

    private async Task ExecuteCoreAsync(
        string script,
        string workingDirectory,
        ExecutionMode mode,
        bool separateTarget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        var root = DeploymentGuard.AbsolutePath(workingDirectory, "DiskPart working directory");
        Directory.CreateDirectory(root);
        ValidateScript(script, separateTarget);
        _plannedScripts.Add(script);
        if (mode.IsDryRun())
        {
            return;
        }

        var scriptPath = Path.Combine(root, $"diskpart-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(scriptPath, script, System.Text.Encoding.ASCII, cancellationToken).ConfigureAwait(false);
        try
        {
            var command = new CommandSpec(
                "diskpart.exe",
                ["/s", scriptPath],
                workingDirectory: root,
                requiresElevation: true,
                timeout: TimeSpan.FromMinutes(30));
            var result = await processRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            CommandFailureException.ThrowIfFailed("Disk partitioning", result);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static void ValidateScript(string script, bool separateTarget)
    {
        var commands = script.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!separateTarget && commands.Any(line => line.Equals("clean", StringComparison.OrdinalIgnoreCase) ||
                                 line.StartsWith("clean ", StringComparison.OrdinalIgnoreCase)))
        {
            throw new DeploymentSafetyException("diskpart.clean.forbidden", "DiskPart CLEAN is forbidden for one-disk local staging.");
        }

        var allowedPrefixes = new[]
        {
            "select disk ", "select volume ", "select partition ", "online disk", "attributes disk ",
            "shrink ", "extend", "create partition ", "format ", "assign ", "delete partition override",
            "set id=", "gpt attributes=", "detail partition", "list partition", "remove letter="
        };
        var unknown = commands.FirstOrDefault(command =>
            !(separateTarget && command is "clean" or "convert gpt") &&
            !command.Equals("assign", StringComparison.OrdinalIgnoreCase) &&
            !allowedPrefixes.Any(prefix => command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        if (unknown is not null)
        {
            throw new DeploymentSafetyException("diskpart.command.forbidden", $"Unsupported DiskPart command: {unknown}");
        }
    }
}
