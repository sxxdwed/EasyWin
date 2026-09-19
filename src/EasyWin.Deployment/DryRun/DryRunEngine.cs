using EasyWin.Core.Manifests;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Core.Security;
using EasyWin.Core.Serialization;
using EasyWin.Deployment.Boot;
using EasyWin.Deployment.Configuration;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Images;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.PostInstall;
using EasyWin.Deployment.Staging;

namespace EasyWin.Deployment.DryRun;

public sealed record DryRunReport(
    bool Success,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string ManifestPath,
    IReadOnlyList<string> Stages,
    IReadOnlyList<string> RecordedCommands,
    IReadOnlyList<string> DiskPartScripts,
    bool DestructiveCommandsExecuted,
    string Message);

public sealed class DryRunEngine
{
    public async Task<DryRunReport> RunAsync(string workspace, string configurationRoot, CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(workspace);
        string runRoot = Path.Combine(root, $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var stages = new List<string>();
        var runner = new RecordingProcessRunner();
        var hashes = new Sha256HashService();
        var json = new SystemTextJsonSerializer();
        var manifests = new ManifestService(json, hashes);
        var checkpoints = new DeploymentCheckpointService(manifests);
        var diskPart = new DiskPartService(runner);
        var scripts = new DiskPartScriptBuilder();

        string stageRoot = Path.Combine(runRoot, "E");
        string source = Path.Combine(runRoot, "source");
        string winpe = Path.Combine(source, "WinPE");
        string post = Path.Combine(source, "PostInstall");
        Directory.CreateDirectory(Path.Combine(winpe, "sources"));
        Directory.CreateDirectory(Path.Combine(winpe, "boot"));
        Directory.CreateDirectory(post);
        await File.WriteAllTextAsync(Path.Combine(winpe, "sources", "boot.wim"), "dryrun boot.wim", cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(winpe, "boot", "boot.sdi"), "dryrun boot.sdi", cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(post, "EasyWin.PostInstall.exe"), "dryrun postinstall", cancellationToken).ConfigureAwait(false);
        string image = Path.Combine(source, "install.wim");
        await File.WriteAllTextAsync(image, "dryrun official image simulation", cancellationToken).ConfigureAwait(false);

        var disk = new DiskIdentity
        {
            DiskNumber = 0,
            DeviceId = "\\\\.\\PhysicalDrive0",
            SerialNumber = "DRYRUN-SERIAL-0001",
            Model = "EasyWin Virtual NVMe",
            SizeBytes = 1024L * 1024 * 1024 * 1024,
            BusType = DiskBusType.Nvme,
            UniqueId = "DRYRUN-DISK-0001",
            IsBootDisk = true,
            IsSystemDisk = true,
        };
        var additionalDisk = new DiskIdentity
        {
            DiskNumber = 1,
            DeviceId = "\\\\.\\PhysicalDrive1",
            SerialNumber = "DRYRUN-SERIAL-0002",
            Model = "EasyWin Virtual SATA Data",
            SizeBytes = 2L * 1024 * 1024 * 1024 * 1024,
            BusType = DiskBusType.Sata,
            UniqueId = "DRYRUN-DISK-0002",
        };
        long stageSize = 8L * 1024 * 1024 * 1024;
        var partition = new StagingPartitionIdentity
        {
            Disk = disk,
            PartitionNumber = 5,
            GptPartitionId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            OffsetBytes = disk.SizeBytes - stageSize,
            SizeBytes = stageSize,
            Label = "EASYWIN_DEPLOY",
            RootPath = stageRoot,
            FileSystem = "NTFS",
        };
        var plan = new ReinstallPlan
        {
            PlanId = Guid.NewGuid(),
            ExecutionMode = ExecutionMode.DryRun,
            TargetDisk = disk,
            AdditionalDisksToErase = [additionalDisk],
            Image = new WindowsImageInfo { SourcePath = image, ImageIndex = 6, Name = "Windows 11 Pro", EditionId = "Professional", Architecture = ProcessorArchitecture.X64, Container = WindowsImageContainer.Wim, SizeBytes = new FileInfo(image).Length },
            ProfileId = "standard",
            ApplicationIds = ["7zip"],
            DriverSelectionMode = DriverSelectionMode.Automatic,
            Language = "ru-RU",
            DataLossAcknowledged = true,
            FinalConfirmationAccepted = true,
        };

        stages.Add("Desktop");
        string createStage = scripts.CreateStagingPartition(new StagingPartitionRequest(0, 'C', 'E', stageSize));
        await diskPart.ExecuteAsync(createStage, runRoot, ExecutionMode.DryRun, cancellationToken).ConfigureAwait(false);
        stages.Add("staging");
        var staging = new StagingService(hashes, manifests);
        (DeploymentManifest manifest, string manifestPath) = await staging.BuildAsync(new StagingBuildRequest(plan, partition, image, winpe, post, Path.GetFullPath(configurationRoot)), cancellationToken).ConfigureAwait(false);

        stages.Add("one-time boot");
        string backup = Path.Combine(stageRoot, "Boot", "bcd.backup");
        var bcd = new BcdService(runner);
        TemporaryBootEntry entry = await bcd.ArmOneTimeWinPeAsync(new TemporaryBootRequest(backup, Path.Combine(stageRoot, "WinPE", "sources", "boot.wim"), Path.Combine(stageRoot, "WinPE", "boot", "boot.sdi")), ExecutionMode.DryRun, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        await File.WriteAllTextAsync(backup, "dryrun bcd backup", cancellationToken).ConfigureAwait(false);
        var backupInfo = new FileInfo(backup);
        manifest = manifest with
        {
            Boot = manifest.Boot with { BootEntryId = entry.LoaderId, OneTimeBootConfigured = true },
            FileInventory = manifest.FileInventory.Append(new ManifestFileEntry { RelativePath = "Boot/bcd.backup", Sha256 = await hashes.ComputeSha256Async(backup, cancellationToken).ConfigureAwait(false), LengthBytes = backupInfo.Length, Kind = ManifestFileKind.BcdBackup }).ToArray(),
        };
        await manifests.SaveAsync(manifest, manifestPath, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.PrepareBoot, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.AwaitingWinPeBoot, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        manifest = await manifests.LoadAndValidateAsync(manifestPath, true, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.BeginAttemptAsync(manifest, manifestPath, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.AwaitingWinPeBoot, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ValidateManifest, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ValidateTargetDisk, recoveryRequired: false, cancellationToken).ConfigureAwait(false);

        stages.Add("WinPE autostart");
        stages.Add("validation");
        var partitions = new[]
        {
            new PartitionInfo { DiskNumber = 0, PartitionNumber = 1, GptPartitionId = Guid.NewGuid(), OffsetBytes = 1024 * 1024, SizeBytes = 260L * 1024 * 1024, Role = PartitionRole.EfiSystem },
            new PartitionInfo { DiskNumber = 0, PartitionNumber = 2, GptPartitionId = Guid.NewGuid(), OffsetBytes = 300L * 1024 * 1024, SizeBytes = 16L * 1024 * 1024, Role = PartitionRole.MicrosoftReserved },
            new PartitionInfo { DiskNumber = 0, PartitionNumber = 3, GptPartitionId = Guid.NewGuid(), DriveLetter = "C", OffsetBytes = 400L * 1024 * 1024, SizeBytes = partition.OffsetBytes - 400L * 1024 * 1024, Role = PartitionRole.Windows },
            new PartitionInfo { DiskNumber = 0, PartitionNumber = 5, GptPartitionId = partition.GptPartitionId, DriveLetter = "E", OffsetBytes = partition.OffsetBytes, SizeBytes = partition.SizeBytes, Role = PartitionRole.Deployment },
        };
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.PrepareDisk, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
        await diskPart.ExecuteAsync(scripts.PrepareTargetPreservingDeployment(new DiskPreparationRequest(0, partition.GptPartitionId, 5, partition.OffsetBytes, partition.SizeBytes), partitions), runRoot, ExecutionMode.DryRun, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.PrepareDisk, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
        var additionalPartitions = new[]
        {
            new PartitionInfo { DiskNumber = 1, PartitionNumber = 1, GptPartitionId = Guid.NewGuid(), OffsetBytes = 1024 * 1024, SizeBytes = 260L * 1024 * 1024, Role = PartitionRole.EfiSystem },
            new PartitionInfo { DiskNumber = 1, PartitionNumber = 2, GptPartitionId = Guid.NewGuid(), OffsetBytes = 300L * 1024 * 1024, SizeBytes = 16L * 1024 * 1024, Role = PartitionRole.MicrosoftReserved },
            new PartitionInfo { DiskNumber = 1, PartitionNumber = 3, GptPartitionId = Guid.NewGuid(), OffsetBytes = 400L * 1024 * 1024, SizeBytes = additionalDisk.SizeBytes - 800L * 1024 * 1024, Role = PartitionRole.Data },
            new PartitionInfo { DiskNumber = 1, PartitionNumber = 4, GptPartitionId = Guid.NewGuid(), OffsetBytes = additionalDisk.SizeBytes - 400L * 1024 * 1024, SizeBytes = 350L * 1024 * 1024, Role = PartitionRole.Recovery },
        };
        stages.Add("Windows install");
        var imageService = new WindowsImageService(runner);
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.ApplyImage, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
        await imageService.ApplyAsync(new ApplyImageRequest(Path.Combine(stageRoot, manifest.Image.RelativePath.Replace('/', Path.DirectorySeparatorChar)), manifest.Image.ImageIndex, "W:\\"), cancellationToken: cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ApplyImage, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
        stages.Add("BCDBoot + unattend");
        var bootFiles = new BootFilesService(runner);
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.ConfigureBoot, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
        await bootFiles.ConfigureAsync(new BootFilesRequest("W:\\", "S:\\", Locale: "ru-RU"), ExecutionMode.DryRun, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ConfigureBoot, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
        string offline = Path.Combine(runRoot, "offline", "Windows", "Panther", "unattend.xml");
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.GenerateUnattend, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
        await new UnattendGenerator().WriteAsync(offline, new UnattendOptions("ru-RU", "ru-RU", "Russian Standard Time", "EASYWIN-PC", null), cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.GenerateUnattend, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.PartitionDisk, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
        await diskPart.ExecuteAsync(
            scripts.EraseAdditionalDiskAndCreateDataVolume(new AdditionalDiskEraseRequest(1), additionalPartitions),
            runRoot,
            ExecutionMode.DryRun,
            cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.PartitionDisk, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
        await bootFiles.RegisterFirmwareAsync(new BootFilesRequest("W:\\", "S:\\", Locale: "ru-RU"), ExecutionMode.DryRun, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.PreparePostInstall, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.PreparePostInstall, recoveryRequired: true, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.AwaitingFirstBoot, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        stages.Add("second disk erase");
        stages.Add("reboot");

        string fakeApp = Path.Combine(stageRoot, "Apps", "7Zip", "setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeApp)!);
        await File.WriteAllTextAsync(fakeApp, "dryrun installer", cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.InstallApplications, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        await new ApplicationInstaller(runner, hashes).InstallAsync([new ApplicationPackage { Id = "7zip", Name = "7-Zip", Installer = "Apps/7Zip/setup.exe", Arguments = ["/S"], Sha256 = new string('0', 64) }], stageRoot, ExecutionMode.DryRun, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.InstallApplications, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        string fakeInf = Path.Combine(stageRoot, "Drivers", "Chipset", "sample.inf");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeInf)!);
        await File.WriteAllTextAsync(fakeInf, "[Version]", cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.InstallDrivers, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        await new DriverInstaller(runner, hashes).InstallAsync([new DriverPackage { Id = "chipset", Name = "Chipset", InfPath = "Drivers/Chipset/sample.inf", Sha256 = new string('0', 64), HardwareIds = ["PCI\\VEN_1234"] }], new HashSet<string>(["PCI\\VEN_1234&DEV_0001"], StringComparer.OrdinalIgnoreCase), stageRoot, ExecutionMode.DryRun, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.InstallDrivers, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.ApplyProfile, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        await new ProfileApplicator(runner).ApplyAsync(new InstallationProfile { Id = "standard", DisplayName = "Standard", Settings = new Dictionary<string, string> { ["showFileExtensions"] = "true", ["disableConsumerSuggestions"] = "true" } }, ExecutionMode.DryRun, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ApplyProfile, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.ActivateWindows, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        _ = await new WindowsActivationService(runner).TryActivateAsync(ExecutionMode.DryRun, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.ActivateWindows, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        stages.Add("PostInstall");
        manifest = await checkpoints.StartAsync(manifest, manifestPath, DeploymentStage.CleanupStaging, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        await diskPart.ExecuteAsync(scripts.RemoveStagingAndCreateRecovery(new FinalizeDiskRequest(0, partition.GptPartitionId, 5)), runRoot, ExecutionMode.DryRun, cancellationToken).ConfigureAwait(false);
        await bcd.RemoveTemporaryEntryAsync(entry.LoaderId, ExecutionMode.DryRun, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.CleanupStaging, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        manifest = await checkpoints.CompleteAsync(manifest, manifestPath, DeploymentStage.Completed, recoveryRequired: false, cancellationToken).ConfigureAwait(false);
        stages.Add("cleanup");
        stages.Add("finished");

        var report = new DryRunReport(
            true,
            started,
            DateTimeOffset.UtcNow,
            manifestPath,
            stages,
            runner.Commands.Select(static command => command.ToDisplayString()).ToArray(),
            diskPart.PlannedScripts.ToArray(),
            false,
            "Desktop → staging → one-time boot → WinPE → two-disk validation/erase → deployment → PostInstall → cleanup completed in DryRun.");
        await json.SerializeToFileAsync(report, Path.Combine(runRoot, "dry-run-report.json"), cancellationToken).ConfigureAwait(false);
        return report;
    }
}
