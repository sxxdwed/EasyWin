using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using EasyWin.Desktop.Models;
using EasyWin.Desktop.Mvvm;
using EasyWin.Desktop.Services;

namespace EasyWin.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDesktopUiWorkflow _workflow;
    private readonly IInteractionService _interaction;
    private CancellationTokenSource _lifetime = new();
    private string _isoPath = string.Empty;
    private EditionChoice? _selectedEdition;
    private ProfileChoice? _selectedProfile;
    private string? _selectedLanguage;
    private DiskChoice? _selectedDisk;
    private DiskChoice? _selectedStagingDisk;
    public DiskChoice? SelectedStagingDisk
    {
        get => _selectedStagingDisk;
        set
        {
            if (SetProperty(ref _selectedStagingDisk, value))
            {
                StagingVolumes.Clear();
                if (value is not null && value != SelectedDisk)
                    foreach (var volume in value.Volumes.Where(p => p.GptPartitionId != Guid.Empty && p.DriveLetter is { Length: 1 }))
                        StagingVolumes.Add(new(volume));
                SelectedStagingVolume = StagingVolumes.Count == 1 ? StagingVolumes[0] : null;
                RebuildAdditionalDiskChoices();
                DestructiveAcknowledged = false;
                OnPropertyChanged(nameof(DiskSelectionSummary));
                OnPropertyChanged(nameof(DestructiveAcknowledgementText));
                InvalidateChecks();
            }
        }
    }
    public ObservableCollection<DiskChoice> StagingDisks { get; } = [];
    public ObservableCollection<StagingVolumeChoice> StagingVolumes { get; } = [];
    private StagingVolumeChoice? _selectedStagingVolume;
    public StagingVolumeChoice? SelectedStagingVolume
    {
        get => _selectedStagingVolume;
        set { if (SetProperty(ref _selectedStagingVolume, value)) { DestructiveAcknowledged = false; InvalidateChecks(); } }
    }
    private DriverModeChoice? _selectedDriverMode;
    private bool _destructiveAcknowledged;
    private bool _isBusy;
    private bool _hasProgress;
    private string _blockingMessage = EasyWin.Core.Localization.DeploymentStrings.Get("Ui48");
    private bool _disposed;
    private string _adkRoot = EasyWin.Deployment.WinPe.WinPePrerequisites.DefaultRoot;
    public string AdkRoot
    {
        get => _adkRoot;
        set { if (SetProperty(ref _adkRoot, value)) { InvalidateChecks(); Prerequisites.Clear(); } }
    }
    public ObservableCollection<PrerequisiteDisplay> Prerequisites { get; } = [];
    public AsyncRelayCommand RefreshPrerequisitesCommand { get; }
    public AsyncRelayCommand BrowseAdkCommand { get; }
    public AsyncRelayCommand OpenAdkDownloadCommand { get; }
    public AsyncRelayCommand CopyErrorCommand { get; }
    private string _lastError = string.Empty;

    public sealed record PrerequisiteDisplay(string Name, string Path, bool Found)
    {
        public string Status => EasyWin.Core.Localization.DeploymentStrings.Get(Found ? "DependencyFound" : "DependencyMissing");
        public System.Windows.Media.Brush StatusBrush => Found ? System.Windows.Media.Brushes.MediumSeaGreen : System.Windows.Media.Brushes.IndianRed;
    }

    private Task RefreshPrerequisitesAsync()
    {
        Prerequisites.Clear();
        foreach (var item in EasyWin.Deployment.WinPe.WinPePrerequisites.Inspect(AdkRoot, AppContext.BaseDirectory))
            Prerequisites.Add(new(item.Name, item.Path, item.Found));
        InvalidateChecks();
        return Task.CompletedTask;
    }

    public MainViewModel(IDesktopUiWorkflow workflow, IInteractionService interaction)
    {
        _workflow = workflow;
        _interaction = interaction;

        DryRun = ResolveDryRunDefault();

        DriverModes.Add(new DriverModeChoice("automatic", EasyWin.Core.Localization.DeploymentStrings.Get("Ui51")));
        DriverModes.Add(new DriverModeChoice("none", EasyWin.Core.Localization.DeploymentStrings.Get("Ui52")));
        _selectedDriverMode = DriverModes[0];

        Stages.Add(new StageDisplayItem("validation", EasyWin.Core.Localization.DeploymentStrings.Get("Ui53"), EasyWin.Core.Localization.DeploymentStrings.Get("Ui54")));
        Stages.Add(new StageDisplayItem("staging", EasyWin.Core.Localization.DeploymentStrings.Get("Ui55"), EasyWin.Core.Localization.DeploymentStrings.Get("Ui56")));
        Stages.Add(new StageDisplayItem("boot", EasyWin.Core.Localization.DeploymentStrings.Get("Ui57"), EasyWin.Core.Localization.DeploymentStrings.Get("Ui58")));
        Stages.Add(new StageDisplayItem("deployment", EasyWin.Core.Localization.DeploymentStrings.Get("Ui59"), EasyWin.Core.Localization.DeploymentStrings.Get("Ui60")));
        Stages.Add(new StageDisplayItem("postinstall", EasyWin.Core.Localization.DeploymentStrings.Get("Ui61"), EasyWin.Core.Localization.DeploymentStrings.Get("Ui62")));

        AddInitialChecks();

        BrowseIsoCommand = new AsyncRelayCommand(BrowseIsoAsync, () => IsNotBusy, HandleError);
        RunChecksCommand = new AsyncRelayCommand(RunChecksAsync, CanRunChecks, HandleError);
        ReinstallCommand = new AsyncRelayCommand(StartAsync, () => CanStart, HandleError);
        RefreshPrerequisitesCommand = new AsyncRelayCommand(_ => RefreshPrerequisitesAsync(), () => IsNotBusy, HandleError);
        BrowseAdkCommand = new AsyncRelayCommand(async token =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = EasyWin.Core.Localization.DeploymentStrings.Get("AdkFolder") };
            if (dialog.ShowDialog(Application.Current.MainWindow) == true)
            {
                AdkRoot = dialog.FolderName;
                await RefreshPrerequisitesAsync();
            }
        }, () => IsNotBusy, HandleError);
        OpenAdkDownloadCommand = new AsyncRelayCommand(token =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(EasyWin.Deployment.WinPe.WinPePrerequisites.DownloadUrl) { UseShellExecute = true });
            return Task.CompletedTask;
        }, () => IsNotBusy, HandleError);
        CopyErrorCommand = new AsyncRelayCommand(token =>
        {
            string diagnostics = string.IsNullOrEmpty(_lastError)
                ? BlockingMessage + Environment.NewLine + string.Join(Environment.NewLine,
                    Checks.Where(check => check.Status == UiCheckStatus.Fail).Select(check => $"[{check.Id}] {check.Name}: {check.Message}"))
                : _lastError;
            Clipboard.SetText(diagnostics);
            return Task.CompletedTask;
        }, () => IsNotBusy, exception => _interaction.ShowError("EasyWin", exception.Message));
        try { _ = RefreshPrerequisitesAsync(); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { BlockingMessage = error.Message; }
    }

    public ObservableCollection<EditionChoice> Editions { get; } = [];
    public ObservableCollection<ProfileChoice> Profiles { get; } = [];
    public ObservableCollection<string> Languages { get; } = [];
    public ObservableCollection<DiskChoice> Disks { get; } = [];
    public ObservableCollection<DiskEraseChoice> AdditionalEraseDisks { get; } = [];
    public ObservableCollection<ApplicationChoice> Applications { get; } = [];
    public ObservableCollection<DriverModeChoice> DriverModes { get; } = [];
    public ObservableCollection<CheckDisplayItem> Checks { get; } = [];
    public ObservableCollection<StageDisplayItem> Stages { get; } = [];
    public ProgressDisplay CurrentProgress { get; } = new();

    public AsyncRelayCommand BrowseIsoCommand { get; }
    public AsyncRelayCommand RunChecksCommand { get; }
    public AsyncRelayCommand ReinstallCommand { get; }

    public bool DryRun { get; }
    public string ModeLabel => DryRun ? EasyWin.Core.Localization.DeploymentStrings.Get("Ui49") : EasyWin.Core.Localization.DeploymentStrings.Get("Ui50");
    public string PrimaryActionLabel => DryRun ? EasyWin.Core.Localization.DeploymentStrings.Get("Ui63") : EasyWin.Core.Localization.DeploymentStrings.Get("Ui40");
    public bool HasEditions => Editions.Count > 0;
    public string EditionSummary => SelectedEdition is null
        ? EasyWin.Core.Localization.DeploymentStrings.Get("Ui64")
        : EasyWin.Core.Localization.DeploymentStrings.Format("EditionSummary", SelectedEdition.DisplayName, SelectedEdition.Architecture);
    public string DiskSelectionSummary => SelectedDisk is null
        ? EasyWin.Core.Localization.DeploymentStrings.Get("Ui65")
        : EasyWin.Core.Localization.DeploymentStrings.Format("TargetSummary", SelectedDisk.DisplayName) +
          (SelectedStagingDisk is null || SelectedStagingDisk == SelectedDisk
              ? EasyWin.Core.Localization.DeploymentStrings.Get("Ui66")
              : EasyWin.Core.Localization.DeploymentStrings.Format("StorageSummary", SelectedStagingDisk.DisplayName)) +
          (SelectedAdditionalDisks.Count == 0
              ? EasyWin.Core.Localization.DeploymentStrings.Get("Ui67")
              : EasyWin.Core.Localization.DeploymentStrings.Format("EraseSummary", SelectedAdditionalDisks[0].DisplayName));
    public string DestructiveAcknowledgementText => SelectedAdditionalDisks.Count == 0
        ? EasyWin.Core.Localization.DeploymentStrings.Get("Ui68")
        : EasyWin.Core.Localization.DeploymentStrings.Get("Ui69");
    public string SelectedApplicationsSummary => Applications.Count(static item => item.IsSelected) switch
    {
        0 => EasyWin.Core.Localization.DeploymentStrings.Get("Ui70"),
        1 => EasyWin.Core.Localization.DeploymentStrings.Get("Ui71"),
        var count => EasyWin.Core.Localization.DeploymentStrings.Format("AppsCount", count),
    };

    public string IsoPath
    {
        get => _isoPath;
        private set
        {
            if (SetProperty(ref _isoPath, value))
            {
                InvalidateChecks();
            }
        }
    }

    public EditionChoice? SelectedEdition
    {
        get => _selectedEdition;
        set
        {
            if (SetProperty(ref _selectedEdition, value))
            {
                OnPropertyChanged(nameof(EditionSummary));
                InvalidateChecks();
            }
        }
    }

    public ProfileChoice? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                InvalidateChecks();
            }
        }
    }

    public string? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value))
            {
                Localization.LocalizedText.Instance.SetLanguage(value == EasyWin.Core.Localization.DeploymentStrings.Get("LanguageEnglish") ? "en-US" : "ru-RU");
                foreach (var item in Checks) item.RefreshLocalization();
                foreach (var item in Stages) item.RefreshLocalization();
                foreach (var item in Applications) item.RefreshLocalization();
                var dependencies = Prerequisites.ToArray();
                Prerequisites.Clear();
                foreach (var item in dependencies) Prerequisites.Add(item);
                System.Windows.Data.CollectionViewSource.GetDefaultView(Profiles).Refresh();
                System.Windows.Data.CollectionViewSource.GetDefaultView(DriverModes).Refresh();
                OnPropertyChanged(string.Empty);
                InvalidateChecks();
            }
        }
    }

    public DiskChoice? SelectedDisk
    {
        get => _selectedDisk;
        set
        {
            if (SetProperty(ref _selectedDisk, value))
            {
                StagingDisks.Clear();
                foreach (var disk in Disks) StagingDisks.Add(disk);
                SelectedStagingDisk = Disks.FirstOrDefault(d => d != value) ?? value;
                RebuildAdditionalDiskChoices();
                DestructiveAcknowledged = false;
                OnPropertyChanged(nameof(DiskSelectionSummary));
                OnPropertyChanged(nameof(DestructiveAcknowledgementText));
                InvalidateChecks();
            }
        }
    }

    public DriverModeChoice? SelectedDriverMode
    {
        get => _selectedDriverMode;
        set
        {
            if (SetProperty(ref _selectedDriverMode, value))
            {
                InvalidateChecks();
            }
        }
    }

    public bool DestructiveAcknowledged
    {
        get => _destructiveAcknowledged;
        set
        {
            if (SetProperty(ref _destructiveAcknowledged, value))
            {
                RefreshCommandState();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                RefreshCommandState();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public bool HasProgress
    {
        get => _hasProgress;
        private set => SetProperty(ref _hasProgress, value);
    }

    public string BlockingMessage
    {
        get => _blockingMessage;
        private set => SetProperty(ref _blockingMessage, value);
    }

    public bool CanStart =>
        !IsBusy &&
        DestructiveAcknowledged &&
        TryCreateRequest(out _) &&
        Checks.All(check => !check.IsCritical || check.Status == UiCheckStatus.Pass);

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var discovery = await _workflow.DiscoverAsync(_lifetime.Token).ConfigureAwait(true);
            Replace(Disks, discovery.Disks);
            Replace(Profiles, discovery.Profiles);
            foreach (ApplicationChoice application in Applications)
            {
                application.SelectionChanged -= OnApplicationSelectionChanged;
            }
            Replace(Applications, discovery.Applications);
            foreach (ApplicationChoice application in Applications)
            {
                application.SelectionChanged += OnApplicationSelectionChanged;
            }
            OnPropertyChanged(nameof(SelectedApplicationsSummary));
            Replace(Languages, discovery.Languages);

            SelectedDisk = Disks
                .Where(static disk => disk.IsRecommendedTarget)
                .OrderByDescending(static disk => disk.IsSystemDisk)
                .ThenByDescending(static disk => disk.StagingCapacityBytes)
                .FirstOrDefault()
                ?? Disks.FirstOrDefault(disk => disk.IsSystemDisk)
                ?? Disks.FirstOrDefault();
            SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id.Equals("standard", StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
            SelectedLanguage = Languages.FirstOrDefault(language => language.Equals(EasyWin.Core.Localization.DeploymentStrings.Get("LanguageRussian"), StringComparison.OrdinalIgnoreCase)) ?? Languages.FirstOrDefault();

            UpdateDiscoveryChecks();
            string? defaultIso = ResolveDefaultIsoPath();
            if (defaultIso is not null)
            {
                try
                {
                    await LoadIsoAsync(defaultIso, _lifetime.Token).ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    MarkCheckFailed("image", EasyWin.Core.Localization.DeploymentStrings.Format("AutoIsoError", exception.Message));
                    BlockingMessage = EasyWin.Core.Localization.DeploymentStrings.Get("Ui72");
                }
            }
        }
        catch (Exception exception)
        {
            MarkCheckFailed("environment", EasyWin.Core.Localization.DeploymentStrings.Format("DiscoveryError", exception.Message));
            BlockingMessage = EasyWin.Core.Localization.DeploymentStrings.Get("Ui73");
        }
        finally
        {
            IsBusy = false;
            RefreshCommandState();
        }
    }

    public void Cancel()
    {
        _lifetime.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task BrowseIsoAsync(CancellationToken cancellationToken)
    {
        var path = _interaction.SelectIso();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await LoadIsoAsync(path, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            RefreshCommandState();
        }
    }

    private async Task LoadIsoAsync(string path, CancellationToken cancellationToken)
    {
        IsoPath = Path.GetFullPath(path);
        Editions.Clear();
        SelectedEdition = null;
        OnPropertyChanged(nameof(HasEditions));
        OnPropertyChanged(nameof(EditionSummary));

        var editions = await _workflow.InspectImageAsync(IsoPath, cancellationToken).ConfigureAwait(true);
        Replace(Editions, editions);
        OnPropertyChanged(nameof(HasEditions));
        SelectedEdition = Editions.FirstOrDefault(edition => edition.DisplayName.Equals("Windows 11 Pro", StringComparison.OrdinalIgnoreCase))
            ?? Editions.FirstOrDefault();

        if (Editions.Count == 0)
        {
            MarkCheckFailed("image", EasyWin.Core.Localization.DeploymentStrings.Get("Ui74"));
            BlockingMessage = EasyWin.Core.Localization.DeploymentStrings.Get("Ui75");
        }
        else
        {
            SetCheck("image", UiCheckStatus.Pass, EasyWin.Core.Localization.DeploymentStrings.Format("EditionsFound", Editions.Count, SelectedEdition!.DisplayName));
            BlockingMessage = EasyWin.Core.Localization.DeploymentStrings.Format("EditionSelected", SelectedEdition.DisplayName);
        }
    }

    private static string? ResolveDefaultIsoPath()
    {
        string? configured = Environment.GetEnvironmentVariable("EASYWIN_DEFAULT_ISO", EnvironmentVariableTarget.Machine)
            ?? Environment.GetEnvironmentVariable("EASYWIN_DEFAULT_ISO", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("EASYWIN_DEFAULT_ISO");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        const string preparedIso = @"D:\EasyWin\ISO\Win11_25H2_Russian_x64_v2.iso";
        if (File.Exists(preparedIso))
        {
            return preparedIso;
        }

        const string preparedDirectory = @"D:\EasyWin\ISO";
        return Directory.Exists(preparedDirectory)
            ? Directory.EnumerateFiles(preparedDirectory, "*.iso", SearchOption.TopDirectoryOnly)
                .OrderBy(static candidate => candidate, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
            : null;
    }

    private async Task RunChecksAsync(CancellationToken cancellationToken)
    {
        if (!TryCreateRequest(out var request))
        {
            BlockingMessage = EasyWin.Core.Localization.DeploymentStrings.Get("Ui76");
            return;
        }

        IsBusy = true;
        Stages[0].Status = UiStageStatus.Active;
        try
        {
            foreach (var check in Checks)
            {
                check.Status = UiCheckStatus.Pending;
                check.Message = EasyWin.Core.Localization.DeploymentStrings.Get("Ui77");
            }

            var results = await _workflow.PreflightAsync(request, cancellationToken).ConfigureAwait(true);
            foreach (var result in results)
            {
                SetCheck(result.Id, result.Status, result.Message, result.Name, result.IsCritical);
            }

            var failed = Checks.Where(check => check.IsCritical && check.Status != UiCheckStatus.Pass).ToArray();
            Stages[0].Status = failed.Length == 0 ? UiStageStatus.Complete : UiStageStatus.Failed;
            BlockingMessage = failed.Length == 0
                ? string.Empty
                : EasyWin.Core.Localization.DeploymentStrings.Format("CriticalChecks", string.Join(", ", failed.Select(item => item.Name)));
        }
        finally
        {
            IsBusy = false;
            RefreshCommandState();
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!TryCreateRequest(out var request) || SelectedDisk is null)
        {
            return;
        }

        await RunChecksAsync(cancellationToken).ConfigureAwait(true);
        if (!CanStart)
        {
            return;
        }

        if (!_interaction.ConfirmDisks(SelectedDisk, request.AdditionalDisksToErase))
        {
            return;
        }

        IsBusy = true;
        HasProgress = true;
        var progress = new Progress<UiDeploymentProgress>(UpdateProgress);
        try
        {
            var result = await _workflow.StageAndArmAsync(request, progress, cancellationToken).ConfigureAwait(true);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }

            foreach (var stage in Stages.Where(stage => stage.Status == UiStageStatus.Active))
            {
                stage.Status = UiStageStatus.Complete;
            }

            _interaction.ShowInformation(
                DryRun ? EasyWin.Core.Localization.DeploymentStrings.Get("Ui78") : EasyWin.Core.Localization.DeploymentStrings.Get("Ui79"),
                result.Message);
        }
        catch
        {
            var active = Stages.FirstOrDefault(stage => stage.Status == UiStageStatus.Active);
            if (active is not null)
            {
                active.Status = UiStageStatus.Failed;
            }

            throw;
        }
        finally
        {
            IsBusy = false;
            RefreshCommandState();
        }
    }

    private void UpdateProgress(UiDeploymentProgress update)
    {
        CurrentProgress.Stage = update.Stage;
        CurrentProgress.Message = update.Message;
        CurrentProgress.Percent = update.Percent;

        var currentIndex = FindStageIndex(update.StageId);
        for (var index = 0; index < Stages.Count; index++)
        {
            if (index < currentIndex)
            {
                Stages[index].Status = UiStageStatus.Complete;
            }
            else if (index == currentIndex)
            {
                Stages[index].Status = UiStageStatus.Active;
            }
        }
    }

    private int FindStageIndex(string stageId)
    {
        for (var index = 0; index < Stages.Count; index++)
        {
            if (Stages[index].Id.Equals(stageId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return Math.Max(0, Stages.Count - 1);
    }

    private bool CanRunChecks() => IsNotBusy && TryCreateRequest(out _);

    private bool TryCreateRequest(out UiPreparationRequest request)
    {
        request = null!;
        if (string.IsNullOrWhiteSpace(IsoPath) ||
            SelectedEdition is null ||
            SelectedProfile is null ||
            string.IsNullOrWhiteSpace(SelectedLanguage) ||
            SelectedDisk is null ||
            SelectedDriverMode is null)
        {
            return false;
        }

        request = new UiPreparationRequest(
            IsoPath,
            SelectedEdition,
            SelectedProfile,
            SelectedLanguage,
            SelectedDisk,
            SelectedAdditionalDisks,
            Applications.Where(item => item.IsSelected).Select(item => item.Id).ToArray(),
            SelectedDriverMode.Id,
            DryRun, SelectedStagingDisk, SelectedStagingVolume?.Volume, AdkRoot);
        return true;
    }

    private IReadOnlyList<DiskChoice> SelectedAdditionalDisks => AdditionalEraseDisks
        .Where(static item => item.IsSelected)
        .Select(static item => item.Disk)
        .ToArray();

    private void RebuildAdditionalDiskChoices()
    {
        foreach (DiskEraseChoice item in AdditionalEraseDisks)
        {
            item.SelectionChanged -= OnAdditionalDiskSelectionChanged;
        }

        AdditionalEraseDisks.Clear();
        foreach (DiskChoice disk in Disks.Where(disk => !ReferenceEquals(disk, SelectedDisk) && !ReferenceEquals(disk, SelectedStagingDisk)))
        {
            var item = new DiskEraseChoice(disk);
            item.SelectionChanged += OnAdditionalDiskSelectionChanged;
            AdditionalEraseDisks.Add(item);
        }
    }

    private void OnAdditionalDiskSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is DiskEraseChoice selected && selected.IsSelected)
        {
            foreach (DiskEraseChoice other in AdditionalEraseDisks.Where(item => !ReferenceEquals(item, selected) && item.IsSelected))
            {
                other.IsSelected = false;
            }
        }

        DestructiveAcknowledged = false;
        OnPropertyChanged(nameof(DiskSelectionSummary));
        OnPropertyChanged(nameof(DestructiveAcknowledgementText));
        InvalidateChecks();
    }

    private void OnApplicationSelectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedApplicationsSummary));
        InvalidateChecks();
    }

    private void AddInitialChecks()
    {
        Checks.Add(new CheckDisplayItem("administrator", EasyWin.Core.Localization.DeploymentStrings.Get("Ui80"), true));
        Checks.Add(new CheckDisplayItem("uefi", EasyWin.Core.Localization.DeploymentStrings.Get("Ui81"), true));
        Checks.Add(new CheckDisplayItem("target-disk", EasyWin.Core.Localization.DeploymentStrings.Get("Ui17"), true));
        Checks.Add(new CheckDisplayItem("erase-disks", EasyWin.Core.Localization.DeploymentStrings.Get("Ui82"), true));
        Checks.Add(new CheckDisplayItem("staging-disk", EasyWin.Core.Localization.DeploymentStrings.Get("Ui20"), true));
        Checks.Add(new CheckDisplayItem("staging-capacity", EasyWin.Core.Localization.DeploymentStrings.Get("Ui83"), true));
        Checks.Add(new CheckDisplayItem("disk-layout", EasyWin.Core.Localization.DeploymentStrings.Get("Ui84"), true));
        Checks.Add(new CheckDisplayItem("bitlocker", "BitLocker", true));
        Checks.Add(new CheckDisplayItem("image", EasyWin.Core.Localization.DeploymentStrings.Get("Ui6"), true));
        Checks.Add(new CheckDisplayItem("space", EasyWin.Core.Localization.DeploymentStrings.Get("Ui85"), true));
        Checks.Add(new CheckDisplayItem("power", EasyWin.Core.Localization.DeploymentStrings.Get("Ui86"), true));
        Checks.Add(new CheckDisplayItem("staging", EasyWin.Core.Localization.DeploymentStrings.Get("Ui87"), true));
        Checks.Add(new CheckDisplayItem("hashes", EasyWin.Core.Localization.DeploymentStrings.Get("Ui88"), true));
    }

    private void UpdateDiscoveryChecks()
    {
        if (Disks.Count == 0)
        {
            MarkCheckFailed("target-disk", EasyWin.Core.Localization.DeploymentStrings.Get("Ui89"));
        }
        else
        {
            SetCheck("target-disk", UiCheckStatus.Pending, EasyWin.Core.Localization.DeploymentStrings.Get("Ui90"));
        }

        BlockingMessage = EasyWin.Core.Localization.DeploymentStrings.Get("Ui91");
    }

    private void InvalidateChecks()
    {
        foreach (var check in Checks)
        {
            if (check.Status != UiCheckStatus.Fail || check.Id == "image")
            {
                check.Status = UiCheckStatus.Pending;
                check.Message = EasyWin.Core.Localization.DeploymentStrings.Get("Ui92");
            }
        }

        foreach (var stage in Stages)
        {
            stage.Status = UiStageStatus.Pending;
        }

        BlockingMessage = EasyWin.Core.Localization.DeploymentStrings.Get("Ui93");
        RefreshCommandState();
    }

    private void MarkCheckFailed(string id, string message) => SetCheck(id, UiCheckStatus.Fail, message);

    private void SetCheck(
        string id,
        UiCheckStatus status,
        string message,
        string? name = null,
        bool isCritical = true)
    {
        var item = Checks.FirstOrDefault(check => check.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new CheckDisplayItem(id, name ?? id, isCritical);
            Checks.Add(item);
        }

        item.Status = status;
        item.Message = message;
        RefreshCommandState();
    }

    private void RefreshCommandState()
    {
        OnPropertyChanged(nameof(CanStart));
        BrowseIsoCommand.NotifyCanExecuteChanged();
        RunChecksCommand.NotifyCanExecuteChanged();
        ReinstallCommand.NotifyCanExecuteChanged();
        RefreshPrerequisitesCommand?.NotifyCanExecuteChanged();
        BrowseAdkCommand?.NotifyCanExecuteChanged();
        OpenAdkDownloadCommand?.NotifyCanExecuteChanged();
        CopyErrorCommand?.NotifyCanExecuteChanged();
    }

    private void HandleError(Exception exception)
    {
        string code = exception is EasyWin.Deployment.Safety.DeploymentSafetyException safety ? safety.Code : "desktop.failed";
        string logRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EasyWin", "Logs");
        string logPath = Path.Combine(logRoot, "desktop-errors.log");
        string stage = string.IsNullOrWhiteSpace(CurrentProgress.Stage) ? "Desktop" : CurrentProgress.Stage;
        _lastError = $"{DateTimeOffset.UtcNow:O}\nStage: {stage}\nCode: {code}\nLog: {logPath}\n{exception}";
        try { Directory.CreateDirectory(logRoot); File.AppendAllText(logPath, _lastError + Environment.NewLine); }
        catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException)
        { _lastError += "\nLog write failed: " + loggingError.Message; }
        BlockingMessage = EasyWin.Core.Localization.DeploymentStrings.Get("GenericFailure") + " [" + code + "]";
        _interaction.ShowError("EasyWin", BlockingMessage + Environment.NewLine + _lastError);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private static bool ResolveDryRunDefault()
    {
        if (Environment.GetCommandLineArgs().Any(argument => argument.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (Environment.GetCommandLineArgs().Any(argument => argument.Equals("--real", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
