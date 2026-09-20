namespace EasyWin.Core.Models;

public sealed record ReinstallPlan
{
    public Guid PlanId { get; init; } = Guid.NewGuid();

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public ExecutionMode ExecutionMode { get; init; }

    public DiskIdentity TargetDisk { get; init; } = new();

    public DiskIdentity? StagingDisk { get; init; }

    public Guid? StagingVolumeId { get; init; }

    public PartitionInfo? ExpectedStagingVolume { get; init; }

    public long RequiredStagingBytes { get; init; }

    public IReadOnlyList<DiskIdentity> AdditionalDisksToErase { get; init; } = Array.Empty<DiskIdentity>();

    public WindowsImageInfo Image { get; init; } = new();

    public string ProfileId { get; init; } = "standard";

    public IReadOnlyList<string> ApplicationIds { get; init; } = Array.Empty<string>();

    public DriverSelectionMode DriverSelectionMode { get; init; } = DriverSelectionMode.Automatic;

    public IReadOnlyList<string> DriverIds { get; init; } = Array.Empty<string>();

    public string Language { get; init; } = "ru-RU";

    public string TimeZone { get; init; } = "Russian Standard Time";

    public string? ComputerName { get; init; }

    public bool DataLossAcknowledged { get; init; }

    public bool FinalConfirmationAccepted { get; init; }
}
