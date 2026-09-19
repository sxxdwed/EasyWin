using EasyWin.Core.Catalogs;
using EasyWin.Core.Manifests;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Core.Security;
using EasyWin.Core.Serialization;
using EasyWin.Deployment.DryRun;
using EasyWin.Deployment.PostInstall;

namespace EasyWin.Tests;

public sealed class CatalogAndDryRunTests
{
    [Fact]
    public async Task Catalogs_LoadStandardLiteAppsAndDriverCategories()
    {
        string root = TestData.FindConfigRoot();
        var catalogs = new CatalogService(new SystemTextJsonSerializer());
        InstallationProfile standard = await catalogs.LoadProfileAsync(Path.Combine(root, "profiles", "standard.json"));
        InstallationProfile lite = await catalogs.LoadProfileAsync(Path.Combine(root, "profiles", "lite.json"));
        Assert.Equal("standard", standard.Id);
        Assert.Empty(standard.RemoveProvisionedAppPackages);
        Assert.Equal("lite", lite.Id);
        Assert.Equal(18, lite.RemoveProvisionedAppPackages.Count);
        Assert.Contains("Microsoft.XboxGamingOverlay", lite.RemoveProvisionedAppPackages);
        Assert.Contains("MSTeams", lite.RemoveProvisionedAppPackages);
        Assert.DoesNotContain("Microsoft.WindowsStore", lite.RemoveProvisionedAppPackages);
        ApplicationCatalog apps = await catalogs.LoadApplicationsAsync(Path.Combine(root, "apps", "catalog.json"));
        Assert.Equal(10, apps.Applications.Count);
        Assert.Contains(apps.Applications, app => app.Id == "nvidia-app" && app.RequiredHardwareIdPrefixes.Contains("PCI\\VEN_10DE"));
        Assert.Contains(apps.Applications, app => app.Id == "amd-software" && app.RequiredHardwareIdPrefixes.Count == 2);
        Assert.Contains(apps.Applications, app => app.Id == "msi-center");
        Assert.Equal(5, (await catalogs.LoadDriversAsync(Path.Combine(root, "drivers", "catalog.json"))).Packages.Count);
    }

    [Fact]
    public async Task DryRun_TraversesFullFlowWithoutExecutingDestructiveCommands()
    {
        string workspace = TestData.NewDirectory();
        DryRunReport report = await new DryRunEngine().RunAsync(workspace, TestData.FindConfigRoot());
        Assert.True(report.Success);
        Assert.False(report.DestructiveCommandsExecuted);
        Assert.Equal("Desktop", report.Stages[0]);
        Assert.Equal("finished", report.Stages[^1]);
        Assert.Contains("one-time boot", report.Stages);
        Assert.Contains("second disk erase", report.Stages);
        Assert.Contains("Windows install", report.Stages);
        Assert.True(report.Stages.ToList().IndexOf("Windows install") < report.Stages.ToList().IndexOf("second disk erase"));
        Assert.Contains("PostInstall", report.Stages);
        Assert.Contains("Lite profile", report.Stages);
        Assert.DoesNotContain(report.DiskPartScripts, script => script.Split(['\r', '\n']).Any(line => line.Trim().Equals("clean", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(report.DiskPartScripts, script => script.StartsWith("select disk 1", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(report.RecordedCommands);
        Assert.Contains(report.RecordedCommands, command => command.Contains("Remove-AppxProvisionedPackage", StringComparison.Ordinal));
        DeploymentManifest manifest = await new ManifestService(new SystemTextJsonSerializer(), new Sha256HashService())
            .LoadAndValidateAsync(report.ManifestPath, verifyFiles: true);
        Assert.Equal(DeploymentStage.Completed, manifest.State.CurrentStage);
        Assert.Equal(DeploymentStage.Completed, manifest.State.LastSuccessfulStage);
        Assert.Contains(DeploymentStage.ApplyImage, manifest.State.CompletedStages);
        Assert.Contains(DeploymentStage.InstallDrivers, manifest.State.CompletedStages);
        Assert.Contains(DeploymentStage.CleanupStaging, manifest.State.CompletedStages);
        Assert.False(manifest.State.RecoveryRequired);
        Assert.Null(manifest.State.LastError);
        Assert.True(manifest.State.AttemptNumber >= 1);
    }

    [Fact]
    public async Task ApplicationInstaller_SkipsPackageForIncompatibleHardware()
    {
        string root = TestData.NewDirectory();
        var runner = new RecordingProcessRunner();
        var installer = new ApplicationInstaller(runner, new Sha256HashService());
        var package = new ApplicationPackage
        {
            Id = "amd-software",
            Name = "AMD Software",
            Installer = "Apps/AMD/setup.exe",
            Sha256 = new string('A', 64),
            RequiredHardwareIdPrefixes = ["PCI\\VEN_1002", "PCI\\VEN_1022"],
        };

        IReadOnlyList<EasyWin.Deployment.Models.AppInstallResult> results = await installer.InstallAsync(
            [package],
            root,
            ExecutionMode.Live,
            new HashSet<string>(["PCI\\VEN_10DE&DEV_2D05"], StringComparer.OrdinalIgnoreCase));

        Assert.Empty(runner.Commands);
        Assert.Equal("Skipped: incompatible hardware", Assert.Single(results).Message);
    }

    [Fact]
    public async Task LiteProfile_AppliesDefaultUserSettingsAndOnlyAllowlistedAppRemoval()
    {
        string config = TestData.FindConfigRoot();
        InstallationProfile lite = await new CatalogService(new SystemTextJsonSerializer())
            .LoadProfileAsync(Path.Combine(config, "profiles", "lite.json"));
        var runner = new RecordingProcessRunner();

        await new ProfileApplicator(runner).ApplyAsync(lite, ExecutionMode.DryRun);

        string[] commands = runner.Commands.Select(static command => command.ToDisplayString()).ToArray();
        Assert.Contains(commands, command => command.Contains("reg.exe add HKU\\EasyWinDefault\\", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(commands, command => command.Contains("reg.exe add HKCU\\", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(commands, command => command.Contains("Clipchamp.Clipchamp", StringComparison.Ordinal));
        Assert.Contains(commands, command => command.Contains("Microsoft.XboxGamingOverlay", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.Contains("Microsoft.WindowsStore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(commands, command => command.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(lite.RemoveProvisionedAppPackages.Count, runner.Commands.Count(command =>
            Path.GetFileName(command.FileName).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task StandardProfile_DoesNotRemoveProvisionedApplications()
    {
        string config = TestData.FindConfigRoot();
        InstallationProfile standard = await new CatalogService(new SystemTextJsonSerializer())
            .LoadProfileAsync(Path.Combine(config, "profiles", "standard.json"));
        var runner = new RecordingProcessRunner();

        await new ProfileApplicator(runner).ApplyAsync(standard, ExecutionMode.DryRun);

        Assert.DoesNotContain(runner.Commands, command =>
            Path.GetFileName(command.FileName).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProfileApplicator_RejectsRemovalOutsideSafetyAllowlist()
    {
        var profile = new InstallationProfile
        {
            Id = "unsafe",
            DisplayName = "Unsafe",
            RemoveProvisionedAppPackages = ["Microsoft.WindowsStore"],
        };

        await Assert.ThrowsAsync<EasyWin.Deployment.Safety.DeploymentSafetyException>(() =>
            new ProfileApplicator(new RecordingProcessRunner()).ApplyAsync(profile, ExecutionMode.DryRun));
    }
}
