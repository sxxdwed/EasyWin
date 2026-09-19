using EasyWin.Core.Catalogs;
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
        Assert.Equal("standard", (await catalogs.LoadProfileAsync(Path.Combine(root, "profiles", "standard.json"))).Id);
        Assert.Equal("lite", (await catalogs.LoadProfileAsync(Path.Combine(root, "profiles", "lite.json"))).Id);
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
        Assert.DoesNotContain(report.DiskPartScripts, script => script.Split(['\r', '\n']).Any(line => line.Trim().Equals("clean", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(report.DiskPartScripts, script => script.StartsWith("select disk 1", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(report.RecordedCommands);
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
}
