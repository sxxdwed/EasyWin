using EasyWin.Core.Manifests;
using EasyWin.Core.Models;
using EasyWin.Core.Security;
using EasyWin.Core.Serialization;
using EasyWin.Core.Validation;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Tests;

public sealed class SafetyTests
{
    [Fact]
    public void TargetDiskValidation_IdentityChanged_Fails()
    {
        var result = new DiskIdentityValidator().Validate(TestData.Disk(), TestData.Disk("OTHER"));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, value => value.Contains("Serial", StringComparison.Ordinal));
    }

    [Fact]
    public void TargetDiskValidation_AllStableFieldsMatch_Passes()
    {
        Assert.True(new DiskIdentityValidator().Validate(TestData.Disk(), TestData.Disk()).IsValid);
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("Apps/../../escape.exe")]
    [InlineData("C:\\Windows\\cmd.exe")]
    [InlineData("\\\\server\\share\\x.exe")]
    [InlineData("Apps/file.exe:stream")]
    public void PathValidation_TraversalAndRootedPaths_Fail(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => PathValidator.ValidateRelativePath(path));
    }

    [Fact]
    public void DeploymentPartitionProtection_ScriptNeverDeletesProtectedPartition()
    {
        Guid stage = Guid.NewGuid();
        long offset = 900L * 1024 * 1024 * 1024;
        long size = 20L * 1024 * 1024 * 1024;
        PartitionInfo[] partitions =
        [
            new() { DiskNumber = 0, PartitionNumber = 1, GptPartitionId = Guid.NewGuid(), OffsetBytes = 1_048_576, SizeBytes = 260L * 1024 * 1024 },
            new() { DiskNumber = 0, PartitionNumber = 2, GptPartitionId = Guid.NewGuid(), OffsetBytes = 300L * 1024 * 1024, SizeBytes = offset - 300L * 1024 * 1024 },
            new() { DiskNumber = 0, PartitionNumber = 3, GptPartitionId = stage, OffsetBytes = offset, SizeBytes = size, Role = PartitionRole.Deployment },
        ];
        string script = new DiskPartScriptBuilder().PrepareTargetPreservingDeployment(new DiskPreparationRequest(0, stage, 3, offset, size), partitions);
        Assert.DoesNotContain("clean", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("select partition 3\r\ndelete", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("create partition efi", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("create partition msr", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeploymentPartitionProtection_StagingNotLast_Fails()
    {
        Guid stage = Guid.NewGuid();
        PartitionInfo[] partitions =
        [
            new() { PartitionNumber = 1, GptPartitionId = stage, OffsetBytes = 100, SizeBytes = 100, Role = PartitionRole.Deployment },
            new() { PartitionNumber = 2, GptPartitionId = Guid.NewGuid(), OffsetBytes = 200, SizeBytes = 100 },
        ];
        Assert.Throws<DeploymentSafetyException>(() => new DiskPartScriptBuilder().PrepareTargetPreservingDeployment(new DiskPreparationRequest(0, stage, 1, 100, 100), partitions));
    }

    [Fact]
    public void AdditionalDiskErase_DeletesOnlyEnumeratedPartitionsWithoutClean()
    {
        PartitionInfo[] partitions =
        [
            new() { DiskNumber = 1, PartitionNumber = 1, GptPartitionId = Guid.NewGuid(), Role = PartitionRole.EfiSystem },
            new() { DiskNumber = 1, PartitionNumber = 3, GptPartitionId = Guid.NewGuid(), Role = PartitionRole.Data },
        ];

        string script = new DiskPartScriptBuilder().EraseAdditionalDiskAndCreateDataVolume(new AdditionalDiskEraseRequest(1), partitions);
        Assert.StartsWith("select disk 1", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clean", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("select partition 3\r\ndelete partition override", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("select partition 1\r\ndelete partition override", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("format fs=ntfs quick label=\"Data\"", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdditionalDiskErase_DiskContainingStaging_Fails()
    {
        PartitionInfo[] partitions =
        [
            new() { DiskNumber = 1, PartitionNumber = 1, GptPartitionId = Guid.NewGuid(), Role = PartitionRole.Deployment },
        ];

        Assert.Throws<DeploymentSafetyException>(() =>
            new DiskPartScriptBuilder().EraseAdditionalDiskAndCreateDataVolume(new AdditionalDiskEraseRequest(1), partitions));
    }

    [Fact]
    public void StagingSourceSelection_UsesEligibleVolumeOnChosenTargetDisk()
    {
        const long required = 20L * 1024 * 1024 * 1024;
        var disk = new PhysicalDiskSnapshot(
            TestData.Disk(),
            false,
            false,
            "GPT",
            [
                new PartitionInfo { DiskNumber = 1, PartitionNumber = 1, OffsetBytes = 1, SizeBytes = 900L * 1024 * 1024 * 1024, ShrinkAvailableBytes = 100L * 1024 * 1024 * 1024, DriveLetter = "D", FileSystem = "NTFS", Role = PartitionRole.Data },
                new PartitionInfo { DiskNumber = 1, PartitionNumber = 2, OffsetBytes = 901L * 1024 * 1024 * 1024, SizeBytes = 1024L * 1024 * 1024, Role = PartitionRole.Recovery },
            ]);

        Assert.Equal("D", LocalStagingPartitionService.SelectShrinkSource(disk, required).DriveLetter);
    }

    [Fact]
    public void StagingSourceSelection_InsufficientSafeShrink_FailsBeforeDiskPart()
    {
        var disk = new PhysicalDiskSnapshot(
            TestData.Disk(),
            false,
            false,
            "GPT",
            [new PartitionInfo { DiskNumber = 0, PartitionNumber = 3, SizeBytes = 500L * 1024 * 1024 * 1024, ShrinkAvailableBytes = 10L * 1024 * 1024 * 1024, DriveLetter = "C", FileSystem = "NTFS", Role = PartitionRole.Windows }]);

        Assert.Throws<DeploymentSafetyException>(() =>
            LocalStagingPartitionService.SelectShrinkSource(disk, 20L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void ExternalUsbDisk_IsRejectedAsDestructiveTarget()
    {
        DiskIdentity usb = TestData.Disk() with { BusType = DiskBusType.Usb };
        Assert.Throws<DeploymentSafetyException>(() => DeploymentGuard.FixedInternalDisk(usb, "test disk"));
    }

    [Fact]
    public async Task ManifestValidation_TargetDuplicatedAsEraseDisk_Fails()
    {
        (DeploymentManifest manifest, _) = await CreateManifestAsync();
        manifest = manifest with { AdditionalDisksToErase = [manifest.TargetDisk] };
        var service = new ManifestService(new SystemTextJsonSerializer(), new Sha256HashService());
        Assert.Throws<InvalidDataException>(() => service.Seal(manifest));
    }

    [Fact]
    public async Task ManifestValidation_MoreThanTwoTotalDisks_Fails()
    {
        (DeploymentManifest manifest, _) = await CreateManifestAsync();
        manifest = manifest with
        {
            AdditionalDisksToErase =
            [
                TestData.Disk("SECOND") with { DiskNumber = 1, DeviceId = "\\\\.\\PhysicalDrive1", UniqueId = "SECOND" },
                TestData.Disk("THIRD") with { DiskNumber = 2, DeviceId = "\\\\.\\PhysicalDrive2", UniqueId = "THIRD" },
            ]
        };
        var service = new ManifestService(new SystemTextJsonSerializer(), new Sha256HashService());
        Assert.Throws<InvalidDataException>(() => service.Seal(manifest));
    }

    [Fact]
    public async Task HashValidation_ModifiedFile_Fails()
    {
        string root = TestData.NewDirectory();
        string file = Path.Combine(root, "payload.bin");
        await File.WriteAllTextAsync(file, "original");
        var hashes = new Sha256HashService();
        string expected = await hashes.ComputeSha256Async(file);
        await File.WriteAllTextAsync(file, "modified");
        Assert.False((await hashes.VerifyFileAsync(file, expected)).IsValid);
    }

    [Fact]
    public async Task ManifestValidation_InvalidIntegrity_Fails()
    {
        (DeploymentManifest manifest, string root) = await CreateManifestAsync();
        var service = new ManifestService(new SystemTextJsonSerializer(), new Sha256HashService());
        DeploymentManifest sealedManifest = service.Seal(manifest);
        sealedManifest = sealedManifest with { ProfileId = "tampered" };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ValidateAsync(sealedManifest, root, true));
    }

    [Fact]
    public async Task ManifestValidation_InvalidFileHash_Fails()
    {
        (DeploymentManifest manifest, string root) = await CreateManifestAsync();
        var service = new ManifestService(new SystemTextJsonSerializer(), new Sha256HashService());
        DeploymentManifest sealedManifest = service.Seal(manifest);
        await File.AppendAllTextAsync(Path.Combine(root, "Images", "install.wim"), "tamper");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ValidateAsync(sealedManifest, root, true));
    }

    [Fact]
    public async Task ImageStoredOutsideProtectedPartition_Fails()
    {
        (DeploymentManifest manifest, string root) = await CreateManifestAsync(includeBoot: true);
        string outside = Path.Combine(TestData.NewDirectory(), "install.wim");
        await File.WriteAllTextAsync(outside, "image");
        PhysicalDiskSnapshot disk = Snapshot(manifest);
        Assert.Throws<DeploymentSafetyException>(() => new DeploymentSafetyValidator(new DiskIdentityValidator()).ValidateBeforeDestructive(manifest, disk, outside));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task BootEnvironmentIncomplete_Fails()
    {
        (DeploymentManifest manifest, string root) = await CreateManifestAsync(includeBoot: false);
        string image = Path.Combine(root, "Images", "install.wim");
        Assert.Throws<DeploymentSafetyException>(() => new DeploymentSafetyValidator(new DiskIdentityValidator()).ValidateBeforeDestructive(manifest, Snapshot(manifest), image));
    }

    [Fact]
    public async Task DeploymentCheckpoints_PersistAttemptStageAndFailureAtomically()
    {
        (DeploymentManifest manifest, string root) = await CreateManifestAsync();
        string path = Path.Combine(root, "manifest.json");
        var service = new ManifestService(new SystemTextJsonSerializer(), new Sha256HashService());
        var checkpoints = new DeploymentCheckpointService(service);
        await service.SaveAsync(manifest, path);

        manifest = await checkpoints.BeginAttemptAsync(manifest, path);
        manifest = await checkpoints.StartAsync(manifest, path, DeploymentStage.PrepareDisk, recoveryRequired: true);
        manifest = await checkpoints.CompleteAsync(manifest, path, DeploymentStage.PrepareDisk, recoveryRequired: true);
        manifest = await checkpoints.FailAsync(manifest, path, "test.failure", "Test failure", "details", recoverable: true);

        DeploymentManifest loaded = await service.LoadAndValidateAsync(path, verifyFiles: true);
        Assert.Equal(1, loaded.State.AttemptNumber);
        Assert.Equal(DeploymentStage.Failed, loaded.State.CurrentStage);
        Assert.Equal(DeploymentStage.PrepareDisk, loaded.State.LastSuccessfulStage);
        Assert.Contains(DeploymentStage.PrepareDisk, loaded.State.CompletedStages);
        Assert.True(loaded.State.RecoveryRequired);
        Assert.Equal("test.failure", loaded.State.LastError?.Code);
        Assert.Empty(Directory.EnumerateFiles(root, ".manifest.json.*.tmp"));
    }

    [Fact]
    public async Task ManifestValidation_LastSuccessfulStageMustBeCompleted_Fails()
    {
        (DeploymentManifest manifest, _) = await CreateManifestAsync();
        manifest = manifest with
        {
            State = manifest.State with { LastSuccessfulStage = DeploymentStage.ApplyImage },
        };
        var service = new ManifestService(new SystemTextJsonSerializer(), new Sha256HashService());
        Assert.Throws<InvalidDataException>(() => service.Seal(manifest));
    }

    [Fact]
    public async Task DeploymentCheckpoints_NewAttemptPreservesCompletedStagesAndClearsError()
    {
        (DeploymentManifest manifest, string root) = await CreateManifestAsync();
        string path = Path.Combine(root, "manifest.json");
        var service = new ManifestService(new SystemTextJsonSerializer(), new Sha256HashService());
        var checkpoints = new DeploymentCheckpointService(service);
        await service.SaveAsync(manifest, path);

        manifest = await checkpoints.BeginAttemptAsync(manifest, path);
        manifest = await checkpoints.CompleteAsync(manifest, path, DeploymentStage.ValidateManifest, recoveryRequired: false);
        manifest = await checkpoints.FailAsync(manifest, path, "test.failure", "Test failure", null, recoverable: true);
        manifest = await checkpoints.BeginAttemptAsync(manifest, path);

        Assert.Equal(2, manifest.State.AttemptNumber);
        Assert.Contains(DeploymentStage.ValidateManifest, manifest.State.CompletedStages);
        Assert.Equal(DeploymentStage.ValidateManifest, manifest.State.LastSuccessfulStage);
        Assert.Null(manifest.State.LastError);
    }

    private static PhysicalDiskSnapshot Snapshot(DeploymentManifest manifest) => new(
        manifest.TargetDisk,
        false,
        false,
        "GPT",
        [
            new PartitionInfo { DiskNumber = 0, PartitionNumber = 1, GptPartitionId = Guid.NewGuid(), OffsetBytes = 1, SizeBytes = 100, Role = PartitionRole.Windows },
            new PartitionInfo { DiskNumber = 0, PartitionNumber = manifest.StagingPartition.PartitionNumber, GptPartitionId = manifest.StagingPartition.GptPartitionId, OffsetBytes = manifest.StagingPartition.OffsetBytes, SizeBytes = manifest.StagingPartition.SizeBytes, DriveLetter = Path.GetPathRoot(manifest.StagingPartition.RootPath)?[0].ToString(), Role = PartitionRole.Deployment },
        ]);

    private static async Task<(DeploymentManifest Manifest, string Root)> CreateManifestAsync(bool includeBoot = false)
    {
        string root = TestData.NewDirectory();
        Directory.CreateDirectory(Path.Combine(root, "Images"));
        Directory.CreateDirectory(Path.Combine(root, "WinPE", "sources"));
        Directory.CreateDirectory(Path.Combine(root, "WinPE", "boot"));
        string image = Path.Combine(root, "Images", "install.wim");
        await File.WriteAllTextAsync(image, "image");
        if (includeBoot)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "WinPE", "sources", "boot.wim"), "boot");
            await File.WriteAllTextAsync(Path.Combine(root, "WinPE", "boot", "boot.sdi"), "sdi");
        }

        var hashes = new Sha256HashService();
        string digest = await hashes.ComputeSha256Async(image);
        DiskIdentity disk = TestData.Disk();
        return (new DeploymentManifest
        {
            PlanId = Guid.NewGuid(),
            ExecutionMode = ExecutionMode.DryRun,
            TargetDisk = disk,
            StagingPartition = new StagingPartitionIdentity { Disk = disk, PartitionNumber = 2, GptPartitionId = Guid.NewGuid(), OffsetBytes = 1000, SizeBytes = 1000, RootPath = root, Label = "EASYWIN_DEPLOY" },
            Image = new DeploymentImageReference { RelativePath = "Images/install.wim", ImageIndex = 1, Name = "Windows 11 Pro", EditionId = "Professional", Architecture = ProcessorArchitecture.X64, Container = WindowsImageContainer.Wim, Sha256 = digest, LengthBytes = new FileInfo(image).Length },
            Boot = new BootConfiguration { BootEntryId = includeBoot ? Guid.NewGuid() : null, OneTimeBootConfigured = includeBoot },
            ProfileId = "standard",
            FileInventory = [new ManifestFileEntry { RelativePath = "Images/install.wim", Sha256 = digest, LengthBytes = new FileInfo(image).Length, Kind = ManifestFileKind.WindowsImage }],
        }, root);
    }
}
