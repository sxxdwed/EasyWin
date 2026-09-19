namespace EasyWin.Core.Models;

public sealed record DiskIdentity
{
    public string DeviceId { get; init; } = string.Empty;

    public int DiskNumber { get; init; } = -1;

    public string SerialNumber { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public DiskBusType BusType { get; init; } = DiskBusType.Unknown;

    public string? UniqueId { get; init; }

    public bool IsSystemDisk { get; init; }

    public bool IsBootDisk { get; init; }
}

public sealed record PartitionInfo
{
    public int DiskNumber { get; init; } = -1;

    public int PartitionNumber { get; init; } = -1;

    public Guid GptPartitionId { get; init; }

    public Guid GptTypeId { get; init; }

    public long OffsetBytes { get; init; }

    public long SizeBytes { get; init; }

    public long ShrinkAvailableBytes { get; init; }

    public long FreeBytes { get; init; }

    public string? DriveLetter { get; init; }

    public string? VolumePath { get; init; }

    public string? Label { get; init; }

    public string? FileSystem { get; init; }

    public PartitionRole Role { get; init; }

    public bool IsReadOnly { get; init; }
}

public sealed record StagingPartitionIdentity
{
    public StagingMode Mode { get; init; } = StagingMode.SameDiskPartition;

    public string FolderRelativePath { get; init; } = string.Empty;

    public DiskIdentity Disk { get; init; } = new();

    public int PartitionNumber { get; init; } = -1;

    public Guid GptPartitionId { get; init; }

    public long OffsetBytes { get; init; }

    public long SizeBytes { get; init; }

    public string Label { get; init; } = "EasyWin";

    public string RootPath { get; init; } = string.Empty;

    public string FileSystem { get; init; } = "NTFS";
}

public enum StagingMode { SameDiskPartition, SeparateDiskFolder }
