using EasyWin.Core.Models;
using EasyWin.Core.Validation;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Disks;

public interface ILocalStagingPartitionService
{
    Task<StagingPartitionIdentity> CreateAsync(
        DiskIdentity expectedDisk,
        StagingPartitionRequest request,
        string workingDirectory,
        ExecutionMode mode,
        CancellationToken cancellationToken = default);

    Task ValidateAsync(StagingPartitionIdentity expected, CancellationToken cancellationToken = default);
}

public sealed class LocalStagingPartitionService(
    IPhysicalDiskService disks,
    IDiskPartService diskPart,
    DiskPartScriptBuilder scriptBuilder,
    DiskIdentityValidator identityValidator) : ILocalStagingPartitionService
{
    public async Task<StagingPartitionIdentity> CreateAsync(
        DiskIdentity expectedDisk,
        StagingPartitionRequest request,
        string workingDirectory,
        ExecutionMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedDisk);
        ArgumentNullException.ThrowIfNull(request);
        var before = await Staging.StagingSelection.ResolveAsync(disks, expectedDisk, cancellationToken).ConfigureAwait(false);
        request = request with { DiskNumber = before.Identity.DiskNumber };
        EnsureDiskIdentity(expectedDisk, before.Identity);
        Staging.StagingSelection.Writable(before);
        if (request.UnallocatedOffsetBytes.HasValue)
        {
            var extent = UnallocatedStaging.Select(before, request.RequestedSizeBytes);
            if (extent is null || extent.OffsetBytes != request.UnallocatedOffsetBytes.Value)
                throw new DeploymentSafetyException("staging.extent.changed", "Unallocated extent changed before staging.");
            if (DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == char.ToUpperInvariant(request.StagingDriveLetter)))
                throw new DeploymentSafetyException("staging.drive_letter_in_use", "Staging drive letter is occupied.");
        }
        else EnsureSafeForShrink(before, request);

        await diskPart.ExecuteAsync(
            scriptBuilder.CreateStagingPartition(request),
            workingDirectory,
            mode,
            cancellationToken).ConfigureAwait(false);

        if (mode.IsDryRun())
        {
            return new StagingPartitionIdentity
            {
                Disk = expectedDisk,
                PartitionNumber = before.Partitions.Count + 1,
                GptPartitionId = request.ExpectedExistingPartitionGuid ?? Guid.Parse("11111111-1111-4111-8111-111111111111"),
                OffsetBytes = expectedDisk.SizeBytes - request.RequestedSizeBytes,
                SizeBytes = request.RequestedSizeBytes,
                Label = request.Label,
                RootPath = $"{char.ToUpperInvariant(request.StagingDriveLetter)}:\\",
                FileSystem = "NTFS"
            };
        }

        var after = await Staging.StagingSelection.ResolveAsync(disks, expectedDisk, cancellationToken).ConfigureAwait(false);
        EnsureDiskIdentity(expectedDisk, after.Identity);
        var letter = char.ToUpperInvariant(request.StagingDriveLetter).ToString();
        var partition = after.Partitions.SingleOrDefault(candidate =>
            string.Equals(candidate.DriveLetter, letter, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Label, request.Label, StringComparison.OrdinalIgnoreCase));
        if (partition is null || partition.GptPartitionId == Guid.Empty)
        {
            throw new DeploymentSafetyException("staging.create_unverified", "The new staging partition could not be identified by drive, label, and GPT GUID.");
        }
        if (request.UnallocatedOffsetBytes.HasValue && partition.OffsetBytes != request.UnallocatedOffsetBytes.Value)
            throw new DeploymentSafetyException("staging.extent.changed", "Created partition does not match the approved extent.");

        var following = after.Partitions.Where(candidate => candidate.OffsetBytes > partition.OffsetBytes).ToArray();
        if (following.Any(candidate => candidate.Role != PartitionRole.Recovery) || partition.SizeBytes < request.RequestedSizeBytes - 16 * 1024 * 1024)
        {
            throw new DeploymentSafetyException("staging.geometry_unsafe", "A non-Recovery partition follows staging or staging has an unexpected size.");
        }

        var identity = new StagingPartitionIdentity
        {
            Disk = after.Identity,
            PartitionNumber = partition.PartitionNumber,
            GptPartitionId = partition.GptPartitionId,
            OffsetBytes = partition.OffsetBytes,
            SizeBytes = partition.SizeBytes,
            Label = request.Label,
            RootPath = $"{letter}:\\",
            FileSystem = partition.FileSystem ?? string.Empty
        };
        await ValidateAsync(identity, cancellationToken).ConfigureAwait(false);
        return identity;
    }

    public async Task ValidateAsync(StagingPartitionIdentity expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var snapshot = await Staging.StagingSelection.ResolveAsync(disks, expected.Disk, cancellationToken).ConfigureAwait(false);
        EnsureDiskIdentity(expected.Disk, snapshot.Identity);
        var actual = snapshot.Partitions.SingleOrDefault(partition => partition.GptPartitionId == expected.GptPartitionId);
        if (actual is null ||
            actual.PartitionNumber != expected.PartitionNumber ||
            actual.OffsetBytes != expected.OffsetBytes ||
            actual.SizeBytes != expected.SizeBytes ||
            !string.Equals(actual.Label, expected.Label, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.FileSystem, expected.FileSystem, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeploymentSafetyException("staging.identity_changed", "The staging partition identity or geometry changed.");
        }

        if (snapshot.Partitions.Any(partition => partition.OffsetBytes > actual.OffsetBytes && partition.Role != PartitionRole.Recovery))
        {
            throw new DeploymentSafetyException("staging.not_last", "A non-Recovery partition follows the staging partition.");
        }
    }

    private void EnsureDiskIdentity(DiskIdentity expected, DiskIdentity actual)
    {
        var result = identityValidator.Validate(expected, actual);
        if (!result.IsValid)
        {
            throw new DeploymentSafetyException("disk.identity_changed", string.Join("; ", result.Errors));
        }
    }

    private static void EnsureSafeForShrink(PhysicalDiskSnapshot disk, StagingPartitionRequest request)
    {
        if (disk.IsOffline || disk.IsReadOnly)
        {
            throw new DeploymentSafetyException("staging.disk_unavailable", "The target disk is offline or read-only.");
        }

        if (!disk.PartitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase))
        {
            throw new DeploymentSafetyException("staging.disk_not_gpt", "Local one-disk staging requires a GPT disk.");
        }

        var sourceLetter = char.ToUpperInvariant(request.SourceDriveLetter).ToString();
        var source = disk.Partitions.SingleOrDefault(partition => string.Equals(partition.DriveLetter, sourceLetter, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            throw new DeploymentSafetyException("staging.source_missing", "The source Windows partition was not found on the target disk.");
        }

        if (DriveInfo.GetDrives().Any(drive =>
                drive.Name.Length >= 2 &&
                char.ToUpperInvariant(drive.Name[0]) == char.ToUpperInvariant(request.StagingDriveLetter)))
        {
            throw new DeploymentSafetyException("staging.drive_letter_in_use", "The selected staging drive letter is already in use.");
        }

        if (source.ShrinkAvailableBytes < request.RequestedSizeBytes)
        {
            throw new DeploymentSafetyException(
                "staging.shrink_insufficient",
                $"The selected target volume can safely shrink by only {source.ShrinkAvailableBytes} bytes; {request.RequestedSizeBytes} bytes are required.");
        }

        var following = disk.Partitions.Where(partition => partition.OffsetBytes > source.OffsetBytes).ToArray();
        if (following.Any(partition => partition.Role != PartitionRole.Recovery))
        {
            throw new DeploymentSafetyException(
                "staging.source_not_last",
                "Only a Recovery partition may follow Windows for safe one-disk staging.");
        }

        if (disk.Partitions.Any(partition => partition.Role == PartitionRole.Deployment))
        {
            throw new DeploymentSafetyException("staging.already_exists", "A deployment partition already exists; resume or clean it explicitly.");
        }
    }

    public static PartitionInfo SelectShrinkSource(PhysicalDiskSnapshot disk, long requiredBytes)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requiredBytes);

        PartitionInfo? source = disk.Partitions
            .Where(partition =>
                !partition.IsReadOnly &&
                partition.DriveLetter is { Length: 1 } &&
                string.Equals(partition.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) &&
                partition.Role is PartitionRole.Windows or PartitionRole.Data &&
                partition.ShrinkAvailableBytes >= requiredBytes &&
                disk.Partitions.Where(candidate => candidate.OffsetBytes > partition.OffsetBytes)
                    .All(candidate => candidate.Role == PartitionRole.Recovery))
            .OrderByDescending(static partition => partition.ShrinkAvailableBytes)
            .FirstOrDefault();

        return source ?? throw new DeploymentSafetyException(
            "staging.no_safe_source",
            "The Windows target disk has no NTFS volume that can be safely shrunk for the protected EasyWin deployment partition.");
    }
}
