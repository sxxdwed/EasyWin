namespace EasyWin.Core.Models;

public static class DeploymentManifestSchema
{
    public const string CurrentVersion = "1.3";
    public const string HashAlgorithm = "SHA-256";
}

public sealed record DeploymentImageReference
{
    public string RelativePath { get; init; } = string.Empty;

    public int ImageIndex { get; init; }

    public string Name { get; init; } = string.Empty;

    public string EditionId { get; init; } = string.Empty;

    public ProcessorArchitecture Architecture { get; init; }

    public WindowsImageContainer Container { get; init; }

    public string Sha256 { get; init; } = string.Empty;

    public long LengthBytes { get; init; }
}

public sealed record BootConfiguration
{
    public Guid? BootEntryId { get; init; }

    public string BcdBackupRelativePath { get; init; } = "Boot/bcd.backup";

    public string WinPeWimRelativePath { get; init; } = "WinPE/sources/boot.wim";

    public string WinPeSdiRelativePath { get; init; } = "WinPE/boot/boot.sdi";

    public string Description { get; init; } = "EasyWin Deployment";

    public bool OneTimeBootConfigured { get; init; }
}

public sealed record ManifestFileEntry
{
    public string RelativePath { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public long LengthBytes { get; init; }

    public ManifestFileKind Kind { get; init; }

    public bool Required { get; init; } = true;
}

public sealed record DeploymentState
{
    public bool FirstBootValidated { get; init; }
    public bool DestructiveWorkStarted { get; init; }
    public IReadOnlyList<PartitionInfo> PreparedTargetLayout { get; init; } = [];
    public IReadOnlyList<DeploymentStage> StartedStages { get; init; } = [];
    public IReadOnlyList<string> OptionalWarnings { get; init; } = [];
    public IReadOnlyList<string> CompletedApplicationIds { get; init; } = [];
    public IReadOnlyList<string> FailedApplicationIds { get; init; } = [];
    public DeploymentStage CurrentStage { get; init; } = DeploymentStage.NotStarted;

    public IReadOnlyList<DeploymentStage> CompletedStages { get; init; } = Array.Empty<DeploymentStage>();

    public IReadOnlyDictionary<string, DateTimeOffset> Checkpoints { get; init; } =
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public int AttemptNumber { get; init; }

    public DeploymentStage? LastSuccessfulStage { get; init; }

    public bool RecoveryRequired { get; init; }

    public DeploymentError? LastError { get; init; }
}

public sealed record ManifestIntegrity
{
    public string Algorithm { get; init; } = DeploymentManifestSchema.HashAlgorithm;

    public string PayloadSha256 { get; init; } = string.Empty;
}

public sealed record DeploymentManifest
{
    public string SchemaVersion { get; init; } = DeploymentManifestSchema.CurrentVersion;

    public Guid ManifestId { get; init; } = Guid.NewGuid();

    public Guid PlanId { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public ExecutionMode ExecutionMode { get; init; }

    public DiskIdentity TargetDisk { get; init; } = new();

    public bool DataLossAcknowledged { get; init; }

    public bool FinalConfirmationAccepted { get; init; }

    public IReadOnlyList<DiskIdentity> AdditionalDisksToErase { get; init; } = Array.Empty<DiskIdentity>();

    public StagingPartitionIdentity StagingPartition { get; init; } = new();

    public DeploymentImageReference Image { get; init; } = new();

    public BootConfiguration Boot { get; init; } = new();

    public string ProfileId { get; init; } = string.Empty;

    public IReadOnlyList<string> ApplicationIds { get; init; } = Array.Empty<string>();

    public DriverSelectionMode DriverSelectionMode { get; init; } = DriverSelectionMode.Automatic;

    public IReadOnlyList<string> DriverIds { get; init; } = Array.Empty<string>();

    public string Language { get; init; } = "ru-RU";

    public string TimeZone { get; init; } = "Russian Standard Time";

    public string? ComputerName { get; init; }

    public IReadOnlyList<ManifestFileEntry> FileInventory { get; init; } = Array.Empty<ManifestFileEntry>();

    public DeploymentState State { get; init; } = new();

    public ManifestIntegrity Integrity { get; init; } = new();
}
