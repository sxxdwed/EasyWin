using System.Globalization;
using System.Text;
using EasyWin.Core.Models;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Disks;

/// <summary>
/// Produces DiskPart input from validated numeric values only.  The generated destructive
/// script deliberately has no CLEAN command: the deployment partition is retained until
/// the new Windows installation has booted successfully.
/// </summary>
public sealed class DiskPartScriptBuilder
{
    public string PrepareTargetWithSeparateStaging(PhysicalDiskSnapshot target, PhysicalDiskSnapshot staging)
    {
        Staging.StagingSelection.Writable(target);
        Staging.StagingSelection.Writable(staging);
        if (target.Identity.DiskNumber == staging.Identity.DiskNumber || Staging.StagingSelection.SameDisk(target.Identity, staging.Identity))
            throw new DeploymentSafetyException("layout.staging_is_target", "Separate staging must not be erased.");
        if (target.Partitions.Any(p => p.Role == PartitionRole.Deployment))
            throw new DeploymentSafetyException("layout.old_staging", "Target contains a protected deployment partition.");
        return JoinLines($"select disk {DeploymentGuard.DiskNumber(target.Identity.DiskNumber)}", "clean", "convert gpt",
            "create partition efi size=260", "format fs=fat32 quick label=\"SYSTEM\"", "assign letter=S",
            "create partition msr size=16", "create partition primary", "format fs=ntfs quick label=\"Windows\"", "assign letter=W",
            "shrink desired=1024 minimum=1024", "create partition primary size=1024", "format fs=ntfs quick label=\"Windows RE tools\"",
            "assign letter=R", "set id=de94bba4-06d1-4d40-a16a-bfd50179d6ac", "gpt attributes=0x8000000000000001");
    }

    private const long MiB = 1024L * 1024L;

    public string CreateStagingPartition(StagingPartitionRequest request)
    {
        var disk = DeploymentGuard.DiskNumber(request.DiskNumber);
        var source = DeploymentGuard.DriveLetter(request.SourceDriveLetter);
        var staging = DeploymentGuard.DriveLetter(request.StagingDriveLetter);
        var label = DeploymentGuard.Label(request.Label);
        var requestedBytes = DeploymentGuard.BytesAtLeast(
            request.RequestedSizeBytes,
            4L * 1024 * MiB,
            "Staging partition size");
        var sizeMiB = checked((requestedBytes + MiB - 1) / MiB);
        if (request.UnallocatedOffsetBytes.HasValue)
        {
            long offset = request.UnallocatedOffsetBytes.Value;
            if (offset < MiB || offset % MiB != 0) throw new DeploymentSafetyException("staging.offset", "Invalid staging extent alignment.");
            return JoinLines($"select disk {disk}", $"create partition primary size={sizeMiB} offset={offset / 1024}",
                $"format fs=ntfs quick label=\"{label}\"", $"assign letter={staging}");
        }

        if (source == staging)
        {
            throw new DeploymentSafetyException("staging.same_drive", "Source and staging drive letters must differ.");
        }

        return JoinLines(
            $"select disk {disk}",
            "online disk noerr",
            "attributes disk clear readonly noerr",
            $"select volume {source}",
            $"shrink desired={sizeMiB.ToString(CultureInfo.InvariantCulture)} minimum={sizeMiB.ToString(CultureInfo.InvariantCulture)}",
            $"select disk {disk}",
            $"create partition primary size={sizeMiB.ToString(CultureInfo.InvariantCulture)}",
            $"format fs=ntfs quick label=\"{label}\"",
            $"assign letter={staging}",
            "detail partition");
    }

    public string PrepareTargetPreservingDeployment(
        DiskPreparationRequest request,
        IReadOnlyList<PartitionInfo> currentPartitions)
    {
        ArgumentNullException.ThrowIfNull(currentPartitions);
        var disk = DeploymentGuard.DiskNumber(request.DiskNumber);
        var protectedGuid = DeploymentGuard.RequiredGuid(request.DeploymentPartitionGuid, "Deployment partition GUID");
        var protectedNumber = DeploymentGuard.PartitionNumber(request.DeploymentPartitionNumber);
        var efi = DeploymentGuard.DriveLetter(request.EfiDriveLetter);
        var windows = DeploymentGuard.DriveLetter(request.WindowsDriveLetter);

        if (efi == windows)
        {
            throw new DeploymentSafetyException("layout.duplicate_letter", "EFI and Windows drive letters must differ.");
        }

        if (request.EfiSizeMiB is < 100 or > 1024 || request.MsrSizeMiB is < 16 or > 128)
        {
            throw new DeploymentSafetyException("layout.invalid_size", "EFI or MSR size is outside the supported range.");
        }

        var protectedPartition = currentPartitions.SingleOrDefault(partition =>
            partition.GptPartitionId == protectedGuid &&
            partition.PartitionNumber == protectedNumber);
        if (protectedPartition is null)
        {
            throw new DeploymentSafetyException("layout.staging_missing", "The protected deployment partition identity does not match.");
        }

        if (protectedPartition.OffsetBytes != request.DeploymentPartitionOffsetBytes ||
            protectedPartition.SizeBytes != request.DeploymentPartitionSizeBytes)
        {
            throw new DeploymentSafetyException("layout.staging_changed", "The protected deployment partition geometry changed.");
        }

        var afterProtected = currentPartitions.Where(partition => partition.OffsetBytes > protectedPartition.OffsetBytes).ToArray();
        if (afterProtected.Any(partition => partition.Role != PartitionRole.Recovery))
        {
            throw new DeploymentSafetyException("layout.staging_not_last", "Only a disposable Recovery partition may follow the protected deployment partition.");
        }

        var partitionsToDelete = currentPartitions
            .Where(partition => partition.GptPartitionId != protectedGuid)
            .OrderByDescending(partition => partition.PartitionNumber)
            .ToArray();

        if (partitionsToDelete.Length == 0)
        {
            throw new DeploymentSafetyException("layout.no_target_partitions", "No target partitions are available to replace.");
        }

        var builder = new StringBuilder();
        AppendLine(builder, $"select disk {disk}");
        AppendLine(builder, "online disk noerr");
        AppendLine(builder, "attributes disk clear readonly noerr");
        foreach (var partition in partitionsToDelete)
        {
            AppendLine(builder, $"select partition {DeploymentGuard.PartitionNumber(partition.PartitionNumber)}");
            AppendLine(builder, "delete partition override");
        }

        AppendLine(builder, $"select disk {disk}");
        AppendLine(builder, $"create partition efi size={request.EfiSizeMiB.ToString(CultureInfo.InvariantCulture)}");
        AppendLine(builder, "format fs=fat32 quick label=\"SYSTEM\"");
        AppendLine(builder, $"assign letter={efi}");
        AppendLine(builder, $"create partition msr size={request.MsrSizeMiB.ToString(CultureInfo.InvariantCulture)}");
        AppendLine(builder, "create partition primary");
        AppendLine(builder, "format fs=ntfs quick label=\"Windows\"");
        AppendLine(builder, $"assign letter={windows}");
        AppendLine(builder, "list partition");
        return builder.ToString();
    }

    public string RemoveStagingAndCreateRecovery(FinalizeDiskRequest request)
    {
        var disk = DeploymentGuard.DiskNumber(request.DiskNumber);
        _ = DeploymentGuard.RequiredGuid(request.DeploymentPartitionGuid, "Deployment partition GUID");
        var partition = DeploymentGuard.PartitionNumber(request.DeploymentPartitionNumber);
        var windows = DeploymentGuard.DriveLetter(request.WindowsDriveLetter);
        var recovery = DeploymentGuard.DriveLetter(request.RecoveryDriveLetter);
        if (windows == recovery)
        {
            throw new DeploymentSafetyException("cleanup.duplicate_letter", "Windows and Recovery drive letters must differ.");
        }

        if (request.RecoverySizeMiB is < 750 or > 4096)
        {
            throw new DeploymentSafetyException("cleanup.recovery_size", "Recovery partition size must be between 750 and 4096 MiB.");
        }

        return JoinLines(
            $"select disk {disk}",
            $"select partition {partition}",
            "delete partition override",
            $"select volume {windows}",
            "extend",
            $"shrink desired={request.RecoverySizeMiB.ToString(CultureInfo.InvariantCulture)} minimum={request.RecoverySizeMiB.ToString(CultureInfo.InvariantCulture)}",
            $"select disk {disk}",
            $"create partition primary size={request.RecoverySizeMiB.ToString(CultureInfo.InvariantCulture)}",
            "format fs=ntfs quick label=\"Windows RE tools\"",
            $"assign letter={recovery}",
            "set id=de94bba4-06d1-4d40-a16a-bfd50179d6ac",
            "gpt attributes=0x8000000000000001",
            "detail partition");
    }

    public string EraseAdditionalDiskAndCreateDataVolume(
        AdditionalDiskEraseRequest request,
        IReadOnlyList<PartitionInfo> currentPartitions)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentPartitions);
        var disk = DeploymentGuard.DiskNumber(request.DiskNumber);
        var label = DeploymentGuard.Label(request.Label);
        if (currentPartitions.Any(static partition => partition.Role == PartitionRole.Deployment))
        {
            throw new DeploymentSafetyException("erase.staging_forbidden", "A disk containing an EasyWin deployment partition cannot be erased as an additional disk.");
        }

        var builder = new StringBuilder();
        AppendLine(builder, $"select disk {disk}");
        AppendLine(builder, "online disk noerr");
        AppendLine(builder, "attributes disk clear readonly noerr");
        foreach (PartitionInfo partition in currentPartitions.OrderByDescending(static partition => partition.PartitionNumber))
        {
            AppendLine(builder, $"select partition {DeploymentGuard.PartitionNumber(partition.PartitionNumber)}");
            AppendLine(builder, "delete partition override");
        }

        AppendLine(builder, $"select disk {disk}");
        AppendLine(builder, "create partition primary");
        AppendLine(builder, $"format fs=ntfs quick label=\"{label}\"");
        AppendLine(builder, "assign");
        AppendLine(builder, "detail partition");
        return builder.ToString();
    }

    private static string JoinLines(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private static void AppendLine(StringBuilder builder, string value) => builder.Append(value).Append("\r\n");
}
