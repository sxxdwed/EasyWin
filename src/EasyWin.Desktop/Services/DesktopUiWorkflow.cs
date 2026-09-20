using System.IO;
using EasyWin.Core.Catalogs;
using EasyWin.Core.Manifests;
using EasyWin.Core.Logging;
using EasyWin.Core.Models;
using EasyWin.Core.Processes;
using EasyWin.Core.Security;
using EasyWin.Core.Serialization;
using EasyWin.Core.Validation;
using EasyWin.Deployment.Boot;
using EasyWin.Deployment.Disks;
using EasyWin.Deployment.DryRun;
using EasyWin.Deployment.Environment;
using EasyWin.Deployment.Images;
using EasyWin.Deployment.Models;
using EasyWin.Deployment.Orchestration;
using EasyWin.Deployment.PostInstall;
using EasyWin.Deployment.Staging;
using EasyWin.Deployment.WinPe;
using EasyWin.Desktop.Models;

namespace EasyWin.Desktop.Services;

public sealed class DesktopUiWorkflow : IDesktopUiWorkflow, IDisposable
{
    private static readonly string[] WinPeComponents = ["WinPE-WMI", "WinPE-NetFX", "WinPE-Scripting", "WinPE-PowerShell", "WinPE-StorageWMI"];
    private readonly IProcessRunner _runner;
    private readonly DeploymentFileLogger _logger;
    private readonly PhysicalDiskService _disks;
    private readonly IsoImageService _iso;
    private readonly WindowsImageService _images;
    private PreparedAppSet? _preparedApps;
    private PreparedDrivers? _preparedDrivers;

    public DesktopUiWorkflow()
    {
        string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EasyWin", "Logs");
        _logger = new DeploymentFileLogger(logs);
        _runner = new LoggingProcessRunner(new ProcessRunner(), _logger);
        _disks = new PhysicalDiskService(_runner);
        _iso = new IsoImageService(_runner);
        _images = new WindowsImageService(_runner);
    }

    public void Dispose() => _logger.Dispose();

    public async Task<DesktopDiscovery> DiscoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PhysicalDiskSnapshot> snapshots = await _disks.GetDisksAsync(cancellationToken).ConfigureAwait(false);
        var disks = snapshots.Select(snapshot => new DiskChoice(
            snapshot.Identity.DeviceId,
            snapshot.Identity.Model,
            snapshot.Identity.SerialNumber,
            snapshot.Identity.SizeBytes,
            snapshot.Identity.BusType.ToString(),
            snapshot.Identity.IsSystemDisk,
            snapshot.Partitions
                .Where(partition => partition.DriveLetter is { Length: 1 } && string.Equals(partition.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
                .Select(static partition => partition.ShrinkAvailableBytes)
                .DefaultIfEmpty(0)
                .Max(),
            snapshot.Identity) { Volumes = snapshot.Partitions }).ToArray();
        var profiles = new[]
        {
            new ProfileChoice("standard", "Standard", EasyWin.Core.Localization.DeploymentStrings.Get("Ui101")),
            new ProfileChoice("lite", "Lite", EasyWin.Core.Localization.DeploymentStrings.Get("Ui102")),
        };
        string configRoot = FindConfigRoot();
        ApplicationCatalog appCatalog = await new CatalogService(new SystemTextJsonSerializer())
            .LoadApplicationsAsync(Path.Combine(configRoot, "apps", "catalog.json"), cancellationToken)
            .ConfigureAwait(false);
        IReadOnlySet<string> hardwareIds;
        try
        {
            hardwareIds = await new DriverInstaller(_runner, new Sha256HashService())
                .DetectHardwareIdsAsync(ExecutionMode.Live, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            hardwareIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var applications = appCatalog.Applications.Select(app =>
        {
            bool hardwareSpecific = app.RequiredHardwareIdPrefixes.Count > 0;
            bool compatible = !hardwareSpecific || app.RequiredHardwareIdPrefixes.Any(prefix =>
                hardwareIds.Any(actual => actual.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            string availability = hardwareSpecific
                ? compatible ? EasyWin.Core.Localization.DeploymentStrings.Get("Ui104") : EasyWin.Core.Localization.DeploymentStrings.Get("Ui105")
                : EasyWin.Core.Localization.DeploymentStrings.Get("Ui106");
            bool present;
            try { present = File.Exists(PathValidator.ResolveUnderRoot(ResolvePayloadRoot(configRoot), app.Installer)); }
            catch (ArgumentException) { present = false; }
            bool downloadable = VerifiedAppAcquisition.Source(app.Id) is not null;
            if (!present) availability = EasyWin.Core.Localization.DeploymentStrings.Get(downloadable ? "PackageDownloadAvailable" : "Ui103");
            return new ApplicationChoice(app.Id, app.Name, app.Description, app.Category, compatible && (present || downloadable), availability);
        }).ToArray();
        return new DesktopDiscovery(disks, profiles, applications, [EasyWin.Core.Localization.DeploymentStrings.Get("LanguageRussian"), EasyWin.Core.Localization.DeploymentStrings.Get("LanguageEnglish")]);
    }

    public async Task<IReadOnlyList<EditionChoice>> InspectImageAsync(string isoPath, CancellationToken cancellationToken)
    {
        MountedIso mounted = await _iso.MountAsync(isoPath, cancellationToken).ConfigureAwait(false);
        try
        {
            string image = _iso.LocateInstallImage(mounted.RootPath);
            IReadOnlyList<WindowsImageInfo> editions = await _images.GetEditionsAsync(image, cancellationToken).ConfigureAwait(false);
            return editions.Select(static edition => new EditionChoice(edition.ImageIndex, edition.Name, edition.Architecture.ToString())).ToArray();
        }
        finally
        {
            await _iso.UnmountAsync(mounted, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<UiPreflightCheck>> PreflightAsync(UiPreparationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DryRun)
        {
            return CheckIds().Select(item => new UiPreflightCheck(item.Id, item.Name, EasyWin.Core.Localization.DeploymentStrings.Get("DryRunCheck"), true, UiCheckStatus.Pass)).ToArray();
        }

        var service = new PreflightService(new WindowsEnvironmentProbe(), new BitLockerService(_runner));
        long stageSize = Math.Max(6L * 1024 * 1024 * 1024, new FileInfo(request.IsoPath).Length + 3L * 1024 * 1024 * 1024);
        long size = stageSize + 4L * 1024 * 1024 * 1024;
        PreflightReport report = await service.CheckAsync(new PreflightRequest(Path.GetPathRoot(Environment.SystemDirectory)!, request.IsoPath, size), cancellationToken).ConfigureAwait(false);
        (bool payloadsValid, string payloadsMessage) = await ValidateSelectedPayloadsAsync(request, cancellationToken).ConfigureAwait(false);
        string? driverError = null;
        if (request.DriverMode != "none")
        {
            try
            {
                if (_preparedDrivers is null)
                {
                    string tool = DriverPreparation.FindSignTool();
                    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EasyWin", "DriverPreparation", Guid.NewGuid().ToString("N"));
                    await new DriverBackupService(_runner).ExportAsync(root, cancellationToken).ConfigureAwait(false);
                    var hardware = await new DriverInstaller(_runner, new Sha256HashService()).DetectHardwareIdsAsync(ExecutionMode.Live, cancellationToken).ConfigureAwait(false);
                    _preparedDrivers = await DriverPreparation.InspectAsync(root, tool, _runner, hardware, ((DiskIdentity)request.TargetDisk.NativeIdentity).BusType, cancellationToken).ConfigureAwait(false);
                }
                await _preparedDrivers.ValidateAsync(cancellationToken).ConfigureAwait(false);
                if (((DiskIdentity)request.TargetDisk.NativeIdentity).BusType == DiskBusType.Raid && _preparedDrivers.WinPeInfs.Count == 0)
                    throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("DriverStorageMissing"));
            }
            catch (Exception error) when (error is not OperationCanceledException) { driverError = error.Message; _preparedDrivers = null; }
        }
        // Acquisition/export has consumed work-volume space. Recheck using the complete payload budget.
        report = await service.CheckAsync(new PreflightRequest(Path.GetPathRoot(Environment.SystemDirectory)!,
            request.IsoPath, EstimateStagingBytes(request.IsoPath)), cancellationToken).ConfigureAwait(false);
        var diskValidation = await ValidateDiskSelectionAsync(request, cancellationToken).ConfigureAwait(false);
        var storageAvailability = await ValidateStorageAvailabilityAsync(request, cancellationToken).ConfigureAwait(false);
        var results = new List<UiPreflightCheck>();
        if (request.DriverMode != "none")
            results.Add(new("driver-export", EasyWin.Core.Localization.DeploymentStrings.Get("DriverSignatureValid"),
                driverError ?? EasyWin.Core.Localization.DeploymentStrings.Get("DriverSignatureValid"), true, driverError is null ? UiCheckStatus.Pass : UiCheckStatus.Fail));
        else
            results.Add(new("driver-export", EasyWin.Core.Localization.DeploymentStrings.Get("Ui52"), EasyWin.Core.Localization.DeploymentStrings.Get("Ui52"), true, UiCheckStatus.Pass));
        if (_preparedDrivers is not null && request.DriverMode != "none")
        {
            results.Add(new("driver-storage", EasyWin.Core.Localization.DeploymentStrings.Get("DriverStorageFound"),
                EasyWin.Core.Localization.DeploymentStrings.Format("DriverStorageCount", _preparedDrivers.WinPeInfs.Count), false,
                _preparedDrivers.WinPeInfs.Count > 0 ? UiCheckStatus.Pass : UiCheckStatus.Warning));
            results.Add(new("driver-network", EasyWin.Core.Localization.DeploymentStrings.Get("DriverNetworkFound"),
                EasyWin.Core.Localization.DeploymentStrings.Get(_preparedDrivers.NetworkFound ? "DriverNetworkFound" : "DriverNetworkWarning"), false,
                _preparedDrivers.NetworkFound ? UiCheckStatus.Pass : UiCheckStatus.Warning));
        }
        foreach (var descriptor in CheckIds())
        {
            PreflightCheck? source = descriptor.Id switch
            {
                "administrator" => report.Checks.FirstOrDefault(check => check.Code == "privilege.admin"),
                "uefi" => report.Checks.FirstOrDefault(check => check.Code == "firmware.uefi"),
                "bitlocker" => report.Checks.FirstOrDefault(check => check.Code.StartsWith("bitlocker", StringComparison.Ordinal)),
                "image" => report.Checks.FirstOrDefault(check => check.Code == "image.exists"),
                "space" => report.Checks.FirstOrDefault(check => check.Code == "storage.free"),
                "power" => report.Checks.FirstOrDefault(check => check.Code == "power.ac"),
                _ => null,
            };
            bool pass = source?.Passed ?? descriptor.Id switch
            {
                "storage-winpe" => storageAvailability.Passed,
                "target-disk" or "erase-disks" or "staging-disk" or "staging-capacity" or "disk-layout" => diskValidation.Single(c => c.Code == descriptor.Id).Passed,
                "staging" => Directory.Exists(request.AdkRoot ?? DefaultAdkRoot()),
                "hashes" => payloadsValid,
                _ => false,
            };
            string message = source?.Message ?? descriptor.Id switch
            {
                "storage-winpe" => storageAvailability.Message,
                "target-disk" or "erase-disks" or "staging-disk" or "staging-capacity" or "disk-layout" => diskValidation.Single(c => c.Code == descriptor.Id).Message,
                "hashes" => payloadsMessage,
                "staging" when !pass => EasyWin.Core.Localization.DeploymentStrings.Get("Ui112"),
                _ => pass ? EasyWin.Core.Localization.DeploymentStrings.Get("Ui110") : EasyWin.Core.Localization.DeploymentStrings.Get("Ui111"),
            };
            results.Add(new UiPreflightCheck(descriptor.Id, descriptor.Name, message, true, pass ? UiCheckStatus.Pass : UiCheckStatus.Fail));
        }

        async Task Check(string code, string name, Func<Task> action)
        {
            try { await action().ConfigureAwait(false); results.Add(new(code, name, "PASS", true, UiCheckStatus.Pass)); }
            catch (Exception e) when (e is not OperationCanceledException) { results.Add(new(code, name, e.Message, true, UiCheckStatus.Fail)); }
        }
        await Check("stale-deployment", EasyWin.Core.Localization.DeploymentStrings.Get("Ui97"), () => StaleDeploymentGuard.ValidateAsync(_disks, _runner, request.Language == EasyWin.Core.Localization.DeploymentStrings.Get("LanguageEnglish") ? "en-US" : "ru-RU", cancellationToken));
        await Check("bcd-capability", "BCD", () => DeploymentPrerequisiteChecks.ProbeBcdAsync(_runner,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EasyWin", "BcdProbes"), cancellationToken));
        await Check("drive-letters", EasyWin.Core.Localization.DeploymentStrings.Get("DriveLetters"), () =>
        {
            DeploymentPrerequisiteChecks.ValidateDriveLetters(DriveInfo.GetDrives().Select(d => d.Name));
            return Task.CompletedTask;
        });
        await Check("image-edition", EasyWin.Core.Localization.DeploymentStrings.Get("Ui98"), async () =>
        {
            var editions = await InspectImageAsync(request.IsoPath, cancellationToken).ConfigureAwait(false);
            if (!editions.Any(e => e == request.Edition) || ParseArchitecture(request.Edition.Architecture) != ProcessorArchitecture.X64)
                throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("ImageEditionUnsupported"));
        });
        await Check("tool-versions", EasyWin.Core.Localization.DeploymentStrings.Get("ToolVersions"), async () =>
        {
            var pe = await _images.GetEditionsAsync(Path.Combine(request.AdkRoot ?? DefaultAdkRoot(), "amd64", "en-us", "winpe.wim"), cancellationToken).ConfigureAwait(false);
            var mount = await _iso.MountAsync(request.IsoPath, cancellationToken).ConfigureAwait(false);
            try
            {
                var image = (await _images.GetEditionsAsync(_iso.LocateInstallImage(mount.RootPath), cancellationToken).ConfigureAwait(false)).Single(i => i.ImageIndex == request.Edition.Index);
                var tool = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(Environment.SystemDirectory, "dism.exe"));
                if (pe.Count != 1 || pe[0].Architecture != ProcessorArchitecture.X64 || !Version.TryParse(pe[0].Version, out var peVersion) || !Version.TryParse(image.Version, out var imageVersion))
                    throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("ToolVersionsInvalid"));
                DeploymentPrerequisiteChecks.ValidateVersions(imageVersion, peVersion, new(tool.FileMajorPart, tool.FileMinorPart, tool.FileBuildPart, tool.FilePrivatePart));
            }
            finally { await _iso.UnmountAsync(mount, CancellationToken.None).ConfigureAwait(false); }
        });
        await Check("winpe-complete", EasyWin.Core.Localization.DeploymentStrings.Get("Ui99"), () =>
        {
            WinPePrerequisites.Validate(request.AdkRoot ?? DefaultAdkRoot(), AppContext.BaseDirectory);
            return Task.CompletedTask;
        });
        if (payloadsValid && _preparedApps is not null)
            results.AddRange(_preparedApps.Applications.Select(app => new UiPreflightCheck("app-" + app.Id, app.Name,
                EasyWin.Core.Localization.DeploymentStrings.Get("PackageReady"), true, UiCheckStatus.Pass)));
        return results;
    }

    private Task<IReadOnlyList<DiskSelectionCheck>> ValidateDiskSelectionAsync(UiPreparationRequest request, CancellationToken cancellationToken)
    {
        var plan = new ReinstallPlan
        {
            TargetDisk = request.TargetDisk.NativeIdentity as DiskIdentity ?? new(),
            StagingDisk = request.StagingDisk?.NativeIdentity as DiskIdentity,
            StagingVolumeId = request.StagingVolume?.GptPartitionId,
            AdditionalDisksToErase = request.AdditionalDisksToErase.Select(d => d.NativeIdentity as DiskIdentity ?? new()).ToArray(),
        };
        return StagingSelection.CheckAsync(_disks, plan, EstimateStagingBytes(request.IsoPath), cancellationToken);
    }

    private async Task<DiskSelectionCheck> ValidateStorageAvailabilityAsync(UiPreparationRequest request, CancellationToken token)
    {
        string language = request.Language == EasyWin.Core.Localization.DeploymentStrings.Get("LanguageEnglish") ? "en-US" : "ru-RU";
        try
        {
            if (request.StagingDisk is not null && request.StagingDisk != request.TargetDisk)
            {
                var plan = new ReinstallPlan { TargetDisk = (DiskIdentity)request.TargetDisk.NativeIdentity,
                    StagingDisk = (DiskIdentity)request.StagingDisk.NativeIdentity, StagingVolumeId = request.StagingVolume?.GptPartitionId,
                    ExpectedStagingVolume = request.StagingVolume, Language = language };
                var volume = await new StagingVolumeValidator(_disks, new BitLockerService(_runner)).ValidateAsync(plan, EstimateStagingBytes(request.IsoPath), token).ConfigureAwait(false);
                if (!Directory.Exists(volume.DriveLetter + ":\\")) throw new IOException(EasyWin.Core.Localization.DeploymentStrings.Get("VolumeInaccessible"));
                await DeploymentPrerequisiteChecks.ProbeWritableAsync(volume.DriveLetter + ":\\", token).ConfigureAwait(false);
            }
            return new("storage-winpe", true, EasyWin.Core.Localization.DeploymentStrings.Get("StorageAvailable", language));
        }
        catch (Exception e) when (e is not OperationCanceledException) { return new("storage-winpe", false, e.Message); }
    }

    private async Task<(bool IsValid, string Message)> ValidateSelectedPayloadsAsync(
        UiPreparationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            string configRoot = FindConfigRoot();
            string payloadRoot = ResolvePayloadRoot(configRoot);
            if (_preparedApps is not null)
            {
                try { await _preparedApps.ValidateAsync(request.ApplicationIds, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is InvalidDataException or IOException) { _preparedApps = null; }
            }
            if (_preparedApps is null)
            {
                using var http = VerifiedAppAcquisition.CreateClient();
                var signature = new AuthenticodeVerifier(_runner);
                _preparedApps = await new PreparedApplications(new VerifiedAppAcquisition(http, signature), signature).PrepareAsync(
                    request.ApplicationIds, configRoot, payloadRoot,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EasyWin", "Acquisition"), cancellationToken).ConfigureAwait(false);
            }
            configRoot = _preparedApps.ConfigurationRoot;
            payloadRoot = _preparedApps.PayloadRoot;
            var hashes = new Sha256HashService();
            var catalogs = new CatalogService(new SystemTextJsonSerializer());
            ApplicationCatalog catalog = await catalogs.LoadApplicationsAsync(Path.Combine(configRoot, "apps", "catalog.json"), cancellationToken).ConfigureAwait(false);
            _ = await catalogs.LoadDriversAsync(Path.Combine(configRoot, "drivers", "catalog.json"), cancellationToken).ConfigureAwait(false);
            _ = await catalogs.LoadProfileAsync(Path.Combine(configRoot, "profiles", $"{request.Profile.Id}.json"), cancellationToken).ConfigureAwait(false);

            if (request.ApplicationIds.Count == 0)
            {
                return (true, EasyWin.Core.Localization.DeploymentStrings.Get("PayloadsNotSelected"));
            }

            foreach (string id in request.ApplicationIds)
            {
                ApplicationPackage package = catalog.Applications.SingleOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Format("PackageCatalogMissing", id));
                if (!Sha256HashService.IsValidSha256(package.Sha256))
                {
                    throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Format("PackageHashMissing", package.Name));
                }

                string path = PathValidator.ResolveUnderRoot(payloadRoot, package.Installer, mustExist: true);
                FileHashVerificationResult verification = await hashes.VerifyFileAsync(path, package.Sha256, package.SizeBytes, cancellationToken).ConfigureAwait(false);
                if (!verification.IsValid)
                {
                    throw new InvalidDataException($"'{package.Name}': {verification.Error}");
                }
            }

            return (true, EasyWin.Core.Localization.DeploymentStrings.Format("PackageHashesChecked", request.ApplicationIds.Count));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (false, exception.Message);
        }
    }

    public async Task<UiWorkflowResult> StageAndArmAsync(UiPreparationRequest request, IProgress<UiDeploymentProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DryRun)
        {
            progress.Report(new UiDeploymentProgress("validation", EasyWin.Core.Localization.DeploymentStrings.Get("Ui53"), EasyWin.Core.Localization.DeploymentStrings.Get("DryRunInputs"), 5));
            string workspace = Path.Combine(Path.GetTempPath(), "EasyWin", "DryRun");
            DryRunReport report = await new DryRunEngine().RunAsync(workspace, FindConfigRoot(), cancellationToken).ConfigureAwait(false);
            progress.Report(new UiDeploymentProgress("postinstall", EasyWin.Core.Localization.DeploymentStrings.Get("Ui61"), report.Message, 100));
            return new UiWorkflowResult(report.Success, report.Message, report.ManifestPath);
        }

        if (request.TargetDisk.NativeIdentity is not DiskIdentity disk)
        {
            return new UiWorkflowResult(false, EasyWin.Core.Localization.DeploymentStrings.Get("TargetIdentityMissing"));
        }

        MountedIso mounted = await _iso.MountAsync(request.IsoPath, cancellationToken).ConfigureAwait(false);
        try
        {
            string imagePath = _iso.LocateInstallImage(mounted.RootPath);
            IReadOnlyList<WindowsImageInfo> currentEditions = await _images.GetEditionsAsync(imagePath, cancellationToken).ConfigureAwait(false);
            WindowsImageInfo selected = currentEditions.SingleOrDefault(edition =>
                edition.ImageIndex == request.Edition.Index &&
                edition.Name.Equals(request.Edition.DisplayName, StringComparison.OrdinalIgnoreCase) &&
                edition.Architecture == ParseArchitecture(request.Edition.Architecture))
                ?? throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("ImageChanged"));
            var plan = new ReinstallPlan
            {
                ExecutionMode = ExecutionMode.Live,
                TargetDisk = disk,
                StagingDisk = request.StagingDisk?.NativeIdentity as DiskIdentity,
                StagingVolumeId = request.StagingVolume?.GptPartitionId,
                ExpectedStagingVolume = request.StagingVolume,
                RequiredStagingBytes = EstimateStagingBytes(request.IsoPath),
                AdditionalDisksToErase = request.AdditionalDisksToErase
                    .Select(choice => choice.NativeIdentity as DiskIdentity ?? throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("AdditionalIdentityMissing")))
                    .ToArray(),
                Image = selected with { SourcePath = imagePath },
                ProfileId = request.Profile.Id,
                ApplicationIds = request.ApplicationIds,
                DriverSelectionMode = request.DriverMode.Equals("none", StringComparison.OrdinalIgnoreCase) ? DriverSelectionMode.None : DriverSelectionMode.Automatic,
                Language = request.Language.Equals(EasyWin.Core.Localization.DeploymentStrings.Get("LanguageRussian"), StringComparison.OrdinalIgnoreCase) ? "ru-RU" : "en-US",
                DataLossAcknowledged = true,
                FinalConfirmationAccepted = true,
            };
            var json = new SystemTextJsonSerializer();
            var hashes = new Sha256HashService();
            var manifests = new ManifestService(json, hashes);
            var diskPart = new DiskPartService(_runner);
            var orchestrator = new DesktopPreparationOrchestrator(
                new PreflightService(new WindowsEnvironmentProbe(), new BitLockerService(_runner)),
                _disks,
                new LocalStagingPartitionService(_disks, diskPart, new DiskPartScriptBuilder(), new DiskIdentityValidator()),
                new WinPeMediaBuilder(_runner),
                new StagingService(hashes, manifests),
                new BcdService(_runner),
                manifests,
                hashes,
                _runner,
                new DiskIdentityValidator());
            string baseDirectory = AppContext.BaseDirectory;
            string winPePayload = Path.Combine(baseDirectory, "EasyWin.WinPE");
            string postInstallPayload = Path.Combine(baseDirectory, "EasyWin.PostInstall");
            if (!Directory.Exists(winPePayload) || !Directory.Exists(postInstallPayload))
            {
                throw new DirectoryNotFoundException(EasyWin.Core.Localization.DeploymentStrings.Get("WorkerPayloadMissing"));
            }

            string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EasyWin", "Work", plan.PlanId.ToString("N"));
            var mapped = new Progress<DeploymentProgress>(value => progress.Report(new UiDeploymentProgress(StageId(value.Stage), value.Stage.ToString(), value.Message, value.Percentage ?? 0)));
            string configRoot = FindConfigRoot();
            if (_preparedApps is null) throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("PackageDownloadInvalid"));
            await _preparedApps.ValidateAsync(request.ApplicationIds, cancellationToken).ConfigureAwait(false);
            DesktopPreparationResult result = await orchestrator.PrepareAsync(new DesktopPreparationRequest(plan, imagePath, request.AdkRoot ?? DefaultAdkRoot(), winPePayload, postInstallPayload, _preparedApps.ConfigurationRoot, work, StagingDriveLetter: SelectStagingDriveLetter(), PayloadRoot: _preparedApps.PayloadRoot, Drivers: _preparedDrivers), mapped, cancellationToken).ConfigureAwait(false);
            return new UiWorkflowResult(true, EasyWin.Core.Localization.DeploymentStrings.Get("DeploymentPrepared"), result.ManifestPath);
        }
        finally
        {
            await _iso.UnmountAsync(mounted, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static IEnumerable<(string Id, string Name)> CheckIds() =>
    [
        ("administrator", EasyWin.Core.Localization.DeploymentStrings.Get("Ui80")), ("uefi", EasyWin.Core.Localization.DeploymentStrings.Get("Ui81")), ("target-disk", EasyWin.Core.Localization.DeploymentStrings.Get("Ui107")),
        ("erase-disks", EasyWin.Core.Localization.DeploymentStrings.Get("Ui82")),
        ("storage-winpe", EasyWin.Core.Localization.DeploymentStrings.Get("Ui100")),
        ("staging-disk", EasyWin.Core.Localization.DeploymentStrings.Get("Ui108")), ("staging-capacity", EasyWin.Core.Localization.DeploymentStrings.Get("Ui109")), ("disk-layout", EasyWin.Core.Localization.DeploymentStrings.Get("Ui84")),
        ("bitlocker", "BitLocker"), ("image", EasyWin.Core.Localization.DeploymentStrings.Get("Ui6")), ("space", EasyWin.Core.Localization.DeploymentStrings.Get("Ui85")),
        ("power", EasyWin.Core.Localization.DeploymentStrings.Get("Ui86")), ("staging", EasyWin.Core.Localization.DeploymentStrings.Get("Ui87")), ("hashes", EasyWin.Core.Localization.DeploymentStrings.Get("Ui88")),
    ];

    private static bool IsSupportedFixedDisk(DiskIdentity disk) => disk.BusType is
        DiskBusType.Nvme or DiskBusType.Sata or DiskBusType.Sas or DiskBusType.Scsi or DiskBusType.Raid or DiskBusType.Ata;

    private long EstimateStagingBytes(string isoPath) => checked((_preparedApps?.Bytes ?? 0) + (_preparedDrivers?.Bytes ?? 0) + Math.Max(
        20L * 1024 * 1024 * 1024,
        checked(new FileInfo(isoPath).Length + 8L * 1024 * 1024 * 1024)));

    private static string FormatGiB(long bytes) => (bytes / 1024d / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static char SelectStagingDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Where(static drive => drive.Name.Length >= 2 && drive.Name[1] == ':')
            .Select(static drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();
        foreach (char candidate in "TUVWYZKLMNOPQ")
        {
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(EasyWin.Core.Localization.DeploymentStrings.Get("NoStageLetter"));
    }
    private static string DefaultAdkRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("EASYWIN_ADK_WINPE_ROOT", EnvironmentVariableTarget.Machine)
            ?? Environment.GetEnvironmentVariable("EASYWIN_ADK_WINPE_ROOT", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("EASYWIN_ADK_WINPE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Assessment and Deployment Kit", "Windows Preinstallation Environment");
    }
    private static ProcessorArchitecture ParseArchitecture(string value) => Enum.TryParse(value, true, out ProcessorArchitecture result) ? result : throw new InvalidDataException(EasyWin.Core.Localization.DeploymentStrings.Get("ArchitectureUnknown"));
    private static string StageId(DeploymentStage stage) => stage switch
    {
        <= DeploymentStage.ValidateTargetDisk => "validation",
        DeploymentStage.PrepareStaging => "staging",
        DeploymentStage.PrepareBoot or DeploymentStage.AwaitingWinPeBoot => "boot",
        >= DeploymentStage.PrepareDisk and <= DeploymentStage.AwaitingFirstBoot => "deployment",
        _ => "postinstall",
    };
    private static string FindConfigRoot()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "config");
        if (Directory.Exists(beside))
        {
            return beside;
        }

        string current = Environment.CurrentDirectory;
        for (int index = 0; index < 8; index++)
        {
            string candidate = Path.Combine(current, "config");
            if (Directory.Exists(candidate)) return candidate;
            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null) break;
            current = parent.FullName;
        }

        throw new DirectoryNotFoundException(EasyWin.Core.Localization.DeploymentStrings.Get("ConfigMissing"));
    }

    private static string ResolvePayloadRoot(string configRoot)
    {
        string? configured = Environment.GetEnvironmentVariable("EASYWIN_PAYLOAD_ROOT", EnvironmentVariableTarget.Machine)
            ?? Environment.GetEnvironmentVariable("EASYWIN_PAYLOAD_ROOT", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("EASYWIN_PAYLOAD_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Directory.GetParent(configRoot)?.FullName
            ?? throw new DirectoryNotFoundException(EasyWin.Core.Localization.DeploymentStrings.Get("PayloadRootMissing"));
    }
}
