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
                RebuildAdditionalDiskChoices();
                DestructiveAcknowledged = false;
                OnPropertyChanged(nameof(DiskSelectionSummary));
                OnPropertyChanged(nameof(DestructiveAcknowledgementText));
                InvalidateChecks();
            }
        }
    }
    public ObservableCollection<DiskChoice> StagingDisks { get; } = [];
    private DriverModeChoice? _selectedDriverMode;
    private bool _destructiveAcknowledged;
    private bool _isBusy;
    private bool _hasProgress;
    private string _blockingMessage = "Выберите образ и выполните проверку.";
    private bool _disposed;

    public MainViewModel(IDesktopUiWorkflow workflow, IInteractionService interaction)
    {
        _workflow = workflow;
        _interaction = interaction;

        DryRun = ResolveDryRunDefault();
        ModeLabel = DryRun ? "DryRun · безопасная симуляция" : "Реальный режим";

        DriverModes.Add(new DriverModeChoice("automatic", "Автоматически по Hardware ID"));
        DriverModes.Add(new DriverModeChoice("none", "Не устанавливать"));
        _selectedDriverMode = DriverModes[0];

        Stages.Add(new StageDisplayItem("validation", "Проверка", "Среда, образ и диск"));
        Stages.Add(new StageDisplayItem("staging", "Подготовка", "Локальный deployment-раздел"));
        Stages.Add(new StageDisplayItem("boot", "Загрузка", "Одноразовая запись WinPE"));
        Stages.Add(new StageDisplayItem("deployment", "Установка", "DISM, GPT и BCDBoot"));
        Stages.Add(new StageDisplayItem("postinstall", "Первый запуск", "Драйверы, приложения, очистка"));

        AddInitialChecks();

        BrowseIsoCommand = new AsyncRelayCommand(BrowseIsoAsync, () => IsNotBusy, HandleError);
        RunChecksCommand = new AsyncRelayCommand(RunChecksAsync, CanRunChecks, HandleError);
        ReinstallCommand = new AsyncRelayCommand(StartAsync, () => CanStart, HandleError);
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
    public string ModeLabel { get; }
    public string PrimaryActionLabel => DryRun ? "Запустить DryRun" : "Переустановить Windows";
    public bool HasEditions => Editions.Count > 0;
    public string EditionSummary => SelectedEdition is null
        ? "Сначала выберите ISO — после проверки здесь появится редакция Windows."
        : $"Будет установлена: {SelectedEdition.DisplayName} · {SelectedEdition.Architecture}";
    public string DiskSelectionSummary => SelectedDisk is null
        ? "Выберите диск для Windows."
        : $"Windows → {SelectedDisk.DisplayName} — БУДЕТ ОЧИЩЕН" +
          (SelectedStagingDisk is null || SelectedStagingDisk == SelectedDisk
              ? " · Хранилище: защищённый раздел на диске Windows"
              : $" · Хранилище → {SelectedStagingDisk.DisplayName} — НЕ БУДЕТ ОЧИЩЕН") +
          (SelectedAdditionalDisks.Count == 0
              ? " · второй диск не очищается"
              : $" · очистить также → {SelectedAdditionalDisks[0].DisplayName}");
    public string DestructiveAcknowledgementText => SelectedAdditionalDisks.Count == 0
        ? "Я понимаю, что все данные на диске Windows будут удалены"
        : "Я понимаю, что все данные на обоих выбранных дисках будут удалены";
    public string SelectedApplicationsSummary => Applications.Count(static item => item.IsSelected) switch
    {
        0 => "Дополнительные программы не выбраны",
        1 => "Выбрана 1 программа",
        var count => $"Выбрано программ: {count}",
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
            SelectedLanguage = Languages.FirstOrDefault(language => language.Equals("Русский", StringComparison.OrdinalIgnoreCase)) ?? Languages.FirstOrDefault();

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
                    MarkCheckFailed("image", $"Не удалось автоматически открыть ISO: {exception.Message}");
                    BlockingMessage = "Автоматическое чтение ISO завершилось ошибкой. Выберите образ вручную.";
                }
            }
        }
        catch (Exception exception)
        {
            MarkCheckFailed("environment", $"Не удалось получить сведения о системе: {exception.Message}");
            BlockingMessage = "Обнаружение системы завершилось ошибкой.";
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
            MarkCheckFailed("image", "В ISO не найден install.wim или install.esd с поддерживаемой редакцией.");
            BlockingMessage = "В выбранном ISO не найдена поддерживаемая редакция Windows.";
        }
        else
        {
            SetCheck("image", UiCheckStatus.Pass, $"Найдено редакций: {Editions.Count}. Выбрана {SelectedEdition!.DisplayName}.");
            BlockingMessage = $"Выбрана {SelectedEdition.DisplayName}. Запустите проверку готовности.";
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
            BlockingMessage = "Заполните образ, редакцию, профиль, язык и целевой диск.";
            return;
        }

        IsBusy = true;
        Stages[0].Status = UiStageStatus.Active;
        try
        {
            foreach (var check in Checks)
            {
                check.Status = UiCheckStatus.Pending;
                check.Message = "Проверяется…";
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
                : $"Критические проверки не пройдены: {string.Join(", ", failed.Select(item => item.Name))}.";
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
                DryRun ? "DryRun завершён" : "Подготовка завершена",
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
            DryRun, SelectedStagingDisk);
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
        Checks.Add(new CheckDisplayItem("administrator", "Права администратора", true));
        Checks.Add(new CheckDisplayItem("uefi", "Режим UEFI", true));
        Checks.Add(new CheckDisplayItem("target-disk", "Целевой диск", true));
        Checks.Add(new CheckDisplayItem("erase-disks", "Диски для очистки", true));
        Checks.Add(new CheckDisplayItem("staging-disk", "Хранилище установки", true));
        Checks.Add(new CheckDisplayItem("staging-capacity", "Место в хранилище", true));
        Checks.Add(new CheckDisplayItem("disk-layout", "Разметка целевого диска", true));
        Checks.Add(new CheckDisplayItem("bitlocker", "BitLocker", true));
        Checks.Add(new CheckDisplayItem("image", "Образ Windows", true));
        Checks.Add(new CheckDisplayItem("space", "Свободное место", true));
        Checks.Add(new CheckDisplayItem("power", "Питание", true));
        Checks.Add(new CheckDisplayItem("staging", "Deployment-среда", true));
        Checks.Add(new CheckDisplayItem("hashes", "Целостность файлов", true));
    }

    private void UpdateDiscoveryChecks()
    {
        if (Disks.Count == 0)
        {
            MarkCheckFailed("target-disk", "Подходящие локальные диски не найдены.");
        }
        else
        {
            SetCheck("target-disk", UiCheckStatus.Pending, "Выберите диск и запустите проверку.");
        }

        BlockingMessage = "Выберите ISO и запустите проверку готовности.";
    }

    private void InvalidateChecks()
    {
        foreach (var check in Checks)
        {
            if (check.Status != UiCheckStatus.Fail || check.Id == "image")
            {
                check.Status = UiCheckStatus.Pending;
                check.Message = "Требуется проверка";
            }
        }

        foreach (var stage in Stages)
        {
            stage.Status = UiStageStatus.Pending;
        }

        BlockingMessage = "Выполните проверку готовности после изменения параметров.";
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
    }

    private void HandleError(Exception exception)
    {
        BlockingMessage = exception.Message;
        _interaction.ShowError("EasyWin", exception.Message);
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
