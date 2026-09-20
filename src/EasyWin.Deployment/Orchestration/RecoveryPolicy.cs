using EasyWin.Core.Localization;
using EasyWin.Core.Models;
using EasyWin.Core.Validation;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;
using EasyWin.Deployment.Staging;

namespace EasyWin.Deployment.Orchestration;

public sealed record RecoveryDecision(bool ApplyImage, bool ConfigureBoot);

public static class RecoveryPolicy
{
    public static readonly Guid EfiType = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid MsrType = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    public static readonly Guid DataType = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    public static readonly Guid RecoveryType = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");

    public static bool NeedsRecovery(DeploymentManifest manifest) => manifest.State.DestructiveWorkStarted ||
        manifest.State.CompletedStages.Any(s => s >= DeploymentStage.PrepareDisk && s <= DeploymentStage.Completed) ||
        manifest.State.CurrentStage is >= DeploymentStage.PrepareDisk and <= DeploymentStage.Completed ||
        manifest.State.LastError?.Stage is >= DeploymentStage.PrepareDisk and <= DeploymentStage.Completed;

    public static bool CanCleanStaging(DeploymentManifest manifest) => !NeedsRecovery(manifest) &&
        !manifest.State.StartedStages.Contains(DeploymentStage.PrepareDisk);

    public static IReadOnlyList<PartitionInfo> Capture(DeploymentManifest manifest, PhysicalDiskSnapshot target)
    {
        StagingSelection.Writable(target);
        if (!new DiskIdentityValidator().Validate(manifest.TargetDisk, target.Identity).IsValid) Fail();
        var partitions = target.Partitions.OrderBy(p => p.OffsetBytes).ToArray();
        bool sameDisk = manifest.StagingPartition.Mode == StagingMode.SameDiskPartition;
        if (partitions.Length != 4 || partitions.Select(p => p.GptPartitionId).Distinct().Count() != 4 ||
            partitions.Any(p => p.GptPartitionId == Guid.Empty || p.IsReadOnly || p.SizeBytes <= 0 || p.OffsetBytes < 1048576)) Fail();
        long end = 0;
        foreach (var p in partitions)
        {
            if (p.OffsetBytes < end || p.OffsetBytes > target.Identity.SizeBytes - p.SizeBytes) Fail();
            end = p.OffsetBytes + p.SizeBytes;
        }
        if (partitions[0].GptTypeId != EfiType || !IsFs(partitions[0], "FAT32") || partitions[0].SizeBytes < 100L << 20 ||
            partitions[1].GptTypeId != MsrType || partitions[1].SizeBytes < 16L << 20 ||
            partitions[2].GptTypeId != DataType || !IsFs(partitions[2], "NTFS") || partitions[2].SizeBytes < 32L << 30) Fail();
        if (sameDisk)
        {
            if (partitions[3].GptPartitionId != manifest.StagingPartition.GptPartitionId) Fail();
            StagingSelection.ValidatePartition(manifest.StagingPartition, target);
        }
        else if (partitions[3].GptTypeId != RecoveryType || !IsFs(partitions[3], "NTFS")) Fail();
        return partitions;
    }

    public static RecoveryDecision Validate(DeploymentManifest manifest, PhysicalDiskSnapshot target, PhysicalDiskSnapshot storage)
    {
        if (!manifest.State.CompletedStages.Contains(DeploymentStage.PrepareDisk) ||
            manifest.State.PreparedTargetLayout.Count == 0 ||
            manifest.State.CompletedStages.Contains(DeploymentStage.Completed) || manifest.State.FirstBootValidated) Fail();
        StagingSelection.ValidatePartition(manifest.StagingPartition, storage);
        var actual = Capture(manifest, target);
        var expected = manifest.State.PreparedTargetLayout.OrderBy(p => p.OffsetBytes).ToArray();
        if (actual.Count != expected.Length) Fail();
        for (int i = 0; i < actual.Count; i++)
        {
            var a = actual[i]; var e = expected[i];
            if (a.GptPartitionId != e.GptPartitionId || a.GptTypeId != e.GptTypeId || a.OffsetBytes != e.OffsetBytes ||
                a.SizeBytes != e.SizeBytes || !string.Equals(a.FileSystem, e.FileSystem, StringComparison.OrdinalIgnoreCase)) Fail();
        }
        bool applied = manifest.State.CompletedStages.Contains(DeploymentStage.ApplyImage);
        bool boot = manifest.State.CompletedStages.Contains(DeploymentStage.ConfigureBoot);
        if (boot && !applied) Fail();
        return new(!applied, !boot);
    }

    public static IReadOnlyList<(PartitionInfo Partition, char Letter)> Mappings(DeploymentManifest manifest, PhysicalDiskSnapshot target)
    {
        var layout = Capture(manifest, target);
        var result = new List<(PartitionInfo, char)> { (layout[0], 'S'), (layout[2], 'W') };
        if (manifest.StagingPartition.Mode == StagingMode.SeparateDiskFolder) result.Add((layout[3], 'R'));
        return result;
    }

    private static bool IsFs(PartitionInfo p, string fs) => string.Equals(p.FileSystem, fs, StringComparison.OrdinalIgnoreCase);
    private static void Fail() => throw new DeploymentSafetyException("recovery.unsafe", DeploymentStrings.Get("RecoveryUnsafe"));
}
