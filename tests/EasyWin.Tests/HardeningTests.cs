using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Deployment.Environment;
using EasyWin.Deployment.PostInstall;
using EasyWin.Deployment.Safety;

namespace EasyWin.Tests;

public sealed class HardeningTests
{
    [Theory]
    [InlineData("S:\\")]
    [InlineData("W")]
    [InlineData("r:")]
    [InlineData("T:\\")]
    public void Preflight_DriveLetterCollision_Fails(string letter) =>
        Assert.Throws<DeploymentSafetyException>(() => DeploymentPrerequisiteChecks.ValidateDriveLetters(["C:\\", letter]));
    [Fact]
    public void Preflight_IncompatibleAdkVersion_Fails() =>
        Assert.Throws<DeploymentSafetyException>(() => DeploymentPrerequisiteChecks.ValidateVersions(new(10, 0, 26100), new(10, 0, 22000), new(10, 0, 26100)));
    [Fact]
    public async Task Preflight_BcdUnavailable_Fails()
    {
        var runner = new RecordingProcessRunner(c => new(c, 1, "", "denied", TimeSpan.Zero));
        await Assert.ThrowsAsync<DeploymentSafetyException>(() => DeploymentPrerequisiteChecks.ProbeBcdAsync(runner, TestData.NewDirectory(), default));
        Assert.Single(runner.Commands);
    }
    [Fact]
    public async Task Preflight_BcdProbe_NeverMutatesLiveStore()
    {
        var runner = new RecordingProcessRunner(c =>
        {
            if (c.Arguments[0] == "/export") File.WriteAllText(c.Arguments[1], "isolated test BCD");
            return new(c, 0, "", "", TimeSpan.Zero);
        });
        await DeploymentPrerequisiteChecks.ProbeBcdAsync(runner, TestData.NewDirectory(), default);
        Assert.All(runner.Commands.Where(c => c.Arguments.Contains("/create") || c.Arguments.Contains("/bootsequence")), c => Assert.Equal("/store", c.Arguments[0]));
        Assert.DoesNotContain(runner.Commands, c => c.Arguments.Contains("/import") || c.Arguments.Contains("/default"));
    }
    [Fact]
    public async Task Drivers_UnsignedRejected()
    {
        string root = await DriverFixture("SCSIAdapter");
        var runner = new RecordingProcessRunner(c => new(c, 1, "", "invalid signature", TimeSpan.Zero));
        await Assert.ThrowsAsync<InvalidDataException>(() => DriverPreparation.InspectAsync(root, "signtool.exe", runner, new HashSet<string> { "PCI\\VEN_1234" }, DiskBusType.Raid, default));
    }
    [Fact]
    public async Task Drivers_StorageDriverInjectedIntoWinPE()
    {
        string root = await DriverFixture("SCSIAdapter");
        var prepared = await DriverPreparation.InspectAsync(root, "signtool.exe", new RecordingProcessRunner(), new HashSet<string> { "PCI\\VEN_1234" }, DiskBusType.Raid, default);
        Assert.Single(prepared.WinPeInfs);
        Assert.Equal(Path.Combine(root, "driver.inf"), prepared.WinPeInfs[0]);
    }
    [Fact]
    public async Task Drivers_NonCriticalDriverNotInjectedIntoWinPE()
    {
        string root = await DriverFixture("Display");
        var prepared = await DriverPreparation.InspectAsync(root, "signtool.exe", new RecordingProcessRunner(), new HashSet<string> { "PCI\\VEN_1234" }, DiskBusType.Nvme, default);
        Assert.Empty(prepared.WinPeInfs);
    }
    [Fact]
    public async Task Drivers_ExportSizeIncludedInPreflight()
    {
        string root = await DriverFixture("Net");
        var prepared = await DriverPreparation.InspectAsync(root, "signtool.exe", new RecordingProcessRunner(), new HashSet<string> { "PCI\\VEN_1234" }, DiskBusType.Nvme, default);
        Assert.Equal(Directory.EnumerateFiles(root).Sum(f => new FileInfo(f).Length), prepared.Bytes);
        Assert.True(prepared.NetworkFound);
        await prepared.ValidateAsync(default);
        await File.AppendAllTextAsync(Path.Combine(root, "driver.inf"), "tamper");
        await Assert.ThrowsAsync<InvalidDataException>(() => prepared.ValidateAsync(default));
    }
    private static async Task<string> DriverFixture(string driverClass)
    {
        string root = TestData.NewDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "driver.inf"), $"[Version]\nClass={driverClass}\nCatalogFile=driver.cat\n[Models]\nPCI\\VEN_1234\n");
        await File.WriteAllTextAsync(Path.Combine(root, "driver.cat"), "signature fixture only");
        return root;
    }
}
