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
    string? PayloadRoot = null,
    PreparedDrivers? Drivers = null);

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
        EasyWin.Core.Localization.DeploymentStrings.SetLanguage(request.Plan.Language);
        if (request.Plan.ExecutionMode.IsDryRun() && runner is not RecordingProcessRunner)
            throw new DeploymentSafetyException("dryrun.runner", "Desktop DryRun requires a recording runner.");
        if (!request.Plan.ExecutionMode.IsDryRun() && (!request.Plan.DataLossAcknowledged || !request.Plan.FinalConfirmationAccepted))
        {
            throw new DeploymentSafetyException("confirmation.required", "Both destructive-operation confirmations are required.");
        }

        string work = DeploymentGuard.AbsolutePath(request.WorkRoot, "EasyWin work root");
        bool separateStorage = request.Plan.StagingDisk is not null && !StagingSelection.SameDisk(request.Plan.TargetDisk, request.Plan.StagingDisk);
        var volumeValidator = new StagingVolumeValidator(disks, new BitLockerService(runner));
        if (separateStorage && !request.Plan.ExecutionMode.IsDryRun())
            _ = await volumeValidator.ValidateAsync(request.Plan, request.Plan.RequiredStagingBytes, cancellationToken).ConfigureAwait(false);
        if (!request.Plan.ExecutionMode.IsDryRun())
            await StaleDeploymentGuard.ValidateAsync(disks, runner, request.Plan.Language, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(work);
        progress?.Report(new DeploymentProgress(DeploymentStage.ValidateEnvironment, EasyWin.Core.Localization.DeploymentStrings.Get("StageValidateEnvironment"), 2));
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

        progress?.Report(new DeploymentProgress(DeploymentStage.PrepareStaging, EasyWin.Core.Localization.DeploymentStrings.Get("StagePrepareStaging"), 10));
        string winPeWork = Path.Combine(work, "winpe-work");
        string winPeOut = Path.Combine(work, "winpe-media");
        string? exportedDrivers = null;
        IReadOnlyList<string> exportedInfs = [];
        if (!request.Plan.ExecutionMode.IsDryRun() && request.Plan.DriverSelectionMode == DriverSelectionMode.Automatic)
        {
            if (request.Drivers is null) throw new DeploymentSafetyException("drivers.preflight.required", EasyWin.Core.Localization.DeploymentStrings.Get("DriverVerificationFailed"));
            await request.Drivers.ValidateAsync(cancellationToken).ConfigureAwait(false);
            exportedDrivers = request.Drivers.Root;
            exportedInfs = request.Drivers.WinPeInfs;
            staticPayloadBytes = checked(staticPayloadBytes + request.Drivers.Bytes);
        }
        WinPeBuildResult built = await winPe.BuildAsync(new WinPeBuildRequest(
            request.AdkWinPeRoot, "amd64", winPeWork, winPeOut, request.WinPePayloadRoot,
            ["WinPE-WMI", "WinPE-NetFX", "WinPE-Scripting", "WinPE-PowerShell", "WinPE-StorageWMI"], exportedInfs, request.Plan.PlanId), request.Plan.ExecutionMode, cancellationToken).ConfigureAwait(false);

        progress?.Report(new DeploymentProgress(DeploymentStage.PrepareStaging, EasyWin.Core.Localization.DeploymentStrings.Get("StagePrepareStaging"), 20));
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
            var volume = request.Plan.ExecutionMode.IsDryRun() ? StagingSelection.SeparateVolume(storage, requestedSize, request.Plan.StagingVolumeId)
                : await volumeValidator.ValidateAsync(request.Plan, requestedSize, cancellationToken).ConfigureAwait(false);
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
                RequiredFreeBytes = requestedSize,
                FolderRelativePath = folder, RootPath = folderRoot, GptPartitionId = volume.GptPartitionId,
                PartitionNumber = volume.PartitionNumber, OffsetBytes = volume.OffsetBytes, SizeBytes = volume.SizeBytes };
        }
        else
        {
            var extent = UnallocatedStaging.Select(current, requestedSize);
            PartitionInfo? source = extent is null ? LocalStagingPartitionService.SelectShrinkSource(current, requestedSize) : null;
            var stageRequest = new StagingPartitionRequest(current.Identity.DiskNumber, source?.DriveLetter?[0] ?? 'C', request.StagingDriveLetter, requestedSize, UnallocatedOffsetBytes: extent?.OffsetBytes);
            partition = await localStaging.CreateAsync(current.Identity, stageRequest, work, request.Plan.ExecutionMode, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new DeploymentProgress(DeploymentStage.PrepareBoot, EasyWin.Core.Localization.DeploymentStrings.Get("StagePrepareBoot"), 35));
        (DeploymentManifest manifest, string manifestPath) = await staging.BuildAsync(new StagingBuildRequest(request.Plan, partition, request.ImagePath, built.MediaRoot, request.PostInstallPayloadRoot, request.ConfigurationRoot, PayloadRoot: request.PayloadRoot, ExportedDriversRoot: exportedDrivers), cancellationToken).ConfigureAwait(false);
        string root = partition.RootPath;
        string backup = Path.Combine(root, "Boot", "bcd.backup");
        var checkpoints = new DeploymentCheckpointService(manifests);
        bool bootArmed = false;
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.PrepareBoot, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        try
        {
            if (separateStorage && !request.Plan.ExecutionMode.IsDryRun())
                _ = await volumeValidator.ValidateAsync(request.Plan, 0, cancellationToken).ConfigureAwait(false);
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
            progress?.Report(new DeploymentProgress(DeploymentStage.AwaitingWinPeBoot, EasyWin.Core.Localization.DeploymentStrings.Get("StageAwaitingWinPeBoot"), 45));
            if (separateStorage && !request.Plan.ExecutionMode.IsDryRun())
                _ = await volumeValidator.ValidateAsync(request.Plan, 0, cancellationToken).ConfigureAwait(false);

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
    public Task RunAsync(string manifestPath, IProgress<DeploymentProgress>? progress = null, CancellationToken cancellationToken = default)
        => RunCoreAsync(manifestPath, progress, false, cancellationToken);

    public Task ResumeAsync(string manifestPath, IProgress<DeploymentProgress>? progress = null, CancellationToken cancellationToken = default)
        => RunCoreAsync(manifestPath, progress, true, cancellationToken);

    private async Task RunCoreAsync(string manifestPath, IProgress<DeploymentProgress>? progress, bool resumeSafely, CancellationToken cancellationToken)
    {
        DeploymentManifest? manifest = null;
        var checkpoints = new DeploymentCheckpointService(manifests);
        try
        {
            progress?.Report(new DeploymentProgress(DeploymentStage.ValidateManifest, EasyWin.Core.Localization.DeploymentStrings.Get("StageValidateManifest"), 46));
            manifest = await manifests.LoadAndValidateAsync(manifestPath, true, cancellationToken).ConfigureAwait(false);
            EasyWin.Core.Localization.DeploymentStrings.SetLanguage(manifest.Language);
            // A reboot must never implicitly replay partition deletion or formatting.
            // Recovery of an interrupted destructive stage requires a separate verified plan.
            bool recovering = RecoveryPolicy.NeedsRecovery(manifest);
            if (recovering && !resumeSafely)
            {
                manifest = manifest with { State = manifest.State with { DestructiveWorkStarted = true } };
                throw new DeploymentSafetyException("winpe.replay.blocked", "A destructive deployment was already started. Automatic replay is blocked; preserve deployment storage and use the recovery procedure.");
            }
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
            RecoveryDecision? recovery = recovering ? RecoveryPolicy.Validate(manifest, disk, storage) : null;
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
                if (!recovering && DriveInfo.GetDrives().Any(d => d.Name is "W:\\" or "S:\\" or "R:\\"))
                    throw new DeploymentSafetyException("winpe.letters", "Required W:, S: or R: drive letter is occupied.");
            }
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ValidateTargetDisk, recoveryRequired: false, cancellationToken).ConfigureAwait(false);

            disk = await StagingSelection.ResolveAsync(disks, manifest.TargetDisk, cancellationToken).ConfigureAwait(false);
            storage = await StagingSelection.ResolveAsync(disks, manifest.StagingPartition.Disk, cancellationToken).ConfigureAwait(false);
            stage = StagingSelection.ValidatePartition(manifest.StagingPartition, storage);
            await manifests.LoadAndValidateAsync(manifestPath, true, cancellationToken).ConfigureAwait(false);
            new DeploymentSafetyValidator(identities).ValidateBeforeDestructive(manifest, disk, image, stagingDisk: storage);
            if (recovering)
            {
                await MapRecoveryVolumesAsync(manifest, stageRoot, cancellationToken).ConfigureAwait(false);
            }
            else
            {
            var prepare = new DiskPreparationRequest(disk.Identity.DiskNumber, stage.GptPartitionId, stage.PartitionNumber, stage.OffsetBytes, stage.SizeBytes);
            progress?.Report(new DeploymentProgress(DeploymentStage.PrepareDisk, EasyWin.Core.Localization.DeploymentStrings.Get("StagePrepareDisk"), 52));
            manifest = manifest with { State = manifest.State with { DestructiveWorkStarted = true } };
            manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.PrepareDisk, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            if (manifest.StagingPartition.Mode == StagingMode.SeparateDiskFolder)
                await diskPart.PrepareSeparateTargetAsync(disk, storage, stageRoot, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            else
                await diskPart.ExecuteAsync(scripts.PrepareTargetPreservingDeployment(prepare, disk.Partitions), stageRoot, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            if (!manifest.ExecutionMode.IsDryRun())
            {
                var prepared = await StagingSelection.ResolveAsync(disks, manifest.TargetDisk, cancellationToken).ConfigureAwait(false);
                manifest = manifest with { State = manifest.State with { PreparedTargetLayout = RecoveryPolicy.Capture(manifest, prepared) } };
            }
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.PrepareDisk, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            }

            if (recovery?.ApplyImage != false)
            {
            progress?.Report(new DeploymentProgress(DeploymentStage.ApplyImage, EasyWin.Core.Localization.DeploymentStrings.Get("StageApplyImage"), 60));
            manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.ApplyImage, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            disk = await StagingSelection.ResolveAsync(disks, manifest.TargetDisk, cancellationToken).ConfigureAwait(false);
            storage = await StagingSelection.ResolveAsync(disks, manifest.StagingPartition.Disk, cancellationToken).ConfigureAwait(false);
            StagingSelection.ValidatePartition(manifest.StagingPartition, storage);
            await manifests.LoadAndValidateAsync(manifestPath, true, cancellationToken).ConfigureAwait(false);
            if (recovering) RecoveryPolicy.Validate(manifest, disk, storage);
            if (!manifest.ExecutionMode.IsDryRun() && !disk.Partitions.Any(p => p.DriveLetter == "W" && p.FileSystem == "NTFS"))
                throw new DeploymentSafetyException("apply.target.mapping", "W: does not resolve to the confirmed Windows target.");
            await images.ApplyAsync(new ApplyImageRequest(image, manifest.Image.ImageIndex, "W:\\"), new Progress<int>(percent => progress?.Report(new DeploymentProgress(DeploymentStage.ApplyImage, EasyWin.Core.Localization.DeploymentStrings.Get("StageApplyImage") + $" {percent}%", 55 + percent * .25))), cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ApplyImage, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            }
            var stagedDrivers = manifest.FileInventory.Where(f => f.RelativePath.StartsWith("ExportedDrivers/", StringComparison.OrdinalIgnoreCase) && f.RelativePath.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
                .Select(f => PathValidator.ResolveUnderRoot(stageRoot, f.RelativePath, true)).ToArray();
            if (stagedDrivers.Length > 0)
                await images.AddDriversAsync(new DriverInjectionRequest("W:\\", stagedDrivers), cancellationToken).ConfigureAwait(false);
            if (recovering)
            {
                disk = await StagingSelection.ResolveAsync(disks, manifest.TargetDisk, cancellationToken).ConfigureAwait(false);
                storage = await StagingSelection.ResolveAsync(disks, manifest.StagingPartition.Disk, cancellationToken).ConfigureAwait(false);
                RecoveryPolicy.Validate(manifest, disk, storage);
                await manifests.LoadAndValidateAsync(manifestPath, true, cancellationToken).ConfigureAwait(false);
                if (!manifest.ExecutionMode.IsDryRun() && !File.Exists("W:\\Windows\\System32\\ntoskrnl.exe"))
                    throw new DeploymentSafetyException("recovery.windows.missing", EasyWin.Core.Localization.DeploymentStrings.Get("RecoveryUnsafe"));
            }
            progress?.Report(new DeploymentProgress(DeploymentStage.ConfigureBoot, EasyWin.Core.Localization.DeploymentStrings.Get("StageConfigureBoot"), 82));
            manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.ConfigureBoot, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            await bootFiles.ConfigureAsync(new BootFilesRequest("W:\\", "S:\\", Locale: manifest.Language), manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ConfigureBoot, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.GenerateUnattend, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            string unattendPath = manifest.ExecutionMode.IsDryRun() ? Path.Combine(stageRoot, "DryRun", "unattend.xml") : "W:\\Windows\\Panther\\unattend.xml";
            await unattend.WriteAsync(unattendPath, new UnattendOptions(manifest.Language, manifest.Language, manifest.TimeZone, manifest.ComputerName ?? "EASYWIN-PC", null), cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.GenerateUnattend, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
            foreach (PhysicalDiskSnapshot additional in additionalDisks)
            {
                if (recovering)
                {
                    if (!manifest.State.CompletedStages.Contains(DeploymentStage.PartitionDisk))
                        manifest = manifest with { State = manifest.State with { OptionalWarnings = manifest.State.OptionalWarnings.Append(EasyWin.Core.Localization.DeploymentStrings.Get("RecoverySkippedErase")).ToArray() } };
                    continue;
                }
                var freshAdditional = await StagingSelection.ResolveAsync(disks, additional.Identity, cancellationToken).ConfigureAwait(false);
                StagingSelection.Writable(freshAdditional);
                var freshTarget = await StagingSelection.ResolveAsync(disks, manifest.TargetDisk, cancellationToken).ConfigureAwait(false);
                var freshStorage = await StagingSelection.ResolveAsync(disks, manifest.StagingPartition.Disk, cancellationToken).ConfigureAwait(false);
                StagingSelection.ValidatePartition(manifest.StagingPartition, freshStorage);
                if (freshAdditional.Identity.DiskNumber == freshTarget.Identity.DiskNumber || freshAdditional.Identity.DiskNumber == freshStorage.Identity.DiskNumber)
                    throw new DeploymentSafetyException("erase.overlap", "Erase disk overlaps target or deployment storage.");
                progress?.Report(new DeploymentProgress(DeploymentStage.PartitionDisk, EasyWin.Core.Localization.DeploymentStrings.Get("StagePartitionDisk"), 86));
                manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.PartitionDisk, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
                await diskPart.ExecuteAsync(
                    scripts.EraseAdditionalDiskAndCreateDataVolume(new AdditionalDiskEraseRequest(freshAdditional.Identity.DiskNumber), freshAdditional.Partitions),
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
            progress?.Report(new DeploymentProgress(DeploymentStage.AwaitingFirstBoot, EasyWin.Core.Localization.DeploymentStrings.Get("StageAwaitingFirstBoot"), 88));
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

    private async Task MapRecoveryVolumesAsync(DeploymentManifest manifest, string work, CancellationToken token)
    {
        foreach (char letter in manifest.StagingPartition.Mode == StagingMode.SeparateDiskFolder ? new[] { 'S', 'W', 'R' } : new[] { 'S', 'W' })
        {
            var target = await StagingSelection.ResolveAsync(disks, manifest.TargetDisk, token).ConfigureAwait(false);
            var storage = await StagingSelection.ResolveAsync(disks, manifest.StagingPartition.Disk, token).ConfigureAwait(false);
            RecoveryPolicy.Validate(manifest, target, storage);
            var partition = RecoveryPolicy.Mappings(manifest, target).Single(m => m.Letter == letter).Partition;
            var all = await disks.GetDisksAsync(token).ConfigureAwait(false);
            if (all.Any(d => d.Partitions.Any(p => string.Equals(p.DriveLetter, letter.ToString(), StringComparison.OrdinalIgnoreCase) &&
                (!StagingSelection.SameDisk(d.Identity, target.Identity) || p.GptPartitionId != partition.GptPartitionId))))
                throw new DeploymentSafetyException("recovery.letter.collision", EasyWin.Core.Localization.DeploymentStrings.Get("RecoveryUnsafe"));
            if (string.Equals(partition.DriveLetter, letter.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!manifest.ExecutionMode.IsDryRun() && DriveInfo.GetDrives().Any(d => d.Name.Equals($"{letter}:\\", StringComparison.OrdinalIgnoreCase)))
                throw new DeploymentSafetyException("recovery.letter.collision", EasyWin.Core.Localization.DeploymentStrings.Get("RecoveryUnsafe"));
            string remove = string.IsNullOrWhiteSpace(partition.DriveLetter) ? "" : $"remove letter={DeploymentGuard.DriveLetter(partition.DriveLetter[0])}\r\n";
            await diskPart.ExecuteAsync($"select disk {DeploymentGuard.DiskNumber(target.Identity.DiskNumber)}\r\nselect partition {DeploymentGuard.PartitionNumber(partition.PartitionNumber)}\r\n{remove}assign letter={letter}\r\n", work, manifest.ExecutionMode, token).ConfigureAwait(false);
            if (!manifest.ExecutionMode.IsDryRun())
            {
                var mapped = await StagingSelection.ResolveAsync(disks, manifest.TargetDisk, token).ConfigureAwait(false);
                if (!mapped.Partitions.Any(p => p.GptPartitionId == partition.GptPartitionId && p.DriveLetter == letter.ToString()))
                    throw new DeploymentSafetyException("recovery.letter.mapping", EasyWin.Core.Localization.DeploymentStrings.Get("RecoveryUnsafe"));
            }
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
        await File.WriteAllTextAsync(Path.Combine(target, "PostInstallTask.xml"), PostInstallBootstrap.TaskXml(), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(target, "RegisterPostInstall.cmd"), PostInstallBootstrap.RegistrationScript, cancellationToken).ConfigureAwait(false);
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
            EasyWin.Core.Localization.DeploymentStrings.SetLanguage(manifest.Language);
            if (manifest.State.CompletedStages.Contains(DeploymentStage.Completed))
            {
                progress?.Report(new DeploymentProgress(DeploymentStage.Completed, EasyWin.Core.Localization.DeploymentStrings.Get("StageCompleted"), 100));
                return;
            }

            manifest = await checkpoints.BeginAttemptAsync(manifest, copiedManifestPath, cancellationToken).ConfigureAwait(false);
            var bootTarget = await StagingSelection.ResolveAsync(disks, manifest.TargetDisk, cancellationToken).ConfigureAwait(false);
            if (!manifest.ExecutionMode.IsDryRun()) FirstBootGuard.ValidateCurrent(manifest, bootTarget);
            manifest = manifest with { State = manifest.State with { FirstBootValidated = true } };
            await manifests.SaveAsync(manifest, copiedManifestPath, cancellationToken).ConfigureAwait(false);
            manifest = manifests.Seal(manifest);
            if (manifest.State.CompletedStages.Contains(DeploymentStage.CleanupStaging))
            {
                await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.Completed, false, cancellationToken).ConfigureAwait(false);
                return;
            }
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

            if (manifest.ExecutionMode.IsDryRun()) stageRoot = manifest.StagingPartition.RootPath;
            else if (manifest.StagingPartition.Mode == StagingMode.SeparateDiskFolder)
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
            if (!manifest.State.CompletedStages.Contains(DeploymentStage.InstallDrivers))
            {
            progress?.Report(new DeploymentProgress(DeploymentStage.InstallDrivers, EasyWin.Core.Localization.DeploymentStrings.Get("StageInstallDrivers"), 90));
            manifest = await checkpoints.StartAsync(manifest, copiedManifestPath, DeploymentStage.InstallDrivers, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            if (!manifest.ExecutionMode.IsDryRun() && manifest.DriverSelectionMode == DriverSelectionMode.Automatic && Directory.Exists(Path.Combine(stageRoot, "ExportedDrivers")))
                await drivers.InstallExportedAsync(Path.Combine(stageRoot, "ExportedDrivers"), cancellationToken).ConfigureAwait(false);
            else
                await drivers.InstallAsync(selectedDrivers, hardware, stageRoot, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.InstallDrivers, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            }
            if (!manifest.State.CompletedStages.Contains(DeploymentStage.InstallApplications))
            {
            progress?.Report(new DeploymentProgress(DeploymentStage.InstallApplications, EasyWin.Core.Localization.DeploymentStrings.Get("StageInstallApplications"), 93));
            manifest = await checkpoints.StartAsync(manifest, copiedManifestPath, DeploymentStage.InstallApplications, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            foreach (var app in appCatalog.Applications.Where(app => manifest.ApplicationIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase)))
            {
                if (manifest.State.CompletedApplicationIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase) ||
                    manifest.State.FailedApplicationIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    await apps.InstallAsync([app], stageRoot, manifest.ExecutionMode, hardware, cancellationToken).ConfigureAwait(false);
                    manifest = manifest with { State = manifest.State with { CompletedApplicationIds = manifest.State.CompletedApplicationIds.Append(app.Id).ToArray() } };
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    manifest = manifest with { State = manifest.State with {
                        FailedApplicationIds = manifest.State.FailedApplicationIds.Append(app.Id).ToArray(),
                        OptionalWarnings = manifest.State.OptionalWarnings.Append(app.Id + ": " + e.Message).ToArray() } };
                }
                // Commit each result before starting the next installer. A cancelled installer
                // remains pending; generic installers cannot guarantee exactly-once execution.
                await manifests.SaveAsync(manifest, copiedManifestPath, cancellationToken).ConfigureAwait(false);
                manifest = manifests.Seal(manifest);
            }
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.InstallApplications, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            }
            if (!manifest.State.CompletedStages.Contains(DeploymentStage.ApplyProfile))
            {
            progress?.Report(new DeploymentProgress(DeploymentStage.ApplyProfile, EasyWin.Core.Localization.DeploymentStrings.Get("StageApplyProfile"), 96));
            manifest = await checkpoints.StartAsync(manifest, copiedManifestPath, DeploymentStage.ApplyProfile, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            await profiles.ApplyAsync(profile, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.ApplyProfile, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            }
            if (!manifest.State.CompletedStages.Contains(DeploymentStage.ActivateWindows))
            {
            progress?.Report(new DeploymentProgress(DeploymentStage.ActivateWindows, EasyWin.Core.Localization.DeploymentStrings.Get("StageActivateWindows"), 97));
            manifest = await checkpoints.StartAsync(manifest, copiedManifestPath, DeploymentStage.ActivateWindows, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            try
            {
                WindowsActivationResult activationResult = await activation.TryActivateAsync(manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
                if (!activationResult.IsActivated)
                    manifest = manifest with { State = manifest.State with { OptionalWarnings = manifest.State.OptionalWarnings.Append("activation: " + activationResult.Message).ToArray() } };
                progress?.Report(new DeploymentProgress(DeploymentStage.ActivateWindows, EasyWin.Core.Localization.DeploymentStrings.Get("StageActivateWindows"), 97));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                manifest = manifest with { State = manifest.State with { OptionalWarnings = manifest.State.OptionalWarnings.Append("activation: " + e.Message).ToArray() } };
            }
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.ActivateWindows, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            }
            progress?.Report(new DeploymentProgress(DeploymentStage.CleanupStaging, EasyWin.Core.Localization.DeploymentStrings.Get("StageCleanupStaging"), 98));
            manifest = await checkpoints.StartAsync(manifest, copiedManifestPath, DeploymentStage.CleanupStaging, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            await cleanup.CleanupAsync(manifest, Path.GetDirectoryName(copiedManifestPath)!, manifest.ExecutionMode, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.CleanupStaging, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            manifest = await checkpoints.CompleteAsync(manifest, copiedManifestPath, DeploymentStage.Completed, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DeploymentProgress(DeploymentStage.Completed, EasyWin.Core.Localization.DeploymentStrings.Get("StageCompleted"), 100));
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
