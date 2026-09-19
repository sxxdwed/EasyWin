using System.Collections.ObjectModel;
using System.Windows.Media;
using EasyWin.Desktop.Mvvm;

namespace EasyWin.Desktop.Models;

public sealed record EditionChoice(int Index, string DisplayName, string Architecture);

public sealed record ProfileChoice(string Id, string DisplayName, string Description);

public sealed record DiskChoice(
    string DeviceId,
    string Model,
    string Serial,
    long SizeBytes,
    string BusType,
    bool IsSystemDisk,
    long StagingCapacityBytes,
    object NativeIdentity)
{
    public string DisplayName => $"{Model}  ·  {FormatSize(SizeBytes)}  ·  {Serial}";
    public string ConfirmationText => $"ERASE {DeviceId}";
    public bool IsRecommendedTarget => StagingCapacityBytes >= 20L * 1024 * 1024 * 1024;
    public string StagingCapacityText => $"безопасный резерв: {FormatSize(StagingCapacityBytes)}";

    private static string FormatSize(long value)
    {
        var gib = value / 1024d / 1024d / 1024d;
        return gib >= 1000 ? $"{gib / 1024d:0.00} TiB" : $"{gib:0} GiB";
    }
}

public sealed record DriverModeChoice(string Id, string DisplayName);

public sealed class DiskEraseChoice(DiskChoice disk) : ObservableObject
{
    private bool _isSelected;

    public event EventHandler? SelectionChanged;

    public DiskChoice Disk { get; } = disk;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

public sealed class ApplicationChoice(
    string id,
    string name,
    string description = "",
    string category = "Общее",
    bool isAvailable = true,
    string availabilityText = "",
    bool isSelected = false) : ObservableObject
{
    private bool _isSelected = isSelected;

    public event EventHandler? SelectionChanged;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string Category { get; } = category;
    public bool IsAvailable { get; } = isAvailable;
    public string AvailabilityText { get; } = availabilityText;
    public string Details => string.IsNullOrWhiteSpace(AvailabilityText)
        ? Description
        : string.IsNullOrWhiteSpace(Description) ? AvailabilityText : $"{Description} · {AvailabilityText}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsAvailable && SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

public enum UiCheckStatus
{
    Pending,
    Pass,
    Warning,
    Fail
}

public sealed class CheckDisplayItem : ObservableObject
{
    private string _message = "Ожидание проверки";
    private UiCheckStatus _status;

    public CheckDisplayItem(string id, string name, bool isCritical)
    {
        Id = id;
        Name = name;
        IsCritical = isCritical;
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsCritical { get; }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public UiCheckStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public string StatusLabel => Status switch
    {
        UiCheckStatus.Pass => "PASS",
        UiCheckStatus.Warning => "WARN",
        UiCheckStatus.Fail => "FAIL",
        _ => "—"
    };

    public Brush StatusBrush => Status switch
    {
        UiCheckStatus.Pass => Brushes.MediumSeaGreen,
        UiCheckStatus.Warning => Brushes.Goldenrod,
        UiCheckStatus.Fail => Brushes.IndianRed,
        _ => Brushes.SlateGray
    };
}

public enum UiStageStatus
{
    Pending,
    Active,
    Complete,
    Failed
}

public sealed class StageDisplayItem : ObservableObject
{
    private UiStageStatus _status;

    public StageDisplayItem(string id, string name, string description)
    {
        Id = id;
        Name = name;
        Description = description;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }

    public UiStageStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public Brush StatusBrush => Status switch
    {
        UiStageStatus.Active => Brushes.CornflowerBlue,
        UiStageStatus.Complete => Brushes.MediumSeaGreen,
        UiStageStatus.Failed => Brushes.IndianRed,
        _ => Brushes.Transparent
    };
}

public sealed class ProgressDisplay : ObservableObject
{
    private string _stage = string.Empty;
    private string _message = string.Empty;
    private double _percent;

    public string Stage
    {
        get => _stage;
        set => SetProperty(ref _stage, value);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public double Percent
    {
        get => _percent;
        set
        {
            if (SetProperty(ref _percent, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(PercentText));
            }
        }
    }

    public string PercentText => $"{Percent:0}%";
}

public sealed record UiPreflightCheck(string Id, string Name, string Message, bool IsCritical, UiCheckStatus Status);

public sealed record UiDeploymentProgress(string StageId, string Stage, string Message, double Percent);

public sealed record DesktopDiscovery(
    IReadOnlyList<DiskChoice> Disks,
    IReadOnlyList<ProfileChoice> Profiles,
    IReadOnlyList<ApplicationChoice> Applications,
    IReadOnlyList<string> Languages);

public sealed record UiPreparationRequest(
    string IsoPath,
    EditionChoice Edition,
    ProfileChoice Profile,
    string Language,
    DiskChoice TargetDisk,
    IReadOnlyList<DiskChoice> AdditionalDisksToErase,
    IReadOnlyList<string> ApplicationIds,
    string DriverMode,
    bool DryRun,
    DiskChoice? StagingDisk = null);

public sealed record UiWorkflowResult(bool Success, string Message, string? ManifestPath = null);
