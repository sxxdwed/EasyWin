using EasyWin.Core.Models;
using EasyWin.Core.Security;
using EasyWin.Core.Validation;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Staging;

public sealed record DiskSelectionCheck(string Code, bool Passed, string Message);

public static class StagingSelection
{
    public static bool SameDisk(DiskIdentity a, DiskIdentity b) =>
        new DiskIdentityValidator().Validate(a, b).IsValid;

    // Enumerate and resolve by stable identity; disk numbers may change after reboot.
    public static async Task<PhysicalDiskSnapshot> ResolveAsync(IPhysicalDiskService disks, DiskIdentity expected, CancellationToken token)
    {
        DeploymentGuard.FixedInternalDisk(expected, "Disk");
        var matches = (await disks.GetDisksAsync(token).ConfigureAwait(false))
            .Where(d => SameDisk(expected, d.Identity)).ToArray();
        if (matches.Length != 1)
            throw new DeploymentSafetyException("disk.identity.changed", "Disk identity changed, missing, or ambiguous: " + expected.Model);
        return matches[0];
    }

    public static void Writable(PhysicalDiskSnapshot disk)
    {
        DeploymentGuard.FixedInternalDisk(disk.Identity, "Disk");
        if (disk.IsOffline || disk.IsReadOnly || !disk.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase))
            throw new DeploymentSafetyException("disk.layout", "Disk must be online, writable, and GPT.");
    }

    public static PartitionInfo SeparateVolume(PhysicalDiskSnapshot disk, long bytes, Guid? volumeId = null)
    {
        Writable(disk);
        return disk.Partitions.Where(p => (!volumeId.HasValue || p.GptPartitionId == volumeId) &&
            p.GptPartitionId != Guid.Empty && !p.IsReadOnly && p.DriveLetter is { Length: 1 } &&
            p.FileSystem == "NTFS" && p.Role is PartitionRole.Data or PartitionRole.Windows && p.FreeBytes >= bytes)
            .OrderByDescending(p => p.FreeBytes).FirstOrDefault()
            ?? throw new DeploymentSafetyException("staging.capacity", "Staging disk does not have an accessible NTFS volume with enough free space.");
    }

    public static async Task<IReadOnlyList<DiskSelectionCheck>> CheckAsync(IPhysicalDiskService disks, ReinstallPlan plan, long bytes, CancellationToken token)
    {
        var checks = new List<DiskSelectionCheck>();
        PhysicalDiskSnapshot? target = null;
        PhysicalDiskSnapshot? storage = null;
        async Task Check(string code, Func<Task> action)
        {
            try { await action().ConfigureAwait(false); checks.Add(new(code, true, "Проверка пройдена.")); }
            catch (Exception e) when (e is not OperationCanceledException) { checks.Add(new(code, false, e.Message)); }
        }
        await Check("target-disk", async () => { target = await ResolveAsync(disks, plan.TargetDisk, token).ConfigureAwait(false); });
        await Check("disk-layout", () => { if (target is null) throw new InvalidDataException("Target disk identity is unavailable."); Writable(target); return Task.CompletedTask; });
        bool separate = plan.StagingDisk is not null && !SameDisk(plan.TargetDisk, plan.StagingDisk);
        await Check("staging-disk", async () =>
        {
            storage = separate ? await ResolveAsync(disks, plan.StagingDisk!, token).ConfigureAwait(false) : target;
            if (storage is null) throw new InvalidDataException("Staging disk is unavailable.");
            Writable(storage);
            if (separate && plan.AdditionalDisksToErase.Any(d => SameDisk(d, storage.Identity)))
                throw new InvalidDataException("Staging disk is also selected for erase.");
        });
        await Check("staging-capacity", () =>
        {
            if (storage is null) throw new InvalidDataException("Staging storage is unavailable.");
            if (separate) _ = SeparateVolume(storage, bytes, plan.StagingVolumeId);
            else if (UnallocatedStaging.Select(storage, bytes) is null) _ = LocalStagingPartitionService.SelectShrinkSource(storage, bytes);
            return Task.CompletedTask;
        });
        await Check("erase-disks", async () =>
        {
            if (plan.AdditionalDisksToErase.Count > 1) throw new InvalidDataException("At most one additional erase disk is supported.");
            foreach (var expected in plan.AdditionalDisksToErase)
            {
                if (SameDisk(expected, plan.TargetDisk)) throw new InvalidDataException("Target disk is also selected as additional erase disk.");
                if (separate && SameDisk(expected, plan.StagingDisk!)) throw new InvalidDataException("Staging disk is also selected for erase.");
                var actual = await ResolveAsync(disks, expected, token).ConfigureAwait(false);
                Writable(actual);
                if (actual.Partitions.Any(p => p.Role == PartitionRole.Deployment)) throw new InvalidDataException("Additional erase disk contains protected staging.");
            }
        });
        return checks;
    }

    public static PartitionInfo ValidatePartition(StagingPartitionIdentity expected, PhysicalDiskSnapshot actual)
    {
        if (!SameDisk(expected.Disk, actual.Identity)) throw new DeploymentSafetyException("staging.identity", "Staging disk identity changed.");
        Writable(actual);
        var matches = actual.Partitions.Where(p => p.GptPartitionId == expected.GptPartitionId).ToArray();
        if (matches.Length != 1) throw new DeploymentSafetyException("staging.missing", "Staging partition missing or ambiguous.");
        var p = matches[0];
        if (p.OffsetBytes != expected.OffsetBytes || p.SizeBytes != expected.SizeBytes || p.IsReadOnly || p.FileSystem != "NTFS")
            throw new DeploymentSafetyException("staging.changed", "Staging partition geometry or filesystem changed.");
        if (expected.Mode == StagingMode.SameDiskPartition && p.Role != PartitionRole.Deployment)
            throw new DeploymentSafetyException("staging.role", "Protected staging partition role changed.");
        return p;
    }

    public static string Root(StagingPartitionIdentity expected, PartitionInfo actual)
    {
        if (actual.DriveLetter is not { Length: 1 }) throw new DeploymentSafetyException("staging.mount", "Staging volume has no accessible drive letter.");
        string root = actual.DriveLetter + ":\\";
        return expected.Mode == StagingMode.SeparateDiskFolder
            ? PathValidator.ResolveUnderRoot(root, expected.FolderRelativePath) : root;
    }
}
