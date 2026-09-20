using System.Xml.Linq;
using EasyWin.Core.Models;
using EasyWin.Deployment.Configuration;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.PostInstall;
using EasyWin.Deployment.Safety;

namespace EasyWin.Tests;

public sealed class RecoveryBootstrapTests
{
    [Fact]
    public void Cleanup_ArchivesNestedLogsByPlanWithoutRemovingSource()
    {
        string root = TestData.NewDirectory(), work = TestData.NewDirectory();
        Directory.CreateDirectory(Path.Combine(root, "Logs", "WinPE"));
        File.WriteAllText(Path.Combine(root, "Logs", "WinPE", "boot.log"), "checkpoint");
        Guid planId = Guid.NewGuid();
        string destination = DeploymentLogArchive.Preserve(root, work, planId);
        Assert.Equal(Path.Combine(work, "Logs", planId.ToString("N"), "Deployment"), destination);
        Assert.Equal("checkpoint", File.ReadAllText(Path.Combine(destination, "WinPE", "boot.log")));
        Assert.True(File.Exists(Path.Combine(root, "Logs", "WinPE", "boot.log")));
        Assert.Throws<InvalidOperationException>(() => DeploymentLogArchive.Preserve(root, root, planId));
    }
    private static PhysicalDiskSnapshot Disk(bool space) => new(TestData.Disk(), false, false, "GPT",
        [new() { OffsetBytes = 1048576, SizeBytes = 100L << 30, Role = PartitionRole.Windows, DriveLetter = "C", FileSystem = "NTFS", ShrinkAvailableBytes = 30L << 30 }])
        { UnallocatedExtents = space ? [new((100L << 30) + 1048576, 60L << 30)] : [] };

    [Fact]
    public void SameDisk_UnallocatedSpace_UsesExistingExtent()
    {
        var disk = Disk(true); var extent = UnallocatedStaging.Select(disk, 20L << 30);
        Assert.NotNull(extent);
        string script = new DiskPartScriptBuilder().CreateStagingPartition(new(0, 'C', 'T', extent.SizeBytes, UnallocatedOffsetBytes: extent.OffsetBytes));
        Assert.Contains("offset=", script); Assert.DoesNotContain("shrink", script); Assert.DoesNotContain("delete", script);
    }
    [Fact]
    public void SameDisk_NoUnallocated_UsesSafeShrink()
    {
        var disk = Disk(false); Assert.Null(UnallocatedStaging.Select(disk, 20L << 30));
        Assert.Equal("C", LocalStagingPartitionService.SelectShrinkSource(disk, 20L << 30).DriveLetter);
    }
    [Fact]
    public void SameDisk_NoSafeStorage_FailsBeforeConfirmation()
    {
        var disk = Disk(false) with { Partitions = [] };
        Assert.Null(UnallocatedStaging.Select(disk, 20L << 30));
        Assert.Throws<DeploymentSafetyException>(() => LocalStagingPartitionService.SelectShrinkSource(disk, 20L << 30));
    }
    [Fact]
    public void SameDisk_ForgedUnallocatedOverlap_Rejected()
    {
        var disk = Disk(true) with { UnallocatedExtents = [new(50L << 30, 30L << 30)] };
        Assert.Null(UnallocatedStaging.Select(disk, 20L << 30));
    }
    [Fact]
    public void OemSetupCompleteSkipped_FallbackTaskIsIndependentlyRegistered()
    {
        var unattended = XDocument.Parse(new UnattendGenerator().Generate(new("en-US", "en-US", "UTC", "EASYWIN", null)));
        Assert.Contains(unattended.Descendants(), e => e.Name.LocalName == "Path" && e.Value == PostInstallBootstrap.RegisterCommand);
        var task = XDocument.Parse(PostInstallBootstrap.TaskXml());
        Assert.Contains(task.Descendants(), e => e.Name.LocalName == "UserId" && e.Value == "S-1-5-18");
        Assert.Contains(task.Descendants(), e => e.Name.LocalName == "LogonTrigger");
        Assert.Contains(task.Descendants(), e => e.Name.LocalName == "BootTrigger");
        Assert.Contains(task.Descendants(), e => e.Name.LocalName == "Arguments" && e.Value.Contains("--defer-until-setup-complete", StringComparison.Ordinal));
        Assert.DoesNotContain("SetupComplete", PostInstallBootstrap.RegistrationScript);
    }
    [Fact]
    public void PostInstall_DoubleLaunch_IsMutuallyExclusiveAndLockRecovers()
    {
        string root = TestData.NewDirectory();
        using (PostInstallBootstrap.AcquireLock(root)) Assert.Throws<IOException>(() => PostInstallBootstrap.AcquireLock(root));
        using var resumed = PostInstallBootstrap.AcquireLock(root);
        Assert.True(resumed.CanWrite);
    }
    [Fact]
    public void FirstBoot_WrongWindowsDisk_PreventsCleanup()
    {
        var disk = Disk(false) with { Identity = TestData.Disk() with { IsBootDisk = true } };
        var manifest = new DeploymentManifest { TargetDisk = disk.Identity, State = new() { CompletedStages = [DeploymentStage.AwaitingFirstBoot] } };
        FirstBootGuard.Validate(manifest, disk, "C:\\Windows", true, false);
        Assert.Throws<DeploymentSafetyException>(() => FirstBootGuard.Validate(manifest, disk, "D:\\Windows", true, false));
        Assert.Throws<DeploymentSafetyException>(() => FirstBootGuard.Validate(manifest, disk, "C:\\Windows", true, true));
        Assert.Throws<DeploymentSafetyException>(() => FirstBootGuard.Validate(manifest, disk with { Identity = disk.Identity with { SerialNumber = "different" } }, "C:\\Windows", true, false));
    }
}
