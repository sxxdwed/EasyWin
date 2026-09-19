namespace EasyWin.Core.Models;

public sealed record WindowsImageInfo
{
    public string SourcePath { get; init; } = string.Empty;

    public int ImageIndex { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string EditionId { get; init; } = string.Empty;

    public ProcessorArchitecture Architecture { get; init; }

    public string? Version { get; init; }

    public long SizeBytes { get; init; }

    public WindowsImageContainer Container { get; init; }
}

public sealed record InstallationProfile
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> DefaultApplicationIds { get; init; } = Array.Empty<string>();

    public bool PreserveWindowsUpdate { get; init; } = true;

    public bool PreserveDefender { get; init; } = true;

    public bool PreserveMicrosoftStore { get; init; } = true;

    public bool PreserveRecovery { get; init; } = true;

    public bool PreserveServicingStack { get; init; } = true;
}

public sealed record ApplicationPackage
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Category { get; init; } = "Общее";

    public string Description { get; init; } = string.Empty;

    public string Installer { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public string Sha256 { get; init; } = string.Empty;

    public long? SizeBytes { get; init; }

    public IReadOnlyList<int> SuccessExitCodes { get; init; } = new[] { 0 };

    public bool RequiresReboot { get; init; }

    public IReadOnlyList<string> RequiredHardwareIdPrefixes { get; init; } = Array.Empty<string>();
}

public sealed record DriverPackage
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public DriverCategory Category { get; init; }

    public string InfPath { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public long? SizeBytes { get; init; }

    public ProcessorArchitecture Architecture { get; init; } = ProcessorArchitecture.X64;

    public IReadOnlyList<string> HardwareIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CompatibleIds { get; init; } = Array.Empty<string>();
}
