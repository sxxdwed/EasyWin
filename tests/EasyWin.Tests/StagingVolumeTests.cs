using EasyWin.Core.Models;
using EasyWin.Core.Localization;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Environment;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;
using EasyWin.Deployment.Staging;

namespace EasyWin.Tests;

public sealed class StagingVolumeTests
{
    private static readonly PartitionInfo Volume = new() { GptPartitionId = Guid.NewGuid(), OffsetBytes = 1048576, SizeBytes = 100L << 30,
        FreeBytes = 50L << 30, DriveLetter = "D", FileSystem = "NTFS", Role = PartitionRole.Data };
    private static ReinstallPlan Plan => new() { StagingDisk = TestData.Disk(), StagingVolumeId = Volume.GptPartitionId, ExpectedStagingVolume = Volume };
    private sealed class Disks(PartitionInfo volume) : IPhysicalDiskService
    {
        public Task<IReadOnlyList<PhysicalDiskSnapshot>> GetDisksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PhysicalDiskSnapshot>>([new(TestData.Disk(), false, false, "GPT", [volume])]);
        public Task<PhysicalDiskSnapshot> GetDiskAsync(int diskNumber, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class Encryption(bool encrypted, bool protectedVolume) : IBitLockerService
    {
        public Task<BitLockerVolumeStatus> GetStatusAsync(string volumeRoot, CancellationToken cancellationToken = default)
        {
            Assert.Equal("D:\\", volumeRoot);
            return Task.FromResult(new BitLockerVolumeStatus(volumeRoot, "numeric", "numeric", encrypted, protectedVolume));
        }
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SeparateStaging_EncryptedVolume_FailsInPreflight(bool encrypted, bool protection)
    {
        var exception = await Assert.ThrowsAsync<DeploymentSafetyException>(() => new StagingVolumeValidator(new Disks(Volume), new Encryption(encrypted, protection)).ValidateAsync(Plan, 1));
        Assert.Equal("staging.bitlocker", exception.Code);
        Assert.Contains("Полностью расшифруйте", exception.Message);
    }
    [Fact]
    public async Task SeparateStaging_UnencryptedVolume_Passes() => Assert.Equal(Volume, await new StagingVolumeValidator(new Disks(Volume), new Encryption(false, false)).ValidateAsync(Plan, 1));
    [Theory]
    [InlineData("guid")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("filesystem")]
    [InlineData("readonly")]
    public async Task SeparateStaging_VolumeChangedAfterPreflight_Fails(string change)
    {
        var changed = change switch { "guid" => Volume with { GptPartitionId = Guid.NewGuid() }, "offset" => Volume with { OffsetBytes = 0 },
            "size" => Volume with { SizeBytes = Volume.SizeBytes - 1 }, "filesystem" => Volume with { FileSystem = "FAT32" }, _ => Volume with { IsReadOnly = true } };
        await Assert.ThrowsAsync<DeploymentSafetyException>(() => new StagingVolumeValidator(new Disks(changed), new Encryption(false, false)).ValidateAsync(Plan, 1));
    }
    [Fact]
    public async Task SeparateStaging_BitLockerEnabledAfterPreflight_Fails()
    {
        await new StagingVolumeValidator(new Disks(Volume), new Encryption(false, false)).ValidateAsync(Plan, 1);
        await Assert.ThrowsAsync<DeploymentSafetyException>(() => new StagingVolumeValidator(new Disks(Volume), new Encryption(true, false)).ValidateAsync(Plan, 0));
    }
    [Fact]
    public async Task SeparateStaging_NoExactGuid_Fails() => await Assert.ThrowsAsync<DeploymentSafetyException>(() =>
        new StagingVolumeValidator(new Disks(Volume), new Encryption(false, false)).ValidateAsync(Plan with { StagingVolumeId = null }, 1));
    [Fact]
    public void BitLocker_NumericStatus_IsLocaleIndependent()
    {
        Assert.False(BitLockerService.ParseStatus("D:\\", "{\"ConversionStatus\":0,\"ProtectionStatus\":0,\"EncryptionPercentage\":0}").IsEncrypted);
        Assert.True(BitLockerService.ParseStatus("D:\\", "{\"ConversionStatus\":3,\"ProtectionStatus\":0,\"EncryptionPercentage\":0}").IsEncrypted);
        Assert.Throws<DeploymentSafetyException>(() => BitLockerService.ParseStatus("D:\\", "{\"ConversionStatus\":0,\"ProtectionStatus\":2,\"EncryptionPercentage\":0}"));
    }
    [Fact]
    public void Localization_FallbackToEnglish()
    {
        Assert.Equal(DeploymentStrings.Get("StorageEncrypted", "en-US"), DeploymentStrings.Get("StorageEncrypted", "fr-FR"));
        Assert.NotEqual("UnknownKey", DeploymentStrings.Get("UnknownKey", "ru-RU"));
    }
}
