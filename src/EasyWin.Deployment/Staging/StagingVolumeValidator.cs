using EasyWin.Core.Localization;
using EasyWin.Core.Models;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Environment;
using EasyWin.Deployment.Safety;

namespace EasyWin.Deployment.Staging;

public sealed class StagingVolumeValidator(IPhysicalDiskService disks, IBitLockerService bitLocker)
{
    public async Task<PartitionInfo> ValidateAsync(ReinstallPlan plan, long requiredBytes, CancellationToken token = default)
    {
        if (plan.StagingDisk is null || !plan.StagingVolumeId.HasValue || plan.ExpectedStagingVolume is null)
            throw new DeploymentSafetyException("staging.volume.required", DeploymentStrings.Get("StorageSelect", plan.Language));
        var storage = await StagingSelection.ResolveAsync(disks, plan.StagingDisk, token).ConfigureAwait(false);
        var volume = StagingSelection.SeparateVolume(storage, requiredBytes, plan.StagingVolumeId);
        var expected = plan.ExpectedStagingVolume;
        if (expected.GptPartitionId != volume.GptPartitionId || expected.OffsetBytes != volume.OffsetBytes ||
            expected.SizeBytes != volume.SizeBytes || !string.Equals(expected.FileSystem, volume.FileSystem, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expected.DriveLetter, volume.DriveLetter, StringComparison.OrdinalIgnoreCase))
            throw new DeploymentSafetyException("staging.volume.changed", DeploymentStrings.Get("StorageChanged", plan.Language));
        var encryption = await bitLocker.GetStatusAsync(volume.DriveLetter + ":\\", token).ConfigureAwait(false);
        if (encryption.IsEncrypted || encryption.IsProtectionEnabled)
            throw new DeploymentSafetyException("staging.bitlocker", DeploymentStrings.Get("StorageEncrypted", plan.Language));
        return volume;
    }
}
