using System.Text.RegularExpressions;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Boot;

public static class BcdCommandFactory
{
    public static CommandSpec Export(string backupPath) => new("bcdedit.exe", ["/export", backupPath], requiresElevation: true);
    public static CommandSpec Import(string backupPath) => new("bcdedit.exe", ["/import", backupPath, "/clean"], requiresElevation: true);
    public static CommandSpec CreateLoader(string description) => new("bcdedit.exe", ["/create", "/d", description, "/application", "osloader"], requiresElevation: true);
    public static CommandSpec Set(string id, string name, string value) => new("bcdedit.exe", ["/set", id, name, value], requiresElevation: true);
    public static CommandSpec BootSequence(string id) => new("bcdedit.exe", ["/bootsequence", id], requiresElevation: true);
    public static CommandSpec Delete(string id) => new("bcdedit.exe", ["/delete", id, "/cleanup"], requiresElevation: true, acceptableExitCodes: new HashSet<int> { 0, 1 });
    public static CommandSpec Enumerate(string id) => new("bcdedit.exe", ["/enum", id], requiresElevation: true);
}

public interface IBcdService
{
    Task<TemporaryBootEntry> ArmOneTimeWinPeAsync(TemporaryBootRequest request, ExecutionMode mode, CancellationToken cancellationToken = default);
    Task RemoveTemporaryEntryAsync(Guid entryId, ExecutionMode mode, CancellationToken cancellationToken = default);
}

public sealed partial class BcdService(IProcessRunner runner) : IBcdService
{
    public async Task<TemporaryBootEntry> ArmOneTimeWinPeAsync(TemporaryBootRequest request, ExecutionMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string wim = DeploymentGuard.AbsolutePath(request.WinPeWimPath, "WinPE WIM", !mode.IsDryRun());
        string sdi = DeploymentGuard.AbsolutePath(request.WinPeSdiPath, "WinPE SDI", !mode.IsDryRun());
        string backup = DeploymentGuard.AbsolutePath(request.BcdStorePath, "BCD backup");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        Guid loader = mode.IsDryRun() ? Guid.Parse("22222222-2222-4222-8222-222222222222") : Guid.Empty;
        bool exported = false;

        try
        {
            await RunAsync(BcdCommandFactory.Export(backup), "Export BCD", mode, cancellationToken).ConfigureAwait(false);
            exported = true;
            if (!mode.IsDryRun())
            {
                ProcessResult options = await runner.RunAsync(BcdCommandFactory.Enumerate("{ramdiskoptions}"), cancellationToken).ConfigureAwait(false);
                if (!options.Succeeded)
                    await RunAsync(new CommandSpec("bcdedit.exe", ["/create", "{ramdiskoptions}", "/d", "EasyWin RAM disk options"], requiresElevation: true), "Create RAM disk options", mode, cancellationToken).ConfigureAwait(false);
                await RunAsync(BcdCommandFactory.Enumerate("{ramdiskoptions}"), "Validate RAM disk options", mode, cancellationToken).ConfigureAwait(false);
            }
            if (!mode.IsDryRun())
            {
                ProcessResult created = await runner.RunAsync(BcdCommandFactory.CreateLoader(request.Description), cancellationToken).ConfigureAwait(false);
                CommandFailureException.ThrowIfFailed("Create WinPE BCD loader", created);
                Match match = GuidRegex().Match(created.StandardOutput);
                if (!match.Success || !Guid.TryParse(match.Value, out loader))
                {
                    throw new InvalidOperationException("BCDEdit did not return the temporary loader GUID.");
                }
            }

            string id = $"{{{loader:D}}}";
            string device = $"ramdisk=[{Path.GetPathRoot(wim)!.TrimEnd('\\')}]\\{wim[Path.GetPathRoot(wim)!.Length..].TrimStart('\\', '/').Replace('/', '\\')},{{ramdiskoptions}}";
            string sdiDevice = $"partition={Path.GetPathRoot(sdi)!.TrimEnd('\\')}";
            string sdiPath = "\\" + sdi[Path.GetPathRoot(sdi)!.Length..].Replace('/', '\\');
            var commands = new[]
            {
                BcdCommandFactory.Set("{ramdiskoptions}", "ramdisksdidevice", sdiDevice),
                BcdCommandFactory.Set("{ramdiskoptions}", "ramdisksdipath", sdiPath),
                BcdCommandFactory.Set(id, "device", device),
                BcdCommandFactory.Set(id, "osdevice", device),
                BcdCommandFactory.Set(id, "path", "\\windows\\system32\\boot\\winload.efi"),
                BcdCommandFactory.Set(id, "systemroot", "\\windows"),
                BcdCommandFactory.Set(id, "winpe", "yes"),
                BcdCommandFactory.Set(id, "detecthal", "yes"),
                BcdCommandFactory.BootSequence(id),
                BcdCommandFactory.Enumerate(id),
            };
            foreach (CommandSpec command in commands)
            {
                await RunAsync(command, "Configure one-time WinPE boot", mode, cancellationToken).ConfigureAwait(false);
            }

            if (!mode.IsDryRun())
            {
                var loaderCheck = await runner.RunAsync(BcdCommandFactory.Enumerate(id), cancellationToken).ConfigureAwait(false);
                var sequenceCheck = await runner.RunAsync(BcdCommandFactory.Enumerate("{bootmgr}"), cancellationToken).ConfigureAwait(false);
                var optionsCheck = await runner.RunAsync(BcdCommandFactory.Enumerate("{ramdiskoptions}"), cancellationToken).ConfigureAwait(false);
                CommandFailureException.ThrowIfFailed("Validate loader", loaderCheck);
                CommandFailureException.ThrowIfFailed("Validate boot sequence", sequenceCheck);
                CommandFailureException.ThrowIfFailed("Validate ramdisk options", optionsCheck);
                if (!loaderCheck.StandardOutput.Contains(id, StringComparison.OrdinalIgnoreCase) ||
                    !loaderCheck.StandardOutput.Contains(device, StringComparison.OrdinalIgnoreCase) ||
                    !sequenceCheck.StandardOutput.Contains(id, StringComparison.OrdinalIgnoreCase) ||
                    !optionsCheck.StandardOutput.Contains(sdiPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("BCD read-back did not confirm the requested WinPE boot configuration.");
            }

            return new TemporaryBootEntry(loader, Guid.Empty, backup);
        }
        catch
        {
            if (exported && !mode.IsDryRun() && File.Exists(backup))
            {
                ProcessResult rollback = await runner.RunAsync(BcdCommandFactory.Import(backup), CancellationToken.None).ConfigureAwait(false);
                CommandFailureException.ThrowIfFailed("Restore BCD backup", rollback);
            }

            throw;
        }
    }

    public Task RemoveTemporaryEntryAsync(Guid entryId, ExecutionMode mode, CancellationToken cancellationToken = default) =>
        RunAsync(BcdCommandFactory.Delete($"{{{entryId:D}}}"), "Remove temporary BCD entry", mode, cancellationToken);

    private async Task RunAsync(CommandSpec command, string stage, ExecutionMode mode, CancellationToken cancellationToken)
    {
        if (mode.IsDryRun())
        {
            if (runner is RecordingProcessRunner)
            {
                _ = await runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        ProcessResult result = await runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed(stage, result);
    }

    [GeneratedRegex("\\{[0-9a-fA-F-]{36}\\}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();
}

public static class BootFilesCommandFactory
{
    public static CommandSpec Create(BootFilesRequest request) => new(
        "bcdboot.exe",
        [Path.Combine(request.WindowsDirectory, "Windows"), "/s", request.SystemPartitionRoot, "/f", "UEFI", "/l", request.Locale],
        requiresElevation: true,
        timeout: TimeSpan.FromMinutes(5));

    public static CommandSpec RegisterFirmware(BootFilesRequest request) => new(
        "bcdboot.exe",
        [Path.Combine(request.WindowsDirectory, "Windows"), "/p", "/l", request.Locale],
        requiresElevation: true,
        timeout: TimeSpan.FromMinutes(5));
}

public sealed class BootFilesService(IProcessRunner runner)
{
    public async Task ConfigureAsync(BootFilesRequest request, ExecutionMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RunAsync(BootFilesCommandFactory.Create(request), mode, cancellationToken).ConfigureAwait(false);
    }

    public async Task RegisterFirmwareAsync(BootFilesRequest request, ExecutionMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RunAsync(BootFilesCommandFactory.RegisterFirmware(request), mode, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(CommandSpec command, ExecutionMode mode, CancellationToken cancellationToken)
    {
        if (mode.IsDryRun() && runner is not RecordingProcessRunner)
        {
            return;
        }

        ProcessResult result = await runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed("BCDBoot", result);
    }
}
