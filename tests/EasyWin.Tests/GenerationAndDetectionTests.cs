using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Deployment.Boot;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Images;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.PostInstall;
using EasyWin.Core.Security;

namespace EasyWin.Tests;

public sealed class GenerationAndDetectionTests
{
    [Fact]
    public async Task DiskDetection_ParsesStableIdentityAndPartitions()
    {
        const string json = """
            [{"Number":0,"DeviceId":"\\\\.\\PhysicalDrive0","SerialNumber":"ABC","Model":"NVMe","SizeBytes":1000,"BusType":"NVMe","UniqueId":"U1","IsBoot":true,"IsSystem":true,"IsReadOnly":false,"IsOffline":false,"PartitionStyle":"GPT","Partitions":[{"PartitionNumber":1,"GptPartitionId":"11111111-1111-4111-8111-111111111111","GptTypeId":"de94bba4-06d1-4d40-a16a-bfd50179d6ac","OffsetBytes":100,"SizeBytes":200,"DriveLetter":"R","Label":"Recovery","FileSystem":"NTFS","IsBoot":false,"IsSystem":false,"IsHidden":true,"IsReadOnly":false}]}]
            """;
        var runner = new RecordingProcessRunner(command => new ProcessResult(command, 0, json, string.Empty, TimeSpan.Zero, true));
        PhysicalDiskSnapshot disk = Assert.Single(await new PhysicalDiskService(runner).GetDisksAsync());
        Assert.Equal("ABC", disk.Identity.SerialNumber);
        Assert.Equal(DiskBusType.Nvme, disk.Identity.BusType);
        Assert.Equal(PartitionRole.Recovery, Assert.Single(disk.Partitions).Role);
    }

    [Fact]
    public async Task HardwareDetection_ParsesIndentedPnPUtilDeviceIds()
    {
        const string output = """
            Instance ID:                PCI\VEN_10DE&DEV_2D05
            Device IDs:
                PCI\VEN_10DE&DEV_2D05&SUBSYS_53711462&REV_A1
                PCI\VEN_10DE&DEV_2D05&SUBSYS_53711462
            """;
        var runner = new RecordingProcessRunner(command => new ProcessResult(command, 0, output, string.Empty, TimeSpan.Zero, true));

        IReadOnlySet<string> ids = await new DriverInstaller(runner, new Sha256HashService())
            .DetectHardwareIdsAsync(ExecutionMode.Live);

        Assert.Contains("PCI\\VEN_10DE&DEV_2D05&SUBSYS_53711462&REV_A1", ids);
        Assert.Contains("PCI\\VEN_10DE&DEV_2D05&SUBSYS_53711462", ids);
    }

    [Fact]
    public async Task WindowsActivation_UsesOnlyBuiltInLicensingAndNeverEmbedsAProductKey()
    {
        var runner = new RecordingProcessRunner(command => command.FileName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            ? new ProcessResult(command, 0, "LICENSED", string.Empty, TimeSpan.Zero, true)
            : new ProcessResult(command, 0, string.Empty, string.Empty, TimeSpan.Zero, true));

        WindowsActivationResult result = await new WindowsActivationService(runner).TryActivateAsync(ExecutionMode.Live);

        Assert.True(result.IsActivated);
        Assert.Equal(2, runner.Commands.Count);
        Assert.Contains(runner.Commands, command => command.FileName.Equals("cscript.exe", StringComparison.OrdinalIgnoreCase) && command.Arguments.Contains("/ato"));
        Assert.DoesNotContain(runner.Commands.SelectMany(command => command.Arguments), argument =>
            System.Text.RegularExpressions.Regex.IsMatch(argument, "(?i)[A-Z0-9]{5}(?:-[A-Z0-9]{5}){4}"));
    }

    [Fact]
    public void ImageDetection_ParsesRealIndexesWithoutHardcoding()
    {
        const string output = """
            Index : 1
            Name : Windows 11 Home
            Description : Windows 11 Home
            Size : 16,000,000,000 bytes
            Architecture : x64
            Version : 10.0.22631

            Index : 6
            Name : Windows 11 Pro
            Description : Windows 11 Pro
            Size : 16,500,000,000 bytes
            Architecture : x64
            Version : 10.0.22631
            """;
        IReadOnlyList<WindowsImageInfo> images = DismOutputParser.ParseImageInfo(output, "C:\\install.wim");
        Assert.Equal([1, 6], images.Select(static image => image.ImageIndex));
        Assert.Equal("Windows 11 Pro", images[1].Name);
        Assert.All(images, static image => Assert.Equal(ProcessorArchitecture.X64, image.Architecture));
    }

    [Fact]
    public void BcdCommandGeneration_UsesArgumentListAndBootSequence()
    {
        CommandSpec command = BcdCommandFactory.BootSequence("{11111111-1111-4111-8111-111111111111}");
        Assert.Equal("bcdedit.exe", command.FileName);
        Assert.Equal(["/bootsequence", "{11111111-1111-4111-8111-111111111111}"], command.Arguments);
    }

    [Fact]
    public void BcdBootRegistration_DoesNotUseExplicitSystemPartition()
    {
        CommandSpec command = BootFilesCommandFactory.RegisterFirmware(new BootFilesRequest("W:\\", "S:\\", Locale: "ru-RU"));
        Assert.DoesNotContain("/s", command.Arguments, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("UEFI", command.Arguments, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DismCommandGeneration_ContainsSelectedIndexAndVerification()
    {
        CommandSpec command = DismCommandFactory.ApplyImage(new ApplyImageRequest("E:\\Images\\install.esd", 7, "W:\\"));
        Assert.Contains("/Index:7", command.Arguments);
        Assert.Contains("/Verify", command.Arguments);
        Assert.Contains("/CheckIntegrity", command.Arguments);
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiskPartGeneration_CreatesFinalRecoveryLayout()
    {
        string script = new DiskPartScriptBuilder().RemoveStagingAndCreateRecovery(new FinalizeDiskRequest(0, Guid.NewGuid(), 4));
        Assert.Contains("extend", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("de94bba4-06d1-4d40-a16a-bfd50179d6ac", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x8000000000000001", script, StringComparison.OrdinalIgnoreCase);
    }
}
