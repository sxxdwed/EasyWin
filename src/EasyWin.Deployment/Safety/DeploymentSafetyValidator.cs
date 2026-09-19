using EasyWin.Core.Models;
using EasyWin.Core.Security;
using EasyWin.Core.Validation;
using EasyWin.Deployment.Models;

namespace EasyWin.Deployment.Safety;

public sealed class DeploymentSafetyValidator(DiskIdentityValidator identities)
{
    public void ValidateBeforeDestructive(DeploymentManifest manifest, PhysicalDiskSnapshot currentDisk, string imagePath, bool requireBootFiles = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(currentDisk);
        DiskIdentityValidationResult disk = identities.Validate(manifest.TargetDisk, currentDisk.Identity);
        if (!disk.IsValid)
        {
            throw new DeploymentSafetyException("safety.disk.changed", string.Join("; ", disk.Errors));
        }

        PartitionInfo stage = currentDisk.Partitions.SingleOrDefault(partition => partition.GptPartitionId == manifest.StagingPartition.GptPartitionId)
            ?? throw new DeploymentSafetyException("safety.staging.missing", "The protected deployment partition is missing.");
        if (stage.PartitionNumber != manifest.StagingPartition.PartitionNumber ||
            stage.OffsetBytes != manifest.StagingPartition.OffsetBytes ||
            stage.SizeBytes != manifest.StagingPartition.SizeBytes ||
            stage.Role != PartitionRole.Deployment)
        {
            throw new DeploymentSafetyException("safety.staging.changed", "The protected deployment partition identity changed.");
        }

        if (currentDisk.Partitions.Any(partition => partition.OffsetBytes > stage.OffsetBytes && partition.Role != PartitionRole.Recovery))
        {
            throw new DeploymentSafetyException("safety.staging.not_last", "A non-Recovery partition follows the protected deployment partition.");
        }

        string actualStageRoot = manifest.ExecutionMode.IsDryRun()
            ? manifest.StagingPartition.RootPath
            : !string.IsNullOrWhiteSpace(stage.DriveLetter)
            ? $"{stage.DriveLetter}:\\"
            : Path.GetPathRoot(imagePath) ?? manifest.StagingPartition.RootPath;
        string expectedImage = PathValidator.ResolveUnderRoot(actualStageRoot, manifest.Image.RelativePath, mustExist: true);
        if (!string.Equals(Path.GetFullPath(imagePath), expectedImage, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeploymentSafetyException("safety.image.on_erased_partition", "The selected image is not stored on the protected deployment partition.");
        }

        if (requireBootFiles)
        {
            try
            {
                _ = PathValidator.ResolveUnderRoot(actualStageRoot, manifest.Boot.WinPeWimRelativePath, mustExist: true);
                _ = PathValidator.ResolveUnderRoot(actualStageRoot, manifest.Boot.WinPeSdiRelativePath, mustExist: true);
            }
            catch (FileNotFoundException exception)
            {
                throw new DeploymentSafetyException("safety.boot.incomplete", $"The WinPE boot environment is incomplete: {exception.Message}");
            }
            if (!manifest.Boot.OneTimeBootConfigured || manifest.Boot.BootEntryId is null)
            {
                throw new DeploymentSafetyException("safety.boot.incomplete", "The one-time WinPE boot environment is incomplete.");
            }
        }
    }
}
