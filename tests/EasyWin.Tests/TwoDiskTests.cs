using System.Text.Json.Nodes;
using EasyWin.Core.Manifests;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Core.Security;
using EasyWin.Core.Serialization;
using EasyWin.Core.Validation;
using EasyWin.Deployment.Boot;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Safety;
using EasyWin.Deployment.Staging;
using Json.Schema;
using EasyWin.Deployment.Configuration;
using EasyWin.Deployment.Images;
using EasyWin.Deployment.Orchestration;
using EasyWin.Deployment.PostInstall;

namespace EasyWin.Tests;

public sealed class TwoDiskTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static PhysicalDiskSnapshot Target(long shrink = 0) => new(TestData.Disk(), false, false, "GPT",
        [new() { DiskNumber = 0, PartitionNumber = 3, GptPartitionId = Guid.NewGuid(), DriveLetter = "C", FileSystem = "NTFS", Role = PartitionRole.Windows, SizeBytes = 400 * GiB, OffsetBytes = GiB, ShrinkAvailableBytes = shrink }]);
    private static PhysicalDiskSnapshot Storage(long free = 100 * GiB) => new(TestData.Disk("STORAGE") with
        { DiskNumber = 1, DeviceId = "STORAGE-DEVICE", UniqueId = "STORAGE-UNIQUE", IsBootDisk = false, IsSystemDisk = false }, false, false, "GPT",
        [new() { DiskNumber = 1, PartitionNumber = 3, GptPartitionId = Guid.NewGuid(), DriveLetter = "D", FileSystem = "NTFS", Role = PartitionRole.Data, SizeBytes = 400 * GiB, OffsetBytes = GiB, FreeBytes = free }]);
    private static ReinstallPlan Plan(PhysicalDiskSnapshot target, PhysicalDiskSnapshot stage) => new() { TargetDisk = target.Identity, StagingDisk = stage.Identity };

    [Fact]
    public async Task TwoDisk_ValidStagingDisk_PassesWithoutTargetShrink()
    {
        var t = Target(); var s = Storage();
        Assert.All(await StagingSelection.CheckAsync(new Disks(t, s), Plan(t, s), 20 * GiB, default), c => Assert.True(c.Passed, c.Message));
    }
    [Fact]
    public async Task TwoDisk_TargetHasZeroShrink_StillPassesWithSeparateStaging()
    {
        var t = Target() with { Partitions = [] }; var s = Storage();
        Assert.All(await StagingSelection.CheckAsync(new Disks(t, s), Plan(t, s), 20 * GiB, default), c => Assert.True(c.Passed, c.Message));
    }
    [Fact]
    public async Task TwoDisk_StagingDiskSelectedForErase_Fails()
    {
        var t = Target(); var s = Storage();
        var checks = await StagingSelection.CheckAsync(new Disks(t, s), Plan(t, s) with { AdditionalDisksToErase = [s.Identity] }, 20 * GiB, default);
        Assert.False(checks.Single(c => c.Code == "erase-disks").Passed);
        Assert.False(checks.Single(c => c.Code == "staging-disk").Passed);
    }
    [Fact]
    public async Task TwoDisk_TargetEqualsStaging_UsesSameDiskModeOnly()
    {
        var t = Target();
        var checks = await StagingSelection.CheckAsync(new Disks(t), Plan(t, t), 20 * GiB, default);
        Assert.False(checks.Single(c => c.Code == "staging-capacity").Passed);
        t = Target(50 * GiB);
        Assert.All(await StagingSelection.CheckAsync(new Disks(t), Plan(t, t), 20 * GiB, default), c => Assert.True(c.Passed, c.Message));
    }
    [Fact]
    public async Task TwoDisk_StagingIdentityChangedAfterReboot_Fails()
    {
        var s = Storage();
        await Assert.ThrowsAsync<DeploymentSafetyException>(() => StagingSelection.ResolveAsync(new Disks(s with { Identity = s.Identity with { SerialNumber = "changed" } }), s.Identity, default));
    }
    [Fact]
    public async Task TwoDisk_TargetIdentityChangedAfterReboot_Fails()
    {
        var t = Target();
        await Assert.ThrowsAsync<DeploymentSafetyException>(() => StagingSelection.ResolveAsync(new Disks(t with { Identity = t.Identity with { UniqueId = "changed" } }), t.Identity, default));
    }
    [Fact]
    public async Task TwoDisk_ChangedDiskNumbersResolveByStableIdentity()
    {
        var s = Storage();
        Assert.Equal(7, (await StagingSelection.ResolveAsync(new Disks(s with { Identity = s.Identity with { DiskNumber = 7 } }), s.Identity, default)).Identity.DiskNumber);
        await Assert.ThrowsAsync<DeploymentSafetyException>(() => StagingSelection.ResolveAsync(new Disks(s, s), s.Identity, default));
    }
    [Fact]
    public async Task TwoDisk_ImageMissingInWinPE_Fails()
    {
        var (m, root, _, _) = await Manifest();
        File.Delete(Path.Combine(root, m.Image.RelativePath));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Manifests().ValidateAsync(m, root));
    }
    [Fact]
    public async Task TwoDisk_InvalidManifestHash_Fails()
    {
        var (m, root, _, _) = await Manifest();
        await Assert.ThrowsAsync<InvalidDataException>(() => Manifests().ValidateAsync(m with { ProfileId = "altered" }, root));
    }
    [Fact]
    public async Task TwoDisk_ValidManifestAndStoragePassDestructiveGuards()
    {
        var (m, root, t, s) = await Manifest();
        await Manifests().ValidateAsync(m, root);
        new DeploymentSafetyValidator(new DiskIdentityValidator()).ValidateBeforeDestructive(m, t, Path.Combine(root, m.Image.RelativePath), stagingDisk: s);
        Assert.Throws<DeploymentSafetyException>(() => new DeploymentSafetyValidator(new DiskIdentityValidator()).ValidateBeforeDestructive(m, t, Path.Combine(root, m.Image.RelativePath), stagingDisk: s with { Identity = s.Identity with { Model = "changed" } }));
    }
    [Fact]
    public void TwoDisk_StagingDiskNeverAppearsInEraseScript()
    {
        string script = new DiskPartScriptBuilder().PrepareTargetWithSeparateStaging(Target(), Storage());
        Assert.DoesNotContain("select disk 1", script);
        Assert.Single(script.Split("select disk ").Skip(1));
        Assert.Throws<DeploymentSafetyException>(() => new DiskPartScriptBuilder().PrepareTargetWithSeparateStaging(Target(), Target()));
    }
    [Fact]
    public void TwoDisk_TargetCanBeFullyRepartitioned()
    {
        string script = new DiskPartScriptBuilder().PrepareTargetWithSeparateStaging(Target() with { Partitions = [] }, Storage());
        Assert.Contains("clean\r\nconvert gpt", script);
        Assert.Contains("create partition efi", script);
        Assert.Contains("create partition msr", script);
        Assert.Contains("Windows RE tools", script);
    }
    [Fact]
    public void SingleDisk_ExistingProtectedStagingStillWorks()
    {
        var t = Target(); var source = t.Partitions[0];
        var stage = source with { PartitionNumber = 4, GptPartitionId = Guid.NewGuid(), Role = PartitionRole.Deployment, OffsetBytes = 450 * GiB, SizeBytes = 20 * GiB };
        string script = new DiskPartScriptBuilder().PrepareTargetPreservingDeployment(new(0, stage.GptPartitionId, 4, stage.OffsetBytes, stage.SizeBytes), [source, stage]);
        Assert.DoesNotContain("clean", script);
        Assert.DoesNotContain("select partition 4", script);
    }
    [Fact]
    public async Task Preflight_TargetErrorDoesNotFakeSecondDiskError()
    {
        var t = Target(); var s = Storage();
        var checks = await StagingSelection.CheckAsync(new Disks(s), Plan(t, s), 20 * GiB, default);
        Assert.False(checks.Single(c => c.Code == "target-disk").Passed);
        Assert.True(checks.Single(c => c.Code == "erase-disks").Passed);
    }
    [Fact]
    public async Task Preflight_StagingErrorIsReportedIndependently()
    {
        var t = Target(); var s = Storage(0);
        var checks = await StagingSelection.CheckAsync(new Disks(t, s), Plan(t, s), 20 * GiB, default);
        Assert.True(checks.Single(c => c.Code == "target-disk").Passed);
        Assert.True(checks.Single(c => c.Code == "erase-disks").Passed);
        Assert.False(checks.Single(c => c.Code == "staging-capacity").Passed);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bcd_RamdiskOptionsMissing_IsCreatedOrHandledSafely(bool failCreate)
    {
        string root = TestData.NewDirectory();
        string wim = Path.Combine(root, "boot.wim"), sdi = Path.Combine(root, "boot.sdi"), backup = Path.Combine(root, "backup");
        await File.WriteAllTextAsync(wim, "boot"); await File.WriteAllTextAsync(sdi, "sdi");
        var runner = new BcdRunner(failCreate);
        var task = new BcdService(runner).ArmOneTimeWinPeAsync(new(backup, wim, sdi), ExecutionMode.Live);
        if (failCreate) { await Assert.ThrowsAnyAsync<Exception>(() => task); Assert.Contains(runner.Commands, c => c.Arguments[0] == "/import"); }
        else { await task; Assert.Contains(runner.Commands, c => c.Arguments[0] == "/bootsequence"); }
        Assert.Contains(runner.Commands, c => c.Arguments.Count >= 2 && c.Arguments[0] == "/create" && c.Arguments[1] == "{ramdiskoptions}");
        Assert.DoesNotContain(runner.Commands, c => c.Arguments[0] == "/default");
    }
    [Fact]
    public void AppCatalogSchema_Version2_IsValid()
    {
        string root = TestData.FindConfigRoot();
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(root, "schemas", "app-catalog.schema.json")));
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "apps", "catalog.json")))!;
        Assert.True(schema.Evaluate(catalog).IsValid);
        catalog["version"] = 1;
        Assert.False(schema.Evaluate(catalog).IsValid);
    }
    private static ManifestService Manifests() => new(new SystemTextJsonSerializer(), new Sha256HashService());

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PostInstall_FullDryRun_PersistsAppResultsAndSecondLaunchDoesNothing(bool failApp, bool interrupt)
    {
        var (m, root, t, s) = await Manifest();
        string config = TestData.FindConfigRoot();
        var inventory = m.FileInventory.ToList();
        foreach (string source in Directory.EnumerateFiles(config, "*.json", SearchOption.AllDirectories))
        {
            string relative = Path.Combine("Config", Path.GetRelativePath(config, source));
            string destination = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            inventory.Add(new() { RelativePath = relative, LengthBytes = new FileInfo(destination).Length,
                Sha256 = await new Sha256HashService().ComputeSha256Async(destination) });
        }
        var json = new SystemTextJsonSerializer();
        var catalogs = new EasyWin.Core.Catalogs.CatalogService(json);
        var app = (await catalogs.LoadApplicationsAsync(Path.Combine(root, "Config", "apps", "catalog.json"))).Applications[0];
        bool interrupted = false;
        bool IsApp(CommandSpec command) => command.FileName.EndsWith(app.Installer.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        var runner = new RecordingProcessRunner(command =>
        {
            if (interrupt && !interrupted && command.FileName == "cscript.exe")
            {
                interrupted = true;
                throw new OperationCanceledException("Simulated restart after the application checkpoint.");
            }
            return new(command, failApp && IsApp(command) ? 1 : 0, "", "simulated optional failure", TimeSpan.Zero, true);
        });
        var diskService = new Disks(t, s);
        var diskPart = new DiskPartService(runner);
        var manifests = Manifests();
        string path = Path.Combine(TestData.NewDirectory(), "manifest.json");
        m = m with { ProfileId = "standard", DriverSelectionMode = DriverSelectionMode.None,
            ApplicationIds = [app.Id], FileInventory = inventory,
            State = new() { CompletedStages = [DeploymentStage.AwaitingFirstBoot] } };
        await manifests.SaveAsync(m, path);
        var orchestrator = new PostInstallOrchestrator(json, manifests, diskService, diskPart, catalogs,
            new(runner, new Sha256HashService()), new(runner, new Sha256HashService()), new(runner), new(runner),
            new(diskService, diskPart, new(), new BcdService(runner), runner, new()));
        if (interrupt)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => orchestrator.RunAsync(path));
            var interruptedManifest = await json.DeserializeFileAsync<DeploymentManifest>(path);
            Assert.Contains(app.Id, interruptedManifest.State.CompletedApplicationIds);
            Assert.DoesNotContain(DeploymentStage.Completed, interruptedManifest.State.CompletedStages);
        }
        await orchestrator.RunAsync(path);
        var completed = await json.DeserializeFileAsync<DeploymentManifest>(path);
        await manifests.ValidateAsync(completed, Path.GetDirectoryName(path)!, false);
        Assert.Contains(DeploymentStage.Completed, completed.State.CompletedStages);
        Assert.True(completed.State.FirstBootValidated);
        Assert.Contains(app.Id, failApp ? completed.State.FailedApplicationIds : completed.State.CompletedApplicationIds);
        if (failApp) Assert.Contains(completed.State.OptionalWarnings, warning => warning.StartsWith(app.Id + ":", StringComparison.Ordinal));
        Assert.Empty(diskPart.PlannedScripts);
        Assert.Single(runner.Commands, IsApp);
        int commands = runner.Commands.Count;
        await orchestrator.RunAsync(path);
        Assert.Equal(commands, runner.Commands.Count);
    }

    [Fact]
    public async Task TwoDisk_FullWinPeDryRun_UsesOnlyTargetAndCompletes()
    {
        var (m, root, t, s) = await Manifest();
        var runner = new RecordingProcessRunner();
        var diskPart = new DiskPartService(runner);
        var manifests = Manifests();
        string path = Path.Combine(root, "manifest.json");
        await manifests.SaveAsync(m, path);
        var orchestrator = new WinPeDeploymentOrchestrator(manifests, new Disks(t, s), diskPart, new(),
            new WindowsImageService(runner), new BootFilesService(runner), new UnattendGenerator(), new(), runner);
        await orchestrator.RunAsync(path);
        Assert.Equal(2, diskPart.PlannedScripts.Count);
        Assert.Contains("clean", diskPart.PlannedScripts[0]);
        Assert.DoesNotContain(diskPart.PlannedScripts, script => script.Contains("select disk 1", StringComparison.Ordinal));
        Assert.Contains(runner.Commands, c => c.FileName == "dism.exe");
        Assert.Contains(runner.Commands, c => c.FileName == "bcdboot.exe");
        Assert.DoesNotContain(runner.Commands, c => c.FileName is "shutdown.exe" or "wpeutil.exe");
        Assert.True(File.Exists(Path.Combine(root, "DryRun", "unattend.xml")));
        var completed = await manifests.LoadAndValidateAsync(path);
        Assert.Contains(DeploymentStage.AwaitingFirstBoot, completed.State.CompletedStages);
        Assert.True(completed.State.DestructiveWorkStarted);
        int commands = runner.Commands.Count;
        var replay = await Assert.ThrowsAsync<DeploymentSafetyException>(() => orchestrator.RunAsync(path));
        Assert.Equal("winpe.replay.blocked", replay.Code);
        Assert.Equal(commands, runner.Commands.Count);
        Assert.Equal(2, diskPart.PlannedScripts.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WinPe_InterruptedBeforeFirstDiskCommand_DoesNotReplayDestruction(bool hasNewMarker)
    {
        var (m, root, t, s) = await Manifest();
        m = m with { State = new() { DestructiveWorkStarted = hasNewMarker, CurrentStage = DeploymentStage.PrepareDisk } };
        var runner = new RecordingProcessRunner(); var diskPart = new DiskPartService(runner);
        var manifests = Manifests(); string path = Path.Combine(root, "manifest.json");
        await manifests.SaveAsync(m, path);
        var orchestrator = new WinPeDeploymentOrchestrator(manifests, new Disks(t, s), diskPart, new(),
            new WindowsImageService(runner), new BootFilesService(runner), new UnattendGenerator(), new(), runner);
        var error = await Assert.ThrowsAsync<DeploymentSafetyException>(() => orchestrator.RunAsync(path));
        Assert.Equal("winpe.replay.blocked", error.Code);
        Assert.Equal("winpe.replay.blocked", (await Assert.ThrowsAsync<DeploymentSafetyException>(() => orchestrator.RunAsync(path))).Code);
        Assert.Empty(diskPart.PlannedScripts);
        Assert.Empty(runner.Commands);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("identity")]
    [InlineData("manifest")]
    public async Task TwoDisk_WinPeFailure_StopsBeforeAnyDiskScript(string failure)
    {
        var (m, root, t, s) = await Manifest();
        var runner = new RecordingProcessRunner(); var diskPart = new DiskPartService(runner);
        var manifests = Manifests(); string path = Path.Combine(root, "manifest.json");
        await manifests.SaveAsync(m, path);
        if (failure == "image") File.Delete(Path.Combine(root, m.Image.RelativePath));
        if (failure == "identity") s = s with { Identity = s.Identity with { SerialNumber = "changed" } };
        if (failure == "manifest") await File.WriteAllTextAsync(path, "{}");
        var orchestrator = new WinPeDeploymentOrchestrator(manifests, new Disks(t, s), diskPart, new(),
            new WindowsImageService(runner), new BootFilesService(runner), new UnattendGenerator(), new(), runner);
        await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.RunAsync(path));
        Assert.Empty(diskPart.PlannedScripts);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task TwoDisk_CleanupDryRun_NeverChangesStorageGeometry()
    {
        var (m, root, t, s) = await Manifest();
        var runner = new RecordingProcessRunner(); var diskPart = new DiskPartService(runner);
        var cleanup = new CleanupService(new Disks(t, s), diskPart, new(), new BcdService(runner), runner, new());
        await cleanup.CleanupAsync(m, TestData.NewDirectory(), ExecutionMode.DryRun);
        Assert.Empty(diskPart.PlannedScripts);
        Assert.True(File.Exists(Path.Combine(root, m.Image.RelativePath)));
        await Assert.ThrowsAsync<DeploymentSafetyException>(() => cleanup.CleanupAsync(m, root, ExecutionMode.DryRun));
    }

    [Fact]
    public async Task SingleDisk_GenericDiskPartStillRejectsClean()
    {
        var service = new DiskPartService(new RecordingProcessRunner());
        await Assert.ThrowsAsync<DeploymentSafetyException>(() => service.ExecuteAsync("select disk 0\r\nclean\r\n", TestData.NewDirectory(), ExecutionMode.DryRun));
        Assert.Empty(service.PlannedScripts);
    }
    private static async Task<(DeploymentManifest, string, PhysicalDiskSnapshot, PhysicalDiskSnapshot)> Manifest()
    {
        string root = TestData.NewDirectory(); var t = Target(); var s = Storage(); var p = s.Partitions[0];
        var files = new List<ManifestFileEntry>();
        foreach (var path in new[] { "install.wim", "boot.wim", "boot.sdi" })
        {
            await File.WriteAllTextAsync(Path.Combine(root, path), path);
            files.Add(new() { RelativePath = path, LengthBytes = new FileInfo(Path.Combine(root, path)).Length, Sha256 = await new Sha256HashService().ComputeSha256Async(Path.Combine(root, path)) });
        }
        Guid plan = Guid.NewGuid();
        var m = new DeploymentManifest { PlanId = plan, TargetDisk = t.Identity, ExecutionMode = ExecutionMode.DryRun,
            StagingPartition = new() { Disk = s.Identity, Mode = StagingMode.SeparateDiskFolder, FolderRelativePath = $"EasyWin-Deployment/{plan:N}", RootPath = root, PartitionNumber = p.PartitionNumber, GptPartitionId = p.GptPartitionId, OffsetBytes = p.OffsetBytes, SizeBytes = p.SizeBytes },
            Image = new() { RelativePath = files[0].RelativePath, Sha256 = files[0].Sha256, LengthBytes = files[0].LengthBytes, ImageIndex = 1 },
            Boot = new() { WinPeWimRelativePath = "boot.wim", WinPeSdiRelativePath = "boot.sdi", OneTimeBootConfigured = true, BootEntryId = Guid.NewGuid() },
            FileInventory = files };
        return (Manifests().Seal(m), root, t, s);
    }
    private sealed class Disks(params PhysicalDiskSnapshot[] snapshots) : IPhysicalDiskService
    {
        public Task<IReadOnlyList<PhysicalDiskSnapshot>> GetDisksAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PhysicalDiskSnapshot>>(snapshots);
        public Task<PhysicalDiskSnapshot> GetDiskAsync(int diskNumber, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Must resolve stable identity, not cached disk number.");
    }
    [Theory]
    [InlineData("/export", false)]
    [InlineData("/set", true)]
    [InlineData("/bootsequence", true)]
    public async Task Bcd_Failure_RollsBackOnlyAfterSuccessfulBackup(string failure, bool rollback)
    {
        string root = TestData.NewDirectory();
        string wim = Path.Combine(root, "boot.wim"), sdi = Path.Combine(root, "boot.sdi");
        await File.WriteAllTextAsync(wim, "boot"); await File.WriteAllTextAsync(sdi, "sdi");
        var runner = new BcdRunner(false, failure);
        await Assert.ThrowsAnyAsync<Exception>(() => new BcdService(runner).ArmOneTimeWinPeAsync(new(Path.Combine(root, "backup"), wim, sdi), ExecutionMode.Live));
        Assert.Equal(rollback, runner.Commands.Any(c => c.Arguments[0] == "/import"));
    }

    [Fact]
    public async Task TwoDisk_MissingStableIdentityFailsBeforeStaging()
    {
        var s = Storage();
        await Assert.ThrowsAsync<DeploymentSafetyException>(() => StagingSelection.ResolveAsync(new Disks(s), s.Identity with { SerialNumber = "" }, default));
        await Assert.ThrowsAsync<DeploymentSafetyException>(() => StagingSelection.ResolveAsync(new Disks(s), s.Identity with { DeviceId = "" }, default));
    }

    [Fact]
    public async Task TwoDisk_MissingRequiredBootInventoryFails()
    {
        var (m, root, _, _) = await Manifest();
        m = m with { FileInventory = m.FileInventory.Where(e => e.RelativePath != "boot.wim").ToArray() };
        Assert.Throws<InvalidDataException>(() => Manifests().Seal(m));
        await Assert.ThrowsAsync<InvalidDataException>(() => Manifests().ValidateAsync(m, root));
    }

    private sealed class BcdRunner(bool failCreate, string? failure = null) : IProcessRunner
    {
        public List<CommandSpec> Commands { get; } = [];
        private bool _created;
        public async Task<ProcessResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command); var a = command.Arguments; int exit = 0;
            if (a[0] == "/export") await File.WriteAllTextAsync(a[1], "backup", cancellationToken);
            if (a[0] == "/enum" && a[1] == "{ramdiskoptions}" && !_created) exit = 1;
            if (a[0] == "/create" && a[1] == "{ramdiskoptions}") { _created = !failCreate; exit = failCreate ? 1 : 0; }
            string output = "{11111111-1111-4111-8111-111111111111}\n" + string.Join("\n", Commands.Where(c => c.Arguments[0] == "/set").Select(c => c.Arguments[^1]));
            return new(command, a[0] == failure ? 1 : exit, output, "", TimeSpan.Zero);
        }
    }
}
