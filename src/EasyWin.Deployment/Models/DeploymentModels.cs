using System.Collections.ObjectModel;
using EasyWin.Core.Models;

namespace EasyWin.Deployment.Models;

/// <summary>Severity of a fail-closed deployment check.</summary>
public enum CheckSeverity
{
    Information,
    Warning,
    Critical
}

public sealed record PreflightCheck(
    string Code,
    string DisplayName,
    CheckSeverity Severity,
    bool Passed,
    string Message);

public sealed record PreflightReport(IReadOnlyList<PreflightCheck> Checks)
{
    public bool CanContinue => Checks.All(check => check.Passed || check.Severity != CheckSeverity.Critical);

    public static PreflightReport From(IEnumerable<PreflightCheck> checks) =>
        new(new ReadOnlyCollection<PreflightCheck>(checks.ToList()));
}

public sealed record EnvironmentSnapshot(
    bool IsAdministrator,
    bool IsUefi,
    bool IsAcPowerConnected,
    int BatteryPercent,
    long AvailableBytes,
    string SystemDrive,
    bool IsWindows,
    Version OperatingSystemVersion);

public sealed record BitLockerVolumeStatus(
    string MountPoint,
    string ConversionStatus,
    string ProtectionStatus,
    bool IsEncrypted,
    bool IsProtectionEnabled);

public sealed record PhysicalDiskSnapshot(
    DiskIdentity Identity,
    bool IsReadOnly,
    bool IsOffline,
    string PartitionStyle,
    IReadOnlyList<PartitionInfo> Partitions);

public sealed record MountedIso(string ImagePath, string RootPath, bool MountedByEasyWin);

public sealed record StagingPartitionRequest(
    int DiskNumber,
    char SourceDriveLetter,
    char StagingDriveLetter,
    long RequestedSizeBytes,
    string Label = "EASYWIN_DEPLOY",
    Guid? ExpectedExistingPartitionGuid = null);

public sealed record DiskPreparationRequest(
    int DiskNumber,
    Guid DeploymentPartitionGuid,
    int DeploymentPartitionNumber,
    long DeploymentPartitionOffsetBytes,
    long DeploymentPartitionSizeBytes,
    char WindowsDriveLetter = 'W',
    char EfiDriveLetter = 'S',
    int EfiSizeMiB = 260,
    int MsrSizeMiB = 16);

public sealed record FinalizeDiskRequest(
    int DiskNumber,
    Guid DeploymentPartitionGuid,
    int DeploymentPartitionNumber,
    char WindowsDriveLetter = 'C',
    char RecoveryDriveLetter = 'R',
    int RecoverySizeMiB = 1024);

public sealed record AdditionalDiskEraseRequest(
    int DiskNumber,
    string Label = "Data");

public sealed record StagingCopyResult(
    string RootPath,
    IReadOnlyList<ManifestFileEntry> Files,
    string ManifestPath,
    long TotalBytes);

public sealed record TemporaryBootRequest(
    string BcdStorePath,
    string WinPeWimPath,
    string WinPeSdiPath,
    string Description = "EasyWin Deployment",
    int TimeoutSeconds = 3);

public sealed record TemporaryBootEntry(Guid LoaderId, Guid RamdiskOptionsId, string BackupPath);

public sealed record WinPeBuildRequest(
    string AdkRoot,
    string Architecture,
    string WorkingDirectory,
    string OutputDirectory,
    string WinPeExecutablePath,
    IReadOnlyList<string> OptionalComponents,
    IReadOnlyList<string> DriverInfPaths);

public sealed record WinPeBuildResult(
    string MediaRoot,
    string BootWimPath,
    string BootSdiPath,
    string StartupScriptPath);

public sealed record ApplyImageRequest(
    string ImagePath,
    int ImageIndex,
    string ApplyDirectory,
    bool Verify = true,
    bool CheckIntegrity = true,
    int? ScratchSpaceMiB = null);

public sealed record DriverInjectionRequest(
    string TargetImagePath,
    IReadOnlyList<string> InfPaths,
    bool Recurse = false,
    bool ForceUnsigned = false);

public sealed record BootFilesRequest(
    string WindowsDirectory,
    string SystemPartitionRoot,
    string? BcdStorePath = null,
    string Locale = "en-US");

public sealed record UnattendOptions(
    string Language,
    string Locale,
    string TimeZone,
    string ComputerName,
    string? ProductKey,
    bool HideEulaPage = true,
    bool SkipWirelessSetup = false);

public sealed record AppInstallResult(string Id, bool Succeeded, int? ExitCode, string Message);

public sealed record DriverMatch(string HardwareId, DriverPackage Driver, int Rank);

public sealed record PostInstallPayloadRequest(
    string TargetWindowsRoot,
    string PostInstallExecutable,
    string ManifestPath,
    IReadOnlyList<string> AdditionalFiles);
