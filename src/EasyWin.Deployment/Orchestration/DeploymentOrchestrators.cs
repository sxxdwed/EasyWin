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
        if (request.Plan.ExecutionMode.IsDryRun() && runner is not RecordingProcessRunner)
            throw new DeploymentSafetyException("dryrun.runner", "Desktop DryRun requires a recording runner.");
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

        PhysicalDiskSnapshot current = await StagingSelection.ResolveAsync(disks, request.Plan.TargetDisk, cancellationToken).ConfigureAwait(false);
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
            PhysicalDiskSnapshot additional = await StagingSelection.ResolveAsync(disks, expected, cancellationToken).ConfigureAwait(false);
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
        var selectionChecks = await StagingSelection.CheckAsync(disks, request.Plan, requestedSize, cancellationToken).ConfigureAwait(false);
        if (selectionChecks.Any(c => !c.Passed))
            throw new DeploymentSafetyException("selection.failed", string.Join("; ", selectionChecks.Where(c => !c.Passed).Select(c => c.Code + ": " + c.Message)));
        StagingPartitionIdentity partition;
        if (request.Plan.StagingDisk is not null && !StagingSelection.SameDisk(request.Plan.TargetDisk, request.Plan.StagingDisk))
        {
            var storage = await StagingSelection.ResolveAsync(disks, request.Plan.StagingDisk, cancellationToken).ConfigureAwait(false);
            var volume = StagingSelection.SeparateVolume(storage, requestedSize, request.Plan.StagingVolumeId);
            string folder = $"EasyWin-Deployment/{request.Plan.PlanId:N}";
            string volumeRoot = volume.DriveLetter + ":\\";
            if (!request.Plan.ExecutionMode.IsDryRun())
            {
                var encryption = await new BitLockerService(runner).GetStatusAsync(volumeRoot, cancellationToken).ConfigureAwait(false);
                if (encryption.IsEncrypted || encryption.IsProtectionEnabled)
                    throw new DeploymentSafetyException("staging.bitlocker", "Separate staging requires an unencrypted volume accessible in WinPE.");
            }
            string folderRoot = PathValidator.ResolveUnderRoot(request.Plan.ExecutionMode.IsDryRun() ? Path.Combine(work, "DryRunStaging") : volumeRoot, folder);
            if (Directory.Exists(folderRoot)) throw new DeploymentSafetyException("staging.exists", "Deployment folder already exists.");
            Directory.CreateDirectory(folderRoot);
            var acl = await runner.RunAsync(new CommandSpec("icacls.exe", [folderRoot, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F"], requiresElevation: true), cancellationToken).ConfigureAwait(false);
            CommandFailureException.ThrowIfFailed("Protect deployment folder", acl);
            partition = new StagingPartitionIdentity { Disk = storage.Identity, Mode = StagingMode.SeparateDiskFolder,
                FolderRelativePath = folder, RootPath = folderRoot, GptPartitionId = volume.GptPartitionId,
                PartitionNumber = volume.PartitionNumber, OffsetBytes = volume.OffsetBytes, SizeBytes = volume.SizeBytes };
        }
        else
        {
            PartitionInfo source = LocalStagingPartitionService.SelectShrinkSource(current, requestedSize);
            var stageRequest = new StagingPartitionRequest(current.Identity.DiskNumber, source.DriveLetter![0], request.StagingDriveLetter, requestedSize);
            partition = await localStaging.CreateAsync(current.Identity, stageRequest, work, request.Plan.ExecutionMode, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new DeploymentProgress(DeploymentStage.PrepareBoot, "Копирование и проверка staged-файлов", 35));
        (DeploymentManifest manifest, string manifestPath) = await staging.BuildAsync(new StagingBuildRequest(request.Plan, partition, request.ImagePath, built.MediaRoot, request.PostInstallPayloadRoot, request.ConfigurationRoot, PayloadRoot: request.PayloadRoot), cancellationToken).ConfigureAwait(false);
        string root = partition.RootPath;
        string backup = Path.Combine(root, "Boot", "bcd.backup");
        var checkpoints = new DeploymentCheckpointService(manifests);
        bool bootArmed = false;
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.PrepareBoot, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        try
        {
            TemporaryBootEntry entry = await bcd.ArmOneTimeWinPeAsync(new TemporaryBootRequest(
                backup,
                Path.Combine(root, manifest.Boot.WinPeWimRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(root, manifest.Boot.WinPeSdiRelativePath.Replace('/', Path.DirectorySeparatorChar))), request.Plan.ExecutionMode, cancellationToken).ConfigureAwait(false);
            bootArmed = true;
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
            StagingSelection.Writable(await StagingSelection.ResolveAsync(disks, manifest.TargetDisk, cancellationToken).ConfigureAwait(false));
            var beforeBootStorage = await StagingSelection.ResolveAsync(disks, partition.Disk, cancellationToken).ConfigureAwait(false);
            var beforeBootVolume = StagingSelection.ValidatePartition(partition, beforeBootStorage);
            if (!request.Plan.ExecutionMode.IsDryRun() && !Path.GetFullPath(root).TrimEnd('\\').Equals(StagingSelection.Root(partition, beforeBootVolume).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new DeploymentSafetyException("staging.mapping.changed", "Staging mount changed before reboot.");
            foreach (var erase in manifest.AdditionalDisksToErase)
                EnsureEraseDiskSafe(erase, await StagingSelection.ResolveAsync(disks, erase, cancellationToken).ConfigureAwait(false), identities, "beforeboot");
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
            if (!request.Plan.ExecutionMode.IsDryRun() && bootArmed && File.Exists(backup))
            {
                var rollback = await runner.RunAsync(BcdCommandFactory.Import(backup), CancellationToken.None).ConfigureAwait(false);
                CommandFailureException.ThrowIfFailed("Restore BCD after preparation failure", rollback);
            }
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
            if (manifest.ExecutionMode.IsDryRun() && runner is not RecordingProcessRunner)
                throw new DeploymentSafetyException("dryrun.runner", "WinPE DryRun requires a recording runner.");
            if (!manifest.ExecutionMode.IsDryRun())
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows PE is required.");
                using var miniNt = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
                if (miniNt is null || !File.Exists(Path.Combine(System.Environment.SystemDirectory, "wpeutil.exe")))
                    throw new DeploymentSafetyException("winpe.environment", "Destructive deployment requires Windows PE.");
                var environment = new WindowsEnvironmentProbe().Capture(Path.GetDirectoryName(manifestPath)!);
                if (!environment.IsAdministrator || !environment.IsUefi)
                    throw new DeploymentSafetyException("winpe.environment", "Administrator privileges and UEFI WinPE are required.");
            }
            manifest = await checkpoints.BeginAttemptAsync(manifest, manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.AwaitingWinPeBoot, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ValidateManifest, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            PhysicalDiskSnapshot disk = await StagingSelection.ResolveAsync(disks, manifest.TargetDisk, cancellationToken).ConfigureAwait(false);
            DeploymentGuard.FixedInternalDisk(manifest.TargetDisk, "Windows target disk");
            DiskIdentityValidationResult identity = identities.Validate(manifest.TargetDisk, disk.Identity);
            if (!identity.IsValid)
            {
                throw new DeploymentSafetyException("winpe.disk.changed", string.Join("; ", identity.Errors));
            }

            var storage = await StagingSelection.ResolveAsync(disks, manifest.StagingPartition.Disk, cancellationToken).ConfigureAwait(false);
            StagingSelection.ValidatePartition(manifest.StagingPartition, storage);
            PartitionInfo stage = storage.Partitions.SingleOrDefault(partition => partition.GptPartitionId == manifest.StagingPartition.GptPartitionId)
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

                PhysicalDiskSnapshot additional = await StagingSelection.ResolveAsync(disks, expected, cancellationToken).ConfigureAwait(false);
                if (additional.Identity.DiskNumber == storage.Identity.DiskNumber || additional.Identity.DiskNumber == disk.Identity.DiskNumber)
                    throw new DeploymentSafetyException("winpe.erase.overlap", "Additional erase disk overlaps target or staging.");
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
            new DeploymentSafetyValidator(identities).ValidateBeforeDestructive(manifest, disk, image, stagingDisk: storage);
            if (!manifest.ExecutionMode.IsDryRun())
            {
                var editions = await images.GetEditionsAsync(image, cancellationToken).ConfigureAwait(false);
                if (!editions.Any(e => e.ImageIndex == manifest.Image.ImageIndex && e.Architecture == manifest.Image.Architecture && e.EditionId == manifest.Image.EditionId))
                    throw new DeploymentSafetyException("winpe.image.invalid", "Staged Windows edition does not match the manifest.");
                if (!string.Equals(Path.GetFullPath(stageRoot).TrimEnd('\\'), StagingSelection.Root(manifest.StagingPartition, stage).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new DeploymentSafetyException("winpe.staging.mapping", "Manifest is not on the validated staging volume.");
                if (DriveInfo.GetDrives().Any(d => d.Name is "W:\\" or "S:\\" or "R:\\"))
                    throw new DeploymentSafetyException("winpe.letters", "Required W:, S: or R: drive letter is occupied.");
            }
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ValidateTargetDisk, recoveryRequired: false, cancellationToken).ConfigureAwait(false);

            var prepare = new DiskPreparationRequest(disk.Identity.DiskNumber, stage.GptPartitionId, stage.PartitionNumber, stage.OffsetBytes, stage.SizeBytes);
            progress?.Report(new DeploymentProgress(DeploymentStage.PrepareDisk, "Подготовка GPT с защитой staging", 52));
            manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.PrepareDisk, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            if (manifest.StagingPartition.Mode == StagingMode.SeparateDiskFolder)
                await diskPart.PrepareSeparateTargetAsync(disk, storage, stageRoot, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            else
                await diskPart.ExecuteAsync(scripts.PrepareTargetPreservingDeployment(prepare, disk.Partitions), stageRoot, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
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
            string unattendPath = manifest.ExecutionMode.IsDryRun() ? Path.Combine(stageRoot, "DryRun", "unattend.xml") : "W:\\Windows\\Panther\\unattend.xml";
            await unattend.WriteAsync(unattendPath, new UnattendOptions(manifest.Language, manifest.Language, manifest.TimeZone, manifest.ComputerName ?? "EASYWIN-PC", null), cancellationToken).ConfigureAwait(false);
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
            if (manifest.StagingPartition.Mode == StagingMode.SeparateDiskFolder)
            {
                if (!manifest.ExecutionMode.IsDryRun()) Directory.CreateDirectory("R:\\Recovery\\WindowsRE");
                foreach (var command in new[] {
                    new CommandSpec("robocopy.exe", ["W:\\Windows\\System32\\Recovery", "R:\\Recovery\\WindowsRE", "winre.wim", "/COPY:DAT", "/R:1", "/W:1"], acceptableExitCodes: Enumerable.Range(0, 8).ToHashSet(), requiresElevation: true),
                    new CommandSpec("reagentc.exe", ["/setreimage", "/path", "R:\\Recovery\\WindowsRE", "/target", "W:\\Windows"], requiresElevation: true) })
                    CommandFailureException.ThrowIfFailed("Prepare target Recovery", await runner.RunAsync(command, cancellationToken).ConfigureAwait(false));
                if (!manifest.ExecutionMode.IsDryRun() && !File.Exists("R:\\Recovery\\WindowsRE\\winre.wim"))
                    throw new DeploymentSafetyException("winre.missing", "Recovery image was not copied.");
                await diskPart.ExecuteAsync("select volume R\r\nremove letter=R\r\n", stageRoot, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            }
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
            var candidate = await json.DeserializeFileAsync<DeploymentManifest>(copiedManifestPath, cancellationToken).ConfigureAwait(false);
            await manifests.ValidateAsync(candidate, Path.GetDirectoryName(copiedManifestPath)!, false, cancellationToken).ConfigureAwait(false);
            manifest = candidate;
            if (manifest.State.CompletedStages.Contains(DeploymentStage.Completed))
            {
                progress?.Report(new DeploymentProgress(DeploymentStage.Completed, "PostInstall уже завершён", 100));
                return;
            }

            manifest = await checkpoints.BeginAttemptAsync(manifest, copiedManifestPath, cancellationToken).ConfigureAwait(false);
            _ = await StagingSelection.ResolveAsync(disks, manifest.TargetDisk, cancellationToken).ConfigureAwait(false);
            PhysicalDiskSnapshot disk = await StagingSelection.ResolveAsync(disks, manifest.StagingPartition.Disk, cancellationToken).ConfigureAwait(false);
            StagingSelection.ValidatePartition(manifest.StagingPartition, disk);
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

            if (manifest.StagingPartition.Mode == StagingMode.SeparateDiskFolder)
                stageRoot = PathValidator.ResolveUnderRoot(stageRoot, manifest.StagingPartition.FolderRelativePath, true);
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
