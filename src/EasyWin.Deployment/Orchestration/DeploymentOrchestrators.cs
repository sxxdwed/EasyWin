using EasyWin.Core.Catalogs;
using EasyWin.Core.Manifests;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Core.Security;
using EasyWin.Core.Serialization;
using EasyWin.Core.Validation;
using EasyWin.Deployment.Boot;
using EasyWin.Deployment.Commands;
using EasyWin.Deployment.Configuration;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Environment;
using EasyWin.Deployment.Images;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.PostInstall;
using EasyWin.Deployment.Safety;
using EasyWin.Deployment.Staging;
using EasyWin.Deployment.WinPe;

namespace EasyWin.Deployment.Orchestration;

public sealed record DesktopPreparationRequest(
    ReinstallPlan Plan,
    string ImagePath,
    string AdkWinPeRoot,
    string WinPePayloadRoot,
    string PostInstallPayloadRoot,
    string ConfigurationRoot,
    string WorkRoot,
    char StagingDriveLetter = 'T',
    string? PayloadRoot = null);

public sealed record DesktopPreparationResult(DeploymentManifest Manifest, string ManifestPath, IReadOnlyList<CommandSpec> PlannedCommands);

public sealed class DesktopPreparationOrchestrator(
    IPreflightService preflight,
    IPhysicalDiskService disks,
    ILocalStagingPartitionService localStaging,
    IWinPeMediaBuilder winPe,
    IStagingService staging,
    IBcdService bcd,
    IManifestService manifests,
    IHashService hashes,
    IProcessRunner runner,
    DiskIdentityValidator identities)
{
    public async Task<DesktopPreparationResult> PrepareAsync(DesktopPreparationRequest request, IProgress<DeploymentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Plan.ExecutionMode.IsDryRun() && (!request.Plan.DataLossAcknowledged || !request.Plan.FinalConfirmationAccepted))
        {
            throw new DeploymentSafetyException("confirmation.required", "Both destructive-operation confirmations are required.");
        }

        string work = DeploymentGuard.AbsolutePath(request.WorkRoot, "EasyWin work root");
        Directory.CreateDirectory(work);
        progress?.Report(new DeploymentProgress(DeploymentStage.ValidateEnvironment, "Проверка среды", 2));
        long staticPayloadBytes = CalculateStaticPayloadBytes(request);
        if (!request.Plan.ExecutionMode.IsDryRun())
        {
            long stageBytes = Math.Max(6L * 1024 * 1024 * 1024, checked(new FileInfo(request.ImagePath).Length + staticPayloadBytes + 4L * 1024 * 1024 * 1024));
            PreflightReport report = await preflight.CheckAsync(new PreflightRequest(Path.GetPathRoot(System.Environment.SystemDirectory)!, request.ImagePath, stageBytes + 4L * 1024 * 1024 * 1024), cancellationToken).ConfigureAwait(false);
            if (!report.CanContinue)
            {
                throw new DeploymentSafetyException("preflight.failed", string.Join("; ", report.Checks.Where(static check => !check.Passed).Select(static check => check.Message)));
            }
        }

        PhysicalDiskSnapshot current = await disks.GetDiskAsync(request.Plan.TargetDisk.DiskNumber, cancellationToken).ConfigureAwait(false);
        DeploymentGuard.FixedInternalDisk(request.Plan.TargetDisk, "Windows target disk");
        DiskIdentityValidationResult identity = identities.Validate(request.Plan.TargetDisk, current.Identity);
        if (!identity.IsValid)
        {
            throw new DeploymentSafetyException("disk.identity.changed", string.Join("; ", identity.Errors));
        }

        if (request.Plan.AdditionalDisksToErase.Count > 1 ||
            request.Plan.AdditionalDisksToErase.Any(disk => disk.DiskNumber == request.Plan.TargetDisk.DiskNumber))
        {
            throw new DeploymentSafetyException("disk.selection.invalid", "The Windows target and the optional second erase disk must be distinct.");
        }

        foreach (DiskIdentity expected in request.Plan.AdditionalDisksToErase)
        {
            PhysicalDiskSnapshot additional = await disks.GetDiskAsync(expected.DiskNumber, cancellationToken).ConfigureAwait(false);
            EnsureEraseDiskSafe(expected, additional, identities, "desktop");
        }

        progress?.Report(new DeploymentProgress(DeploymentStage.PrepareStaging, "Проверка и сборка WinPE до изменения разметки", 10));
        string winPeWork = Path.Combine(work, "winpe-work");
        string winPeOut = Path.Combine(work, "winpe-media");
        WinPeBuildResult built = await winPe.BuildAsync(new WinPeBuildRequest(
            request.AdkWinPeRoot, "amd64", winPeWork, winPeOut, request.WinPePayloadRoot,
            ["WinPE-WMI", "WinPE-NetFX", "WinPE-Scripting", "WinPE-PowerShell", "WinPE-StorageWMI"], []), request.Plan.ExecutionMode, cancellationToken).ConfigureAwait(false);

        progress?.Report(new DeploymentProgress(DeploymentStage.PrepareStaging, "Создание локального deployment-раздела", 20));
        long requestedSize = Math.Max(
            6L * 1024 * 1024 * 1024,
            checked(new FileInfo(request.ImagePath).Length + staticPayloadBytes + DirectoryBytes(built.MediaRoot) + 2L * 1024 * 1024 * 1024));
        PartitionInfo source = LocalStagingPartitionService.SelectShrinkSource(current, requestedSize);
        var stageRequest = new StagingPartitionRequest(request.Plan.TargetDisk.DiskNumber, source.DriveLetter![0], request.StagingDriveLetter, requestedSize);
        StagingPartitionIdentity partition = await localStaging.CreateAsync(request.Plan.TargetDisk, stageRequest, work, request.Plan.ExecutionMode, cancellationToken).ConfigureAwait(false);

        progress?.Report(new DeploymentProgress(DeploymentStage.PrepareBoot, "Копирование и проверка staged-файлов", 35));
        (DeploymentManifest manifest, string manifestPath) = await staging.BuildAsync(new StagingBuildRequest(request.Plan, partition, request.ImagePath, built.MediaRoot, request.PostInstallPayloadRoot, request.ConfigurationRoot, PayloadRoot: request.PayloadRoot), cancellationToken).ConfigureAwait(false);
        string root = partition.RootPath;
        string backup = Path.Combine(root, "Boot", "bcd.backup");
        var checkpoints = new DeploymentCheckpointService(manifests);
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.PrepareBoot, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        try
        {
            TemporaryBootEntry entry = await bcd.ArmOneTimeWinPeAsync(new TemporaryBootRequest(
                backup,
                Path.Combine(root, manifest.Boot.WinPeWimRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, manifest.Boot.WinPeSdiRelativePath.Replace('/', Path.DirectorySeparatorChar))), request.Plan.ExecutionMode, cancellationToken).ConfigureAwait(false);
            if (request.Plan.ExecutionMode.IsDryRun() && !File.Exists(backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                await File.WriteAllTextAsync(backup, "EASYWIN DRYRUN BCD BACKUP", cancellationToken).ConfigureAwait(false);
            }

            var backupInfo = new FileInfo(backup);
            var files = manifest.FileInventory.Append(new ManifestFileEntry
            {
                RelativePath = "Boot/bcd.backup",
                Sha256 = await hashes.ComputeSha256Async(backup, cancellationToken).ConfigureAwait(false),
                LengthBytes = backupInfo.Length,
                Kind = ManifestFileKind.BcdBackup,
                Required = true,
            }).OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            manifest = manifest with
            {
                Boot = manifest.Boot with { BootEntryId = entry.LoaderId, OneTimeBootConfigured = true },
                FileInventory = files,
            };
            await manifests.SaveAsync(manifest, manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.PrepareBoot, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.AwaitingWinPeBoot, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            await manifests.ValidateAsync(manifest, root, true, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DeploymentProgress(DeploymentStage.AwaitingWinPeBoot, request.Plan.ExecutionMode.IsDryRun() ? "DryRun: перезагрузка WinPE смоделирована" : "Перезагрузка в WinPE", 45));

            if (!request.Plan.ExecutionMode.IsDryRun())
            {
                ProcessResult reboot = await runner.RunAsync(new CommandSpec("shutdown.exe", ["/r", "/t", "0", "/d", "p:4:1", "/c", "EasyWin WinPE deployment"], requiresElevation: true), cancellationToken).ConfigureAwait(false);
                CommandFailureException.ThrowIfFailed("Reboot to WinPE", reboot);
            }

            return new DesktopPreparationResult(manifests.Seal(manifest), manifestPath, runner is RecordingProcessRunner recording ? recording.Commands : []);
        }
        catch (Exception exception)
        {
            string code = exception is DeploymentSafetyException safety ? safety.Code : "desktop.prepare_boot.failed";
            try
            {
                await checkpoints.FailAsync(manifest, manifestPath, code, exception.Message, exception.ToString(), recoverable: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original preparation failure.
            }

            throw;
        }
    }

    private static void EnsureEraseDiskSafe(DiskIdentity expected, PhysicalDiskSnapshot actual, DiskIdentityValidator validator, string phase)
    {
        DeploymentGuard.FixedInternalDisk(expected, "Additional erase disk");
        DiskIdentityValidationResult result = validator.Validate(expected, actual.Identity);
        if (!result.IsValid)
        {
            throw new DeploymentSafetyException($"{phase}.erase_disk.changed", string.Join("; ", result.Errors));
        }

        if (actual.IsOffline || actual.IsReadOnly || !actual.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase))
        {
            throw new DeploymentSafetyException($"{phase}.erase_disk.unavailable", "The additional erase disk must be online, writable, and GPT.");
        }

        if (actual.Partitions.Any(static partition => partition.Role == PartitionRole.Deployment))
        {
            throw new DeploymentSafetyException($"{phase}.erase_disk.contains_staging", "The additional erase disk unexpectedly contains an EasyWin deployment partition.");
        }
    }

    private static long CalculateStaticPayloadBytes(DesktopPreparationRequest request)
    {
        string payloadRoot = string.IsNullOrWhiteSpace(request.PayloadRoot)
            ? Path.GetDirectoryName(Path.GetFullPath(request.ConfigurationRoot)) ?? throw new DirectoryNotFoundException("Package payload root was not found.")
            : Path.GetFullPath(request.PayloadRoot);
        return checked(
            DirectoryBytes(request.PostInstallPayloadRoot) +
            DirectoryBytes(request.ConfigurationRoot) +
            DirectoryBytes(Path.Combine(payloadRoot, "Apps")) +
            DirectoryBytes(Path.Combine(payloadRoot, "Drivers")));
    }

    private static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            total = checked(total + new FileInfo(file).Length);
        }

        return total;
    }
}

public sealed class WinPeDeploymentOrchestrator(
    IManifestService manifests,
    IPhysicalDiskService disks,
    IDiskPartService diskPart,
    DiskPartScriptBuilder scripts,
    IWindowsImageService images,
    BootFilesService bootFiles,
    UnattendGenerator unattend,
    DiskIdentityValidator identities,
    IProcessRunner runner)
{
    public async Task RunAsync(string manifestPath, IProgress<DeploymentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        DeploymentManifest? manifest = null;
        var checkpoints = new DeploymentCheckpointService(manifests);
        try
        {
            progress?.Report(new DeploymentProgress(DeploymentStage.ValidateManifest, "Проверка manifest и SHA-256", 46));
            manifest = await manifests.LoadAndValidateAsync(manifestPath, true, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.BeginAttemptAsync(manifest, manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.AwaitingWinPeBoot, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ValidateManifest, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            PhysicalDiskSnapshot disk = await disks.GetDiskAsync(manifest.TargetDisk.DiskNumber, cancellationToken).ConfigureAwait(false);
            DeploymentGuard.FixedInternalDisk(manifest.TargetDisk, "Windows target disk");
            DiskIdentityValidationResult identity = identities.Validate(manifest.TargetDisk, disk.Identity);
            if (!identity.IsValid)
            {
                throw new DeploymentSafetyException("winpe.disk.changed", string.Join("; ", identity.Errors));
            }

            PartitionInfo stage = disk.Partitions.SingleOrDefault(partition => partition.GptPartitionId == manifest.StagingPartition.GptPartitionId)
                ?? throw new DeploymentSafetyException("winpe.staging.missing", "Protected deployment partition is missing.");
            if (stage.OffsetBytes != manifest.StagingPartition.OffsetBytes || stage.SizeBytes != manifest.StagingPartition.SizeBytes)
            {
                throw new DeploymentSafetyException("winpe.staging.changed", "Protected deployment partition geometry changed.");
            }

            var additionalDisks = new List<PhysicalDiskSnapshot>();
            foreach (DiskIdentity expected in manifest.AdditionalDisksToErase)
            {
                if (expected.DiskNumber == manifest.TargetDisk.DiskNumber)
                {
                    throw new DeploymentSafetyException("winpe.erase_disk.same_as_target", "The Windows target cannot also be an additional erase disk.");
                }

                PhysicalDiskSnapshot additional = await disks.GetDiskAsync(expected.DiskNumber, cancellationToken).ConfigureAwait(false);
                DeploymentGuard.FixedInternalDisk(expected, "Additional erase disk");
                DiskIdentityValidationResult additionalIdentity = identities.Validate(expected, additional.Identity);
                if (!additionalIdentity.IsValid)
                {
                    throw new DeploymentSafetyException("winpe.erase_disk.changed", string.Join("; ", additionalIdentity.Errors));
                }

                if (additional.IsOffline || additional.IsReadOnly || !additional.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DeploymentSafetyException("winpe.erase_disk.unavailable", "The additional erase disk is offline, read-only, or not GPT.");
                }

                if (additional.Partitions.Any(static partition => partition.Role == PartitionRole.Deployment))
                {
                    throw new DeploymentSafetyException("winpe.erase_disk.contains_staging", "The additional erase disk contains the protected deployment partition.");
                }

                additionalDisks.Add(additional);
            }

            string stageRoot = Path.GetDirectoryName(manifestPath)!;
            string image = PathValidator.ResolveUnderRoot(stageRoot, manifest.Image.RelativePath, true);
            new DeploymentSafetyValidator(identities).ValidateBeforeDestructive(manifest, disk, image);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ValidateTargetDisk, recoveryRequired: false, cancellationToken).ConfigureAwait(false);

            var prepare = new DiskPreparationRequest(disk.Identity.DiskNumber, stage.GptPartitionId, stage.PartitionNumber, stage.OffsetBytes, stage.SizeBytes);
            progress?.Report(new DeploymentProgress(DeploymentStage.PrepareDisk, "Подготовка GPT с защитой staging", 52));
            manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.PrepareDisk, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            await diskPart.ExecuteAsync(scripts.PrepareTargetPreservingDeployment(prepare, disk.Partitions), Path.GetDirectoryName(manifestPath)!, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.PrepareDisk, recoveryRequired: true, cancellationToken).ConfigureAwait(false);

            progress?.Report(new DeploymentProgress(DeploymentStage.ApplyImage, "Применение Windows через DISM", 60));
            manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.ApplyImage, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            await images.ApplyAsync(new ApplyImageRequest(image, manifest.Image.ImageIndex, "W:\\"), new Progress<int>(percent => progress?.Report(new DeploymentProgress(DeploymentStage.ApplyImage, $"Установка Windows {percent}%", 55 + percent * .25))), cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ApplyImage, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DeploymentProgress(DeploymentStage.ConfigureBoot, "Создание UEFI boot files", 82));
            manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.ConfigureBoot, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            await bootFiles.ConfigureAsync(new BootFilesRequest("W:\\", "S:\\", Locale: manifest.Language), manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ConfigureBoot, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.GenerateUnattend, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            await unattend.WriteAsync("W:\\Windows\\Panther\\unattend.xml", new UnattendOptions(manifest.Language, manifest.Language, manifest.TimeZone, manifest.ComputerName ?? "EASYWIN-PC", null), cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.GenerateUnattend, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            foreach (PhysicalDiskSnapshot additional in additionalDisks)
            {
                progress?.Report(new DeploymentProgress(DeploymentStage.PartitionDisk, $"Очистка дополнительного диска {additional.Identity.DiskNumber}", 86));
                manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.PartitionDisk, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
                await diskPart.ExecuteAsync(
                    scripts.EraseAdditionalDiskAndCreateDataVolume(new AdditionalDiskEraseRequest(additional.Identity.DiskNumber), additional.Partitions),
                    stageRoot,
                    manifest.ExecutionMode,
                    cancellationToken).ConfigureAwait(false);
                manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.PartitionDisk, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            }

            await bootFiles.RegisterFirmwareAsync(new BootFilesRequest("W:\\", "S:\\", Locale: manifest.Language), manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.PreparePostInstall, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            await PreparePostInstallAsync(stageRoot, manifestPath, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.PreparePostInstall, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.AwaitingFirstBoot, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            CopyManifestToTarget(manifestPath, manifest.ExecutionMode);
            progress?.Report(new DeploymentProgress(DeploymentStage.AwaitingFirstBoot, "Первый запуск Windows", 88));
            if (!manifest.ExecutionMode.IsDryRun())
            {
                ProcessResult reboot = await runner.RunAsync(new CommandSpec("wpeutil.exe", ["reboot"], requiresElevation: true), cancellationToken).ConfigureAwait(false);
                CommandFailureException.ThrowIfFailed("Reboot to new Windows", reboot);
            }
        }
        catch (Exception exception)
        {
            if (manifest is not null)
            {
                string code = exception is DeploymentSafetyException safety ? safety.Code : "winpe.deployment.failed";
                try
                {
                    await checkpoints.FailAsync(manifest, manifestPath, code, exception.Message, exception.ToString(), manifest.State.RecoveryRequired, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original failure; logging still captures both paths.
                }
            }

            throw;
        }
    }

    private static async Task PreparePostInstallAsync(string stageRoot, string manifestPath, ExecutionMode mode, CancellationToken cancellationToken)
    {
        if (mode.IsDryRun())
        {
            return;
        }

        string target = "W:\\ProgramData\\EasyWin";
        CopyDirectory(Path.Combine(stageRoot, "PostInstall"), target);
        File.Copy(manifestPath, Path.Combine(target, "manifest.json"), true);
        string scriptsRoot = "W:\\Windows\\Setup\\Scripts";
        Directory.CreateDirectory(scriptsRoot);
        string setup = "@echo off\r\n\"C:\\ProgramData\\EasyWin\\EasyWin.PostInstall.exe\" --manifest \"C:\\ProgramData\\EasyWin\\manifest.json\" >> \"C:\\ProgramData\\EasyWin\\postinstall-bootstrap.log\" 2>&1\r\nexit /b %errorlevel%\r\n";
        await File.WriteAllTextAsync(Path.Combine(scriptsRoot, "SetupComplete.cmd"), setup, cancellationToken).ConfigureAwait(false);
    }

    private static void CopyManifestToTarget(string manifestPath, ExecutionMode mode)
    {
        if (!mode.IsDryRun())
        {
            File.Copy(manifestPath, "W:\\ProgramData\\EasyWin\\manifest.json", true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}

public sealed class PostInstallOrchestrator(
    IJsonSerializer json,
    IManifestService manifests,
    IPhysicalDiskService disks,
    IDiskPartService diskPart,
    ICatalogService catalogs,
    ApplicationInstaller apps,
    DriverInstaller drivers,
    ProfileApplicator profiles,
    WindowsActivationService activation,
    CleanupService cleanup)
{
    public async Task RunAsync(string copiedManifestPath, IProgress<DeploymentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        DeploymentManifest? manifest = null;
        var checkpoints = new DeploymentCheckpointService(manifests);
        try
        {
            manifest = await json.DeserializeFileAsync<DeploymentManifest>(copiedManifestPath, cancellationToken).ConfigureAwait(false);
            if (manifest.State.CompletedStages.Contains(DeploymentStage.Completed))
            {
                progress?.Report(new DeploymentProgress(DeploymentStage.Completed, "PostInstall уже завершён", 100));
                return;
            }

            manifest = await checkpoints.BeginAttemptAsync(manifest, copiedManifestPath, cancellationToken).ConfigureAwait(false);
            PhysicalDiskSnapshot disk = await disks.GetDiskAsync(manifest.TargetDisk.DiskNumber, cancellationToken).ConfigureAwait(false);
            PartitionInfo stage = disk.Partitions.SingleOrDefault(partition => partition.GptPartitionId == manifest.StagingPartition.GptPartitionId)
                ?? throw new DeploymentSafetyException("postinstall.staging.missing", "Deployment partition is missing; PostInstall stopped.");
            if (stage.OffsetBytes != manifest.StagingPartition.OffsetBytes || stage.SizeBytes != manifest.StagingPartition.SizeBytes)
            {
                throw new DeploymentSafetyException("postinstall.staging.changed", "Deployment partition geometry changed; PostInstall stopped.");
            }

            string stageRoot;
            if (!string.IsNullOrWhiteSpace(stage.DriveLetter))
            {
                stageRoot = $"{stage.DriveLetter}:\\";
            }
            else
            {
                char letter = SelectAvailableDriveLetter(disk.Partitions, manifest.StagingPartition.RootPath);
                string assign = $"select disk {disk.Identity.DiskNumber}\r\nselect partition {stage.PartitionNumber}\r\nassign letter={letter}\r\n";
                await diskPart.ExecuteAsync(assign, Path.GetDirectoryName(copiedManifestPath)!, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
                stageRoot = $"{letter}:\\";
            }

            await manifests.ValidateAsync(manifest, stageRoot, true, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.ValidateManifest, recoveryRequired: false, cancellationToken).ConfigureAwait(false);

            ApplicationCatalog appCatalog = await catalogs.LoadApplicationsAsync(Path.Combine(stageRoot, "Config", "apps", "catalog.json"), cancellationToken).ConfigureAwait(false);
            string machineDriverCatalog = Path.Combine(stageRoot, "Drivers", "catalog.json");
            string driverCatalogPath = manifest.DriverSelectionMode == DriverSelectionMode.Automatic && File.Exists(machineDriverCatalog)
                ? machineDriverCatalog
                : Path.Combine(stageRoot, "Config", "drivers", "catalog.json");
            DriverCatalog driverCatalog = await catalogs.LoadDriversAsync(driverCatalogPath, cancellationToken).ConfigureAwait(false);
            InstallationProfile profile = await catalogs.LoadProfileAsync(Path.Combine(stageRoot, "Config", "profiles", $"{manifest.ProfileId}.json"), cancellationToken).ConfigureAwait(false);
            IReadOnlySet<string> hardware = await drivers.DetectHardwareIdsAsync(manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            IEnumerable<DriverPackage> selectedDrivers = manifest.DriverSelectionMode == DriverSelectionMode.None ? [] : driverCatalog.Packages.Where(driver => manifest.DriverSelectionMode == DriverSelectionMode.Automatic || manifest.DriverIds.Contains(driver.Id, StringComparer.OrdinalIgnoreCase));
            progress?.Report(new DeploymentProgress(DeploymentStage.InstallDrivers, "Установка совместимых драйверов", 90));
            manifest = await checkpoints.StartAsync(manifest, copiedManifestPath, DeploymentStage.InstallDrivers, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            await drivers.InstallAsync(selectedDrivers, hardware, stageRoot, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.InstallDrivers, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DeploymentProgress(DeploymentStage.InstallApplications, "Установка приложений", 93));
            manifest = await checkpoints.StartAsync(manifest, copiedManifestPath, DeploymentStage.InstallApplications, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            await apps.InstallAsync(appCatalog.Applications.Where(app => manifest.ApplicationIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase)), stageRoot, manifest.ExecutionMode, hardware, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.InstallApplications, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DeploymentProgress(DeploymentStage.ApplyProfile, "Применение профиля", 96));
            manifest = await checkpoints.StartAsync(manifest, copiedManifestPath, DeploymentStage.ApplyProfile, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            await profiles.ApplyAsync(profile, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.ApplyProfile, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DeploymentProgress(DeploymentStage.ActivateWindows, "Проверка цифровой лицензии и штатная активация Windows", 97));
            manifest = await checkpoints.StartAsync(manifest, copiedManifestPath, DeploymentStage.ActivateWindows, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            WindowsActivationResult activationResult = await activation.TryActivateAsync(manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.ActivateWindows, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DeploymentProgress(DeploymentStage.ActivateWindows, activationResult.Message, 97));
            progress?.Report(new DeploymentProgress(DeploymentStage.CleanupStaging, "Удаление временного раздела и настройка WinRE", 98));
            manifest = await checkpoints.StartAsync(manifest, copiedManifestPath, DeploymentStage.CleanupStaging, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            await cleanup.CleanupAsync(manifest, Path.GetDirectoryName(copiedManifestPath)!, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.CleanupStaging, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.Completed, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DeploymentProgress(DeploymentStage.Completed, "Готово", 100));
        }
        catch (Exception exception)
        {
            if (manifest is not null)
            {
                string code = exception is DeploymentSafetyException safety ? safety.Code : "postinstall.failed";
                try
                {
                    await checkpoints.FailAsync(manifest, copiedManifestPath, code, exception.Message, exception.ToString(), recoverable: true, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original failure.
                }
            }

            throw;
        }
    }

    private static char SelectAvailableDriveLetter(IReadOnlyList<PartitionInfo> partitions, string preferredRoot)
    {
        var used = partitions
            .Select(static partition => partition.DriveLetter)
            .Where(static letter => !string.IsNullOrWhiteSpace(letter))
            .Select(static letter => char.ToUpperInvariant(letter![0]))
            .ToHashSet();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string name = drive.Name;
            if (name.Length >= 2 && name[1] == ':')
            {
                used.Add(char.ToUpperInvariant(name[0]));
            }
        }
        char preferred = !string.IsNullOrWhiteSpace(preferredRoot) && preferredRoot.Length >= 2 && preferredRoot[1] == ':'
            ? char.ToUpperInvariant(preferredRoot[0])
            : 'E';
        if (preferred is >= 'D' and <= 'Z' && !used.Contains(preferred))
        {
            return preferred;
        }

        for (char candidate = 'E'; candidate <= 'Z'; candidate++)
        {
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new DeploymentSafetyException("postinstall.drive_letter.unavailable", "No safe drive letter is available for the deployment partition.");
    }
}
