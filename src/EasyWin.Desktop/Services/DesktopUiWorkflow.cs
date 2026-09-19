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
    private readonly IProcessRunner _runner;
    private readonly DeploymentFileLogger _logger;
    private readonly PhysicalDiskService _disks;
    private readonly IsoImageService _iso;
    private readonly WindowsImageService _images;

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
            snapshot.Identity)).ToArray();
        var profiles = new[]
        {
            new ProfileChoice("standard", "Standard", "Стандартная конфигурация Windows без удаления системных компонентов."),
            new ProfileChoice("lite", "Lite", "Удаляет выбранные потребительские приложения, Xbox/Game Bar и Teams. Update, Defender, Store, Edge и Recovery сохраняются."),
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
                ? compatible ? "совместимое оборудование найдено" : "нет совместимого оборудования — установка отключена"
                : "готово для офлайн-установки";
            return new ApplicationChoice(app.Id, app.Name, app.Description, app.Category, compatible, availability);
        }).ToArray();
        return new DesktopDiscovery(disks, profiles, applications, ["Русский", "English"]);
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
            return CheckIds().Select(item => new UiPreflightCheck(item.Id, item.Name, "DryRun: проверка смоделирована без системных изменений.", true, UiCheckStatus.Pass)).ToArray();
        }

        var service = new PreflightService(new WindowsEnvironmentProbe(), new BitLockerService(_runner));
        long stageSize = Math.Max(6L * 1024 * 1024 * 1024, new FileInfo(request.IsoPath).Length + 3L * 1024 * 1024 * 1024);
        long size = stageSize + 4L * 1024 * 1024 * 1024;
        PreflightReport report = await service.CheckAsync(new PreflightRequest(Path.GetPathRoot(Environment.SystemDirectory)!, request.IsoPath, size), cancellationToken).ConfigureAwait(false);
        var diskValidation = await ValidateDiskSelectionAsync(request, cancellationToken).ConfigureAwait(false);
        (bool payloadsValid, string payloadsMessage) = await ValidateSelectedPayloadsAsync(request, cancellationToken).ConfigureAwait(false);
        var results = new List<UiPreflightCheck>();
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
                "target-disk" or "erase-disks" or "staging-disk" or "staging-capacity" or "disk-layout" => diskValidation.Single(c => c.Code == descriptor.Id).Passed,
                "staging" => Directory.Exists(DefaultAdkRoot()),
                "hashes" => payloadsValid,
                _ => false,
            };
            string message = source?.Message ?? descriptor.Id switch
            {
                "target-disk" or "erase-disks" or "staging-disk" or "staging-capacity" or "disk-layout" => diskValidation.Single(c => c.Code == descriptor.Id).Message,
                "hashes" => payloadsMessage,
                "staging" when !pass => "Windows ADK + WinPE add-on не найдены.",
                _ => pass ? "Проверка пройдена." : "Проверка не пройдена.",
            };
            results.Add(new UiPreflightCheck(descriptor.Id, descriptor.Name, message, true, pass ? UiCheckStatus.Pass : UiCheckStatus.Fail));
        }

        return results;
    }

    private Task<IReadOnlyList<DiskSelectionCheck>> ValidateDiskSelectionAsync(UiPreparationRequest request, CancellationToken cancellationToken)
    {
        var plan = new ReinstallPlan
        {
            TargetDisk = request.TargetDisk.NativeIdentity as DiskIdentity ?? new(),
            StagingDisk = request.StagingDisk?.NativeIdentity as DiskIdentity,
            AdditionalDisksToErase = request.AdditionalDisksToErase.Select(d => d.NativeIdentity as DiskIdentity ?? new()).ToArray(),
        };
        return StagingSelection.CheckAsync(_disks, plan, EstimateStagingBytes(request.IsoPath), cancellationToken);
    }

    private static async Task<(bool IsValid, string Message)> ValidateSelectedPayloadsAsync(
        UiPreparationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            string configRoot = FindConfigRoot();
            string payloadRoot = ResolvePayloadRoot(configRoot);
            var hashes = new Sha256HashService();
            var catalogs = new CatalogService(new SystemTextJsonSerializer());
            ApplicationCatalog catalog = await catalogs.LoadApplicationsAsync(Path.Combine(configRoot, "apps", "catalog.json"), cancellationToken).ConfigureAwait(false);
            _ = await catalogs.LoadDriversAsync(Path.Combine(configRoot, "drivers", "catalog.json"), cancellationToken).ConfigureAwait(false);
            _ = await catalogs.LoadProfileAsync(Path.Combine(configRoot, "profiles", $"{request.Profile.Id}.json"), cancellationToken).ConfigureAwait(false);

            if (request.ApplicationIds.Count == 0)
            {
                return (true, "Профиль проверен; дополнительные пакеты не выбраны. Системные файлы будут хэшированы при staging.");
            }

            foreach (string id in request.ApplicationIds)
            {
                ApplicationPackage package = catalog.Applications.SingleOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Приложение '{id}' отсутствует в каталоге.");
                if (!Sha256HashService.IsValidSha256(package.Sha256))
                {
                    throw new InvalidDataException($"Для '{package.Name}' не задан доверенный SHA-256.");
                }

                string path = PathValidator.ResolveUnderRoot(payloadRoot, package.Installer, mustExist: true);
                FileHashVerificationResult verification = await hashes.VerifyFileAsync(path, package.Sha256, package.SizeBytes, cancellationToken).ConfigureAwait(false);
                if (!verification.IsValid)
                {
                    throw new InvalidDataException($"'{package.Name}': {verification.Error}");
                }
            }

            return (true, $"Проверены SHA-256 всех выбранных пакетов: {request.ApplicationIds.Count}.");
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
            progress.Report(new UiDeploymentProgress("validation", "Проверка", "DryRun: входные данные проверены", 5));
            string workspace = Path.Combine(Path.GetTempPath(), "EasyWin", "DryRun");
            DryRunReport report = await new DryRunEngine().RunAsync(workspace, FindConfigRoot(), cancellationToken).ConfigureAwait(false);
            progress.Report(new UiDeploymentProgress("postinstall", "Первый запуск", report.Message, 100));
            return new UiWorkflowResult(report.Success, report.Message, report.ManifestPath);
        }

        if (request.TargetDisk.NativeIdentity is not DiskIdentity disk)
        {
            return new UiWorkflowResult(false, "Не удалось получить стабильную идентичность целевого диска.");
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
                ?? throw new InvalidDataException("ISO или выбранная редакция изменились после проверки. Повторно выберите образ.");
            var plan = new ReinstallPlan
            {
                ExecutionMode = ExecutionMode.Live,
                TargetDisk = disk,
                StagingDisk = request.StagingDisk?.NativeIdentity as DiskIdentity,
                AdditionalDisksToErase = request.AdditionalDisksToErase
                    .Select(choice => choice.NativeIdentity as DiskIdentity ?? throw new InvalidDataException("Стабильная идентичность дополнительного диска потеряна."))
                    .ToArray(),
                Image = selected with { SourcePath = imagePath },
                ProfileId = request.Profile.Id,
                ApplicationIds = request.ApplicationIds,
                DriverSelectionMode = request.DriverMode.Equals("none", StringComparison.OrdinalIgnoreCase) ? DriverSelectionMode.None : DriverSelectionMode.Automatic,
                Language = request.Language.Equals("Русский", StringComparison.OrdinalIgnoreCase) ? "ru-RU" : "en-US",
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
                throw new DirectoryNotFoundException("Self-contained WinPE/PostInstall payloads are missing. Publish them with the supplied profiles before a real run.");
            }

            string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EasyWin", "Work", plan.PlanId.ToString("N"));
            var mapped = new Progress<DeploymentProgress>(value => progress.Report(new UiDeploymentProgress(StageId(value.Stage), value.Stage.ToString(), value.Message, value.Percentage ?? 0)));
            string configRoot = FindConfigRoot();
            DesktopPreparationResult result = await orchestrator.PrepareAsync(new DesktopPreparationRequest(plan, imagePath, DefaultAdkRoot(), winPePayload, postInstallPayload, configRoot, work, StagingDriveLetter: SelectStagingDriveLetter(), PayloadRoot: ResolvePayloadRoot(configRoot)), mapped, cancellationToken).ConfigureAwait(false);
            return new UiWorkflowResult(true, "Deployment подготовлен; выполняется одноразовая загрузка WinPE.", result.ManifestPath);
        }
        finally
        {
            await _iso.UnmountAsync(mounted, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static IEnumerable<(string Id, string Name)> CheckIds() =>
    [
        ("administrator", "Права администратора"), ("uefi", "Режим UEFI"), ("target-disk", "Диск для Windows"),
        ("erase-disks", "Диски для очистки"),
        ("staging-disk", "Диск хранения — сохраняется"), ("staging-capacity", "Место для установки"), ("disk-layout", "Разметка целевого диска"),
        ("bitlocker", "BitLocker"), ("image", "Образ Windows"), ("space", "Свободное место"),
        ("power", "Питание"), ("staging", "Deployment-среда"), ("hashes", "Целостность файлов"),
    ];

    private static bool IsSupportedFixedDisk(DiskIdentity disk) => disk.BusType is
        DiskBusType.Nvme or DiskBusType.Sata or DiskBusType.Sas or DiskBusType.Scsi or DiskBusType.Raid or DiskBusType.Ata;

    private static long EstimateStagingBytes(string isoPath) => Math.Max(
        20L * 1024 * 1024 * 1024,
        checked(new FileInfo(isoPath).Length + 8L * 1024 * 1024 * 1024));

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

        throw new InvalidOperationException("Нет свободной буквы диска для защищённого раздела EasyWin.");
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
    private static ProcessorArchitecture ParseArchitecture(string value) => Enum.TryParse(value, true, out ProcessorArchitecture result) ? result : ProcessorArchitecture.X64;
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

        throw new DirectoryNotFoundException("EasyWin config directory was not found.");
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
            ?? throw new DirectoryNotFoundException("Корень payload не найден.");
    }
}
