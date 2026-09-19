using System.Text.RegularExpressions;
using EasyWin.Core.Catalogs;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Core.Security;
using EasyWin.Deployment.Boot;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.PostInstall;

public sealed class ApplicationInstaller(IProcessRunner runner, IHashService hashes)
{
    public async Task<IReadOnlyList<AppInstallResult>> InstallAsync(
        IEnumerable<ApplicationPackage> applications,
        string payloadRoot,
        ExecutionMode mode,
        CancellationToken cancellationToken = default)
        => await InstallAsync(applications, payloadRoot, mode, hardwareIds: null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<AppInstallResult>> InstallAsync(
        IEnumerable<ApplicationPackage> applications,
        string payloadRoot,
        ExecutionMode mode,
        IReadOnlySet<string>? hardwareIds,
        CancellationToken cancellationToken = default)
    {
        string root = DeploymentGuard.AbsolutePath(payloadRoot, "Application payload root", !mode.IsDryRun());
        var results = new List<AppInstallResult>();
        foreach (ApplicationPackage app in applications)
        {
            bool compatible = app.RequiredHardwareIdPrefixes.Count == 0 || hardwareIds is null ||
                app.RequiredHardwareIdPrefixes.Any(prefix => hardwareIds.Any(actual => actual.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            if (!compatible && !mode.IsDryRun())
            {
                results.Add(new AppInstallResult(app.Id, true, null, "Skipped: incompatible hardware"));
                continue;
            }

            string installer = PathValidator.ResolveUnderRoot(root, app.Installer, mustExist: !mode.IsDryRun());
            if (!mode.IsDryRun())
            {
                FileHashVerificationResult hash = await hashes.VerifyFileAsync(installer, app.Sha256, app.SizeBytes, cancellationToken).ConfigureAwait(false);
                if (!hash.IsValid)
                {
                    throw new InvalidDataException($"Application '{app.Name}' failed hash validation: {hash.Error}");
                }
            }

            var command = new CommandSpec(installer, app.Arguments, workingDirectory: Path.GetDirectoryName(installer), requiresElevation: true, timeout: TimeSpan.FromHours(1), acceptableExitCodes: app.SuccessExitCodes.ToHashSet());
            if (mode.IsDryRun() && runner is not RecordingProcessRunner)
            {
                results.Add(new AppInstallResult(app.Id, true, 0, "DryRun"));
                continue;
            }

            ProcessResult process = await runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            results.Add(new AppInstallResult(app.Id, process.Succeeded, process.ExitCode, process.Succeeded ? "Installed" : process.StandardError));
            CommandFailureException.ThrowIfFailed($"Install {app.Name}", process);
        }

        return results;
    }
}

public sealed partial class DriverInstaller(IProcessRunner runner, IHashService hashes)
{
    public async Task<IReadOnlySet<string>> DetectHardwareIdsAsync(ExecutionMode mode, CancellationToken cancellationToken = default)
    {
        var command = new CommandSpec("pnputil.exe", ["/enum-devices", "/connected", "/deviceids"], timeout: TimeSpan.FromMinutes(2));
        if (mode.IsDryRun() && runner is not RecordingProcessRunner)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        ProcessResult result = await runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed("Detect hardware IDs", result);
        return HardwareIdRegex().Matches(result.StandardOutput).Select(static match => match.Value.Trim().ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task InstallAsync(IEnumerable<DriverPackage> drivers, IReadOnlySet<string> hardwareIds, string payloadRoot, ExecutionMode mode, CancellationToken cancellationToken = default)
    {
        string root = DeploymentGuard.AbsolutePath(payloadRoot, "Driver payload root", !mode.IsDryRun());
        foreach (DriverPackage driver in drivers)
        {
            bool compatible = driver.HardwareIds.Concat(driver.CompatibleIds).Any(supported => hardwareIds.Any(actual => actual.Equals(supported, StringComparison.OrdinalIgnoreCase) || actual.StartsWith(supported + "&", StringComparison.OrdinalIgnoreCase)));
            if (!compatible && !mode.IsDryRun())
            {
                continue;
            }

            string inf = PathValidator.ResolveUnderRoot(root, driver.InfPath, mustExist: !mode.IsDryRun());
            if (!mode.IsDryRun())
            {
                FileHashVerificationResult hash = await hashes.VerifyFileAsync(inf, driver.Sha256, driver.SizeBytes, cancellationToken).ConfigureAwait(false);
                if (!hash.IsValid)
                {
                    throw new InvalidDataException($"Driver '{driver.Name}' failed hash validation: {hash.Error}");
                }
            }

            var command = new CommandSpec("pnputil.exe", ["/add-driver", inf, "/install"], requiresElevation: true, timeout: TimeSpan.FromMinutes(20));
            if (mode.IsDryRun() && runner is not RecordingProcessRunner)
            {
                continue;
            }

            ProcessResult result = await runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            CommandFailureException.ThrowIfFailed($"Install driver {driver.Name}", result);
        }
    }

    [GeneratedRegex("(?im)^\\s*(?:PCI|USB|HDAUDIO|ACPI|SWD|ROOT)\\\\[^\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex HardwareIdRegex();
}

public sealed class ProfileApplicator(IProcessRunner runner)
{
    private const string DefaultHiveName = "EasyWinDefault";

    private static readonly IReadOnlyDictionary<string, RegistryValue> Allowed = new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase)
    {
        ["showFileExtensions"] = new("HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "HideFileExt", "REG_DWORD", value => Bool(value) ? "0" : "1"),
        ["showHiddenFiles"] = new("HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "Hidden", "REG_DWORD", value => Bool(value) ? "1" : "2"),
        ["taskbarAlignment"] = new("HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "TaskbarAl", "REG_DWORD", value => value.Equals("left", StringComparison.OrdinalIgnoreCase) ? "0" : "1"),
        ["disableConsumerSuggestions"] = new("HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager", "SubscribedContent-338388Enabled", "REG_DWORD", value => Bool(value) ? "0" : "1"),
        ["disableAdvertisingId"] = new("HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo", "Enabled", "REG_DWORD", value => Bool(value) ? "0" : "1"),
        ["disableTailoredExperiences"] = new("HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", "REG_DWORD", value => Bool(value) ? "0" : "1"),
        ["disableWidgets"] = new("HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "TaskbarDa", "REG_DWORD", value => Bool(value) ? "0" : "1"),
        ["disableChatAutoStart"] = new("HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "TaskbarMn", "REG_DWORD", value => Bool(value) ? "0" : "1"),
        ["disableGameDvrBackgroundCapture"] = new("HKCU\\System\\GameConfigStore", "GameDVR_Enabled", "REG_DWORD", value => Bool(value) ? "0" : "1"),
    };

    public async Task ApplyAsync(InstallationProfile profile, ExecutionMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.PreserveWindowsUpdate || !profile.PreserveDefender || !profile.PreserveMicrosoftStore || !profile.PreserveRecovery || !profile.PreserveServicingStack)
        {
            throw new DeploymentSafetyException("profile.protected_subsystem", "The profile attempts to disable a protected Windows subsystem.");
        }

        if (profile.RemoveProvisionedAppPackages.Any(package => !ProfileSafetyPolicy.RemovableProvisionedAppPackages.Contains(package)))
        {
            throw new DeploymentSafetyException("profile.appx.unsafe", "The profile attempts to remove a protected or unapproved Windows application.");
        }

        if (mode.IsDryRun() && runner is not RecordingProcessRunner)
        {
            return;
        }

        await ApplyDefaultUserSettingsAsync(profile, mode, cancellationToken).ConfigureAwait(false);
        await RemoveProvisionedAppsAsync(profile, mode, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyDefaultUserSettingsAsync(InstallationProfile profile, ExecutionMode mode, CancellationToken cancellationToken)
    {
        string systemRoot = Path.GetPathRoot(System.Environment.SystemDirectory) ?? "C:\\";
        string defaultHive = Path.Combine(systemRoot, "Users", "Default", "NTUSER.DAT");
        if (!mode.IsDryRun() && !File.Exists(defaultHive))
        {
            throw new FileNotFoundException("The default Windows user registry hive was not found.", defaultHive);
        }

        var unloadIfPresent = new CommandSpec(
            "reg.exe",
            ["unload", $"HKU\\{DefaultHiveName}"],
            requiresElevation: true,
            acceptableExitCodes: new HashSet<int> { 0, 1 },
            logName: "postinstall.log");
        _ = await runner.RunAsync(unloadIfPresent, cancellationToken).ConfigureAwait(false);
        await RunAsync(
            new CommandSpec("reg.exe", ["load", $"HKU\\{DefaultHiveName}", defaultHive], requiresElevation: true, logName: "postinstall.log"),
            "Load default user registry hive",
            cancellationToken).ConfigureAwait(false);

        try
        {
            foreach ((string key, string value) in profile.Settings)
            {
                if (!Allowed.TryGetValue(key, out RegistryValue? registry))
                {
                    throw new DeploymentSafetyException("profile.setting.unknown", $"Unsupported profile setting '{key}'.");
                }

                string defaultUserPath = registry.Path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)
                    ? $"HKU\\{DefaultHiveName}\\{registry.Path[5..]}"
                    : throw new DeploymentSafetyException("profile.registry.scope", "Only default-user registry settings are allowed.");
                var command = new CommandSpec(
                    "reg.exe",
                    ["add", defaultUserPath, "/v", registry.Name, "/t", registry.Type, "/d", registry.Convert(value), "/f"],
                    requiresElevation: true,
                    logName: "postinstall.log");
                await RunAsync(command, $"Apply profile setting {key}", cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            var unload = new CommandSpec(
                "reg.exe",
                ["unload", $"HKU\\{DefaultHiveName}"],
                requiresElevation: true,
                logName: "postinstall.log");
            await RunAsync(unload, "Unload default user registry hive", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RemoveProvisionedAppsAsync(InstallationProfile profile, ExecutionMode mode, CancellationToken cancellationToken)
    {
        foreach (string package in profile.RemoveProvisionedAppPackages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!ProfileSafetyPolicy.RemovableProvisionedAppPackages.Contains(package) ||
                package.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '.'))
            {
                throw new DeploymentSafetyException("profile.appx.invalid", $"Unsupported provisioned application id '{package}'.");
            }

            string script =
                "$ErrorActionPreference='Stop'; " +
                $"Get-AppxPackage -AllUsers -Name '{package}' | ForEach-Object {{ Remove-AppxPackage -Package $_.PackageFullName -AllUsers -Confirm:$false }}; " +
                $"Get-AppxProvisionedPackage -Online | Where-Object {{ $_.DisplayName -eq '{package}' }} | ForEach-Object {{ Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -AllUsers | Out-Null }}";
            var command = new CommandSpec(
                "powershell.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
                requiresElevation: true,
                timeout: TimeSpan.FromMinutes(5),
                logName: "postinstall.log");
            await RunAsync(command, $"Remove provisioned app {package}", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CommandSpec command, string stage, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        CommandFailureException.ThrowIfFailed(stage, result);
    }

    private static bool Bool(string value) => bool.TryParse(value, out bool result) && result;
    private sealed record RegistryValue(string Path, string Name, string Type, Func<string, string> Convert);
}

public sealed record WindowsActivationResult(bool IsActivated, bool FirmwareKeyPresent, string Message);

public sealed class WindowsActivationService(IProcessRunner runner)
{
    private const string WindowsApplicationId = "55c92734-d682-4d71-983e-d6ec3f16059f";

    public async Task<WindowsActivationResult> TryActivateAsync(
        ExecutionMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode.IsDryRun() && runner is not RecordingProcessRunner)
        {
            return new WindowsActivationResult(false, false, "DryRun: штатная активация Windows смоделирована.");
        }

        var activateCommand = new CommandSpec(
            "cscript.exe",
            ["//B", "//NoLogo", Path.Combine(System.Environment.SystemDirectory, "slmgr.vbs"), "/ato"],
            requiresElevation: true,
            timeout: TimeSpan.FromMinutes(5));
        ProcessResult activation = await runner.RunAsync(activateCommand, cancellationToken).ConfigureAwait(false);

        string statusScript =
            "$service=Get-CimInstance -ClassName SoftwareLicensingService; " +
            $"$licensed=Get-CimInstance -ClassName SoftwareLicensingProduct -Filter \"ApplicationID='{WindowsApplicationId}' AND LicenseStatus=1\" | Where-Object {{$_.PartialProductKey}}; " +
            "if($licensed){'LICENSED'}elseif($service.OA3xOriginalProductKey){'OEM_KEY_PRESENT'}else{'LICENSE_REQUIRED'}";
        var statusCommand = new CommandSpec(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", statusScript],
            requiresElevation: true,
            timeout: TimeSpan.FromMinutes(2));
        ProcessResult status = await runner.RunAsync(statusCommand, cancellationToken).ConfigureAwait(false);

        string marker = status.StandardOutput.Trim().ToUpperInvariant();
        if (status.Succeeded && marker.Contains("LICENSED", StringComparison.Ordinal))
        {
            return new WindowsActivationResult(true, marker.Contains("OEM_KEY_PRESENT", StringComparison.Ordinal), "Windows активирована штатной службой лицензирования.");
        }

        bool firmwareKey = status.Succeeded && marker.Contains("OEM_KEY_PRESENT", StringComparison.Ordinal);
        string message = firmwareKey
            ? "В UEFI найден OEM-ключ, но активация пока не подтверждена. Подключите интернет и убедитесь, что редакция Windows совпадает с лицензией."
            : "Действующая лицензия не обнаружена. Для активации потребуется собственная цифровая лицензия или ключ продукта.";
        if (!activation.Succeeded)
        {
            message += $" Код штатной службы активации: {activation.ExitCode}.";
        }

        return new WindowsActivationResult(false, firmwareKey, message);
    }
}

public sealed class CleanupService(
    IPhysicalDiskService disks,
    IDiskPartService diskPart,
    DiskPartScriptBuilder scripts,
    IBcdService bcd,
    IProcessRunner runner,
    Core.Validation.DiskIdentityValidator diskValidator)
{
    public async Task CleanupAsync(DeploymentManifest manifest, string workDirectory, ExecutionMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        PhysicalDiskSnapshot snapshot = await disks.GetDiskAsync(manifest.TargetDisk.DiskNumber, cancellationToken).ConfigureAwait(false);
        if (!diskValidator.Validate(manifest.TargetDisk, snapshot.Identity).IsValid)
        {
            throw new DeploymentSafetyException("cleanup.disk.changed", "Target disk identity changed; cleanup was cancelled.");
        }

        PartitionInfo stage = snapshot.Partitions.SingleOrDefault(partition => partition.GptPartitionId == manifest.StagingPartition.GptPartitionId)
            ?? throw new DeploymentSafetyException("cleanup.staging.missing", "Deployment partition could not be identified by GPT GUID.");
        if (stage.OffsetBytes != manifest.StagingPartition.OffsetBytes || stage.SizeBytes != manifest.StagingPartition.SizeBytes)
        {
            throw new DeploymentSafetyException("cleanup.staging.changed", "Deployment partition geometry changed; cleanup was cancelled.");
        }

        var request = new FinalizeDiskRequest(snapshot.Identity.DiskNumber, stage.GptPartitionId, stage.PartitionNumber);
        await diskPart.ExecuteAsync(scripts.RemoveStagingAndCreateRecovery(request), workDirectory, mode, cancellationToken).ConfigureAwait(false);
        if (manifest.Boot.BootEntryId.HasValue)
        {
            await bcd.RemoveTemporaryEntryAsync(manifest.Boot.BootEntryId.Value, mode, cancellationToken).ConfigureAwait(false);
        }

        if (!mode.IsDryRun())
        {
            Directory.CreateDirectory("R:\\Recovery\\WindowsRE");
        }

        var commands = new[]
        {
            new CommandSpec("robocopy.exe", ["C:\\Windows\\System32\\Recovery", "R:\\Recovery\\WindowsRE", "winre.wim", "/COPY:DAT", "/R:1", "/W:1"], requiresElevation: true, acceptableExitCodes: Enumerable.Range(0, 8).ToHashSet()),
            new CommandSpec("reagentc.exe", ["/setreimage", "/path", "R:\\Recovery\\WindowsRE", "/target", "C:\\Windows"], requiresElevation: true),
            new CommandSpec("reagentc.exe", ["/enable"], requiresElevation: true),
        };
        foreach (CommandSpec command in commands)
        {
            if (mode.IsDryRun() && runner is not RecordingProcessRunner)
            {
                continue;
            }

            ProcessResult result = await runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            CommandFailureException.ThrowIfFailed("Configure Windows Recovery", result);
        }

        await diskPart.ExecuteAsync("select volume R\r\nremove letter=R\r\n", workDirectory, mode, cancellationToken).ConfigureAwait(false);
    }
}
