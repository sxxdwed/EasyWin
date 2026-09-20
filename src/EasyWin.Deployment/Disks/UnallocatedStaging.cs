using EasyWin.Core.Models;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Disks;

public static class UnallocatedStaging
{
    private const long MiB = 1048576;
    public static IReadOnlyList<DiskExtent> FindExtents(long diskBytes, IReadOnlyList<PartitionInfo> partitions)
    {
        var result = new List<DiskExtent>(); long cursor = MiB;
        foreach (var p in partitions.OrderBy(p => p.OffsetBytes))
        {
            if (p.SizeBytes <= 0 || p.OffsetBytes < cursor || checked(p.OffsetBytes + p.SizeBytes) > diskBytes)
                return []; // Uncertain or overlapping mapping cannot authorize a new partition.
            if (p.OffsetBytes > cursor) result.Add(new(cursor, p.OffsetBytes - cursor));
            cursor = checked(p.OffsetBytes + p.SizeBytes);
        }
        if (cursor < diskBytes - MiB) result.Add(new(cursor, diskBytes - MiB - cursor));
        return result;
    }

    public static DiskExtent? Select(PhysicalDiskSnapshot disk, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        if (disk.Partitions.Any(p => p.Role == PartitionRole.Deployment))
            throw new DeploymentSafetyException("staging.already_exists", "An existing deployment partition requires explicit recovery.");
        long size = checked((bytes + MiB - 1) / MiB * MiB);
        var actual = FindExtents(disk.Identity.SizeBytes, disk.Partitions);
        foreach (var extent in disk.UnallocatedExtents.OrderByDescending(e => e.OffsetBytes))
        {
            // Place staging at the end of a verified extent, leaving room for Windows before it.
            long offset = (checked(extent.OffsetBytes + extent.SizeBytes) - size) / MiB * MiB;
            if (offset < extent.OffsetBytes || offset < (64L << 30) ||
                !actual.Any(e => offset >= e.OffsetBytes && offset + size <= e.OffsetBytes + e.SizeBytes) ||
                disk.Partitions.Any(p => p.OffsetBytes > offset && p.Role != PartitionRole.Recovery)) continue;
            return new(offset, size);
        }
        return null;
    }
}
